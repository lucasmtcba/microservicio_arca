using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Arca.Facturacion.Api.Infraestructura;

/// <summary>Permisos que puede tener un cliente de la API.</summary>
public static class Permisos
{
    public const string Emitir = "emitir";
    public const string Consultar = "consultar";
    public const string Admin = "admin";
}

/// <summary>Cliente configurado (Seguridad:Clientes). Varias claves por cliente permiten rotarlas sin cortar el servicio;
/// la idempotencia se aísla por Nombre, así que sobrevive a la rotación.</summary>
public sealed class ClienteApi
{
    public string Nombre { get; set; } = string.Empty;
    public List<string> Claves { get; set; } = new();
    /// <summary>Hashes SHA-256 (hex) de claves: alternativa a Claves para no guardar secretos en claro.</summary>
    public List<string> ClavesHash { get; set; } = new();
    /// <summary>Secreto para firmar solicitudes (HMAC-SHA256). Si se define, toda solicitud de este cliente debe venir firmada.</summary>
    public string? SecretoFirma { get; set; }
    /// <summary>Fecha de vencimiento de las claves de este cliente (null = no vencen).</summary>
    public DateTimeOffset? VenceEl { get; set; }
    /// <summary>IPs o redes CIDR desde las que se acepta este cliente (vacío = cualquiera).</summary>
    public List<string> IpsPermitidas { get; set; } = new();
    /// <summary>Límite propio de solicitudes por minuto (null = el general).</summary>
    public int? RateLimitPorMinuto { get; set; }
    /// <summary>Máximo de comprobantes emitidos por día (hora argentina). Null = sin cuota.</summary>
    public int? CuotaDiariaEmisiones { get; set; }

    /// <summary>¿La IP está permitida para este cliente?</summary>
    public bool IpPermitida(System.Net.IPAddress? ip)
    {
        if (IpsPermitidas.Count == 0) return true;
        if (ip == null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        foreach (var regla in IpsPermitidas)
        {
            if (System.Net.IPAddress.TryParse(regla, out var exacta) && exacta.Equals(ip)) return true;
            if (System.Net.IPNetwork.TryParse(regla, out var red) && red.Contains(ip)) return true;
        }
        return false;
    }

    /// <summary>Permisos del cliente. Vacío = emitir + consultar. (No se inicializa con valores: el binder de configuración agregaría a la lista.)</summary>
    public List<string> Permisos { get; set; } = new();
}

/// <summary>Resuelve la API key a un cliente, en tiempo constante.</summary>
public sealed class DirectorioClientes
{
    private volatile List<(byte[] Hash, ClienteApi Cliente)> _claves = new();
    private readonly IConfiguration _config;
    private readonly ILogger<DirectorioClientes>? _log;

    public DirectorioClientes(IConfiguration config, ILogger<DirectorioClientes>? log = null)
    {
        _config = config;
        _log = log;
        Cargar();
        // Recarga en caliente al cambiar la configuración (alta/baja/rotación de claves sin reiniciar).
        Microsoft.Extensions.Primitives.ChangeToken.OnChange(config.GetReloadToken, () =>
        {
            Cargar();
            _log?.LogInformation("Clientes de API recargados: {Cantidad} claves", Cantidad);
        });
    }

