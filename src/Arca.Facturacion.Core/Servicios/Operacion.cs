using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;

namespace Arca.Facturacion.Core.Servicios;

/// <summary>Comprobante rechazado por ARCA (no consumió número).</summary>
public sealed record RechazoReciente(DateTimeOffset Fecha, string Cliente, int PtoVta, int CbteTipo, IReadOnlyList<ArcaMensaje> Errores, IReadOnlyList<ArcaMensaje> Observaciones);

/// <summary>Últimos rechazos en memoria (anillo acotado) para diagnóstico rápido de problemas de datos.</summary>
public sealed class RegistroRechazos
{
    private readonly ConcurrentQueue<RechazoReciente> _q = new();
    private readonly int _max;
    public RegistroRechazos(int max = 500) => _max = max;

    public void Registrar(string cliente, ResultadoEmision r, DateTimeOffset fecha)
    {
        _q.Enqueue(new RechazoReciente(fecha, cliente, r.PtoVta, r.CbteTipo, r.Errores, r.Observaciones));
        while (_q.Count > _max && _q.TryDequeue(out _)) { }
    }

    public IReadOnlyList<RechazoReciente> Ultimos(int n) => _q.Reverse().Take(Math.Clamp(n, 1, _max)).ToList();

    /// <summary>Códigos de rechazo más frecuentes.</summary>
    public IReadOnlyList<(int Codigo, string Mensaje, int Cantidad)> Frecuentes() =>
        _q.SelectMany(r => r.Errores.Concat(r.Observaciones)).GroupBy(m => m.Code)
            .Select(g => (g.Key, g.First().Msg, g.Count())).OrderByDescending(x => x.Item3).Take(20).ToList();
}

public sealed record VentaPorReceptor(int DocTipo, long DocNro, int Comprobantes, decimal Total);
public sealed record VentaPorDia(string Fecha, int Comprobantes, decimal Total);
public sealed record VentaPorPuntoVenta(int PtoVta, int Comprobantes, decimal Total, long UltimoNumero);

public sealed class CierreDiario
{
    public string Fecha { get; set; } = string.Empty;
    public List<CierreTipo> Tipos { get; set; } = new();
    public int Pendientes { get; set; }
    public bool Consistente => Tipos.All(t => t.Consistente) && Pendientes == 0;
}

public sealed class CierreTipo
{
    public int PtoVta { get; set; }
    public int CbteTipo { get; set; }
    public string? Descripcion { get; set; }
    public int Emitidos { get; set; }
    public decimal Total { get; set; }
    public long UltimoRegistro { get; set; }
    public long UltimoArca { get; set; }
    /// <summary>true si el último número de ARCA coincide con el último emitido por este servicio.</summary>
    public bool Consistente => UltimoArca == UltimoRegistro;
}

public sealed class ReportesService
{
    private readonly IRegistroEmisiones _reg;
    private readonly IWsfeClient _wsfe;
    private readonly ComprobanteValidator _val;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public ReportesService(IRegistroEmisiones reg, IWsfeClient wsfe, ComprobanteValidator val) { _reg = reg; _wsfe = wsfe; _val = val; }

    private static decimal Signo(int t) => t is 3 or 8 or 13 or 53 or 203 or 208 or 213 ? -1m : 1m;
    private static decimal EnPesos(ComprobanteDetalle d) => d.ImpTotal * (d.MonId == "PES" ? 1m : d.MonCotiz);

    private IEnumerable<RegistroEmision> Autorizadas(string? desde, string? hasta) =>
        _reg.Listar().Where(r => r.Estado == EstadoEmision.Autorizado && r.Detalle != null && r.Resultado?.CbteFch is { } f
            && (desde == null || string.CompareOrdinal(f, desde) >= 0) && (hasta == null || string.CompareOrdinal(f, hasta) <= 0));

    public IReadOnlyList<VentaPorReceptor> PorReceptor(string? desde, string? hasta, int top) =>
        Autorizadas(desde, hasta).GroupBy(r => (r.Detalle!.DocTipo, r.Detalle.DocNro))
            .Select(g => new VentaPorReceptor(g.Key.DocTipo, g.Key.DocNro, g.Count(), g.Sum(r => Signo(r.CbteTipo) * EnPesos(r.Detalle!))))
            .OrderByDescending(v => v.Total).Take(Math.Clamp(top, 1, 1000)).ToList();

    public IReadOnlyList<VentaPorDia> PorDia(string? desde, string? hasta) =>
        Autorizadas(desde, hasta).GroupBy(r => r.Resultado!.CbteFch!)
            .Select(g => new VentaPorDia(g.Key, g.Count(), g.Sum(r => Signo(r.CbteTipo) * EnPesos(r.Detalle!))))
            .OrderBy(v => v.Fecha, StringComparer.Ordinal).ToList();

