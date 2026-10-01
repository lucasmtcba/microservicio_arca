using System.ComponentModel.DataAnnotations;
using Arca.Facturacion.Api.Infraestructura;
using Arca.Facturacion.Core;
using Arca.Facturacion.Core.Configuracion;
using Arca.Facturacion.Core.Resiliencia;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Wsaa;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Arca.Facturacion.Api.Controllers;

/// <summary>Ítem de un lote de emisión.</summary>
public sealed class ItemLote
{
    /// <summary>Clave de idempotencia propia de este comprobante.</summary>
    [Required, StringLength(100, MinimumLength = 1)] public string IdempotencyKey { get; set; } = string.Empty;
    [Range(1, 99998)] public int PtoVta { get; set; }
    [Range(1, 999)] public int CbteTipo { get; set; }
    [Required] public ComprobanteDetalle Comprobante { get; set; } = new();
}

public sealed class SolicitudLote
{
    [Required, MinLength(1), MaxLength(500)] public List<ItemLote> Items { get; set; } = new();
}

/// <summary>Resultado individual de un ítem del lote: o bien Resultado, o bien Error.</summary>
public sealed class ResultadoItemLote
{
    public string IdempotencyKey { get; set; } = string.Empty;
    public ResultadoEmision? Resultado { get; set; }
    public int? Estado { get; set; }
    public string? Error { get; set; }
    public IReadOnlyList<string>? Errores { get; set; }
}

/// <summary>Operaciones de valor agregado sobre la emisión.</summary>
[ApiController]
[Route("api/v1/comprobantes")]
[Produces("application/json")]
[RequierePermiso(Permisos.Consultar)]
[Tags("Comprobantes")]
public sealed class OperacionesController : ControllerBase
{
    private readonly IFacturacionService _svc;
    private readonly NotaCreditoService _nc;
    private readonly IWsfeClient _wsfe;
    private readonly ArcaOptions _opt;

    public OperacionesController(IFacturacionService svc, NotaCreditoService nc, IWsfeClient wsfe, IOptions<ArcaOptions> opt)
    { _svc = svc; _nc = nc; _wsfe = wsfe; _opt = opt.Value; }

    private string Cliente => HttpContext.Items[ApiKeyMiddleware.ItemCliente] as string ?? "anon";

    /// <summary>Emite hasta 50 comprobantes en una llamada. Cada ítem tiene su propia Idempotency-Key y su propio resultado;
    /// un error en un ítem no detiene al resto. Reintentar el lote completo es seguro.</summary>
    [HttpPost("lote")]
    [RequierePermiso(Permisos.Emitir)]
    [ConsumeCuota]
    [EnableRateLimiting("emision")]
    public async Task<IReadOnlyList<ResultadoItemLote>> EmitirLote([FromBody] SolicitudLote req, CancellationToken ct)
    {
        var max = HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue("Api:MaxItemsLote", 50);
        if (req.Items.Count > max) throw new ValidacionException(new[] { $"El lote admite como máximo {max} comprobantes." });
        var duplicadas = req.Items.GroupBy(i => i.IdempotencyKey).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicadas.Count > 0) throw new ValidacionException(new[] { "Idempotency-Key repetida en el lote: " + string.Join(", ", duplicadas) });

        var res = new List<ResultadoItemLote>();
        foreach (var i in req.Items)
        {
            var r = new ResultadoItemLote { IdempotencyKey = i.IdempotencyKey };
            try
            {
                r.Resultado = await _svc.EmitirAsync(i.PtoVta, i.CbteTipo, i.Comprobante, i.IdempotencyKey, Cliente, ct);
                r.Estado = 200;
                // Se cuenta para la cuota en el momento: si el lote se corta después (cancelación, error), este ya se emitió.
                if (r.Resultado is { Aprobado: true, Repetido: false }) ConsumeCuotaAttribute.RegistrarEmitido(HttpContext);
            }
            catch (ValidacionException ex) { r.Estado = 400; r.Error = "Comprobante inválido"; r.Errores = ex.Errores; }
            catch (IdempotenciaConflictoException ex) { r.Estado = 409; r.Error = ex.Message; }
            catch (ArcaComunicacionException ex) { r.Estado = 503; r.Error = ex.Message; }
            catch (ArcaException ex) { r.Estado = 422; r.Error = ex.Message; }
            res.Add(r);
        }
        return res;
    }

    /// <summary>Calcula importes coherentes (IVA por alícuota, redondeo a 2 decimales y total) a partir de líneas netas.
    /// La respuesta puede usarse directamente como "comprobante" completando receptor, concepto y condición IVA.</summary>
    [HttpPost("calcular")]
    public ComprobanteDetalle Calcular([FromBody] SolicitudCalculo req) => CalculadoraImportes.Calcular(req);

    /// <summary>Anula totalmente un comprobante emitiendo la Nota de Crédito de la misma clase, asociada al original.</summary>
    [HttpPost("{ptoVta:int}/{cbteTipo:int}/{cbteNro:long}/nota-credito")]
    [RequierePermiso(Permisos.Emitir)]
    [ComprobanteDelCliente]
    [ConsumeCuota]
    [EnableRateLimiting("emision")]
    public Task<ResultadoEmision> NotaCredito(int ptoVta, int cbteTipo, long cbteNro,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(100, MinimumLength = 1)] string idempotencyKey, CancellationToken ct)
        => _nc.AnularAsync(ptoVta, cbteTipo, cbteNro, idempotencyKey, Cliente, ct);

    /// <summary>URL del QR fiscal (RG 4892) de un comprobante ya autorizado.</summary>
    [HttpGet("{ptoVta:int}/{cbteTipo:int}/{cbteNro:long}/qr")]
    public async Task<ActionResult<object>> Qr(int ptoVta, int cbteTipo, long cbteNro, CancellationToken ct)
    {
        if (HttpContext.Items[ApiKeyMiddleware.ItemEsAdmin] is not true
            && !HttpContext.RequestServices.GetRequiredService<ConsultasService>().EsDelCliente(ptoVta, cbteTipo, cbteNro, Cliente))
            return NotFound();
        var c = await _wsfe.FECompConsultarAsync(ptoVta, cbteTipo, cbteNro, ct);
        var url = c == null ? null : QrFiscal.GenerarUrl(_opt.CuitNumerico, c);
        return url == null ? NotFound() : new { url };
    }

    /// <summary>Estado de una emisión por su Idempotency-Key (Pendiente / Autorizado). 404 si no existe.</summary>
    [HttpGet("emisiones/{idempotencyKey}")]
    public ActionResult<RegistroEmision> Emision(string idempotencyKey)
        => _svc.ObtenerEmision(idempotencyKey, Cliente) is { } r ? r : NotFound();
}

