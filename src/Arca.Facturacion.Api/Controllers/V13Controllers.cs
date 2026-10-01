using System.ComponentModel.DataAnnotations;
using System.Text;
using Arca.Facturacion.Api.Infraestructura;
using Arca.Facturacion.Core;
using Arca.Facturacion.Core.Configuracion;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Arca.Facturacion.Api.Controllers;

/// <summary>Resultado de la validación previa (sin emitir).</summary>
public sealed record ResultadoValidacion(bool Valido, IReadOnlyList<string> Errores, IReadOnlyList<string> Advertencias, ComprobanteDetalle Normalizado);

public sealed class ConsultaEstados
{
    [Required, MinLength(1), MaxLength(200)] public List<string> Claves { get; set; } = new();
}

/// <summary>Validación previa, estados por lote, impresión y próximo número.</summary>
[ApiController]
[Route("api/v1/comprobantes")]
[Produces("application/json")]
[RequierePermiso(Permisos.Consultar)]
[Tags("Comprobantes")]
public sealed class ComprobantesExtraController : ControllerBase
{
    private readonly ComprobanteValidator _val;
    private readonly IFacturacionService _svc;
    private readonly IWsfeClient _wsfe;
    private readonly ArcaOptions _opt;
    public ComprobantesExtraController(ComprobanteValidator val, IFacturacionService svc, IWsfeClient wsfe, IOptions<ArcaOptions> opt)
    { _val = val; _svc = svc; _wsfe = wsfe; _opt = opt.Value; }
    private string Cliente => HttpContext.Items[ApiKeyMiddleware.ItemCliente] as string ?? "anon";

    /// <summary>Un cliente sólo ve la representación de sus propios comprobantes (admin ve todos).</summary>
    private bool PuedeVer(int pv, int tipo, long nro) =>
        HttpContext.Items[ApiKeyMiddleware.ItemEsAdmin] is true
        || HttpContext.RequestServices.GetRequiredService<ConsultasService>().EsDelCliente(pv, tipo, nro, Cliente);

    /// <summary>Valida un comprobante SIN emitirlo: errores (impiden emitir) y advertencias (conviene revisar).</summary>
    [HttpPost("validar")]
    public ResultadoValidacion Validar([FromBody] EmitirComprobanteRequest req, [FromServices] AgendaReceptores agenda)
    {
        var d = FacturacionService.Normalizar(FacturacionService.Clonar(req.Comprobante));
        if (!string.IsNullOrWhiteSpace(req.ReceptorId) && agenda.Obtener(Cliente, req.ReceptorId) is { } r) AgendaReceptores.Aplicar(r, d);
        d.CbteFch ??= _val.HoyArgentina().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var errores = _val.Validar(req.PtoVta, req.CbteTipo, d);
        return new ResultadoValidacion(errores.Count == 0, errores, _val.Advertencias(req.CbteTipo, d), d);
    }

    /// <summary>Estado de varias emisiones por sus Idempotency-Key (máx. 200).</summary>
    [HttpPost("emisiones/estado")]
    public IReadOnlyDictionary<string, object?> Estados([FromBody] ConsultaEstados req) =>
        req.Claves.Distinct().ToDictionary(k => k, k => _svc.ObtenerEmision(k, Cliente) is { } r
            ? new { estado = r.Estado.ToString(), r.PtoVta, r.CbteTipo, r.CbteNro, cae = r.Resultado?.Cae } : (object?)null);

    /// <summary>Próximo número que asignaría ARCA (informativo; la emisión numera bajo bloqueo).</summary>
    [HttpGet("proximo")]
    public async Task<object> Proximo([FromQuery, Range(1, 99998)] int ptoVta, [FromQuery, Range(1, 999)] int cbteTipo,
        [FromServices] ReportesService rep, CancellationToken ct)
        => new { ptoVta, cbteTipo, proximo = await rep.ProximoNumeroAsync(ptoVta, cbteTipo, ct) };

