using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Arca.Facturacion.Api.Infraestructura;
using Arca.Facturacion.Core;
using Arca.Facturacion.Core.Servicios;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;

// Comandos de consola (generar-clave no necesita configuración).
if (args.Length > 0 && args[0] == "generar-clave")
{
    var (clave, hash) = DirectorioClientes.GenerarClave();
    Console.WriteLine($"Clave (entregar al cliente, no se vuelve a mostrar): {clave}");
    Console.WriteLine($"Hash SHA-256 (para Seguridad:Clientes[].ClavesHash): {hash}");
    return;
}

var builder = WebApplication.CreateBuilder(args.Where(a => !Cli.EsComando(a)).ToArray());
var cfg = builder.Configuration;

// Secretos desde archivos (convención Docker/Kubernetes): Arca__CertificadoPassword_FILE=/run/secrets/pfx => Arca:CertificadoPassword.
builder.Configuration.AddInMemoryCollection(SecretosArchivo.Leer(Environment.GetEnvironmentVariables()));

builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = cfg.GetValue("Api:MaxBodyBytes", 512 * 1024);
    k.Limits.MaxConcurrentConnections = cfg.GetValue<long?>("Api:MaxConexiones", 1000);
    k.Limits.MaxRequestHeadersTotalSize = cfg.GetValue("Api:MaxBytesCabeceras", 32 * 1024);
    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(cfg.GetValue("Api:TimeoutCabecerasSegundos", 15));
    k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(cfg.GetValue("Api:KeepAliveSegundos", 120));
    k.AddServerHeader = false;
});
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(cfg.GetValue("Api:ApagadoSegundos", 90)));

// Logs: formato JSON opcional para agregadores (Loki, ELK, CloudWatch).
if (cfg.GetValue("Logging:Json", false))
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; });
}

// Webhook: una sola instancia como notificador y como servicio en segundo plano.
builder.Services.AddHttpClient(WebhookNotificador.NombreHttpClient, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<WebhookNotificador>();
builder.Services.AddSingleton<INotificadorEmisiones>(sp => sp.GetRequiredService<WebhookNotificador>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookNotificador>());

builder.Services.AddArcaFacturacion(cfg);
builder.Services.AddSingleton<DirectorioClientes>();
builder.Services.AddHostedService<ReconciliadorHostedService>();
builder.Services.AddHostedService<PrecargaParametrosHostedService>();
builder.Services.AddHostedService<CaeaAutomaticoHostedService>();
builder.Services.AddSingleton<CuotasEmision>();
builder.Services.AddSingleton<EstadoMantenimiento>();
builder.Services.AddSingleton<EstadoVida>();
builder.Services.AddSingleton(sp => new ProteccionFuerzaBruta(sp.GetRequiredService<TimeProvider>())
{
    MaxFallos = cfg.GetValue("Seguridad:MaxIntentosFallidos", 10)
});
builder.Services.AddSingleton<ExportadorPrometheus>();
builder.Services.AddSingleton<IndicadoresServicio>();
builder.Services.AddHostedService<SuscripcionesHostedService>();
builder.Services.AddRequestDecompression();
builder.Services.AddOutputCache(o => o.AddPolicy("catalogos", p => p.Expire(TimeSpan.FromMinutes(5)).SetVaryByHeader(ApiKeyMiddleware.Cabecera)));
var cors = cfg.GetSection("Api:CorsOrigenes").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (cors.Length > 0) p.WithOrigins(cors).AllowAnyHeader().WithMethods("GET", "POST", "PUT", "DELETE").WithExposedHeaders("X-Correlation-Id", "Location", "Idempotency-Replayed");
}));
builder.Services.AddScoped<AuditoriaFiltro>();
builder.Services.AddRequestTimeouts(o =>
{
    o.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
    {
        Timeout = TimeSpan.FromSeconds(cfg.GetValue("Api:TimeoutSolicitudSegundos", 180)),
        TimeoutStatusCode = StatusCodes.Status504GatewayTimeout
    };
});
builder.Services.AddHostedService<RenovadorTicketHostedService>();

builder.Services.AddControllers(o =>
    {
        o.Filters.AddService<AuditoriaFiltro>();
        o.Filters.Add(new Microsoft.AspNetCore.Mvc.ConsumesAttribute("application/json"));
    })
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.JsonSerializerOptions.MaxDepth = 32;
        if (cfg.GetValue("Api:RechazarCamposDesconocidos", false))
            o.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    });
