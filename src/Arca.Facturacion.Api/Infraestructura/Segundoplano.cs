using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Arca.Facturacion.Core.Configuracion;
using Arca.Facturacion.Core.Resiliencia;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Wsaa;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Arca.Facturacion.Api.Infraestructura;

/// <summary>Reconcilia emisiones pendientes y purga el registro según la retención.</summary>
public sealed class ReconciliadorHostedService : BackgroundService
{
    private readonly IFacturacionService _svc;
    private readonly ArcaOptions _opt;
    private readonly ILogger<ReconciliadorHostedService> _log;
    private readonly TimeProvider _reloj;

    private readonly IAuditoria? _aud;

    public ReconciliadorHostedService(IFacturacionService svc, IOptions<ArcaOptions> opt, ILogger<ReconciliadorHostedService> log, TimeProvider reloj,
        IAuditoria? aud = null)
    { _svc = svc; _opt = opt.Value; _log = log; _reloj = reloj; _aud = aud; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_opt.ReconciliadorIntervaloSegundos <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_opt.ReconciliadorIntervaloSegundos), _reloj);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var n = await _svc.ReconciliarPendientesAsync(TimeSpan.FromSeconds(_opt.ReconciliadorAntiguedadMinimaSegundos), ct);
                if (n > 0) _log.LogInformation("Reconciliador: {N} emisiones pendientes resueltas", n);
                var p = _svc.Purgar(TimeSpan.FromDays(_opt.RetencionRegistroDias));
                if (p > 0) _log.LogInformation("Retención: {N} registros purgados", p);
                if (_aud is AuditoriaArchivo aa && aa.Purgar(TimeSpan.FromDays(_opt.RetencionAuditoriaDias), _reloj.GetUtcNow()) is var pa and > 0)
                    _log.LogInformation("Retención: {N} archivos de auditoría purgados", pa);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Error en el reconciliador");
            }
        }
    }
}

/// <summary>Precarga el TA al iniciar y lo renueva antes de vencer, para que ninguna emisión espere a WSAA.</summary>
public sealed class RenovadorTicketHostedService : BackgroundService
{
    private readonly ITicketAccesoProvider _tp;
    private readonly ArcaOptions _opt;
    private readonly ILogger<RenovadorTicketHostedService> _log;

    public RenovadorTicketHostedService(ITicketAccesoProvider tp, IOptions<ArcaOptions> opt, ILogger<RenovadorTicketHostedService> log)
    { _tp = tp; _opt = opt.Value; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.RenovacionProactivaTicket || _opt.ModoSimulacion) return;
        while (!ct.IsCancellationRequested)
        {
            try { await _tp.ObtenerAsync(WsfeClient.Servicio, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _log.LogWarning(ex, "No se pudo precargar/renovar el TA; se reintentará"); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { return; }
        }
    }
}

/// <summary>Entrega de un evento de webhook (para diagnóstico).</summary>
public sealed record EntregaWebhook(DateTimeOffset Fecha, string Url, string Evento, string Comprobante, int Intentos, bool Exitosa, string? Detalle);

/// <summary>Webhooks de comprobantes: cola en memoria acotada, POST JSON firmado (HMAC-SHA256) con reintentos,
/// varias URLs, evento de rechazo opcional, registro de entregas y reenvío manual.</summary>
public sealed class WebhookNotificador : BackgroundService, INotificadorEmisiones
{
    public const string NombreHttpClient = "arca-webhook";
    public const string CabeceraFirma = "X-Firma-Arca";
    public const string CabeceraEvento = "X-Evento-Arca";
    private readonly Channel<(ResultadoEmision R, string Evento)> _cola = Channel.CreateBounded<(ResultadoEmision, string)>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly System.Collections.Concurrent.ConcurrentQueue<EntregaWebhook> _entregas = new();
    private readonly IHttpClientFactory _http;
    private readonly ArcaOptions _opt;
    private readonly ILogger<WebhookNotificador> _log;
    private readonly TimeProvider _reloj;
    private readonly IReadOnlyList<string> _urls;

    public WebhookNotificador(IHttpClientFactory http, IOptions<ArcaOptions> opt, ILogger<WebhookNotificador> log, TimeProvider reloj)
    {
        _http = http; _opt = opt.Value; _log = log; _reloj = reloj;
        _urls = UrlsValidas(_opt.WebhookUrls.Append(_opt.WebhookUrl), _log);
    }

