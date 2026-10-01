using System.Diagnostics;
using Arca.Facturacion.Core.Configuracion;
using Arca.Facturacion.Core.Soap;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arca.Facturacion.Core.Resiliencia;

/// <summary>Decorador de <see cref="IWsfeClient"/> que agrega:
/// caché de tablas de parámetros y cotizaciones, reintentos con backoff SOLO en operaciones de lectura,
/// circuit breaker, métricas y trazas. Las operaciones que generan comprobantes nunca se reintentan aquí
/// (eso lo resuelve la reconciliación de FacturacionService).</summary>
public sealed class WsfeClienteResiliente : IWsfeClient, IDisposable
{
    private readonly IWsfeClient _inner;
    private readonly IMemoryCache _cache;
    private readonly CircuitBreaker _breaker;
    private readonly ArcaOptions _opt;
    private readonly TimeProvider _reloj;
    private readonly ILogger<WsfeClienteResiliente> _log;
    private readonly IReadOnlyList<TimeSpan> _backoff;
    private readonly SemaphoreSlim _bulkhead;

    public WsfeClienteResiliente(IWsfeClient inner, IMemoryCache cache, CircuitBreaker breaker, IOptions<ArcaOptions> opt,
        TimeProvider reloj, ILogger<WsfeClienteResiliente> log, IReadOnlyList<TimeSpan>? backoff = null)
    {
        _inner = inner; _cache = cache; _breaker = breaker; _opt = opt.Value; _reloj = reloj; _log = log;
        _backoff = backoff ?? _opt.ReintentosLecturaMs.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray();
        _bulkhead = new SemaphoreSlim(_opt.MaxLlamadasConcurrentesArca, _opt.MaxLlamadasConcurrentesArca);
    }

    public void Dispose() => _bulkhead.Dispose();

    /// <summary>Llamadas a ARCA en curso (diagnóstico).</summary>
    public int LlamadasEnCurso => _opt.MaxLlamadasConcurrentesArca - _bulkhead.CurrentCount;

    public CircuitBreaker Breaker => _breaker;

