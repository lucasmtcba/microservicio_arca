using System.ComponentModel.DataAnnotations;
using System.Text;
using Arca.Facturacion.Api.Infraestructura;
using Arca.Facturacion.Core;
using Arca.Facturacion.Core.Configuracion;
using Arca.Facturacion.Core.Resiliencia;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Arca.Facturacion.Api.Controllers;

/// <summary>Datos para emitir desde una plantilla: importes nuevos y, opcionalmente, otro receptor.</summary>
public sealed class EmisionDesdePlantilla
{
    /// <summary>Importes a facturar (se calculan con la calculadora). Si se omite, se usan los de la plantilla.</summary>
    public SolicitudCalculo? Importes { get; set; }
    public int? DocTipo { get; set; }
    public long? DocNro { get; set; }
    public int? CondicionIVAReceptorId { get; set; }
}

/// <summary>Variantes de emisión a partir de comprobantes existentes.</summary>
[ApiController]
[Route("api/v1/comprobantes")]
[Produces("application/json")]
[RequierePermiso(Permisos.Emitir)]
[Tags("Comprobantes")]
public sealed class VariantesController : ControllerBase
{
    private readonly NotaCreditoService _nc;
    private readonly ConsultasService _q;
    public VariantesController(NotaCreditoService nc, ConsultasService q) { _nc = nc; _q = q; }
    private string Cliente => HttpContext.Items[ApiKeyMiddleware.ItemCliente] as string ?? "anon";

    /// <summary>Nota de Crédito PARCIAL asociada a un comprobante: devuelve sólo los importes indicados.</summary>
    [HttpPost("{ptoVta:int}/{cbteTipo:int}/{cbteNro:long}/nota-credito-parcial")]
    [ComprobanteDelCliente]
    [EnableRateLimiting("emision")]
    [ConsumeCuota]
    public Task<ResultadoEmision> NotaCreditoParcial(int ptoVta, int cbteTipo, long cbteNro, [FromBody] SolicitudCalculo importes,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(100, MinimumLength = 1)] string idempotencyKey, CancellationToken ct)
        => _nc.NotaCreditoParcialAsync(ptoVta, cbteTipo, cbteNro, importes, idempotencyKey, Cliente, ct);

    /// <summary>Emite un comprobante nuevo idéntico a uno existente, con fecha de hoy (facturación recurrente).</summary>
    [HttpPost("{ptoVta:int}/{cbteTipo:int}/{cbteNro:long}/duplicar")]
    [ComprobanteDelCliente]
    [EnableRateLimiting("emision")]
    [ConsumeCuota]
    public Task<ResultadoEmision> Duplicar(int ptoVta, int cbteTipo, long cbteNro,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(100, MinimumLength = 1)] string idempotencyKey, CancellationToken ct)
        => _nc.DuplicarAsync(ptoVta, cbteTipo, cbteNro, idempotencyKey, Cliente, ct);

    /// <summary>Consulta en ARCA un rango de números (máx. 100). Los inexistentes se omiten, igual que los emitidos por
    /// otros clientes (salvo permiso admin).</summary>
    [HttpGet("{ptoVta:int}/{cbteTipo:int}")]
    [RequierePermiso(Permisos.Consultar)]
    public async Task<IReadOnlyList<ComprobanteConsultado>> Rango(int ptoVta, int cbteTipo, [FromQuery, Range(1, long.MaxValue)] long desde,
        [FromQuery, Range(1, long.MaxValue)] long hasta, CancellationToken ct)
    {
        if (!PropiedadComprobantes.EsAdmin(HttpContext))
        {
            // Sólo se consulta a ARCA lo que es del cliente (no se gastan llamadas en comprobantes ajenos).
            var q = HttpContext.RequestServices.GetRequiredService<ConsultasService>();
            var cliente = PropiedadComprobantes.Cliente(HttpContext);
            if (hasta >= desde && hasta - desde < ConsultasService.MaxRango
                && !Enumerable.Range(0, (int)(hasta - desde + 1)).Any(i => q.EsDelCliente(ptoVta, cbteTipo, desde + i, cliente)))
                return Array.Empty<ComprobanteConsultado>();
        }
        var res = await _q.ConsultarRangoAsync(ptoVta, cbteTipo, desde, hasta, ct);
        return PropiedadComprobantes.Filtrar(HttpContext, res, c => (ptoVta, cbteTipo, c.CbteDesde));
    }
}

