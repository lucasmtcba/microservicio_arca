using System.ComponentModel.DataAnnotations;
using Arca.Facturacion.Core;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;
using Arca.Facturacion.Api.Infraestructura;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Arca.Facturacion.Api.Controllers;

/// <summary>Solicitud de emisión con numeración automática.</summary>
public sealed class EmitirComprobanteRequest
{
    /// <summary>Punto de venta habilitado para web services (1-99998).</summary>
    [Range(1, 99998)] public int PtoVta { get; set; }
    /// <summary>Tipo de comprobante (1=Factura A, 6=Factura B, 11=Factura C, 3=NC A, 8=NC B, 13=NC C, ...).</summary>
    [Range(1, 999)] public int CbteTipo { get; set; }
    [Required] public ComprobanteDetalle Comprobante { get; set; } = new();
    /// <summary>Opcional: Id de un receptor de la agenda; completa DocTipo, DocNro y CondicionIVAReceptorId.</summary>
    public string? ReceptorId { get; set; }
}

/// <summary>Emisión y consulta de comprobantes con CAE (WSFEv1).</summary>
[ApiController]
[Route("api/v1/comprobantes")]
[Produces("application/json")]
[RequierePermiso(Permisos.Consultar)]
[Tags("Comprobantes")]
public sealed class ComprobantesController : ControllerBase
{
    private readonly IFacturacionService _svc;
    private readonly IWsfeClient _wsfe;
    private readonly ComprobanteValidator _val;

    public ComprobantesController(IFacturacionService svc, IWsfeClient wsfe, ComprobanteValidator val)
    { _svc = svc; _wsfe = wsfe; _val = val; }