    private async Task<T> EjecutarAsync<T>(string metodo, bool lectura, Func<CancellationToken, Task<T>> f, CancellationToken ct)
    {
        var intentos = lectura ? _backoff.Count + 1 : 1;
        for (var i = 0; ; i++)
        {
            _breaker.Verificar();
            using var act = ArcaTelemetria.Actividades.StartActivity("ARCA " + metodo, ActivityKind.Client);
            act?.SetTag("arca.metodo", metodo);
            act?.SetTag("arca.intento", i + 1);
            var sw = Stopwatch.StartNew();
            var resultado = "ok";
            var limite = TimeSpan.FromSeconds(lectura ? _opt.TimeoutLecturaSegundos : _opt.TimeoutEmisionSegundos);
            await _bulkhead.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(limite);
                T r;
                try { r = await f(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ArcaComunicacionException($"Timeout de {limite.TotalSeconds:N0} s en {metodo}.");
                }
                _breaker.RegistrarExito();
                return r;
            }
            catch (ArcaException ex) when (ex is ArcaComunicacionException or SoapFaultException)
            {
                resultado = "falla_comunicacion";
                _breaker.RegistrarFalla();
                act?.SetStatus(ActivityStatusCode.Error, ex.Message);
                if (i + 1 >= intentos) throw;
                _log.LogWarning("Falla transitoria en {Metodo} (intento {Intento}); se reintenta", metodo, i + 1);
                await Task.Delay(_backoff[i], _reloj, ct).ConfigureAwait(false);
            }
            catch (ArcaException)
            {
                resultado = "error_negocio";
                _breaker.RegistrarExito(); // ARCA respondió: el canal está sano
                throw;
            }
            finally
            {
                _bulkhead.Release();
                sw.Stop();
                ArcaTelemetria.LlamadasArca.Add(1, new("metodo", metodo), new("resultado", resultado));
                ArcaTelemetria.DuracionArca.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("metodo", metodo));
            }
        }
    }

    private Task<T> Lectura<T>(string m, Func<CancellationToken, Task<T>> f, CancellationToken ct) => EjecutarAsync(m, true, f, ct);
    private Task<T> Escritura<T>(string m, Func<CancellationToken, Task<T>> f, CancellationToken ct) => EjecutarAsync(m, false, f, ct);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<object?>>> _enVuelo = new();

    /// <summary>La llamada compartida se ejecuta con <see cref="CancellationToken.None"/> (tiene su propio timeout en
    /// <see cref="EjecutarAsync"/>): la cancelación de UN llamador no aborta a los demás; cada uno espera con su token.</summary>
    private async Task<T> CacheadoAsync<T>(string clave, TimeSpan ttl, Func<CancellationToken, Task<T>> f, CancellationToken ct)
    {
        if (ttl > TimeSpan.Zero && _cache.TryGetValue(clave, out T? v) && v != null)
        {
            ArcaTelemetria.AciertosCache.Add(1);
            return v;
        }
        // Single-flight: solicitudes concurrentes de la misma tabla comparten UNA llamada a ARCA.
        var lazy = _enVuelo.GetOrAdd(clave, k => new Lazy<Task<object?>>(async () =>
        {
            try
            {
                var r = await f(CancellationToken.None).ConfigureAwait(false);
                if (ttl > TimeSpan.Zero) _cache.Set(clave, r, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = 1 });
                return r;
            }
            finally { _enVuelo.TryRemove(clave, out _); }
        }));
        return (T)(await lazy.Value.WaitAsync(ct).ConfigureAwait(false))!;
    }

    private TimeSpan TtlParam => TimeSpan.FromMinutes(_opt.CacheParametrosMinutos);
    private TimeSpan TtlCotiz => TimeSpan.FromMinutes(_opt.CacheCotizacionMinutos);

    private Task<IReadOnlyList<ParametroItem>> Param(string m, Func<CancellationToken, Task<IReadOnlyList<ParametroItem>>> f, CancellationToken ct, string extra = "")
        => CacheadoAsync($"param:{m}:{extra}", TtlParam, c => Lectura(m, f, c), ct);

    /// <summary>Vacía la caché de parámetros (p.ej. tras habilitar un punto de venta nuevo).</summary>
    public void LimpiarCache()
    {
        if (_cache is MemoryCache mc) mc.Compact(1.0);
    }

    public Task<EstadoServidores> FEDummyAsync(CancellationToken ct) => Lectura("FEDummy", _inner.FEDummyAsync, ct);
    public Task<RespuestaCae> FECAESolicitarAsync(SolicitudCae s, CancellationToken ct) => Escritura("FECAESolicitar", c => _inner.FECAESolicitarAsync(s, c), ct);
    public Task<UltimoComprobante> FECompUltimoAutorizadoAsync(int pv, int t, CancellationToken ct) => Lectura("FECompUltimoAutorizado", c => _inner.FECompUltimoAutorizadoAsync(pv, t, c), ct);
    public async Task<ComprobanteConsultado?> FECompConsultarAsync(int pv, int t, long n, CancellationToken ct)
    {
        // Un comprobante autorizado es inmutable: se cachea. Los inexistentes NO (pueden aparecer luego).
        var clave = $"cbte:{pv}:{t}:{n}";
        if (_opt.CacheComprobantesHoras > 0 && _cache.TryGetValue(clave, out ComprobanteConsultado? c0) && c0 != null)
        {
            ArcaTelemetria.AciertosCache.Add(1);
            return c0;
        }
        var r = await Lectura("FECompConsultar", c => _inner.FECompConsultarAsync(pv, t, n, c), ct).ConfigureAwait(false);
        if (r is { Resultado: "A" } && _opt.CacheComprobantesHoras > 0)
            _cache.Set(clave, r, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(_opt.CacheComprobantesHoras), Size = 1 });
        return r;
    }
    public Task<int> FECompTotXRequestAsync(CancellationToken ct) => CacheadoAsync("param:totx", TtlParam, c => Lectura("FECompTotXRequest", _inner.FECompTotXRequestAsync, c), ct);
    public Task<Caea> FECAEASolicitarAsync(int p, short o, CancellationToken ct) => Escritura("FECAEASolicitar", c => _inner.FECAEASolicitarAsync(p, o, c), ct);
    public Task<Caea?> FECAEAConsultarAsync(int p, short o, CancellationToken ct) => Lectura("FECAEAConsultar", c => _inner.FECAEAConsultarAsync(p, o, c), ct);
    public Task<RespuestaCae> FECAEARegInformativoAsync(InformeCaea i, CancellationToken ct) => Escritura("FECAEARegInformativo", c => _inner.FECAEARegInformativoAsync(i, c), ct);
    public Task<CaeaSinMovimiento> FECAEASinMovimientoInformarAsync(int pv, string caea, CancellationToken ct) => Escritura("FECAEASinMovimientoInformar", c => _inner.FECAEASinMovimientoInformarAsync(pv, caea, c), ct);
    public Task<IReadOnlyList<CaeaSinMovimiento>> FECAEASinMovimientoConsultarAsync(string caea, int pv, CancellationToken ct) => Lectura("FECAEASinMovimientoConsultar", c => _inner.FECAEASinMovimientoConsultarAsync(caea, pv, c), ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposCbteAsync(CancellationToken ct) => Param("FEParamGetTiposCbte", _inner.FEParamGetTiposCbteAsync, ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposConceptoAsync(CancellationToken ct) => Param("FEParamGetTiposConcepto", _inner.FEParamGetTiposConceptoAsync, ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposDocAsync(CancellationToken ct) => Param("FEParamGetTiposDoc", _inner.FEParamGetTiposDocAsync, ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposIvaAsync(CancellationToken ct) => Param("FEParamGetTiposIva", _inner.FEParamGetTiposIvaAsync, ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposMonedasAsync(CancellationToken ct) => Param("FEParamGetTiposMonedas", _inner.FEParamGetTiposMonedasAsync, ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposOpcionalAsync(CancellationToken ct) => Param("FEParamGetTiposOpcional", _inner.FEParamGetTiposOpcionalAsync, ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposTributosAsync(CancellationToken ct) => Param("FEParamGetTiposTributos", _inner.FEParamGetTiposTributosAsync, ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposPaisesAsync(CancellationToken ct) => Param("FEParamGetTiposPaises", _inner.FEParamGetTiposPaisesAsync, ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetPtosVentaAsync(CancellationToken ct) => Param("FEParamGetPtosVenta", _inner.FEParamGetPtosVentaAsync, ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetActividadesAsync(CancellationToken ct) => Param("FEParamGetActividades", _inner.FEParamGetActividadesAsync, ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetCondicionIvaReceptorAsync(string? clase, CancellationToken ct)
        => Param("FEParamGetCondicionIvaReceptor", c => _inner.FEParamGetCondicionIvaReceptorAsync(clase, c), ct, clase ?? "");
    public Task<Cotizacion> FEParamGetCotizacionAsync(string monId, string? fch, CancellationToken ct)
        => CacheadoAsync($"cotiz:{monId}:{fch}", TtlCotiz, c0 => Lectura("FEParamGetCotizacion", c => _inner.FEParamGetCotizacionAsync(monId, fch, c), c0), ct);
}