    /// <summary>QR fiscal en PNG de un comprobante autorizado.</summary>
    [HttpGet("{ptoVta:int}/{cbteTipo:int}/{cbteNro:long}/qr.png")]
    [Produces("image/png")]
    public async Task<IActionResult> QrPng(int ptoVta, int cbteTipo, long cbteNro, CancellationToken ct)
    {
        if (!PuedeVer(ptoVta, cbteTipo, cbteNro)) return NotFound();
        var c = await _wsfe.FECompConsultarAsync(ptoVta, cbteTipo, cbteNro, ct);
        var url = c == null ? null : QrFiscal.GenerarUrl(_opt.CuitNumerico, c);
        return url == null ? NotFound() : File(Impresion.QrPng(url), "image/png");
    }

    /// <summary>Representación imprimible (HTML) con datos del emisor, receptor, importes, importe en letras, QR y CAE.
    /// copia: ORIGINAL, DUPLICADO o TRIPLICADO.</summary>
    [HttpGet("{ptoVta:int}/{cbteTipo:int}/{cbteNro:long}/html")]
    [Produces("text/html")]
    public async Task<IActionResult> Html(int ptoVta, int cbteTipo, long cbteNro,
        [FromQuery, RegularExpression("^(ORIGINAL|DUPLICADO|TRIPLICADO)$")] string? copia, CancellationToken ct)
    {
        if (!PuedeVer(ptoVta, cbteTipo, cbteNro)) return NotFound();
        var c = await _wsfe.FECompConsultarAsync(ptoVta, cbteTipo, cbteNro, ct);
        if (c == null) return NotFound();
        var html = Impresion.Html(c, _opt.Emisor, _opt.CuitNumerico, QrFiscal.GenerarUrl(_opt.CuitNumerico, c), copia ?? "ORIGINAL");
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src data:; style-src 'unsafe-inline'";
        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>Importe en letras.</summary>
    [HttpGet("importe-en-letras")]
    public object ImporteEnLetras([FromQuery] decimal importe, [FromQuery, RegularExpression("^[A-Z0-9]{3}$")] string? monId)
        => new { texto = NumeroALetras.Importe(importe, monId ?? "PES") };
}

/// <summary>Agenda de receptores frecuentes.</summary>
[ApiController]
[Route("api/v1/receptores")]
[Produces("application/json")]
[RequierePermiso(Permisos.Emitir)]
[Tags("Receptores")]
public sealed class ReceptoresController : ControllerBase
{
    private readonly AgendaReceptores _a;
    public ReceptoresController(AgendaReceptores a) => _a = a;
    private string Cliente => HttpContext.Items[ApiKeyMiddleware.ItemCliente] as string ?? "anon";

    /// <summary>Busca por nombre, documento o id (texto opcional).</summary>
    [HttpGet] public IReadOnlyList<Receptor> Buscar([FromQuery, StringLength(100)] string? texto) => _a.Buscar(Cliente, texto);

    [HttpGet("{id}")] public ActionResult<Receptor> Obtener(string id) => _a.Obtener(Cliente, id) is { } r ? r : NotFound();

    /// <summary>Crea o reemplaza un receptor (valida CUIT y condición frente al IVA).</summary>
    [HttpPut("{id}")]
    public Receptor Guardar(string id, [FromBody] Receptor r)
    {
        r.Id = id;
        return _a.Guardar(Cliente, r);
    }

    [HttpDelete("{id}")] public IActionResult Eliminar(string id) => _a.Eliminar(Cliente, id) ? NoContent() : NotFound();
}

/// <summary>Facturación recurrente.</summary>
[ApiController]
[Route("api/v1/suscripciones")]
[Produces("application/json")]
[RequierePermiso(Permisos.Emitir)]
[Tags("Suscripciones")]
public sealed class SuscripcionesController : ControllerBase
{
    private readonly SuscripcionesService _s;
    public SuscripcionesController(SuscripcionesService s) => _s = s;
    private string Cliente => HttpContext.Items[ApiKeyMiddleware.ItemCliente] as string ?? "anon";

