using System.Globalization;
using Arca.Facturacion.Core.Configuracion;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Arca.Facturacion.Api.Infraestructura;

/// <summary>Registra en la auditoría toda operación que modifica estado (POST/PUT/DELETE): cliente, operación, comprobante y resultado.</summary>
public sealed class AuditoriaFiltro : IAsyncActionFilter
{
    private readonly IAuditoria _aud;
    private readonly TimeProvider _reloj;
    public AuditoriaFiltro(IAuditoria aud, TimeProvider reloj) { _aud = aud; _reloj = reloj; }

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var res = await next();
        var metodo = ctx.HttpContext.Request.Method;
        if (HttpMethods.IsGet(metodo) || HttpMethods.IsHead(metodo)) return;

        var e = new EntradaAuditoria
        {
            Fecha = _reloj.GetUtcNow(),
            Cliente = ctx.HttpContext.Items[ApiKeyMiddleware.ItemCliente] as string ?? "anon",
            Operacion = $"{metodo} {ctx.ActionDescriptor.AttributeRouteInfo?.Template}",
            CorrelationId = ctx.HttpContext.TraceIdentifier,
            Ip = ctx.HttpContext.Connection.RemoteIpAddress?.ToString(),
            Agente = Sanitizar.Log(ctx.HttpContext.Request.Headers.UserAgent.ToString(), 120)
        };
        switch (res.Result)
        {
            case ObjectResult { Value: ResultadoEmision r }:
                e.Comprobante = r.Descripcion; e.Resultado = r.Resultado; e.Cae = r.Cae;
                e.Importe = ctx.HttpContext.Items["arca.importe"] as decimal?;
                break;
            case ObjectResult { Value: IEnumerable<Controllers.ResultadoItemLote> l }:
                var lista = l.ToList();
                e.Resultado = $"lote: {lista.Count(i => i.Resultado?.Aprobado == true)}/{lista.Count} aprobados";
                break;
            case ObjectResult o:
                e.Resultado = (o.StatusCode ?? 200).ToString(CultureInfo.InvariantCulture);
                break;
            case IStatusCodeActionResult s:
                e.Resultado = (s.StatusCode ?? 200).ToString(CultureInfo.InvariantCulture);
                break;
        }
        if (res.Exception != null && !res.ExceptionHandled)
        {
            e.Resultado = "error";
            e.Detalle = res.Exception.GetType().Name + ": " + res.Exception.Message;
        }
        try { _aud.Registrar(e); }
        catch (IOException ex) { ctx.HttpContext.RequestServices.GetRequiredService<ILogger<AuditoriaFiltro>>().LogError(ex, "No se pudo escribir la auditoría"); }
    }
}

/// <summary>Documenta en OpenAPI las cabeceras que el servicio agrega a todas las respuestas.</summary>
public sealed class CabecerasRespuestaFiltro : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        foreach (var r in operation.Responses.Values)
        {
            r.Headers ??= new Dictionary<string, OpenApiHeader>();
            r.Headers.TryAdd(CabecerasMiddleware.Correlacion, new OpenApiHeader { Description = "Identificador de correlación (eco del enviado o generado).", Schema = new OpenApiSchema { Type = "string" } });
            r.Headers.TryAdd("X-Api-Version", new OpenApiHeader { Description = "Versión del servicio.", Schema = new OpenApiSchema { Type = "string" } });
        }
        operation.Parameters ??= new List<OpenApiParameter>();
        if (operation.Parameters.All(p => p.Name != CabecerasMiddleware.Correlacion))
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = CabecerasMiddleware.Correlacion, In = ParameterLocation.Header, Required = false,
                Description = "Opcional: identificador de correlación propio (máx. 64, letras, dígitos, - y _).",
                Schema = new OpenApiSchema { Type = "string" }
            });
    }
}