    private void Cargar()
    {
        var nuevas = new List<(byte[] Hash, ClienteApi Cliente)>();
        foreach (var c in _config.GetSection("Seguridad:Clientes").Get<List<ClienteApi>>() ?? new())
        {
            if (c.Permisos.Count == 0) c.Permisos = new() { Infraestructura.Permisos.Emitir, Infraestructura.Permisos.Consultar };
            foreach (var k in c.Claves.Where(k => !string.IsNullOrWhiteSpace(k)))
                nuevas.Add((Hash(k), c));
            // Claves guardadas sólo como hash SHA-256 (hex): la configuración no contiene la clave en claro.
            foreach (var h in c.ClavesHash.Where(h => !string.IsNullOrWhiteSpace(h)))
            {
                try { nuevas.Add((Convert.FromHexString(h.Trim()), c)); }
                catch (FormatException) { _log?.LogWarning("Hash de clave inválido para el cliente {Cliente}", c.Nombre); }
            }
        }

        // Compatibilidad: Seguridad:ApiKeys (sin nombre) => cliente derivado del hash, permisos emitir+consultar.
        foreach (var k in (_config.GetSection("Seguridad:ApiKeys").Get<string[]>() ?? Array.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            var h = Hash(k);
            nuevas.Add((h, new ClienteApi { Nombre = Convert.ToHexString(h, 0, 8),
                Permisos = new() { Infraestructura.Permisos.Emitir, Infraestructura.Permisos.Consultar } }));
        }
        _claves = nuevas;
    }

    /// <summary>Genera una clave aleatoria (256 bits, base64url) y su hash para Seguridad:Clientes[].ClavesHash.</summary>
    public static (string Clave, string Hash) GenerarClave()
    {
        var clave = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (clave, Convert.ToHexString(Hash(clave)));
    }

    public int Cantidad => _claves.Count;

    /// <summary>Cliente por nombre (para procesos internos como las suscripciones).</summary>
    public ClienteApi? PorNombre(string nombre) => _claves.Select(c => c.Cliente).FirstOrDefault(c => c.Nombre == nombre);

    private static byte[] Hash(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));

    public ClienteApi? Resolver(string? clave)
    {
        if (string.IsNullOrEmpty(clave)) return null;
        var h = Hash(clave);
        ClienteApi? encontrado = null;
        foreach (var (k, c) in _claves.ToList())
            if (CryptographicOperations.FixedTimeEquals(h, k)) encontrado ??= c; // recorre todo: tiempo constante
        return encontrado;
    }
}

/// <summary>Exige un permiso del cliente autenticado (403 si no lo tiene).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequierePermisoAttribute : Attribute, IAuthorizationFilter
{
    public string Permiso { get; }
    public RequierePermisoAttribute(string permiso) => Permiso = permiso;

    public void OnAuthorization(AuthorizationFilterContext ctx)
    {
        // El permiso del método prevalece sobre el de la clase.
        if (ctx.ActionDescriptor is Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor cad)
        {
            var efectivo = cad.MethodInfo.GetCustomAttributes(typeof(RequierePermisoAttribute), true).Cast<RequierePermisoAttribute>().FirstOrDefault()
                           ?? cad.ControllerTypeInfo.GetCustomAttributes(typeof(RequierePermisoAttribute), true).Cast<RequierePermisoAttribute>().FirstOrDefault();
            if (efectivo != null && efectivo.Permiso != Permiso) return; // lo evalúa el filtro efectivo
        }
        if (ctx.HttpContext.Items[ApiKeyMiddleware.ItemPermisos] is IReadOnlyCollection<string> p && p.Contains(Permiso)) return;
        ctx.Result = new ObjectResult(new ProblemDetails
        {
            Status = 403, Title = "Permiso insuficiente", Detail = $"Se requiere el permiso '{Permiso}'."
        }) { StatusCode = 403 };
    }
}

/// <summary>Correlation-ID, cabeceras de seguridad y versión de API en todas las respuestas.</summary>
public sealed class CabecerasMiddleware
{
    public const string Correlacion = "X-Correlation-Id";
    private readonly RequestDelegate _next;
    private readonly string _version;

