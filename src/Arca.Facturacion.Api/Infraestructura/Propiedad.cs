using Arca.Facturacion.Core.Servicios;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Arca.Facturacion.Api.Infraestructura;

/// <summary>Reglas de visibilidad de comprobantes: un cliente sólo opera sobre los comprobantes que emitió él a través del
/// servicio; el permiso admin ve todos. Se responde 404 (no 403) para no revelar la existencia de comprobantes ajenos.</summary>
public static class PropiedadComprobantes
{
    public static bool EsAdmin(HttpContext ctx) => ctx.Items[ApiKeyMiddleware.ItemEsAdmin] is true;

    public static string Cliente(HttpContext ctx) => ctx.Items[ApiKeyMiddleware.ItemCliente] as string ?? "anon";

    /// <summary>¿El llamador puede ver u operar sobre el comprobante?</summary>
    public static bool PuedeAcceder(HttpContext ctx, int pv, int tipo, long nro) =>
        EsAdmin(ctx) || ctx.RequestServices.GetRequiredService<ConsultasService>().EsDelCliente(pv, tipo, nro, Cliente(ctx));

    /// <summary>Filtra una lista de comprobantes dejando sólo los visibles para el llamador.</summary>
    public static IReadOnlyList<T> Filtrar<T>(HttpContext ctx, IReadOnlyList<T> items, Func<T, (int Pv, int Tipo, long Nro)> clave)
    {
        if (EsAdmin(ctx)) return items;
        var q = ctx.RequestServices.GetRequiredService<ConsultasService>();
        var cliente = Cliente(ctx);
        return items.Where(i => { var (pv, tipo, nro) = clave(i); return q.EsDelCliente(pv, tipo, nro, cliente); }).ToList();
    }
}

/// <summary>Exige que el comprobante de la ruta ({ptoVta}/{cbteTipo}/{cbteNro}) pertenezca al cliente autenticado
/// (admin exento). Si no, 404. Corre después de los filtros de permisos y antes de reservar cuota.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ComprobanteDelClienteAttribute : Attribute, IAuthorizationFilter, IOrderedFilter
{
    /// <summary>Después de <see cref="RequierePermisoAttribute"/> (orden 0): sin permiso sigue siendo 403.</summary>
    public int Order => 1;

    public void OnAuthorization(AuthorizationFilterContext ctx)
    {
        if (ctx.Result != null) return;
        var rv = ctx.RouteData.Values;
        if (!int.TryParse(rv["ptoVta"]?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pv)
            || !int.TryParse(rv["cbteTipo"]?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tipo)
            || !long.TryParse(rv["cbteNro"]?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var nro))
        {
            ctx.Result = new NotFoundResult();
            return;
        }
        if (!PropiedadComprobantes.PuedeAcceder(ctx.HttpContext, pv, tipo, nro))
            ctx.Result = new ObjectResult(new ProblemDetails { Status = 404, Title = "Comprobante no encontrado",
                Detail = "El comprobante no existe o no fue emitido por este cliente." }) { StatusCode = 404 };
    }
}