/// <summary>Operación del servicio (requiere permiso admin).</summary>
[ApiController]
[Route("api/v1/admin")]
[Produces("application/json")]
[RequierePermiso(Permisos.Admin)]
[Tags("Administración")]
public sealed class AdminController : ControllerBase
{
    private readonly IFacturacionService _svc;
    private readonly IWsfeClient _wsfe;
    public AdminController(IFacturacionService svc, IWsfeClient wsfe) { _svc = svc; _wsfe = wsfe; }

    /// <summary>Emisiones con resultado aún no confirmado.</summary>
    [HttpGet("emisiones/pendientes")]
    public IReadOnlyList<RegistroEmision> Pendientes() => _svc.Pendientes();

    /// <summary>Fuerza la reconciliación de todas las emisiones pendientes.</summary>
    [HttpPost("emisiones/reconciliar")]
    public async Task<object> Reconciliar(CancellationToken ct) => new { resueltas = await _svc.ReconciliarPendientesAsync(TimeSpan.Zero, ct) };

    /// <summary>Resumen del registro de emisiones (totales, del día y por tipo).</summary>
    [HttpGet("estadisticas")]
    public EstadisticasEmision Estadisticas() => _svc.Estadisticas();

    /// <summary>Vacía la caché de tablas de parámetros y cotizaciones.</summary>
    [HttpPost("cache/limpiar")]
    public IActionResult LimpiarCache()
    {
        (_wsfe as WsfeClienteResiliente)?.LimpiarCache();
        return NoContent();
    }
}

/// <summary>Información del servicio.</summary>
[ApiController]
[Route("api/v1/info")]
[RequierePermiso(Permisos.Consultar)]
[Tags("Salud")]
public sealed class InfoController : ControllerBase
{
    /// <summary>Versión, ambiente, CUIT (enmascarada), vencimiento del certificado y estado del canal con ARCA.</summary>
    [HttpGet]
    public object Get([FromServices] IOptions<ArcaOptions> opt, [FromServices] CircuitBreaker cb, [FromServices] EstadoArca estado,
        [FromServices] IServiceProvider sp)
    {
        DateTime? venc = null;
        try
        {
            venc = sp.GetService<ICmsFirmante>() switch
            {
                FirmanteDiferido d => d.Vencimiento,
                CertificadoCmsFirmante c => c.Vencimiento,
                _ => null
            };
        }
        catch (ArcaException) { }
        var cuit = opt.Value.Cuit;
        return new
        {
            version = typeof(InfoController).Assembly.GetName().Version?.ToString(3),
            ambiente = opt.Value.Ambiente.ToString(),
            cuit = cuit.Length == 11 ? cuit[..2] + "-*****" + cuit[8..] : "?",
            certificadoVence = venc?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            circuito = cb.Estado,
            ultimoExitoArca = estado.UltimoExito,
            desfasajeRelojSegundos = Math.Round(estado.DesfasajeReloj.TotalSeconds),
            modoSimulacion = opt.Value.ModoSimulacion,
            commit = Environment.GetEnvironmentVariable("APP_COMMIT"),
            fechaCompilacion = Environment.GetEnvironmentVariable("APP_BUILD_DATE"),
            iniciado = sp.GetRequiredService<EstadoVida>().Inicio,
            activoSegundos = Math.Round((DateTimeOffset.UtcNow - sp.GetRequiredService<EstadoVida>().Inicio).TotalSeconds),
            mantenimiento = sp.GetRequiredService<EstadoMantenimiento>().Motivo
        };
    }
}