/// <summary>Ejemplos en OpenAPI para los modelos principales.</summary>
public sealed class EjemplosFiltro : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(Controllers.EmitirComprobanteRequest))
            schema.Example = new OpenApiObject
            {
                ["ptoVta"] = new OpenApiInteger(3),
                ["cbteTipo"] = new OpenApiInteger(6),
                ["comprobante"] = new OpenApiObject
                {
                    ["concepto"] = new OpenApiInteger(1), ["docTipo"] = new OpenApiInteger(99), ["docNro"] = new OpenApiLong(0),
                    ["impTotal"] = new OpenApiDouble(121), ["impNeto"] = new OpenApiDouble(100), ["impIVA"] = new OpenApiDouble(21),
                    ["monId"] = new OpenApiString("PES"), ["monCotiz"] = new OpenApiDouble(1), ["condicionIVAReceptorId"] = new OpenApiInteger(5),
                    ["iva"] = new OpenApiArray { new OpenApiObject { ["id"] = new OpenApiInteger(5), ["baseImp"] = new OpenApiDouble(100), ["importe"] = new OpenApiDouble(21) } }
                }
            };
        else if (context.Type == typeof(SolicitudCalculo))
            schema.Example = new OpenApiObject
            {
                ["cbteTipo"] = new OpenApiInteger(6), ["preciosConIvaIncluido"] = new OpenApiBoolean(true),
                ["lineas"] = new OpenApiArray
                {
                    new OpenApiObject { ["cantidad"] = new OpenApiDouble(2), ["precioUnitario"] = new OpenApiDouble(1210), ["alicuotaIvaId"] = new OpenApiInteger(5), ["bonificacionPorcentaje"] = new OpenApiDouble(10) }
                }
            };
    }
}

/// <summary>Precarga las tablas de parámetros al iniciar para que la primera emisión no espere a ARCA.</summary>
public sealed class PrecargaParametrosHostedService : BackgroundService
{
    private readonly IWsfeClient _w;
    private readonly ArcaOptions _opt;
    private readonly ILogger<PrecargaParametrosHostedService> _log;
    public PrecargaParametrosHostedService(IWsfeClient w, IOptions<ArcaOptions> opt, ILogger<PrecargaParametrosHostedService> log) { _w = w; _opt = opt.Value; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.PrecargarParametros) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct); // deja arrancar al TA primero
            await _w.FEParamGetTiposCbteAsync(ct);
            await _w.FEParamGetTiposIvaAsync(ct);
            await _w.FEParamGetTiposMonedasAsync(ct);
            await _w.FEParamGetPtosVentaAsync(ct);
            await _w.FEParamGetCondicionIvaReceptorAsync(null, ct);
            _log.LogInformation("Tablas de parámetros precargadas");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "No se pudieron precargar las tablas de parámetros; se cargarán a demanda");
        }
    }
}

/// <summary>Asegura el CAEA de la próxima quincena (contingencia) cada 6 horas, si está habilitado.</summary>
public sealed class CaeaAutomaticoHostedService : BackgroundService
{
    private readonly CaeaService _caea;
    private readonly ArcaOptions _opt;
    private readonly ILogger<CaeaAutomaticoHostedService> _log;
    public CaeaAutomaticoHostedService(CaeaService caea, IOptions<ArcaOptions> opt, ILogger<CaeaAutomaticoHostedService> log) { _caea = caea; _opt = opt.Value; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.SolicitarCaeaAutomatico) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var c = await _caea.AsegurarProximoAsync(ct);
                if (c != null) _log.LogInformation("CAEA {Caea} asegurado para {Periodo}/{Orden}", c.Codigo, c.Periodo, c.Orden);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "No se pudo asegurar el CAEA de la próxima quincena");
            }
            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch (OperationCanceledException) { return; }
        }
    }
}

/// <summary>Ciclo de vida del proceso (para probes de inicio y drenaje en apagado ordenado).</summary>
public sealed class EstadoVida
{
    private volatile bool _iniciado;
    private volatile bool _deteniendo;
    public bool Iniciado { get => _iniciado; set => _iniciado = value; }
    public bool Deteniendo { get => _deteniendo; set => _deteniendo = value; }
    public DateTimeOffset Inicio { get; } = DateTimeOffset.UtcNow;
}

/// <summary>Durante el apagado ordenado responde no-listo para que el balanceador deje de enviar tráfico.</summary>
public sealed class DrenajeHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    private readonly EstadoVida _v;
    public DrenajeHealthCheck(EstadoVida v) => _v = v;
    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext c, CancellationToken ct = default) =>
        Task.FromResult(_v.Deteniendo
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("El servicio se está deteniendo: no enviar tráfico nuevo.")
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());
}