    public CabecerasMiddleware(RequestDelegate next)
    {
        _next = next;
        _version = typeof(CabecerasMiddleware).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    public async Task InvokeAsync(HttpContext ctx, ILogger<CabecerasMiddleware> log)
    {
        var id = ctx.Request.Headers[Correlacion].ToString();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            id = Guid.NewGuid().ToString("N");
        ctx.TraceIdentifier = id;
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h[Correlacion] = id;
            h["X-Api-Version"] = _version;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            h["Cache-Control"] = "no-store";
            if (!ctx.Request.Path.StartsWithSegments("/swagger"))
                h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            return Task.CompletedTask;
        });
        using (log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
            await _next(ctx);
    }
}

/// <summary>Cuenta comprobantes emitidos por cliente y día (hora argentina) para aplicar cuotas.</summary>
public sealed class CuotasEmision
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), int> _uso = new();
    private readonly Arca.Facturacion.Core.Validacion.ComprobanteValidator _val;
    public CuotasEmision(Arca.Facturacion.Core.Validacion.ComprobanteValidator val) => _val = val;

    private string Hoy => _val.HoyArgentina().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

    public int Usadas(string cliente) => _uso.GetValueOrDefault((cliente, Hoy));

    private readonly object _lock = new();

    public void Sumar(string cliente, int cantidad) => Sumar(cliente, cantidad, Hoy);

    private void Sumar(string cliente, int cantidad, string dia)
    {
        if (cantidad == 0) return;
        var hoy = Hoy;
        lock (_lock)
        {
            // Un ajuste de un día que ya pasó no toca la cuenta de hoy (ese día ya no cuenta para la cuota).
            if (dia == hoy) _uso.AddOrUpdate((cliente, hoy), Math.Max(0, cantidad), (_, v) => Math.Max(0, v + cantidad));
            foreach (var k in _uso.Keys.Where(k => k.Item2 != hoy).ToList()) _uso.TryRemove(k, out _); // limpia días anteriores
        }
    }

    /// <summary>Reserva atómicamente <paramref name="cantidad"/> unidades de la cuota (sin carreras entre solicitudes concurrentes).</summary>
    public bool TryReservar(string cliente, int? maximo, int cantidad) => TryReservar(cliente, maximo, cantidad, out _);

    /// <summary>Igual que <see cref="TryReservar(string, int?, int)"/>, informando el día (yyyyMMdd) al que se imputó la reserva.</summary>
    public bool TryReservar(string cliente, int? maximo, int cantidad, out string dia)
    {
        lock (_lock)
        {
            dia = Hoy;
            if (maximo is int max && Usadas(cliente) + cantidad > max) return false;
            Sumar(cliente, cantidad, dia);
            return true;
        }
    }

    /// <summary>Devuelve unidades reservadas que no se usaron (rechazos, errores, reintentos).</summary>
    public void Devolver(string cliente, int cantidad) { if (cantidad > 0) Sumar(cliente, -cantidad); }

    /// <summary>Devuelve unidades al día en que se reservaron: si la solicitud cruzó la medianoche, no se descuentan de hoy.</summary>
    public void Devolver(string cliente, int cantidad, string diaReserva) { if (cantidad > 0) Sumar(cliente, -cantidad, diaReserva); }
}

