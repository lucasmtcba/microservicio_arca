using Arca.Facturacion.Core;
using Arca.Facturacion.Core.Resiliencia;
using Arca.Facturacion.Core.Servicios;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Arca.Facturacion.Api.Infraestructura;

/// <summary>Autenticación por API key (cabecera X-Api-Key) contra <see cref="DirectorioClientes"/>.
/// Si no hay claves configuradas, rechaza todo excepto /health y /swagger (fail-closed).</summary>
public sealed class ApiKeyMiddleware
{
    public const string Cabecera = "X-Api-Key";
    /// <summary>Clave de HttpContext.Items con el nombre del cliente autenticado.</summary>
    public const string ItemCliente = "arca.cliente";
    /// <summary>Clave de HttpContext.Items con los permisos del cliente.</summary>
    public const string ItemPermisos = "arca.permisos";
    /// <summary>Clave de HttpContext.Items con la cuota diaria de emisiones del cliente (int?).</summary>
    public const string ItemCuota = "arca.cuota";
    public const string ItemEsAdmin = "arca.admin";
    private readonly RequestDelegate _next;
    private readonly DirectorioClientes _dir;
    private readonly ProteccionFuerzaBruta _fb;
    private readonly bool _swaggerPublico;
    private readonly TimeProvider _reloj;

    public ApiKeyMiddleware(RequestDelegate next, DirectorioClientes dir, ProteccionFuerzaBruta fb, IConfiguration cfg, IWebHostEnvironment env, TimeProvider reloj)
    {
        _next = next;
        _dir = dir;
        _fb = fb;
        _reloj = reloj;
        // Fuera de Development, Swagger exige una clave con permiso admin salvo que se declare público explícitamente.
        _swaggerPublico = env.IsDevelopment() || cfg.GetValue("Swagger:Publico", false);
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/health") || path.Equals("/metrics") || (_swaggerPublico && path.StartsWithSegments("/swagger")))
        {
            await _next(ctx);
            return;
        }
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "desconocida";
        if (_fb.Bloqueada(ip))
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ctx.Response.Headers.RetryAfter = ((int)_fb.Bloqueo.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Results.Problem(title: "Demasiados intentos fallidos", detail: "IP bloqueada temporalmente por intentos de autenticación fallidos.",
                statusCode: 429).ExecuteAsync(ctx);
            return;
        }
        var clave = ctx.Request.Headers[Cabecera].ToString();
        var cliente = _dir.Resolver(clave);
        // Sólo una clave presentada e inválida cuenta como intento fallido: las solicitudes sin cabecera (sondas, navegadores,
        // health checks de terceros detrás del mismo proxy) no deben bloquear la IP. Un acierto NO reinicia el contador:
        // así no se puede intercalar una clave válida para seguir probando claves; los fallos expiran con la ventana.
        if (cliente == null && !string.IsNullOrEmpty(clave)) _fb.RegistrarFallo(ip);
        if (cliente != null && path.StartsWithSegments("/swagger") && !cliente.Permisos.Contains(Permisos.Admin, StringComparer.OrdinalIgnoreCase))
            cliente = null;
        if (cliente == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Results.Problem(title: "No autorizado", detail: $"Falta o es inválida la cabecera {Cabecera}.",
                statusCode: 401).ExecuteAsync(ctx);
            return;
        }
        if (await ValidarClienteAsync(ctx, cliente, _reloj) is { } error)
        {
            ctx.Response.StatusCode = error.Estado;
            await Results.Problem(title: error.Titulo, detail: error.Detalle, statusCode: error.Estado).ExecuteAsync(ctx);
            return;
        }
        ctx.Items[ItemCliente] = cliente.Nombre;
        ctx.Items[ItemCuota] = cliente.CuotaDiariaEmisiones;
        ctx.Items[ItemEsAdmin] = cliente.Permisos.Contains(Permisos.Admin, StringComparer.OrdinalIgnoreCase);
        ctx.Items[ItemPermisos] = (IReadOnlyCollection<string>)cliente.Permisos.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await _next(ctx);
    }

    /// <summary>Error de autenticación de un cliente ya resuelto.</summary>
    public sealed record ErrorAutenticacion(int Estado, string Titulo, string Detalle);

    /// <summary>Validaciones de un cliente resuelto por su clave: vencimiento, IP de origen y firma HMAC.
    /// Única fuente de verdad: la usan este middleware y los endpoints fuera de él (/metrics, detalle de /health/ready).</summary>
    public static async Task<ErrorAutenticacion?> ValidarClienteAsync(HttpContext ctx, ClienteApi cliente, TimeProvider reloj)
    {
        if (cliente.VenceEl is { } vence && reloj.GetUtcNow() > vence)
            return new(StatusCodes.Status401Unauthorized, "No autorizado", "La clave de API venció. Solicitar una nueva.");
        if (!cliente.IpPermitida(ctx.Connection.RemoteIpAddress))
            return new(StatusCodes.Status403Forbidden, "Origen no permitido", "La IP de origen no está habilitada para este cliente.");
        if (!string.IsNullOrEmpty(cliente.SecretoFirma)
            && await FirmaSolicitudes.VerificarAsync(ctx, cliente.SecretoFirma, reloj.GetUtcNow()) is { } errorFirma)
            return new(StatusCodes.Status401Unauthorized, "Firma inválida", errorFirma);
        return null;
    }