/// <summary>Plantillas de comprobantes frecuentes (por cliente).</summary>
[ApiController]
[Route("api/v1/plantillas")]
[Produces("application/json")]
[RequierePermiso(Permisos.Emitir)]
[Tags("Plantillas")]
public sealed class PlantillasController : ControllerBase
{
    private readonly IRepositorioPlantillas _repo;
    private readonly IFacturacionService _svc;
    private readonly TimeProvider _reloj;
    public PlantillasController(IRepositorioPlantillas repo, IFacturacionService svc, TimeProvider reloj) { _repo = repo; _svc = svc; _reloj = reloj; }
    private string Cliente => HttpContext.Items[ApiKeyMiddleware.ItemCliente] as string ?? "anon";

    /// <summary>Lista las plantillas del cliente.</summary>
    [HttpGet] public IReadOnlyList<PlantillaComprobante> Listar() => _repo.Listar(Cliente);

    /// <summary>Obtiene una plantilla.</summary>
    [HttpGet("{id}")]
    public ActionResult<PlantillaComprobante> Obtener(string id) => _repo.Obtener(Cliente, id) is { } p ? p : NotFound();

    /// <summary>Crea o reemplaza una plantilla.</summary>
    [HttpPut("{id}")]
    public PlantillaComprobante Guardar(string id, [FromBody] PlantillaComprobante p)
    {
        p.Id = id;
        p.Actualizada = _reloj.GetUtcNow();
        _repo.Guardar(Cliente, p);
        return p;
    }

    /// <summary>Elimina una plantilla.</summary>
    [HttpDelete("{id}")]
    public IActionResult Eliminar(string id) => _repo.Eliminar(Cliente, id) ? NoContent() : NotFound();

    /// <summary>Emite un comprobante a partir de la plantilla, con importes y receptor opcionales.</summary>
    [HttpPost("{id}/emitir")]
    [EnableRateLimiting("emision")]
    [ConsumeCuota]
    public async Task<ActionResult<ResultadoEmision>> Emitir(string id, [FromBody] EmisionDesdePlantilla datos,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(100, MinimumLength = 1)] string idempotencyKey, CancellationToken ct)
    {
        var p = _repo.Obtener(Cliente, id);
        if (p == null) return NotFound();
        var d = FacturacionService.Clonar(p.Comprobante);
        if (datos.Importes != null)
        {
            datos.Importes.CbteTipo = p.CbteTipo;
            var calc = CalculadoraImportes.Calcular(datos.Importes);
            d.ImpTotal = calc.ImpTotal; d.ImpNeto = calc.ImpNeto; d.ImpIVA = calc.ImpIVA; d.ImpTrib = calc.ImpTrib;
            d.ImpOpEx = calc.ImpOpEx; d.ImpTotConc = calc.ImpTotConc; d.Iva = calc.Iva; d.Tributos = calc.Tributos;
        }
        if (datos.DocTipo is int dt) d.DocTipo = dt;
        if (datos.DocNro is long dn) d.DocNro = dn;
        if (datos.CondicionIVAReceptorId is int ci) d.CondicionIVAReceptorId = ci;
        d.CbteFch = null; // fecha del día
        return await _svc.EmitirAsync(p.PtoVta, p.CbteTipo, d, idempotencyKey, Cliente, ct);
    }
}

/// <summary>Catálogos locales (no consultan a ARCA).</summary>
[ApiController]
[Route("api/v1/catalogos")]
[Produces("application/json")]
[RequierePermiso(Permisos.Consultar)]
[Etag]
[Tags("Catálogos")]
public sealed class CatalogosController : ControllerBase
{
    /// <summary>Tipos de comprobante, documentos, condiciones frente al IVA y alícuotas.</summary>
    [HttpGet]
    public object Todos() => new
    {
        tiposComprobante = Catalogos.TiposComprobante,
        tiposDocumento = Catalogos.TiposDocumento,
        condicionesIva = Catalogos.CondicionesIva,
        alicuotasIva = Catalogos.AlicuotasIva
    };
}

/// <summary>CAEA de la quincena en curso.</summary>
[ApiController]
[Route("api/v1/caea/vigente")]
[Produces("application/json")]
[RequierePermiso(Permisos.Consultar)]
[Tags("CAEA")]
public sealed class CaeaVigenteController : ControllerBase
{
    /// <summary>Devuelve el CAEA de la quincena actual (404 si no fue solicitado).</summary>
    [HttpGet]
    public async Task<ActionResult<Caea>> Get([FromServices] CaeaService caea, CancellationToken ct)
        => await caea.VigenteAsync(ct) is { } c ? c : NotFound();
}