    /// <summary>Emite un comprobante: numera (último autorizado + 1), solicita CAE y reconcilia ante fallas de red.</summary>
    /// <remarks>Enviar siempre <c>Idempotency-Key</c> (UUID) para poder reintentar sin duplicar comprobantes.
    /// Devuelve 200 con Resultado "A" (CAE otorgado) o "R" (rechazado, ver Errores/Observaciones).</remarks>
    /// <param name="req">Datos del comprobante.</param>
    /// <param name="idempotencyKey">Clave única por intento lógico de emisión (máx. 100 caracteres).</param>
    [HttpPost]
    [RequierePermiso(Permisos.Emitir)]
    [EnableRateLimiting("emision")]
    [ConsumeCuota]
    [ProducesResponseType(typeof(ResultadoEmision), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 503)]
    public async Task<ActionResult<ResultadoEmision>> Emitir([FromBody] EmitirComprobanteRequest req,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(100, MinimumLength = 1)] string idempotencyKey,
        [FromServices] AgendaReceptores agenda, CancellationToken ct)
    {
        var cliente = HttpContext.Items[Infraestructura.ApiKeyMiddleware.ItemCliente] as string ?? "anon";
        if (!string.IsNullOrWhiteSpace(req.ReceptorId))
        {
            var rec = agenda.Obtener(cliente, req.ReceptorId) ?? throw new ValidacionException(new[] { $"El receptor '{req.ReceptorId}' no existe en la agenda." });
            AgendaReceptores.Aplicar(rec, req.Comprobante);
        }
        HttpContext.Items["arca.importe"] = req.Comprobante.ImpTotal;
        var r = await _svc.EmitirAsync(req.PtoVta, req.CbteTipo, req.Comprobante, idempotencyKey, cliente, ct);
        return Resultado(this, r);
    }

    /// <summary>Agrega Location (si hay CAE) e Idempotency-Replayed (si es un reintento) a la respuesta.</summary>
    internal static ActionResult<ResultadoEmision> Resultado(ControllerBase c, ResultadoEmision r)
    {
        if (r.Repetido) c.Response.Headers["Idempotency-Replayed"] = "true";
        if (r.Aprobado) c.Response.Headers.Location = $"/api/v1/comprobantes/{r.PtoVta}/{r.CbteTipo}/{r.CbteNro}";
        return r;
    }

    /// <summary>FECAESolicitar directo (lote con numeración provista por el llamador). Uso avanzado.</summary>
    /// <remarks>Deshabilitado por defecto (Arca:PermitirSolicitudDirecta). NO tiene idempotencia, lock de numeración
    /// ni reconciliación: ante un timeout el llamador debe consultar con FECompConsultar antes de reintentar.</remarks>
    [HttpPost("cae")]
    [RequierePermiso(Permisos.Emitir)]
    [ProducesResponseType(typeof(RespuestaCae), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    public async Task<ActionResult<RespuestaCae>> SolicitarCae([FromBody] SolicitudCae req,
        [FromServices] Microsoft.Extensions.Options.IOptions<Arca.Facturacion.Core.Configuracion.ArcaOptions> opt, CancellationToken ct)
    {
        if (!opt.Value.PermitirSolicitudDirecta)
            return Problem(statusCode: 403, title: "Operación deshabilitada", detail: "Habilitar Arca:PermitirSolicitudDirecta para usar este endpoint.");
        ValidarLote(req.PtoVta, req.CbteTipo, req.Comprobantes);
        return await _wsfe.FECAESolicitarAsync(req, ct);
    }

    /// <summary>FECompUltimoAutorizado: último número autorizado para punto de venta y tipo.</summary>
    [HttpGet("ultimo")]
    public Task<UltimoComprobante> Ultimo([FromQuery, Range(1, 99998)] int ptoVta, [FromQuery, Range(1, 999)] int cbteTipo, CancellationToken ct)
        => _wsfe.FECompUltimoAutorizadoAsync(ptoVta, cbteTipo, ct);

    /// <summary>FECompConsultar: datos de un comprobante emitido. 404 si no existe.</summary>
    [HttpGet("{ptoVta:int}/{cbteTipo:int}/{cbteNro:long}")]
    [ComprobanteDelCliente]
    [ProducesResponseType(typeof(ComprobanteConsultado), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ComprobanteConsultado>> Consultar(int ptoVta, int cbteTipo, long cbteNro, CancellationToken ct)
        => await _wsfe.FECompConsultarAsync(ptoVta, cbteTipo, cbteNro, ct) is { } c ? c : NotFound();

    /// <summary>FECompTotXRequest: cantidad máxima de registros por lote.</summary>
    [HttpGet("max-registros")]
    public async Task<object> MaxRegistros(CancellationToken ct) => new { RegXReq = await _wsfe.FECompTotXRequestAsync(ct) };

    internal void ValidarLote<T>(int ptoVta, int cbteTipo, List<T> cbtes) where T : ComprobanteNumerado
    {
        var errores = new List<string>();
        if (cbtes.Count == 0) errores.Add("Debe informarse al menos un comprobante.");
        else if (cbtes.Count > 250) errores.Add("Lote demasiado grande.");
        for (var i = 0; i < cbtes.Count; i++)
        {
            var c = cbtes[i];
            if (c.CbteDesde < 1 || c.CbteHasta < c.CbteDesde) errores.Add($"[{i}] Numeración inválida.");
            if (c.CbteHasta != c.CbteDesde && !TiposComprobante.ClaseB.Contains(cbteTipo))
                errores.Add($"[{i}] Rangos de numeración sólo se admiten en comprobantes clase B.");
            errores.AddRange(_val.Validar(ptoVta, cbteTipo, c).Select(e => $"[{i}] {e}"));
        }
        if (errores.Count > 0) throw new ValidacionException(errores);
    }
}

/// <summary>Código de Autorización Electrónico Anticipado (CAEA).</summary>
[ApiController]
[Route("api/v1/caea")]
[Produces("application/json")]
[RequierePermiso(Permisos.Consultar)]
[Tags("CAEA")]
public sealed class CaeaController : ControllerBase
{
    private readonly IWsfeClient _wsfe;
    private readonly ComprobanteValidator _val;
    public CaeaController(IWsfeClient wsfe, ComprobanteValidator val) { _wsfe = wsfe; _val = val; }

    /// <summary>FECAEASolicitar. Periodo yyyyMM, orden 1 (días 1-15) o 2 (16-fin de mes).</summary>
    [HttpPost("{periodo:int}/{orden:int}")]
    [RequierePermiso(Permisos.Emitir)]
    public Task<Caea> Solicitar([Range(200001, 299912)] int periodo, [Range(1, 2)] short orden, CancellationToken ct)
        => _wsfe.FECAEASolicitarAsync(periodo, orden, ct);

    /// <summary>FECAEAConsultar. 404 si no existe.</summary>
    [HttpGet("{periodo:int}/{orden:int}")]
    public async Task<ActionResult<Caea>> Consultar([Range(200001, 299912)] int periodo, [Range(1, 2)] short orden, CancellationToken ct)
        => await _wsfe.FECAEAConsultarAsync(periodo, orden, ct) is { } c ? c : NotFound();

    /// <summary>FECAEARegInformativo: informa comprobantes emitidos con CAEA.</summary>
    [HttpPost("comprobantes")]
    [RequierePermiso(Permisos.Emitir)]
    public async Task<RespuestaCae> Informar([FromBody] InformeCaea req, CancellationToken ct)
    {
        var errores = new List<string>();
        if (req.Comprobantes.Count == 0) errores.Add("Debe informarse al menos un comprobante.");
        else if (req.Comprobantes.Count > 250) errores.Add("Lote demasiado grande (máx. 250).");
        for (var i = 0; i < req.Comprobantes.Count; i++)
        {
            var c = req.Comprobantes[i];
            if (string.IsNullOrWhiteSpace(c.Caea) || c.Caea.Length != 14 || !c.Caea.All(char.IsAsciiDigit))
                errores.Add($"[{i}] CAEA debe tener 14 dígitos.");
            if (c.CbteDesde < 1 || c.CbteHasta < c.CbteDesde) errores.Add($"[{i}] Numeración inválida.");
            // Los comprobantes CAEA se informan con posterioridad: no aplica la ventana de ±5/10 días de CbteFch.
            errores.AddRange(_val.Validar(req.PtoVta, req.CbteTipo, c, validarVentanaFecha: false).Select(e => $"[{i}] {e}"));
        }
        if (errores.Count > 0) throw new ValidacionException(errores);
        return await _wsfe.FECAEARegInformativoAsync(req, ct);
    }

    /// <summary>FECAEASinMovimientoInformar: informa que un punto de venta no tuvo movimientos con un CAEA.</summary>
    [HttpPost("{caea}/sin-movimiento/{ptoVta:int}")]
    [RequierePermiso(Permisos.Emitir)]
    public Task<CaeaSinMovimiento> InformarSinMovimiento([RegularExpression(@"^\d{14}$")] string caea, [Range(1, 99998)] int ptoVta, CancellationToken ct)
        => _wsfe.FECAEASinMovimientoInformarAsync(ptoVta, caea, ct);

    /// <summary>FECAEASinMovimientoConsultar.</summary>
    [HttpGet("{caea}/sin-movimiento/{ptoVta:int}")]
    public Task<IReadOnlyList<CaeaSinMovimiento>> ConsultarSinMovimiento([RegularExpression(@"^\d{14}$")] string caea, [Range(1, 99998)] int ptoVta, CancellationToken ct)
        => _wsfe.FECAEASinMovimientoConsultarAsync(caea, ptoVta, ct);
}

/// <summary>Tablas de parámetros de WSFEv1 (FEParamGet*).</summary>
[ApiController]
[Route("api/v1/parametros")]
[Produces("application/json")]
[RequierePermiso(Permisos.Consultar)]
[Etag]
[Tags("Parámetros de ARCA")]
public sealed class ParametrosController : ControllerBase
{
    private readonly IWsfeClient _w;
    public ParametrosController(IWsfeClient w) => _w = w;

    /// <summary>FEParamGetTiposCbte</summary>
    [HttpGet("tipos-comprobante")] public Task<IReadOnlyList<ParametroItem>> TiposCbte(CancellationToken ct) => _w.FEParamGetTiposCbteAsync(ct);
    /// <summary>FEParamGetTiposConcepto</summary>
    [HttpGet("tipos-concepto")] public Task<IReadOnlyList<ParametroItem>> TiposConcepto(CancellationToken ct) => _w.FEParamGetTiposConceptoAsync(ct);
    /// <summary>FEParamGetTiposDoc</summary>
    [HttpGet("tipos-documento")] public Task<IReadOnlyList<ParametroItem>> TiposDoc(CancellationToken ct) => _w.FEParamGetTiposDocAsync(ct);
    /// <summary>FEParamGetTiposIva</summary>
    [HttpGet("tipos-iva")] public Task<IReadOnlyList<ParametroItem>> TiposIva(CancellationToken ct) => _w.FEParamGetTiposIvaAsync(ct);
    /// <summary>FEParamGetTiposMonedas</summary>
    [HttpGet("monedas")] public Task<IReadOnlyList<ParametroItem>> Monedas(CancellationToken ct) => _w.FEParamGetTiposMonedasAsync(ct);
    /// <summary>FEParamGetTiposOpcional</summary>
    [HttpGet("tipos-opcional")] public Task<IReadOnlyList<ParametroItem>> Opcionales(CancellationToken ct) => _w.FEParamGetTiposOpcionalAsync(ct);
    /// <summary>FEParamGetTiposTributos</summary>
    [HttpGet("tipos-tributo")] public Task<IReadOnlyList<ParametroItem>> Tributos(CancellationToken ct) => _w.FEParamGetTiposTributosAsync(ct);
    /// <summary>FEParamGetTiposPaises</summary>
    [HttpGet("paises")] public Task<IReadOnlyList<ParametroItem>> Paises(CancellationToken ct) => _w.FEParamGetTiposPaisesAsync(ct);
    /// <summary>FEParamGetPtosVenta (puntos de venta habilitados para web services).</summary>
    [HttpGet("puntos-venta")] public Task<IReadOnlyList<ParametroItem>> PtosVenta(CancellationToken ct) => _w.FEParamGetPtosVentaAsync(ct);
    /// <summary>FEParamGetActividades (actividades del emisor).</summary>
    [HttpGet("actividades")] public Task<IReadOnlyList<ParametroItem>> Actividades(CancellationToken ct) => _w.FEParamGetActividadesAsync(ct);
    /// <summary>FEParamGetCondicionIvaReceptor. claseCmp opcional: A, B, C o M.</summary>
    [HttpGet("condiciones-iva-receptor")]
    public Task<IReadOnlyList<ParametroItem>> CondicionIva([FromQuery, RegularExpression("^(A|B|C|M)$")] string? claseCmp, CancellationToken ct)
        => _w.FEParamGetCondicionIvaReceptorAsync(claseCmp, ct);
    /// <summary>FEParamGetCotizacion. fecha opcional yyyyMMdd.</summary>
    [HttpGet("cotizacion/{monId}")]
    public Task<Cotizacion> Cotizacion([RegularExpression("^[A-Z0-9]{3}$")] string monId,
        [FromQuery, RegularExpression(@"^\d{8}$")] string? fecha, CancellationToken ct)
        => _w.FEParamGetCotizacionAsync(monId, fecha, ct);
}

/// <summary>Estado de los servidores de ARCA.</summary>
[ApiController]
[Route("api/v1/salud")]
[RequierePermiso(Permisos.Consultar)]
[Tags("Salud")]
public sealed class SaludController : ControllerBase
{
    private readonly IWsfeClient _w;
    public SaludController(IWsfeClient w) => _w = w;

    /// <summary>FEDummy: estado de AppServer, DbServer y AuthServer de WSFEv1. 503 si alguno no está OK.</summary>
    [HttpGet("arca")]
    [ProducesResponseType(typeof(EstadoServidores), 200)]
    [ProducesResponseType(typeof(EstadoServidores), 503)]
    public async Task<IActionResult> Arca(CancellationToken ct)
    {
        var e = await _w.FEDummyAsync(ct);
        return e.TodoOk ? Ok(e) : StatusCode(503, e);
    }
}