/// <summary>Aplica la cuota diaria de emisiones del cliente (429 si se agotó) y suma lo emitido con éxito.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ConsumeCuotaAttribute : Attribute, IAsyncActionFilter
{
    /// <summary>Clave de HttpContext.Items donde la acción acumula, ítem por ítem, los comprobantes nuevos emitidos (int).
    /// Así lo emitido cuenta aunque la acción termine con una excepción (p.ej. cancelación a mitad de un lote).</summary>
    public const string ItemEmitidos = "arca.cuota.emitidos";

    /// <summary>Registra un comprobante nuevo emitido en la solicitud actual (lo usa la emisión por lotes).</summary>
    public static void RegistrarEmitido(HttpContext ctx) =>
        ctx.Items[ItemEmitidos] = (ctx.Items[ItemEmitidos] as int? ?? 0) + 1;

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var cliente = ctx.HttpContext.Items[ApiKeyMiddleware.ItemCliente] as string ?? "anon";
        var cuota = ctx.HttpContext.Items[ApiKeyMiddleware.ItemCuota] as int?;
        var cuotas = ctx.HttpContext.RequestServices.GetRequiredService<CuotasEmision>();
        var mant = ctx.HttpContext.RequestServices.GetRequiredService<EstadoMantenimiento>();
        if (mant.Activo)
        {
            ctx.HttpContext.Response.Headers.RetryAfter = "300";
            ctx.Result = new ObjectResult(new ProblemDetails { Status = 503, Title = "Servicio en mantenimiento",
                Detail = $"La emisión está suspendida temporalmente: {mant.Motivo}. Las consultas siguen disponibles.",
                Extensions = { ["codigo"] = "MANTENIMIENTO" } }) { StatusCode = 503 };
            return;
        }
        // Reserva previa (atómica): un lote reserva tantas unidades como ítems.
        var reservadas = ctx.ActionArguments.Values.OfType<Controllers.SolicitudLote>().FirstOrDefault()?.Items.Count ?? 1;
        if (!cuotas.TryReservar(cliente, cuota, reservadas, out var dia))
        {
            ctx.HttpContext.Response.Headers.RetryAfter = "3600";
            ctx.Result = new ObjectResult(new ProblemDetails { Status = 429, Title = "Cuota diaria agotada",
                Detail = $"El cliente alcanzó su cuota de {cuota} comprobantes por día.", Extensions = { ["codigo"] = "CUOTA_AGOTADA" } }) { StatusCode = 429 };
            return;
        }
        ctx.HttpContext.Items.Remove(ItemEmitidos);
        var emitidos = 0;
        try
        {
            var res = await next();
            if (res.Exception == null && res.Result is ObjectResult obj)
                emitidos = obj.Value switch
                {
                    Arca.Facturacion.Core.Servicios.ResultadoEmision r when r.Aprobado && !r.Repetido => 1,
                    IEnumerable<Controllers.ResultadoItemLote> l => l.Count(i => i.Resultado is { Aprobado: true, Repetido: false }),
                    _ => 0
                };
        }
        finally
        {
            // Lo contado ítem por ítem prevalece: cubre acciones que emiten y luego fallan (excepción o cancelación).
            if (ctx.HttpContext.Items[ItemEmitidos] is int contados) emitidos = Math.Max(emitidos, contados);
            cuotas.Devolver(cliente, Math.Max(0, reservadas - emitidos), dia);
        }
    }
}

/// <summary>ETag fuerte (SHA-256 del JSON) y 304 Not Modified en respuestas GET que casi no cambian.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class EtagAttribute : Attribute, IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext ctx, ResultExecutionDelegate next)
    {
        if (HttpMethods.IsGet(ctx.HttpContext.Request.Method) && ctx.Result is ObjectResult { Value: not null } o && (o.StatusCode ?? 200) == 200)
        {
            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(o.Value);
            var etag = "\"" + Convert.ToHexString(SHA256.HashData(json), 0, 16) + "\"";
            ctx.HttpContext.Response.Headers.ETag = etag;
            if (ctx.HttpContext.Request.Headers.IfNoneMatch.Any(v => v == etag))
            {
                ctx.Result = new StatusCodeResult(StatusCodes.Status304NotModified);
            }
        }
        await next();
    }
}

