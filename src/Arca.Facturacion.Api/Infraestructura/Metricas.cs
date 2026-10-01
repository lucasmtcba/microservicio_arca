using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Arca.Facturacion.Core.Resiliencia;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Wsaa;

namespace Arca.Facturacion.Api.Infraestructura;

/// <summary>Indicadores observables (gauges) del servicio: pendientes, días al vencimiento del certificado,
/// emisiones del día por cuotas y llamadas a ARCA en curso.</summary>
public sealed class IndicadoresServicio : IDisposable
{
    private readonly List<IDisposable> _registros = new();

    public IndicadoresServicio(IServiceProvider sp)
    {
        var m = ArcaTelemetria.Medidor;
        var pendientes = 0; var leido = DateTime.MinValue;
        m.CreateObservableGauge("arca.emisiones.pendientes", () => Medir(() =>
        {
            // Se recalcula como mucho cada 30 s: un scrape frecuente no recorre el registro en cada llamada.
            if (DateTime.UtcNow - leido > TimeSpan.FromSeconds(30)) { pendientes = sp.GetRequiredService<IFacturacionService>().Pendientes().Count; leido = DateTime.UtcNow; }
            return pendientes;
        }),
            description: "Emisiones con resultado no confirmado");
        m.CreateObservableGauge("arca.certificado.dias_restantes", () => Medir(() =>
        {
            try
            {
                return sp.GetRequiredService<ICmsFirmante>() switch
                {
                    FirmanteDiferido d => (d.Vencimiento.ToUniversalTime() - DateTime.UtcNow).TotalDays,
                    CertificadoCmsFirmante c => (c.Vencimiento.ToUniversalTime() - DateTime.UtcNow).TotalDays,
                    _ => double.NaN
                };
            }
            catch (Core.ArcaException) { return double.NaN; }
        }), description: "Días hasta el vencimiento del certificado de ARCA");
        m.CreateObservableGauge("arca.circuito.abierto", () => Medir(() => sp.GetRequiredService<CircuitBreaker>().Estado == "Abierto" ? 1 : 0));
        m.CreateObservableGauge("arca.llamadas.en_curso", () => Medir(() => (sp.GetRequiredService<Core.Wsfe.IWsfeClient>() as WsfeClienteResiliente)?.LlamadasEnCurso ?? 0));
    }

    private volatile bool _desechado;

    /// <summary>El medidor es estático: tras reiniciar el host (o con varios hosts en el mismo proceso) los gauges viejos
    /// siguen registrados. Una vez desechado, o si el contenedor ya no existe, no informan nada en vez de romper /metrics.</summary>
    private Measurement<T>[] Medir<T>(Func<T> f) where T : struct
    {
        if (_desechado) return Array.Empty<Measurement<T>>();
        try { return new[] { new Measurement<T>(f()) }; }
        catch (ObjectDisposedException) { _desechado = true; return Array.Empty<Measurement<T>>(); }
    }

    public void Dispose() { _desechado = true; foreach (var r in _registros) r.Dispose(); }
}

/// <summary>Exportador mínimo en formato de texto de Prometheus (/metrics) del medidor "Arca.Facturacion",
/// sin dependencias externas. Contadores e histogramas (count/sum) se acumulan; los gauges se observan en cada scrape.</summary>
public sealed class ExportadorPrometheus : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, double> _sumas = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private readonly ConcurrentDictionary<string, string> _tipos = new();

    public ExportadorPrometheus()
    {
        _listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == ArcaTelemetria.Nombre) l.EnableMeasurementEvents(inst);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Registrar(i, v, tags));
        _listener.SetMeasurementEventCallback<int>((i, v, tags, _) => Registrar(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Registrar(i, v, tags));
        _listener.Start();
    }

    private static string Nombre(Instrument i) => "arca_" + i.Name.Replace("arca.", "", StringComparison.Ordinal).Replace('.', '_');

    private static string Etiquetas(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0) return "";
        var sb = new StringBuilder("{");
        for (var i = 0; i < tags.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var v = Convert.ToString(tags[i].Value, CultureInfo.InvariantCulture)?.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
            sb.Append(tags[i].Key).Append("=\"").Append(v).Append('"');
        }
        return sb.Append('}').ToString();
    }

    private void Registrar(Instrument i, double v, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var n = Nombre(i);
        var et = Etiquetas(tags);
        switch (i)
        {
            case Histogram<double>:
                _tipos[n] = "summary";
                _sumas.AddOrUpdate(n + "_count" + et, 1, (_, x) => x + 1);
                _sumas.AddOrUpdate(n + "_sum" + et, v, (_, x) => x + v);
                break;
            case ObservableGauge<int> or ObservableGauge<long> or ObservableGauge<double>:
                _tipos[n] = "gauge";
                _gauges[n + et] = v;
                break;
            default:
                _tipos[n] = "counter";
                _sumas.AddOrUpdate(n + "_total" + et, v, (_, x) => x + v);
                break;
        }
    }

    /// <summary>Texto de exposición de Prometheus.</summary>
    public string Exponer()
    {
        try { _listener.RecordObservableInstruments(); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* un gauge defectuoso no tumba el scrape */ }
        var sb = new StringBuilder();
        foreach (var (n, t) in _tipos.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append("# TYPE ").Append(n).Append(t == "counter" ? "_total" : "").Append(' ').Append(t).Append('\n');
            var fuente = t == "gauge" ? _gauges : _sumas;
            static bool De(string clave, string n) => clave == n || clave.StartsWith(n + "{", StringComparison.Ordinal)
                || clave.StartsWith(n + "_total", StringComparison.Ordinal) || clave.StartsWith(n + "_count", StringComparison.Ordinal)
                || clave.StartsWith(n + "_sum", StringComparison.Ordinal);
            foreach (var (k, v) in fuente.Where(k => De(k.Key, n)).OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(k).Append(' ').Append(double.IsNaN(v) ? "NaN" : v.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    public void Dispose() => _listener.Dispose();
}