    /// <summary>Normaliza y valida las URLs de webhook: sólo absolutas http/https. Las inválidas se registran y descartan
    /// (una URL mal escrita no debe tumbar el servicio en segundo plano ni el host).</summary>
    public static IReadOnlyList<string> UrlsValidas(IEnumerable<string?> urls, ILogger? log = null)
    {
        var res = new List<string>();
        foreach (var u in urls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u!.Trim()).Distinct())
        {
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrEmpty(uri.Host))
                res.Add(uri.AbsoluteUri);
            else
                log?.LogError("URL de webhook inválida (se descarta; debe ser absoluta http/https): {Url}", Sanitizar.Log(u, 200));
        }
        return res.Distinct().ToList();
    }

    public bool Habilitado => _urls.Count > 0;

    public void Notificar(ResultadoEmision r)
    {
        if (!Habilitado) return;
        if (r.Aprobado) _cola.Writer.TryWrite((r, "comprobante.autorizado"));
        else if (_opt.WebhookNotificarRechazos) _cola.Writer.TryWrite((r, "comprobante.rechazado"));
    }

    /// <summary>Reenvía manualmente un evento (p.ej. si el receptor estuvo caído).</summary>
    public bool Reenviar(ResultadoEmision r) => Habilitado && _cola.Writer.TryWrite((r, r.Aprobado ? "comprobante.autorizado" : "comprobante.rechazado"));

    /// <summary>Envía un evento de prueba a todas las URLs.</summary>
    public bool Probar() => Habilitado && _cola.Writer.TryWrite((new ResultadoEmision { Resultado = "A", Cae = "00000000000000", PtoVta = 0, CbteTipo = 0 }, "webhook.prueba"));

    public IReadOnlyList<EntregaWebhook> Entregas(int n) => _entregas.Reverse().Take(Math.Clamp(n, 1, 500)).ToList();

    public static string Firmar(string cuerpo, string secreto) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secreto), Encoding.UTF8.GetBytes(cuerpo))).ToLowerInvariant();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var (r, evento) in _cola.Reader.ReadAllAsync(ct))
        {
            try { await EntregarAsync(r, evento, ct); }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                // Red de seguridad: un evento problemático se descarta y se registra, la cola sigue funcionando.
                _log.LogError(ex, "Error inesperado entregando el webhook {Evento}", evento);
            }
        }
    }

    private async Task EntregarAsync(ResultadoEmision r, string evento, CancellationToken ct)
    {
        var cuerpo = JsonSerializer.Serialize(new { evento, fecha = _reloj.GetUtcNow(), cuit = _opt.Cuit, comprobante = r });
        foreach (var url in _urls)
        {
            var ok = false; string? detalle = null; var intento = 0;
            for (intento = 1; intento <= 3 && !ok; intento++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };
                    req.Headers.Add(CabeceraEvento, evento);
                    if (!string.IsNullOrEmpty(_opt.WebhookSecreto)) req.Headers.Add(CabeceraFirma, Firmar(cuerpo, _opt.WebhookSecreto));
                    using var resp = await _http.CreateClient(NombreHttpClient).SendAsync(req, ct);
                    ok = resp.IsSuccessStatusCode;
                    detalle = ((int)resp.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    // Cualquier falla de una entrega (red, timeout, URL o cabecera inválida, etc.) se registra y se reintenta;
                    // nunca debe propagarse: una excepción no controlada en un BackgroundService detiene el host.
                    detalle = ex.GetType().Name;
                }
                if (!ok && intento < 3) await Task.Delay(TimeSpan.FromSeconds(Math.Pow(3, intento) * _opt.WebhookEsperaFactor), ct);
            }
            if (!ok) _log.LogWarning("Webhook {Url} no aceptó {Evento} tras {N} intentos ({Detalle})", url, evento, intento - 1, detalle);
            _entregas.Enqueue(new EntregaWebhook(_reloj.GetUtcNow(), url, evento, r.Descripcion, intento - 1, ok, detalle));
            while (_entregas.Count > 500 && _entregas.TryDequeue(out _)) { }
        }
    }
}