/// <summary>Bloqueo temporal de IPs con muchos intentos de autenticación fallidos (fuerza bruta).</summary>
public sealed class ProteccionFuerzaBruta
{
    private readonly ConcurrentDictionary<string, (int Fallos, DateTimeOffset Desde, DateTimeOffset? BloqueadoHasta)> _ips = new();
    private readonly TimeProvider _reloj;
    public int MaxFallos { get; init; } = 10;
    public TimeSpan Ventana { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan Bloqueo { get; init; } = TimeSpan.FromMinutes(15);

    public ProteccionFuerzaBruta(TimeProvider reloj) => _reloj = reloj;

    public bool Bloqueada(string ip) =>
        _ips.TryGetValue(ip, out var e) && e.BloqueadoHasta is { } h && _reloj.GetUtcNow() < h;

    public void RegistrarFallo(string ip)
    {
        var ahora = _reloj.GetUtcNow();
        _ips.AddOrUpdate(ip, (1, ahora, null), (_, e) =>
        {
            if (ahora - e.Desde > Ventana) return (1, ahora, null);
            var f = e.Fallos + 1;
            return (f, e.Desde, f >= MaxFallos ? ahora + Bloqueo : e.BloqueadoHasta);
        });
        if (_ips.Count > 100_000) foreach (var k in _ips.Where(k => ahora - k.Value.Desde > Ventana && k.Value.BloqueadoHasta is null).Select(k => k.Key).ToList()) _ips.TryRemove(k, out _);
    }

    /// <summary>Olvida los fallos de una IP. NO se llama al autenticar con éxito (una clave válida no debe habilitar a
    /// seguir probando claves desde la misma IP); queda para desbloqueos administrativos.</summary>
    public void RegistrarExito(string ip) => _ips.TryRemove(ip, out _);
}

/// <summary>Verificación de firma HMAC de solicitudes entrantes: X-Firma-Tiempo (unix s) y
/// X-Firma = hex(HMAC-SHA256(secreto, tiempo + "." + cuerpo)). Rechaza firmas fuera de ±5 minutos (anti-replay).</summary>
public static class FirmaSolicitudes
{
    public const string CabeceraFirma = "X-Firma";
    public const string CabeceraTiempo = "X-Firma-Tiempo";
    public static readonly TimeSpan Tolerancia = TimeSpan.FromMinutes(5);
    /// <summary>Tamaño máximo de cuerpo que se lee para verificar la firma.</summary>
    public const int MaxCuerpoFirmado = 1024 * 1024;
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Usadas = new();
    /// <summary>Cada cuántas firmas registradas se purga la caché anti-replay (además de la purga por tiempo).</summary>
    public const int PurgaCadaInserciones = 1024;
    private static int _inserciones;
    private static long _ultimaPurgaTicks;

    /// <summary>Cantidad de firmas recordadas (diagnóstico y pruebas).</summary>
    public static int FirmasRecordadas => Usadas.Count;

    /// <summary>Quita de la caché anti-replay las firmas fuera de la ventana. No recorre la caché en cada solicitud:
    /// sólo cada <see cref="PurgaCadaInserciones"/> inserciones o si pasó una tolerancia desde la última purga.</summary>
    internal static void PurgarSiCorresponde(DateTimeOffset ahora, bool forzar = false)
    {
        var ultima = Interlocked.Read(ref _ultimaPurgaTicks);
        if (!forzar && Volatile.Read(ref _inserciones) < PurgaCadaInserciones && ahora.UtcTicks - ultima < Tolerancia.Ticks) return;
        if (!forzar && Interlocked.CompareExchange(ref _ultimaPurgaTicks, ahora.UtcTicks, ultima) != ultima) return; // otra solicitud ya purga
        if (forzar) Interlocked.Exchange(ref _ultimaPurgaTicks, ahora.UtcTicks);
        Interlocked.Exchange(ref _inserciones, 0);
        foreach (var k in Usadas.Where(k => ahora - k.Value > Tolerancia * 2).Select(k => k.Key).ToList()) Usadas.TryRemove(k, out _);
    }

    /// <summary>Purga inmediata de la caché anti-replay (pruebas y mantenimiento).</summary>
    public static void Purgar(DateTimeOffset ahora) => PurgarSiCorresponde(ahora, forzar: true);

    /// <summary>Destino de la solicitud tal como llegó por la red (ruta + query, sin decodificar), que es lo que firma el cliente.
    /// Request.Path está decodificado y al re-codificarlo no coincide con lo enviado (p.ej. %3A vs ':').</summary>
    public static string DestinoCrudo(HttpContext ctx)
    {
        var crudo = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(crudo)) return ctx.Request.PathBase.ToUriComponent() + ctx.Request.Path.ToUriComponent() + ctx.Request.QueryString.ToUriComponent();
        // Forma absoluta ("http://host/ruta?x"): se firma sólo ruta + query.
        if (!crudo.StartsWith('/') && Uri.TryCreate(crudo, UriKind.Absolute, out var abs)) return abs.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
        return crudo;
    }