    /// <summary>Resuelve la clave de la solicitud y aplica TODAS las validaciones del cliente. Null si no autentica.</summary>
    /// <param name="permitirBearer">Acepta también "Authorization: Bearer {clave}" (para /metrics: Prometheus envía credenciales
    /// así con authorization.credentials_file, pero no cabeceras arbitrarias como X-Api-Key).</param>
    public static async Task<ClienteApi?> AutenticarAsync(HttpContext ctx, bool permitirBearer = false)
    {
        var clave = ctx.Request.Headers[Cabecera].ToString();
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(clave) && permitirBearer && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            clave = auth["Bearer ".Length..].Trim();
        var cli = ctx.RequestServices.GetRequiredService<DirectorioClientes>().Resolver(clave);
        if (cli == null) return null;
        return await ValidarClienteAsync(ctx, cli, ctx.RequestServices.GetRequiredService<TimeProvider>()) == null ? cli : null;
    }

    /// <summary>¿La solicitud trae una clave admin válida (vigente, desde IP permitida y con firma correcta si corresponde)?</summary>
    public static async Task<bool> EsAdminAutenticadoAsync(HttpContext ctx, bool permitirBearer = false) =>
        (await AutenticarAsync(ctx, permitirBearer))?.Permisos.Contains(Permisos.Admin, StringComparer.OrdinalIgnoreCase) == true;
}

/// <summary>Traduce excepciones de dominio a ProblemDetails (RFC 7807) sin filtrar detalles internos.</summary>
public sealed class ManejadorErrores : IExceptionHandler
{
    private readonly ILogger<ManejadorErrores> _log;
    public ManejadorErrores(ILogger<ManejadorErrores> log) => _log = log;

    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        int? reintentar = null;
        var (status, titulo, extra) = ex switch
        {
            ValidacionException v => (400, "Comprobante inválido", (object)v.Errores),
            IdempotenciaConflictoException => (409, "Conflicto de idempotencia", null),
            CircuitoAbiertoException c => (503, "ARCA no disponible (circuito abierto)", null),
            ArcaComunicacionException => (503, "ARCA no disponible o resultado desconocido", null),
            OperationCanceledException when ctx.RequestAborted.IsCancellationRequested => (499, "Solicitud cancelada por el cliente", null),
            WsaaException => (502, "Error de autenticación con ARCA (WSAA)", null),
            Arca.Facturacion.Core.Soap.SoapFaultException => (502, "ARCA devolvió un error técnico (SOAP Fault)", null),
            ArcaException a => (422, "ARCA rechazó la operación", a.Errores),
            BadHttpRequestException b => (b.StatusCode == 413 ? 413 : 400, "Solicitud inválida", null),
            _ => (500, "Error interno", null)
        };
        var codigo = ex switch
        {
            ValidacionException => "VALIDACION",
            IdempotenciaConflictoException => "IDEMPOTENCIA_CONFLICTO",
            CircuitoAbiertoException => "ARCA_NO_DISPONIBLE",
            ArcaComunicacionException => "ARCA_RESULTADO_INCIERTO",
            OperationCanceledException => "CANCELADA",
            WsaaException => "WSAA_ERROR",
            Arca.Facturacion.Core.Soap.SoapFaultException => "ARCA_ERROR_TECNICO",
            ArcaException => "ARCA_RECHAZO",
            BadHttpRequestException => "SOLICITUD_INVALIDA",
            _ => "ERROR_INTERNO"
        };
        if (ex is CircuitoAbiertoException ca) reintentar = (int)Math.Ceiling(ca.ReintentarEn.TotalSeconds);
        else if (status == 503) reintentar = 5;

        if (status >= 500) _log.LogError(ex, "Error procesando {Path}", ctx.Request.Path);
        else _log.LogWarning("{Tipo}: {Msg}", ex.GetType().Name, Sanitizar.Log(ex.Message, 500));

        var pd = new ProblemDetails
        {
            Status = status, Title = titulo, Type = "/docs/errores#" + codigo.ToLowerInvariant(),
            Detail = status is 500 or 502 or 499 ? "Ver logs del servicio con el traceId indicado." : ex.Message,
            Instance = ctx.Request.Path
        };
        if (extra != null) pd.Extensions["errores"] = extra;
        if (ex is ArcaException ae && ae.Errores.Count > 0)
        {
            var ayuda = CatalogoErroresArca.AyudasPara(ae.Errores);
            if (ayuda.Count > 0) pd.Extensions["ayuda"] = ayuda;
        }
        pd.Extensions["traceId"] = ctx.TraceIdentifier;
        pd.Extensions["codigo"] = codigo;
        ctx.Response.StatusCode = status;
        if (reintentar is int s) ctx.Response.Headers.RetryAfter = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ctx.Response.WriteAsJsonAsync(pd, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", ct);
        return true;
    }
}