/// <summary>Reportes y auditoría (permiso admin).</summary>
[ApiController]
[Route("api/v1/admin")]
[Produces("application/json")]
[RequierePermiso(Permisos.Admin)]
[Tags("Administración")]
public sealed class ReportesController : ControllerBase
{
    private readonly ConsultasService _q;
    public ReportesController(ConsultasService q) => _q = q;

    /// <summary>Búsqueda paginada de emisiones por punto de venta, tipo, fechas, documento, estado o cliente.</summary>
    [HttpGet("emisiones")]
    public Pagina<RegistroEmision> Buscar([FromQuery] FiltroEmisiones filtro)
    {
        var p = _q.Buscar(filtro);
        // Enlaces de paginación (RFC 8288).
        var enlaces = new List<string>();
        string Url(int n) => QueryString.Create(Request.Query.Where(q => q.Key != "pagina").SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string?>(q.Key, v)))
            .Append(new KeyValuePair<string, string?>("pagina", n.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToUriComponent();
        if (p.NumeroPagina * p.TamanoPagina < p.Total) enlaces.Add($"<{Request.Path}{Url(p.NumeroPagina + 1)}>; rel=\"next\"");
        if (p.NumeroPagina > 1) enlaces.Add($"<{Request.Path}{Url(p.NumeroPagina - 1)}>; rel=\"prev\"");
        if (enlaces.Count > 0) Response.Headers.Link = string.Join(", ", enlaces);
        Response.Headers["X-Total-Count"] = p.Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return p;
    }

    /// <summary>Resumen de IVA por alícuota del período (en pesos; las notas de crédito restan).</summary>
    [HttpGet("resumen-iva")]
    public ResumenIva ResumenIva([FromQuery, Required, RegularExpression(@"^\d{8}$")] string desde, [FromQuery, Required, RegularExpression(@"^\d{8}$")] string hasta)
        => _q.ResumenIva(desde, hasta);

    /// <summary>Exporta a CSV (Excel) los comprobantes autorizados del período.</summary>
    [HttpGet("exportar.csv")]
    [Produces("text/csv")]
    public IActionResult Exportar([FromQuery, RegularExpression(@"^\d{8}$")] string? desde, [FromQuery, RegularExpression(@"^\d{8}$")] string? hasta)
        => File(Encoding.UTF8.GetBytes(_q.ExportarCsv(desde, hasta)), "text/csv; charset=utf-8", $"comprobantes_{desde ?? "inicio"}_{hasta ?? "hoy"}.csv");

    /// <summary>Números autorizados en ARCA que no emitió este servicio (otro sistema usa el punto de venta).</summary>
    [HttpGet("auditoria-numeracion/{ptoVta:int}/{cbteTipo:int}")]
    public async Task<object> AuditarNumeracion(int ptoVta, int cbteTipo, [FromQuery, Range(1, long.MaxValue)] long desde, CancellationToken ct)
    {
        var ajenos = await _q.AuditarNumeracionAsync(ptoVta, cbteTipo, desde, ct);
        return new { ptoVta, cbteTipo, desde, cantidad = ajenos.Count, numerosAjenos = ajenos };
    }

    /// <summary>Consulta la auditoría (más recientes primero).</summary>
    [HttpGet("auditoria")]
    public IReadOnlyList<EntradaAuditoria> Auditoria([FromServices] IAuditoria aud, [FromQuery] DateTimeOffset? desde,
        [FromQuery] string? cliente, [FromQuery, Range(1, 1000)] int maximo = 100)
        => aud.Consultar(desde, cliente, maximo);

    /// <summary>Configuración efectiva (sin secretos) y advertencias de riesgo.</summary>
    [HttpGet("config")]
    public object Config([FromServices] IOptions<ArcaOptions> opt, [FromServices] DirectorioClientes dir, [FromServices] IConfiguration cfg,
        [FromServices] IWebHostEnvironment env, [FromServices] IWsfeClient wsfe)
        => new
        {
            configuracion = DiagnosticoConfiguracion.Resumen(opt.Value),
            advertencias = DiagnosticoConfiguracion.Advertencias(opt.Value, cfg.GetValue("Swagger:Habilitado", env.IsDevelopment()), dir.Cantidad),
            llamadasArcaEnCurso = (wsfe as WsfeClienteResiliente)?.LlamadasEnCurso
        };
}