    /// <summary>Texto canónico firmado (v2): "v2\n{tiempo}\n{MÉTODO}\n{ruta?query}\n{cuerpo}", donde ruta?query es el destino
    /// CRUDO de la línea de solicitud (tal como lo codificó el cliente, ver <see cref="DestinoCrudo"/>).
    /// Incluye método y ruta: una firma no puede reutilizarse en otra operación.</summary>
    public static string Calcular(string secreto, string tiempo, string metodo, string rutaYQuery, ReadOnlySpan<byte> cuerpo)
    {
        var cab = Encoding.UTF8.GetBytes($"v2\n{tiempo}\n{metodo.ToUpperInvariant()}\n{rutaYQuery}\n");
        var datos = new byte[cab.Length + cuerpo.Length];
        cab.CopyTo(datos, 0);
        cuerpo.CopyTo(datos.AsSpan(cab.Length));
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secreto), datos)).ToLowerInvariant();
    }

    public static async Task<string?> VerificarAsync(HttpContext ctx, string secreto, DateTimeOffset ahora)
    {
        var tiempo = ctx.Request.Headers[CabeceraTiempo].ToString();
        var firma = ctx.Request.Headers[CabeceraFirma].ToString().ToLowerInvariant();
        if (string.IsNullOrEmpty(tiempo) || string.IsNullOrEmpty(firma)) return "Faltan las cabeceras de firma.";
        // El rango se valida antes de convertir: FromUnixTimeSeconds lanza fuera de [-62135596800, 253402300799] (=> 500).
        if (!long.TryParse(tiempo, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var unix)
            || unix < DateTimeOffset.MinValue.ToUnixTimeSeconds() || unix > DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            || (ahora - DateTimeOffset.FromUnixTimeSeconds(unix)).Duration() > Tolerancia)
            return "Marca de tiempo de la firma inválida o vencida.";
        if (ctx.Request.ContentLength > MaxCuerpoFirmado) return "Cuerpo demasiado grande para una solicitud firmada.";
        ctx.Request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: MaxCuerpoFirmado);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
        {
            if (ms.Length + n > MaxCuerpoFirmado) return "Cuerpo demasiado grande para una solicitud firmada.";
            ms.Write(buf, 0, n);
        }
        ctx.Request.Body.Position = 0;
        var ruta = DestinoCrudo(ctx);
        var esperada = Calcular(secreto, tiempo, ctx.Request.Method, ruta, ms.ToArray());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(esperada), Encoding.ASCII.GetBytes(firma)))
            return "Firma inválida.";
        // Anti-replay: cada firma se acepta una sola vez dentro de la ventana.
        PurgarSiCorresponde(ahora);
        if (!Usadas.TryAdd(firma, ahora)) return "Firma ya utilizada (replay).";
        Interlocked.Increment(ref _inserciones);
        return null;
    }
}

/// <summary>Modo mantenimiento: rechaza operaciones que emiten comprobantes (503) sin afectar consultas.</summary>
public sealed class EstadoMantenimiento
{
    private volatile string? _motivo;
    public bool Activo => _motivo != null;
    public string? Motivo => _motivo;
    public void Activar(string motivo) => _motivo = string.IsNullOrWhiteSpace(motivo) ? "Mantenimiento programado" : motivo;
    public void Desactivar() => _motivo = null;
}

/// <summary>Quita saltos de línea y caracteres de control de valores provistos por usuarios antes de loguearlos (log injection).</summary>
public static class Sanitizar
{
    public static string Log(string? v, int max = 200)
    {
        if (string.IsNullOrEmpty(v)) return "";
        var limpio = new string(v.Where(ch => !char.IsControl(ch)).ToArray());
        return limpio.Length > max ? limpio[..max] + "…" : limpio;
    }
}

/// <summary>Clave de partición por IP: IPv4 completa; IPv6 agrupada por prefijo /64 (las IPv6 de un mismo cliente rotan dentro de él).</summary>
public static class ParticionIp
{
    public static string De(System.Net.IPAddress? ip)
    {
        if (ip == null) return "desconocida";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            for (var i = 8; i < 16; i++) b[i] = 0;
            return new System.Net.IPAddress(b) + "/64";
        }
        return ip.ToString();
    }
}