/// <summary>Degradado si el certificado vence pronto; no saludable si venció.</summary>
public sealed class CertificadoHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _sp;
    private readonly ArcaOptions _opt;
    private readonly TimeProvider _reloj;
    public CertificadoHealthCheck(IServiceProvider sp, IOptions<ArcaOptions> opt, TimeProvider reloj) { _sp = sp; _opt = opt.Value; _reloj = reloj; }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (_opt.ModoSimulacion) return Task.FromResult(HealthCheckResult.Healthy("Modo simulación: certificado no requerido."));
        try
        {
            DateTime venc;
            switch (_sp.GetRequiredService<ICmsFirmante>())
            {
                case FirmanteDiferido d: venc = d.Vencimiento; break;
                case CertificadoCmsFirmante c: venc = c.Vencimiento; break;
                default: return Task.FromResult(HealthCheckResult.Healthy("Firmante no basado en certificado."));
            }
            var dias = (venc.ToUniversalTime() - _reloj.GetUtcNow().UtcDateTime).TotalDays;
            var data = new Dictionary<string, object> { ["diasRestantes"] = Math.Floor(dias) };
            var cuitCert = (_sp.GetRequiredService<ICmsFirmante>() as FirmanteDiferido)?.CuitDelCertificado;
            if (cuitCert != null) data["cuitCertificado"] = cuitCert;
            if (cuitCert != null && cuitCert != _opt.Cuit && dias >= 0)
                return Task.FromResult(HealthCheckResult.Degraded(
                    $"El certificado es de la CUIT {cuitCert} y se factura como {_opt.Cuit}: válido sólo si existe delegación (representación) en ARCA.", data: data));
            return Task.FromResult(dias < 0 ? HealthCheckResult.Unhealthy("Certificado vencido.", data: data)
                : dias < _opt.AlertaVencimientoCertificadoDias ? HealthCheckResult.Degraded($"El certificado vence en {dias:N0} días.", data: data)
                : HealthCheckResult.Healthy("Certificado vigente.", data));
        }
        catch (Exception ex) { return Task.FromResult(HealthCheckResult.Unhealthy("No se pudo cargar el certificado.", ex)); }
    }
}

/// <summary>Verifica que el directorio del registro de emisiones sea escribible.</summary>
public sealed class RegistroHealthCheck : IHealthCheck
{
    private readonly ArcaOptions _opt;
    public RegistroHealthCheck(IOptions<ArcaOptions> opt) => _opt = opt.Value;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.RegistroEmisionesPath))
            return Task.FromResult(HealthCheckResult.Degraded("Registro de emisiones en memoria: no sobrevive reinicios."));
        try
        {
            Directory.CreateDirectory(_opt.RegistroEmisionesPath!);
            var p = Path.Combine(_opt.RegistroEmisionesPath!, ".health-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(p, "ok");
            File.Delete(p);
            return Task.FromResult(HealthCheckResult.Healthy("Registro escribible."));
        }
        catch (Exception ex) { return Task.FromResult(HealthCheckResult.Unhealthy("Registro de emisiones no escribible.", ex)); }
    }
}

/// <summary>Estado del canal con ARCA según el circuit breaker y el desfasaje de reloj.</summary>
public sealed class ArcaHealthCheck : IHealthCheck
{
    private readonly CircuitBreaker _cb;
    private readonly EstadoArca _estado;
    public ArcaHealthCheck(CircuitBreaker cb, EstadoArca estado) { _cb = cb; _estado = estado; }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>
        {
            ["circuito"] = _cb.Estado,
            ["desfasajeRelojSegundos"] = Math.Round(_estado.DesfasajeReloj.TotalSeconds),
            ["ultimoExito"] = _estado.UltimoExito?.ToString("o") ?? "-"
        };
        if (_cb.Estado == "Abierto") return Task.FromResult(HealthCheckResult.Unhealthy("Circuito hacia ARCA abierto.", data: data));
        if (_estado.DesfasajeReloj.Duration() > TimeSpan.FromMinutes(5))
            return Task.FromResult(HealthCheckResult.Degraded("Desfasaje de reloj con ARCA mayor a 5 minutos.", data: data));
        return Task.FromResult(HealthCheckResult.Healthy("Canal con ARCA operativo.", data));
    }
}

/// <summary>Ejecuta periódicamente las suscripciones (facturación recurrente) vencidas.</summary>
public sealed class SuscripcionesHostedService : BackgroundService
{
    private readonly SuscripcionesService _svc;
    private readonly ArcaOptions _opt;
    private readonly ILogger<SuscripcionesHostedService> _log;
    public SuscripcionesHostedService(SuscripcionesService svc, IOptions<ArcaOptions> opt, ILogger<SuscripcionesHostedService> log) { _svc = svc; _opt = opt.Value; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_opt.SuscripcionesIntervaloMinutos <= 0) return;
        using var t = new PeriodicTimer(TimeSpan.FromMinutes(_opt.SuscripcionesIntervaloMinutos));
        do
        {
            try
            {
                var n = await _svc.EjecutarVencidasAsync(ct);
                if (n > 0) _log.LogInformation("Suscripciones: {N} comprobantes emitidos", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "Error ejecutando suscripciones"); }
        } while (await t.WaitForNextTickAsync(ct));
    }
}