    [HttpGet] public IReadOnlyList<Suscripcion> Listar() => _s.Listar(Cliente);
    [HttpGet("{id}")] public ActionResult<Suscripcion> Obtener(string id) => _s.Obtener(Cliente, id) is { } x ? x : NotFound();

    /// <summary>Crea o reemplaza una suscripción. Se valida el comprobante con un período de ejemplo.</summary>
    [HttpPut("{id}")]
    public Suscripcion Guardar(string id, [FromBody] Suscripcion s)
    {
        s.Id = id;
        return _s.Guardar(Cliente, s);
    }

    [HttpDelete("{id}")] public IActionResult Eliminar(string id) => _s.Eliminar(Cliente, id) ? NoContent() : NotFound();

    /// <summary>Libera una suscripción retenida por períodos atrasados.</summary>
    [HttpPost("{id}/confirmar-atrasos")]
    public ActionResult<Suscripcion> ConfirmarAtrasos(string id) => _s.ConfirmarAtrasos(Cliente, id) is { } x ? x : NotFound();

    /// <summary>Pausa la suscripción.</summary>
    [HttpPost("{id}/pausar")]
    public ActionResult<Suscripcion> Pausar(string id) => _s.CambiarEstado(Cliente, id, false) is { } x ? x : NotFound();

    /// <summary>Reanuda la suscripción.</summary>
    [HttpPost("{id}/reanudar")]
    public ActionResult<Suscripcion> Reanudar(string id) => _s.CambiarEstado(Cliente, id, true) is { } x ? x : NotFound();
}

/// <summary>Operación avanzada (permiso admin).</summary>
[ApiController]
[Route("api/v1/admin")]
[Produces("application/json")]
[RequierePermiso(Permisos.Admin)]
[Tags("Administración")]
public sealed class OperacionAvanzadaController : ControllerBase
{
    private readonly ReportesService _r;
    public OperacionAvanzadaController(ReportesService r) => _r = r;

    /// <summary>Ventas por receptor (top N, en pesos; NC restan).</summary>
    [HttpGet("reportes/por-receptor")]
    public IReadOnlyList<VentaPorReceptor> PorReceptor([FromQuery, RegularExpression(@"^\d{8}$")] string? desde,
        [FromQuery, RegularExpression(@"^\d{8}$")] string? hasta, [FromQuery, Range(1, 1000)] int top = 20) => _r.PorReceptor(desde, hasta, top);

    /// <summary>Serie diaria de ventas.</summary>
    [HttpGet("reportes/por-dia")]
    public IReadOnlyList<VentaPorDia> PorDia([FromQuery, RegularExpression(@"^\d{8}$")] string? desde, [FromQuery, RegularExpression(@"^\d{8}$")] string? hasta)
        => _r.PorDia(desde, hasta);

    /// <summary>Ventas por punto de venta.</summary>
    [HttpGet("reportes/por-punto-venta")]
    public IReadOnlyList<VentaPorPuntoVenta> PorPuntoVenta([FromQuery, RegularExpression(@"^\d{8}$")] string? desde, [FromQuery, RegularExpression(@"^\d{8}$")] string? hasta)
        => _r.PorPuntoVenta(desde, hasta);

    /// <summary>Cierre diario: emitidos y totales por punto de venta y tipo, y control del último número contra ARCA.</summary>
    [HttpGet("cierre-diario")]
    public Task<CierreDiario> Cierre([FromQuery, RegularExpression(@"^\d{8}$")] string? fecha, CancellationToken ct) => _r.CierreAsync(fecha, ct);

    /// <summary>Puntos de venta con estado legible (habilitado / bloqueado / dado de baja).</summary>
    [HttpGet("puntos-venta")]
    public Task<IReadOnlyList<object>> PuntosVenta(CancellationToken ct) => _r.PuntosVentaAsync(ct);