    public IReadOnlyList<VentaPorPuntoVenta> PorPuntoVenta(string? desde, string? hasta) =>
        Autorizadas(desde, hasta).GroupBy(r => r.PtoVta)
            .Select(g => new VentaPorPuntoVenta(g.Key, g.Count(), g.Sum(r => Signo(r.CbteTipo) * EnPesos(r.Detalle!)), g.Max(r => r.CbteNro)))
            .OrderBy(v => v.PtoVta).ToList();

    /// <summary>Cierre del día: por punto de venta y tipo, emitidos y total; compara el último número con ARCA.</summary>
    public async Task<CierreDiario> CierreAsync(string? fecha, CancellationToken ct)
    {
        fecha ??= _val.HoyArgentina().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var cierre = new CierreDiario { Fecha = fecha, Pendientes = _reg.Listar().Count(r => r.Estado == EstadoEmision.Pendiente) };
        foreach (var g in Autorizadas(fecha, fecha).GroupBy(r => (r.PtoVta, r.CbteTipo)).OrderBy(g => g.Key))
        {
            var ultimoReg = _reg.Listar().Where(r => r.PtoVta == g.Key.PtoVta && r.CbteTipo == g.Key.CbteTipo && r.Estado == EstadoEmision.Autorizado).Max(r => r.CbteNro);
            var ultimoArca = (await _wsfe.FECompUltimoAutorizadoAsync(g.Key.PtoVta, g.Key.CbteTipo, ct).ConfigureAwait(false)).CbteNro;
            cierre.Tipos.Add(new CierreTipo
            {
                PtoVta = g.Key.PtoVta, CbteTipo = g.Key.CbteTipo, Descripcion = Catalogos.TiposComprobante.GetValueOrDefault(g.Key.CbteTipo),
                Emitidos = g.Count(), Total = g.Sum(r => Signo(r.CbteTipo) * EnPesos(r.Detalle!)), UltimoRegistro = ultimoReg, UltimoArca = ultimoArca
            });
        }
        return cierre;
    }

    /// <summary>Próximo número que asignaría ARCA (último autorizado + 1). Sólo informativo: la emisión numera bajo lock.</summary>
    public async Task<long> ProximoNumeroAsync(int pv, int tipo, CancellationToken ct) =>
        (await _wsfe.FECompUltimoAutorizadoAsync(pv, tipo, ct).ConfigureAwait(false)).CbteNro + 1;

    /// <summary>Puntos de venta con estado legible.</summary>
    public async Task<IReadOnlyList<object>> PuntosVentaAsync(CancellationToken ct) =>
        (await _wsfe.FEParamGetPtosVentaAsync(ct).ConfigureAwait(false)).Select(p => (object)new
        {
            numero = int.TryParse(p.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
            emisionTipo = p.Atributos.GetValueOrDefault("EmisionTipo"),
            bloqueado = p.Atributos.GetValueOrDefault("Bloqueado") == "S",
            dadoDeBaja = p.Atributos.TryGetValue("FchBaja", out var b) && !string.IsNullOrWhiteSpace(b) && b != "NULL",
            fechaBaja = p.Atributos.GetValueOrDefault("FchBaja") is { } fb && fb != "NULL" ? fb : null,
            habilitado = p.Atributos.GetValueOrDefault("Bloqueado") != "S" && (!p.Atributos.TryGetValue("FchBaja", out var b2) || string.IsNullOrWhiteSpace(b2) || b2 == "NULL")
        }).ToList();

    /// <summary>Respaldo completo del registro de emisiones en JSON Lines.</summary>
    public string Exportar() => string.Join('\n', _reg.Listar().OrderBy(r => r.Creado).Select(r => JsonSerializer.Serialize(r, Json)));

    /// <summary>Restaura un respaldo JSONL. Sólo agrega claves inexistentes (no pisa estado actual). Devuelve (importados, omitidos).</summary>
    public (int Importados, int Omitidos) Importar(string jsonl)
    {
        int imp = 0, omi = 0;
        foreach (var linea in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            RegistroEmision? r;
            try { r = JsonSerializer.Deserialize<RegistroEmision>(linea, Json); }
            catch (JsonException) { omi++; continue; }
            // Sólo se restauran emisiones AUTORIZADAS y completas: un Pendiente importado podría reenviar un número a ARCA.
            if (r == null || string.IsNullOrEmpty(r.Clave) || string.IsNullOrEmpty(r.Hash) || !r.Clave.Contains(':', StringComparison.Ordinal)
                || r.Estado != EstadoEmision.Autorizado || string.IsNullOrEmpty(r.Resultado?.Cae) || r.CbteNro < 1
                || _reg.Obtener(r.Clave) != null) { omi++; continue; }
            _reg.Guardar(r);
            imp++;
        }
        return (imp, omi);
    }
}