builder.Services.AddExceptionHandler<ManejadorErrores>();
builder.Services.AddProblemDetails();

builder.Services.AddHealthChecks()
    .AddCheck("vivo", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddCheck<CertificadoHealthCheck>("certificado", tags: new[] { "ready" })
    .AddCheck<RegistroHealthCheck>("registro", tags: new[] { "ready" })
    .AddCheck<ArcaHealthCheck>("arca", tags: new[] { "ready" })
    .AddCheck<DrenajeHealthCheck>("drenaje", tags: new[] { "ready" })
    .AddCheck<InicioHealthCheck>("inicio", tags: new[] { "startup" });

builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var ip in cfg.GetSection("Api:ProxiesConfiables").Get<string[]>() ?? Array.Empty<string>())
        if (System.Net.IPAddress.TryParse(ip, out var a)) o.KnownProxies.Add(a);
    // Redes de proxies/balanceadores en notación CIDR (p.ej. la red de pods de Kubernetes, 10.0.0.0/8).
    foreach (var red in RedesConfiables.Leer(cfg))
        o.KnownNetworks.Add(red);
});

builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields = HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestPath | HttpLoggingFields.ResponseStatusCode | HttpLoggingFields.Duration;
    o.RequestHeaders.Clear(); // nunca registrar X-Api-Key ni otras cabeceras
    o.ResponseHeaders.Clear();
});

var porMinuto = cfg.GetValue("Api:RateLimitPorMinuto", 120);
var emisionesConcurrentes = cfg.GetValue("Api:EmisionesConcurrentes", 16);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
    // Partición por cliente RESUELTO; claves inválidas caen en la partición de su IP (IPv6 agrupada por /64),
    // así inventar claves no esquiva el límite ni crea particiones sin fin.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var cli = ctx.RequestServices.GetRequiredService<DirectorioClientes>().Resolver(ctx.Request.Headers[ApiKeyMiddleware.Cabecera].ToString());
        var particion = cli != null ? "c:" + cli.Nombre : "ip:" + ParticionIp.De(ctx.Connection.RemoteIpAddress);
        return RateLimitPartition.GetFixedWindowLimiter(particion,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = cli?.RateLimitPorMinuto ?? porMinuto, Window = TimeSpan.FromMinutes(1) });
    });
    o.AddConcurrencyLimiter("emision", l => { l.PermitLimit = emisionesConcurrentes; l.QueueLimit = emisionesConcurrentes * 4; });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Microservicio de Facturación Electrónica ARCA (ex AFIP)",
        Version = "v1",
        Description = "Fachada REST sobre WSAA + WSFEv1 de ARCA. Obtiene CAE/CAEA, consulta comprobantes y tablas de parámetros, " +
                      "emite notas de crédito, calcula importes y genera el QR fiscal. Autenticación por cabecera X-Api-Key " +
                      "(permisos emitir / consultar / admin). Fechas yyyyMMdd, importes con punto decimal y hasta 2 decimales. " +
                      "Todas las respuestas incluyen X-Correlation-Id."
    });
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = ApiKeyMiddleware.Cabecera,
        Description = "Clave de API configurada en Seguridad:Clientes"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } }] = Array.Empty<string>()
    });
    c.OperationFilter<CabecerasRespuestaFiltro>();
    foreach (var url in cfg.GetSection("Api:UrlsPublicas").Get<string[]>() ?? Array.Empty<string>())
        c.AddServer(new OpenApiServer { Url = url });
    c.SchemaFilter<EjemplosFiltro>();
    foreach (var a in new[] { typeof(Program).Assembly, typeof(ArcaException).Assembly })
    {
        var xml = Path.Combine(AppContext.BaseDirectory, a.GetName().Name + ".xml");
        if (File.Exists(xml)) c.IncludeXmlComments(xml);
    }
});

var app = builder.Build();
var vida = app.Services.GetRequiredService<EstadoVida>();
app.Lifetime.ApplicationStarted.Register(() => vida.Iniciado = true);
app.Lifetime.ApplicationStopping.Register(() => vida.Deteniendo = true);
app.Services.GetRequiredService<IndicadoresServicio>();
// Las suscripciones respetan el modo mantenimiento y la cuota diaria del cliente (igual que la API).
{
    var sus = app.Services.GetRequiredService<SuscripcionesService>();
    var mant = app.Services.GetRequiredService<EstadoMantenimiento>();
    var cuotas = app.Services.GetRequiredService<CuotasEmision>();
    var dirCli = app.Services.GetRequiredService<DirectorioClientes>();
    sus.ControlEmision = cliente =>
    {
        if (mant.Activo) return "mantenimiento: " + mant.Motivo;
        var max = dirCli.PorNombre(cliente)?.CuotaDiariaEmisiones;
        return max is int m && cuotas.Usadas(cliente) >= m ? "cuota diaria agotada" : null;
    };
    sus.AlEmitir = cliente => cuotas.Sumar(cliente, 1);
}
app.Services.GetRequiredService<ExportadorPrometheus>();

