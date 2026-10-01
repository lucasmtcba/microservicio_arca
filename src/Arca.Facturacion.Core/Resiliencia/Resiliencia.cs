using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Arca.Facturacion.Core.Resiliencia;

/// <summary>El circuito hacia ARCA está abierto: se falla rápido sin llamar.</summary>
public sealed class CircuitoAbiertoException : ArcaException
{
    public TimeSpan ReintentarEn { get; }
    public CircuitoAbiertoException(TimeSpan reintentarEn)
        : base($"ARCA no responde: circuito abierto. Reintentar en {Math.Ceiling(reintentarEn.TotalSeconds)} s.")
        => ReintentarEn = reintentarEn;
}

/// <summary>Circuit breaker simple (cerrado → abierto tras N fallas de comunicación consecutivas → semiabierto tras la pausa).</summary>
public sealed class CircuitBreaker
{
    private readonly int _umbral;
    private readonly TimeSpan _pausa;
    private readonly TimeProvider _reloj;
    private readonly object _lock = new();
    private int _fallas;
    private DateTimeOffset? _abiertoHasta;
    /// <summary>Inicio de la sonda en curso en estado semiabierto (null si no hay ninguna).</summary>
    private DateTimeOffset? _sondaDesde;

    public CircuitBreaker(int umbral, TimeSpan pausa, TimeProvider reloj)
    {
        _umbral = Math.Max(1, umbral); _pausa = pausa; _reloj = reloj;
    }

    public string Estado
    {
        get
        {
            lock (_lock)
                return _abiertoHasta is { } h ? (_reloj.GetUtcNow() < h ? "Abierto" : "SemiAbierto") : "Cerrado";
        }
    }

    public void Verificar()
    {
        lock (_lock)
        {
            var ahora = _reloj.GetUtcNow();
            if (_abiertoHasta is not { } h) return;
            if (ahora < h) throw new CircuitoAbiertoException(h - ahora);
            // Semiabierto: pasa UNA sola llamada de prueba. Si la sonda quedó colgada (cancelada sin resultado),
            // tras otra pausa se permite una nueva.
            if (_sondaDesde is { } s && ahora < s + _pausa) throw new CircuitoAbiertoException(s + _pausa - ahora);
            _sondaDesde = ahora;
        }
    }

    public void RegistrarExito()
    {
        lock (_lock) { _fallas = 0; _abiertoHasta = null; _sondaDesde = null; }
    }

    public void RegistrarFalla()
    {
        lock (_lock)
        {
            _fallas++;
            _sondaDesde = null;
            // En semiabierto, una sola falla vuelve a abrir.
            if (_fallas >= _umbral || _abiertoHasta != null)
            {
                _abiertoHasta = _reloj.GetUtcNow() + _pausa;
                ArcaTelemetria.CircuitosAbiertos.Add(1);
            }
        }
    }
}

/// <summary>Métricas (System.Diagnostics.Metrics) y trazas (ActivitySource) del servicio, exportables por OpenTelemetry.</summary>
public static class ArcaTelemetria
{
    public const string Nombre = "Arca.Facturacion";
    public static readonly ActivitySource Actividades = new(Nombre);
    public static readonly Meter Medidor = new(Nombre);
    public static readonly Counter<long> LlamadasArca = Medidor.CreateCounter<long>("arca.llamadas", description: "Llamadas a WSFEv1 por método y resultado");
    public static readonly Histogram<double> DuracionArca = Medidor.CreateHistogram<double>("arca.duracion", "ms", "Duración de llamadas a ARCA");
    public static readonly Counter<long> Emisiones = Medidor.CreateCounter<long>("arca.emisiones", description: "Comprobantes emitidos por resultado");
    public static readonly Counter<long> Reconciliaciones = Medidor.CreateCounter<long>("arca.reconciliaciones");
    public static readonly Counter<long> CircuitosAbiertos = Medidor.CreateCounter<long>("arca.circuito.aperturas");
    public static readonly Histogram<double> DuracionEmision = Medidor.CreateHistogram<double>("arca.emision.duracion", "ms", "Duración total de una emisión (validación + numeración + ARCA)");
    public static readonly Counter<long> Advertencias = Medidor.CreateCounter<long>("arca.emisiones.advertencias");
    public static readonly Counter<long> AciertosCache = Medidor.CreateCounter<long>("arca.cache.aciertos");
}

/// <summary>Estado observado de ARCA para diagnóstico (/info, health).</summary>
public sealed class EstadoArca
{
    private long _ultimoExitoTicks;
    private long _desfasajeMs;

    public DateTimeOffset? UltimoExito => _ultimoExitoTicks == 0 ? null : new DateTimeOffset(Interlocked.Read(ref _ultimoExitoTicks), TimeSpan.Zero);
    /// <summary>Hora local menos hora de ARCA (cabecera Date). Positivo = reloj local adelantado.</summary>
    public TimeSpan DesfasajeReloj => TimeSpan.FromMilliseconds(Interlocked.Read(ref _desfasajeMs));

    public void RegistrarRespuesta(DateTimeOffset ahora, DateTimeOffset? fechaServidor)
    {
        Interlocked.Exchange(ref _ultimoExitoTicks, ahora.UtcTicks);
        if (fechaServidor is { } f) Interlocked.Exchange(ref _desfasajeMs, (long)(ahora - f).TotalMilliseconds);
    }
}