/// <summary>Probe de inicio: saludable una vez arrancados los servicios.</summary>
public sealed class InicioHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    private readonly EstadoVida _v;
    public InicioHealthCheck(EstadoVida v) => _v = v;
    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext c, CancellationToken ct = default) =>
        Task.FromResult(_v.Iniciado
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy()
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Iniciando"));
}

/// <summary>Lee secretos de archivos según la convención VARIABLE_FILE=/ruta (Docker secrets, Kubernetes).</summary>
public static class SecretosArchivo
{
    public static Dictionary<string, string?> Leer(System.Collections.IDictionary entorno)
    {
        var r = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in entorno)
        {
            var k = e.Key?.ToString();
            if (k == null || !k.EndsWith("_FILE", StringComparison.Ordinal) || e.Value?.ToString() is not { Length: > 0 } ruta || !File.Exists(ruta)) continue;
            r[k[..^5].Replace("__", ":", StringComparison.Ordinal)] = File.ReadAllText(ruta).Trim();
        }
        return r;
    }
}

/// <summary>Comandos de consola: verificar, probar-wsaa, ultimo, reconciliar (además de generar-clave).</summary>
public static class Cli
{
    private static readonly string[] Comandos = { "verificar", "probar-wsaa", "ultimo", "reconciliar" };
    public static bool EsComando(string a) => Comandos.Contains(a);
    public static string? Comando(string[] args) => args.FirstOrDefault(EsComando);

    public static async Task<int> EjecutarAsync(IServiceProvider sp, string comando, string[] args)
    {
        var opt = sp.GetRequiredService<IOptions<ArcaOptions>>().Value;
        try
        {
            switch (comando)
            {
                case "verificar":
                    Console.WriteLine($"Ambiente: {opt.Ambiente}  CUIT: {opt.Cuit}  WSFE: {opt.WsfeUrlEfectiva}");
                    foreach (var a in Core.DiagnosticoConfiguracion.Advertencias(opt, false, sp.GetRequiredService<DirectorioClientes>().Cantidad))
                        Console.WriteLine("ADVERTENCIA: " + a);
                    if (sp.GetRequiredService<Core.Wsaa.ICmsFirmante>() is Core.Wsaa.FirmanteDiferido f)
                        Console.WriteLine($"Certificado: vence {f.Vencimiento:yyyy-MM-dd}, CUIT {f.CuitDelCertificado ?? "?"}");
                    var d = await sp.GetRequiredService<IWsfeClient>().FEDummyAsync(CancellationToken.None);
                    Console.WriteLine($"FEDummy: App={d.AppServer} Db={d.DbServer} Auth={d.AuthServer}");
                    return d.TodoOk ? 0 : 2;
                case "probar-wsaa":
                    var ta = await sp.GetRequiredService<Core.Wsaa.ITicketAccesoProvider>().ObtenerAsync(Core.Wsfe.WsfeClient.Servicio, CancellationToken.None);
                    Console.WriteLine($"TA obtenido. Vence: {ta.ExpiraEn:o}");
                    return 0;
                case "ultimo":
                    var i = Array.IndexOf(args, "ultimo");
                    if (args.Length < i + 3) { Console.WriteLine("Uso: ultimo <ptoVta> <cbteTipo>"); return 1; }
                    var u = await sp.GetRequiredService<IWsfeClient>().FECompUltimoAutorizadoAsync(
                        int.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture), int.Parse(args[i + 2], System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
                    Console.WriteLine($"Último autorizado {u.PtoVta}-{u.CbteTipo}: {u.CbteNro}");
                    return 0;
                case "reconciliar":
                    // Nunca TimeSpan.Zero: una emisión en curso (aún dentro de su timeout y esperas de reconciliación)
                    // podría liberarse y su número reutilizarse.
                    var minima = Math.Max(opt.ReconciliadorAntiguedadMinimaSegundos,
                        opt.TimeoutEmisionSegundos + (opt.ReconciliacionEsperasSegundos?.Sum() ?? 0) + 30);
                    var n = await sp.GetRequiredService<IFacturacionService>().ReconciliarPendientesAsync(TimeSpan.FromSeconds(minima), CancellationToken.None);
                    Console.WriteLine($"Pendientes resueltas: {n}");
                    return 0;
            }
        }
        catch (Exception ex) when (ex is Core.ArcaException or InvalidOperationException)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 3;
        }
        return 1;
    }
}