if (Cli.Comando(args) is { } comando)
{
    Environment.ExitCode = await Cli.EjecutarAsync(app.Services, comando, args);
    return;
}

// Diagnóstico de configuración al iniciar: advertencias de riesgo, sin secretos.
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Arca.Facturacion.Inicio");
    var opt = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Arca.Facturacion.Core.Configuracion.ArcaOptions>>().Value;
    var swagger = cfg.GetValue("Swagger:Habilitado", app.Environment.IsDevelopment());
    foreach (var a in DiagnosticoConfiguracion.Advertencias(opt, swagger, app.Services.GetRequiredService<DirectorioClientes>().Cantidad))
        log.LogWarning("Configuración: {Advertencia}", a);
    foreach (var a in RedesConfiables.Advertencias(cfg))
        log.LogWarning("Configuración: {Advertencia}", a);
    log.LogInformation("Microservicio ARCA iniciado. Ambiente {Ambiente}, WSFE {Url}", opt.Ambiente, opt.WsfeUrlEfectiva);
}

app.UseForwardedHeaders();
app.UseMiddleware<CabecerasMiddleware>();
app.UseExceptionHandler();
if (cfg.GetValue("Api:ForzarHttps", false))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseHttpLogging();
app.UseResponseCompression();
app.UseRequestDecompression();
app.UseCors();
app.UseRateLimiter();
app.UseMiddleware<ApiKeyMiddleware>();
// Swagger va DESPUÉS de la autenticación: fuera de Development requiere una clave con permiso admin.
if (cfg.GetValue("Swagger:Habilitado", app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.DocumentTitle = "ARCA Facturación API");
}
app.UseRequestTimeouts();
app.UseOutputCache();

// El detalle de salud (rutas, CUIT del certificado, etc.) sólo se muestra a clientes con permiso admin o en Development.
// La clave admin pasa por las MISMAS validaciones que en la API (vencimiento, IPs permitidas, firma HMAC).
static async Task EscribirSalud(HttpContext ctx, HealthReport r)
{
    var detalle = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment()
                  || await ApiKeyMiddleware.EsAdminAutenticadoAsync(ctx);
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync(JsonSerializer.Serialize(detalle
        ? new
        {
            estado = r.Status.ToString(),
            duracionMs = r.TotalDuration.TotalMilliseconds,
            chequeos = (object)r.Entries.ToDictionary(e => e.Key, e => new { estado = e.Value.Status.ToString(), e.Value.Description, e.Value.Data })
        }
        : new { estado = r.Status.ToString(), duracionMs = r.TotalDuration.TotalMilliseconds, chequeos = (object)r.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()) }));
}

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") });
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready"),
    ResponseWriter = EscribirSalud,
    ResultStatusCodes = { [HealthStatus.Degraded] = 200, [HealthStatus.Unhealthy] = 503, [HealthStatus.Healthy] = 200 }
});
app.MapHealthChecks("/health/startup", new HealthCheckOptions { Predicate = c => c.Tags.Contains("startup") });
app.MapGet("/metrics", async (ExportadorPrometheus p, HttpContext ctx) =>
    {
        // Las métricas exponen volúmenes de negocio: públicas sólo si se habilita explícitamente; si no, requieren clave admin
        // válida (vigente, desde IP permitida y con firma si el cliente la exige), en X-Api-Key o "Authorization: Bearer".
        // Para Prometheus conviene un cliente admin propio SIN SecretoFirma y con IpsPermitidas acotadas al scraper.
        if (!cfg.GetValue("Api:MetricasPublicas", false) && !await ApiKeyMiddleware.EsAdminAutenticadoAsync(ctx, permitirBearer: true))
            return Results.Unauthorized();
        return Results.Text(p.Exponer(), "text/plain; version=0.0.4");
    }).ExcludeFromDescription();
app.MapControllers();
await app.RunAsync();

public partial class Program { }
