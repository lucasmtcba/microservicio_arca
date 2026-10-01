using Arca.Facturacion.Api.Infraestructura;
using Arca.Facturacion.Core.Padron;
using Arca.Facturacion.Core.Servicios;
using Microsoft.AspNetCore.Mvc;

namespace Arca.Facturacion.Api.Controllers;

/// <summary>Consulta de contribuyentes en el padrón A13 de ARCA y alta automática en la agenda de receptores.</summary>
[ApiController]
[Route("api/v1/contribuyentes")]
[Produces("application/json")]
[RequierePermiso(Permisos.Consultar)]
[Tags("Padrón")]
public sealed class PadronController : ControllerBase
{
    private readonly IPadronClient _padron;
    public PadronController(IPadronClient padron) => _padron = padron;

    private string Cliente => HttpContext.Items[ApiKeyMiddleware.ItemCliente] as string ?? "anon";

    /// <summary>Datos del contribuyente (denominación, estado de clave y domicilios). 400 si la CUIT es inválida, 404 si no existe.</summary>
    [HttpGet("{cuit:long}")]
    [ProducesResponseType<Contribuyente>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Obtener(long cuit, CancellationToken ct)
    {
        if (!Arca.Facturacion.Core.Validacion.Cuit.EsValida(cuit.ToString(System.Globalization.CultureInfo.InvariantCulture))) return Problem(statusCode: 400, title: "CUIT inválida (longitud o dígito verificador).");
        return await _padron.ObtenerAsync(cuit, ct) is { } c ? Ok(c) : Problem(statusCode: 404, title: "La CUIT no figura en el padrón.");
    }

    /// <summary>Crea o actualiza un receptor de la agenda con los datos del padrón (DocTipo 80). La condición frente al IVA se
    /// indica en la consulta porque el padrón A13 no la informa.</summary>
    [HttpPost("{cuit:long}/agenda")]
    [RequierePermiso(Permisos.Emitir)]
    [ProducesResponseType<Receptor>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> AgregarAAgenda(long cuit, [FromQuery] int condicionIva, [FromServices] AgendaReceptores agenda, CancellationToken ct)
    {
        if (!Arca.Facturacion.Core.Validacion.Cuit.EsValida(cuit.ToString(System.Globalization.CultureInfo.InvariantCulture))) return Problem(statusCode: 400, title: "CUIT inválida (longitud o dígito verificador).");
        if (condicionIva is < 1 or > 16) return Problem(statusCode: 400, title: "condicionIva debe ser un código de FEParamGetCondicionIvaReceptor (1..16).");
        var c = await _padron.ObtenerAsync(cuit, ct);
        if (c == null) return Problem(statusCode: 404, title: "La CUIT no figura en el padrón.");
        if (!c.Activo) return Problem(statusCode: 422, title: $"La CUIT tiene estado de clave {c.EstadoClave}.");
        var id = "cuit-" + cuit;
        var r = agenda.Obtener(Cliente, id) ?? new Receptor { Id = id };
        r.Nombre = c.Denominacion; r.DocTipo = 80; r.DocNro = cuit; r.CondicionIVAReceptorId = condicionIva; r.Domicilio = c.DomicilioFiscal;
        return Ok(agenda.Guardar(Cliente, r));
    }
}