    /// <summary>Rechazos recientes de ARCA y códigos más frecuentes.</summary>
    [HttpGet("rechazos")]
    public object Rechazos([FromServices] RegistroRechazos rech, [FromQuery, Range(1, 500)] int maximo = 50)
        => new { ultimos = rech.Ultimos(maximo), frecuentes = rech.Frecuentes().Select(f => new { codigo = f.Codigo, mensaje = f.Mensaje, cantidad = f.Cantidad }) };

    /// <summary>Respaldo del registro de emisiones (JSON Lines).</summary>
    [HttpGet("registro/exportar")]
    [Produces("application/x-ndjson")]
    public IActionResult Exportar() => File(Encoding.UTF8.GetBytes(_r.Exportar()), "application/x-ndjson", "registro-emisiones.jsonl");

    /// <summary>Restaura un respaldo JSONL: sólo agrega claves inexistentes.</summary>
    [HttpPost("registro/importar")]
    [Consumes("application/x-ndjson", "text/plain")]
    public async Task<object> Importar()
    {
        using var sr = new StreamReader(Request.Body, Encoding.UTF8);
        var (i, o) = _r.Importar(await sr.ReadToEndAsync());
        return new { importados = i, omitidos = o };
    }

    /// <summary>Resuelve manualmente una emisión pendiente (por clave interna "cliente:idempotencyKey").</summary>
    [HttpPost("emisiones/resolver")]
    public async Task<object> Resolver([FromQuery, Required] string clave, [FromServices] IFacturacionService svc, CancellationToken ct)
        => new { clave, resultado = await svc.ResolverPendienteAsync(clave, ct) };

    /// <summary>Reenvía el webhook de una emisión registrada.</summary>
    [HttpPost("webhook/reenviar")]
    public IActionResult Reenviar([FromQuery, Required] string clave, [FromServices] IRegistroEmisiones reg, [FromServices] WebhookNotificador wh)
        => reg.Obtener(clave)?.Resultado is { } r && wh.Reenviar(r) ? Accepted() : NotFound();

    /// <summary>Envía un evento de prueba a las URLs de webhook configuradas.</summary>
    [HttpPost("webhook/prueba")]
    public IActionResult ProbarWebhook([FromServices] WebhookNotificador wh) => wh.Probar() ? Accepted() : Problem(statusCode: 409, title: "No hay webhooks configurados");

    /// <summary>Últimas entregas de webhook.</summary>
    [HttpGet("webhook/entregas")]
    public IReadOnlyList<EntregaWebhook> Entregas([FromServices] WebhookNotificador wh, [FromQuery, Range(1, 500)] int maximo = 50) => wh.Entregas(maximo);

    /// <summary>Activa el modo mantenimiento (las emisiones responden 503; las consultas siguen).</summary>
    [HttpPost("mantenimiento")]
    public object ActivarMantenimiento([FromServices] EstadoMantenimiento m, [FromQuery, StringLength(200)] string? motivo)
    {
        m.Activar(motivo ?? "");
        return new { activo = true, m.Motivo };
    }

    /// <summary>Desactiva el modo mantenimiento.</summary>
    [HttpDelete("mantenimiento")]
    public object DesactivarMantenimiento([FromServices] EstadoMantenimiento m)
    {
        m.Desactivar();
        return new { activo = false };
    }

    /// <summary>Verifica la integridad de la cadena de hashes de la auditoría.</summary>
    [HttpGet("auditoria/verificar")]
    public object VerificarAuditoria([FromServices] IAuditoria aud) =>
        aud is AuditoriaArchivo a ? a.Verificar() : new VerificacionAuditoria(true, 0, "Auditoría no basada en archivo.");

    /// <summary>Ejecuta ahora las suscripciones vencidas.</summary>
    [HttpPost("suscripciones/ejecutar")]
    public async Task<object> EjecutarSuscripciones([FromServices] SuscripcionesService s, CancellationToken ct)
        => new { emitidas = await s.EjecutarVencidasAsync(ct) };
}
