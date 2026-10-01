using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Arca.Facturacion.Api.Infraestructura;
using Arca.Facturacion.Cliente;
using Arca.Facturacion.Core.Configuracion;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Arca.Facturacion.Tests;

/// <summary>Servicio de emisión que falla con una excepción no controlada cuando la Idempotency-Key empieza con "boom"
/// (simula un corte a mitad de un lote, después de haber emitido otros ítems).</summary>
internal sealed class FacturacionQueFalla : IFacturacionService
{
    private readonly IFacturacionService _real;
    public FacturacionQueFalla(IFacturacionService real) => _real = real;
    public Task<ResultadoEmision> EmitirAsync(int ptoVta, int cbteTipo, ComprobanteDetalle detalle, string idempotencyKey, string cliente, CancellationToken ct)
        => idempotencyKey.StartsWith("boom", StringComparison.Ordinal)
            ? throw new InvalidOperationException("corte simulado")
            : _real.EmitirAsync(ptoVta, cbteTipo, detalle, idempotencyKey, cliente, ct);
    public RegistroEmision? ObtenerEmision(string idempotencyKey, string cliente) => _real.ObtenerEmision(idempotencyKey, cliente);
    public IReadOnlyList<RegistroEmision> Pendientes() => _real.Pendientes();
    public Task<int> ReconciliarPendientesAsync(TimeSpan antiguedadMinima, CancellationToken ct) => _real.ReconciliarPendientesAsync(antiguedadMinima, ct);
    public int Purgar(TimeSpan retencion) => _real.Purgar(retencion);
    public EstadisticasEmision Estadisticas() => _real.Estadisticas();
    public Task<string> ResolverPendienteAsync(string clave, CancellationToken ct) => _real.ResolverPendienteAsync(clave, ct);
}

public sealed class RevisionFactory : WebApplicationFactory<Program>
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "rev_" + Guid.NewGuid().ToString("N"));

    private static string H(string k) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(k)));

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseSetting("Arca:Cuit", Util.CuitEmisor);
        b.UseSetting("Arca:CertificadoPath", "no-usado.pfx");
        b.UseSetting("Arca:ModoSimulacion", "true");
        b.UseSetting("Arca:CacheTicketPath", "");
        b.UseSetting("Arca:RegistroEmisionesSqlite", Path.Combine(Dir, "registro.db"));
        b.UseSetting("Arca:BloqueoEntreProcesosPath", Path.Combine(Dir, "bloqueos"));
        b.UseSetting("Arca:AuditoriaPath", Path.Combine(Dir, "aud", "auditoria.jsonl"));
        b.UseSetting("Arca:PlantillasPath", Path.Combine(Dir, "plantillas"));
        b.UseSetting("Arca:SuscripcionesIntervaloMinutos", "0");
        b.UseSetting("Arca:PrecargarParametros", "false");
        b.UseSetting("Arca:ReconciliadorIntervaloSegundos", "0");
        b.UseSetting("Swagger:Habilitado", "true");
        b.UseSetting("Seguridad:MaxIntentosFallidos", "3");
        b.UseSetting("Api:RedesConfiables:0", "10.0.0.0/8");
        b.UseSetting("Api:RedesConfiables:1", "fd00::/8");
        var i = 0;
        void Cliente(string nombre, string clave, params string[] permisos)
        {
            b.UseSetting($"Seguridad:Clientes:{i}:Nombre", nombre);
            b.UseSetting($"Seguridad:Clientes:{i}:ClavesHash:0", H(clave));
            for (var p = 0; p < permisos.Length; p++) b.UseSetting($"Seguridad:Clientes:{i}:Permisos:{p}", permisos[p]);
            i++;
        }
        Cliente("a", "k-a", "emitir", "consultar");
        Cliente("b", "k-b", "emitir", "consultar");
        Cliente("adm", "k-adm", "emitir", "consultar", "admin");
        Cliente("cuota", "k-cuota", "emitir", "consultar");
        b.UseSetting($"Seguridad:Clientes:{i - 1}:CuotaDiariaEmisiones", "10");
        Cliente("adm-vencido", "k-adm-vencido", "admin");
        b.UseSetting($"Seguridad:Clientes:{i - 1}:VenceEl", "2020-01-01T00:00:00Z");
        Cliente("adm-ip", "k-adm-ip", "admin");
        b.UseSetting($"Seguridad:Clientes:{i - 1}:IpsPermitidas:0", "203.0.113.7");
        Cliente("adm-firma", "k-adm-firma", "admin");
        b.UseSetting($"Seguridad:Clientes:{i - 1}:SecretoFirma", "sec-adm");
        b.UseSetting($"Seguridad:Clientes:{i}:Nombre", "firmado");
        b.UseSetting($"Seguridad:Clientes:{i}:Claves:0", "k-firma");
        b.UseSetting($"Seguridad:Clientes:{i}:SecretoFirma", "s3cr3t");
        b.UseEnvironment("Revision");
        b.ConfigureTestServices(s =>
        {
            var d = s.Last(x => x.ServiceType == typeof(IFacturacionService));
            s.Remove(d);
            s.AddSingleton<IFacturacionService>(sp => new FacturacionQueFalla((IFacturacionService)d.ImplementationFactory!(sp)));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

public class RevisionApiTests : IClassFixture<RevisionFactory>
{
    private readonly RevisionFactory _f;
    public RevisionApiTests(RevisionFactory f) => _f = f;

    private HttpClient C(string? clave)
    {
        var c = _f.CreateClient();
        if (clave != null) c.DefaultRequestHeaders.Add("X-Api-Key", clave);
        return c;
    }

    private static string Hoy() => new ComprobanteValidator(TimeProvider.System).HoyArgentina().ToString("yyyyMMdd");

    private static object Detalle() => new
    {
        concepto = 1, docTipo = 99, docNro = 0, cbteFch = Hoy(), impTotal = 121, impNeto = 100, impIVA = 21,
        monId = "PES", monCotiz = 1, condicionIVAReceptorId = 5, iva = new[] { new { id = 5, baseImp = 100, importe = 21 } }
    };

    private static async Task<HttpResponseMessage> Post(HttpClient c, string url, object? body, string? clave = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body ?? new { }) };
        if (clave != null) req.Headers.Add("Idempotency-Key", clave);
        return await c.SendAsync(req);
    }

    private static async Task<long> Emitir(HttpClient c, int pv, string clave)
    {
        var r = await Post(c, "/api/v1/comprobantes", new { ptoVta = pv, cbteTipo = 6, comprobante = Detalle() }, clave);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cbteNro").GetInt64();
    }

    // ---------------------------------------------------------------- 1 y 2: IDOR

    [Fact]
    public async Task Operaciones_sobre_comprobante_ajeno_devuelven_404_y_no_consumen_cuota()
    {
        var a = C("k-a");
        var b = C("k-b");
        var nro = await Emitir(a, 70, "idor-a1");
        var baseUrl = $"/api/v1/comprobantes/70/6/{nro}";
        var cuotas = _f.Services.GetRequiredService<CuotasEmision>();
        var usadasB = cuotas.Usadas("b");

        Assert.Equal(HttpStatusCode.NotFound, (await Post(b, baseUrl + "/nota-credito", null, "idor-b1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(b, baseUrl + "/nota-credito-parcial",
            new { cbteTipo = 8, lineas = new[] { new { neto = 10 } } }, "idor-b2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(b, baseUrl + "/duplicar", null, "idor-b3")).StatusCode);
        Assert.Equal(usadasB, cuotas.Usadas("b"));
        // Un número que nadie emitió tampoco se puede anular/duplicar.
        Assert.Equal(HttpStatusCode.NotFound, (await Post(a, "/api/v1/comprobantes/70/6/9999/duplicar", null, "idor-a9")).StatusCode);

        // El dueño sí puede.
        Assert.Equal(HttpStatusCode.OK, (await Post(a, baseUrl + "/duplicar", null, "idor-a2")).StatusCode);
        // Admin puede operar sobre cualquiera.
        Assert.Equal(HttpStatusCode.OK, (await Post(C("k-adm"), baseUrl + "/nota-credito", null, "idor-adm1")).StatusCode);
    }

    [Fact]
    public async Task Lecturas_de_comprobante_ajeno_y_rango_filtrado()
    {
        var a = C("k-a");
        var b = C("k-b");
        var n1 = await Emitir(a, 71, "lect-a1");
        var n2 = await Emitir(b, 71, "lect-b1");

        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/api/v1/comprobantes/71/6/{n1}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/comprobantes/71/6/{n1}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await C("k-adm").GetAsync($"/api/v1/comprobantes/71/6/{n1}")).StatusCode);

        var url = $"/api/v1/comprobantes/71/6?desde={Math.Min(n1, n2)}&hasta={Math.Max(n1, n2)}";
        var deA = await a.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal(new[] { n1 }, deA.EnumerateArray().Select(x => x.GetProperty("cbteDesde").GetInt64()).ToArray());
        var deB = await b.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal(new[] { n2 }, deB.EnumerateArray().Select(x => x.GetProperty("cbteDesde").GetInt64()).ToArray());
        Assert.Equal(2, (await C("k-adm").GetFromJsonAsync<JsonElement>(url)).GetArrayLength());
        // Rango sin nada propio: vacío (no se filtra lo ajeno).
        Assert.Equal(0, (await C("k-cuota").GetFromJsonAsync<JsonElement>(url)).GetArrayLength());
    }

    private sealed class RegistroContador : IRegistroEmisiones
    {
        private readonly RegistroEmisionesMemoria _r = new();
        public int Listados;
        public RegistroEmision? Obtener(string clave) => _r.Obtener(clave);
        public void Guardar(RegistroEmision r) => _r.Guardar(r);
        public void Eliminar(string clave) => _r.Eliminar(clave);
        public IEnumerable<RegistroEmision> Listar() { Interlocked.Increment(ref Listados); return _r.Listar(); }
    }

    [Fact]
    public void EsDelCliente_usa_indice_y_no_recorre_el_registro_en_cada_llamada()
    {
        var reg = new RegistroContador();
        for (var i = 1; i <= 50; i++)
            reg.Guardar(new RegistroEmision { Clave = "k" + i, PtoVta = 1, CbteTipo = 6, CbteNro = i, Estado = EstadoEmision.Autorizado, Cliente = i % 2 == 0 ? "par" : "impar" });
        reg.Guardar(new RegistroEmision { Clave = "pend", PtoVta = 1, CbteTipo = 6, CbteNro = 99, Estado = EstadoEmision.Pendiente, Cliente = "par" });
        var q = new ConsultasService(reg, new Arca.Facturacion.Core.Simulacion.WsfeSimulador(TimeProvider.System));
        for (var vuelta = 0; vuelta < 20; vuelta++)
            for (var i = 1; i <= 50; i++)
            {
                Assert.Equal(i % 2 == 0, q.EsDelCliente(1, 6, i, "par"));
                Assert.Equal(i % 2 != 0, q.EsDelCliente(1, 6, i, "impar"));
            }
        Assert.True(reg.Listados <= 2, $"Listar se llamó {reg.Listados} veces");
        Assert.False(q.EsDelCliente(1, 6, 99, "par")); // pendiente: no cuenta
        // Una emisión nueva se ve aunque el índice ya esté construido.
        reg.Guardar(new RegistroEmision { Clave = "nuevo", PtoVta = 1, CbteTipo = 6, CbteNro = 51, Estado = EstadoEmision.Autorizado, Cliente = "impar" });
        Assert.True(q.EsDelCliente(1, 6, 51, "impar"));
        Assert.False(q.EsDelCliente(1, 6, 51, "par"));
    }

    // ---------------------------------------------------------------- 3: cuota

    [Fact]
    public async Task Lote_cortado_por_excepcion_cuenta_lo_ya_emitido()
    {
        var c = C("k-cuota");
        var cuotas = _f.Services.GetRequiredService<CuotasEmision>();
        var antes = cuotas.Usadas("cuota");
        var lote = new
        {
            items = new object[]
            {
                new { idempotencyKey = "lc-1", ptoVta = 72, cbteTipo = 6, comprobante = Detalle() },
                new { idempotencyKey = "boom-2", ptoVta = 72, cbteTipo = 6, comprobante = Detalle() },
                new { idempotencyKey = "lc-3", ptoVta = 72, cbteTipo = 6, comprobante = Detalle() }
            }
        };
        var r = await Post(c, "/api/v1/comprobantes/lote", lote);
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        // El primer ítem se emitió antes del corte: debe contar (antes se devolvía toda la reserva).
        Assert.Equal(antes + 1, cuotas.Usadas("cuota"));
    }

    [Fact]
    public void Devolver_usa_el_dia_de_la_reserva()
    {
        // 23:59 hora argentina (UTC-3) => 02:59 UTC del día siguiente.
        var reloj = new FakeTimeProvider(new DateTimeOffset(2026, 3, 10, 2, 59, 0, TimeSpan.Zero));
        var cuotas = new CuotasEmision(new ComprobanteValidator(reloj));
        Assert.True(cuotas.TryReservar("x", 5, 2, out var dia));
        Assert.Equal("20260309", dia);
        reloj.Advance(TimeSpan.FromMinutes(2)); // ya es el 10/03 en Argentina
        cuotas.Sumar("x", 1);
        cuotas.Devolver("x", 2, dia); // la devolución del día anterior no descuenta lo de hoy
        Assert.Equal(1, cuotas.Usadas("x"));
        // Misma jornada: sí descuenta.
        Assert.True(cuotas.TryReservar("x", 5, 3, out var hoy));
        cuotas.Devolver("x", 3, hoy);
        Assert.Equal(1, cuotas.Usadas("x"));
    }

    // ---------------------------------------------------------------- 4: /metrics y /health/ready

    [Fact]
    public async Task Metricas_y_salud_aplican_todas_las_validaciones_del_cliente()
    {
        Assert.Equal(HttpStatusCode.OK, (await C("k-adm").GetAsync("/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await C(null).GetAsync("/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await C("k-a").GetAsync("/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await C("k-adm-vencido").GetAsync("/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await C("k-adm-ip").GetAsync("/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await C("k-adm-firma").GetAsync("/metrics")).StatusCode); // sin firma
        // Bearer (Prometheus authorization.credentials_file).
        var bearer = _f.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "k-adm");
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/metrics")).StatusCode);

        static async Task<bool> ConDetalle(HttpClient c)
        {
            var r = await c.GetAsync("/health/ready");
            var j = await r.Content.ReadFromJsonAsync<JsonElement>();
            return j.GetProperty("chequeos").EnumerateObject().First().Value.ValueKind == JsonValueKind.Object;
        }
        Assert.True(await ConDetalle(C("k-adm")));
        Assert.False(await ConDetalle(C("k-adm-vencido")));
        Assert.False(await ConDetalle(C("k-adm-ip")));
        Assert.False(await ConDetalle(C("k-adm-firma")));
        Assert.False(await ConDetalle(C("k-a")));
    }

    // ---------------------------------------------------------------- 5: proxies y fuerza bruta

    [Fact]
    public void Redes_confiables_CIDR_se_agregan_a_KnownNetworks()
    {
        var o = _f.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>().Value;
        Assert.Contains(o.KnownNetworks, n => n.Prefix.Equals(IPAddress.Parse("10.0.0.0")) && n.PrefixLength == 8);
        Assert.Contains(o.KnownNetworks, n => n.Prefix.Equals(IPAddress.Parse("fd00::")) && n.PrefixLength == 8);

        var vacia = new ConfigurationBuilder().Build();
        Assert.Contains(RedesConfiables.Advertencias(vacia), a => a.Contains("No hay proxies confiables"));
        var mala = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Api:RedesConfiables:0"] = "10.0.0.0/99", ["Api:RedesConfiables:1"] = "192.168.0.0/16", ["Api:ProxiesConfiables:0"] = "172.16.0.0/12"
        }).Build();
        Assert.Single(RedesConfiables.Leer(mala));
        var adv = RedesConfiables.Advertencias(mala).ToList();
        Assert.Contains(adv, a => a.Contains("CIDR inválida"));
        Assert.Contains(adv, a => a.Contains("no es una IP"));
        Assert.DoesNotContain(adv, a => a.Contains("No hay proxies confiables"));
    }

    [Fact]
    public async Task Fuerza_bruta_ignora_solicitudes_sin_clave_y_el_exito_no_reinicia()
    {
        using var f = new RevisionFactory();
        var sin = f.CreateClient();
        for (var i = 0; i < 6; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await sin.GetAsync("/api/v1/catalogos")).StatusCode);
        var bueno = f.CreateClient();
        bueno.DefaultRequestHeaders.Add("X-Api-Key", "k-a");
        Assert.Equal(HttpStatusCode.OK, (await bueno.GetAsync("/api/v1/catalogos")).StatusCode); // no quedó bloqueada

        var mala = f.CreateClient();
        mala.DefaultRequestHeaders.Add("X-Api-Key", "mala");
        await mala.GetAsync("/api/v1/catalogos");
        await mala.GetAsync("/api/v1/catalogos");
        Assert.Equal(HttpStatusCode.OK, (await bueno.GetAsync("/api/v1/catalogos")).StatusCode); // un acierto no borra los fallos
        await mala.GetAsync("/api/v1/catalogos"); // tercer fallo dentro de la ventana => bloqueo
        Assert.Equal((HttpStatusCode)429, (await bueno.GetAsync("/api/v1/catalogos")).StatusCode);
    }

    // ---------------------------------------------------------------- 6: canonicalización de la firma

    /// <summary>Servidor Kestrel real (TestServer no informa RawTarget) que sólo verifica la firma: 404 si es válida, 401 si no.</summary>
    private static async Task<WebApplication> ServidorFirmas()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        var app = b.Build();
        app.Run(async ctx =>
        {
            var error = await FirmaSolicitudes.VerificarAsync(ctx, "s3cr3t", DateTimeOffset.UtcNow);
            ctx.Response.StatusCode = error == null ? 404 : 401;
            await ctx.Response.WriteAsJsonAsync(new { title = error ?? "no existe" });
        });
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Firma_con_caracteres_reservados_en_la_ruta_coincide_SDK_y_servicio()
    {
        await using var app = await ServidorFirmas();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First() + "/") };
        var sdk = new ClienteFacturacion(http, "k", reintentos: 0, secretoFirma: "s3cr3t");
        // ':' y espacio: el SDK los envía como %3A y %20. Antes el servicio firmaba Request.Path decodificado y
        // re-codificado (':' sin codificar) => 401. Ahora firma el destino crudo => la firma valida (404: no existe).
        Assert.Null(await sdk.EstadoEmisionAsync("clave:con espacio"));
        Assert.Null(await sdk.EstadoEmisionAsync("a/b?c=d&e ñ"));

        // Prefijo en BaseAddress (servicio publicado bajo /arca/): se firma la ruta completa enviada.
        using var http2 = new HttpClient { BaseAddress = new Uri(app.Urls.First() + "/arca/") };
        Assert.Null(await new ClienteFacturacion(http2, "k", reintentos: 0, secretoFirma: "s3cr3t").EstadoEmisionAsync("x:y z"));

        // Firma manual del destino exacto, con query (como lo hace el ejemplo PHP con rawurlencode).
        var destino = "/api/v1/comprobantes/emisiones/x%3Ay%20z?origen=a%3Ab";
        async Task<HttpStatusCode> Enviar(string firmado)
        {
            var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var req = new HttpRequestMessage(HttpMethod.Get, destino);
            req.Headers.Add("X-Firma-Tiempo", t);
            req.Headers.Add("X-Firma", ClienteFacturacion.Firmar("s3cr3t", t, "GET", firmado, Array.Empty<byte>()));
            return (await http.SendAsync(req)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.NotFound, await Enviar(destino));
        // Firmar una forma decodificada/normalizada distinta de la enviada no es válido.
        Assert.Equal(HttpStatusCode.Unauthorized, await Enviar("/api/v1/comprobantes/emisiones/x:y%20z?origen=a%3Ab"));
    }

    [Fact]
    public void DestinoCrudo_usa_RawTarget_y_soporta_forma_absoluta()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/a:b c";
        ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget = "/api/a%3Ab%20c?q=1";
        Assert.Equal("/api/a%3Ab%20c?q=1", FirmaSolicitudes.DestinoCrudo(ctx));
        ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget = "http://host:8080/api/a%3Ab?q=1";
        Assert.Equal("/api/a%3Ab?q=1", FirmaSolicitudes.DestinoCrudo(ctx));
    }

    // ---------------------------------------------------------------- 7: webhook

    private sealed class HandlerQueFalla : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("falla inesperada del handler");
    }

    private sealed class FabricaHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HandlerQueFalla());
    }

    [Fact]
    public void Webhook_descarta_URLs_invalidas()
    {
        var urls = WebhookNotificador.UrlsValidas(new[] { "ftp://x/y", "no es url", "/relativa", "mailto:a@b.c", " https://ok.example/h ", null, "http://otro.example/" });
        Assert.Equal(new[] { "https://ok.example/h", "http://otro.example/" }, urls);
        var opt = Options.Create(new ArcaOptions { Cuit = Util.CuitEmisor, CertificadoPath = "x", WebhookUrl = "file:///etc/passwd", WebhookUrls = new[] { "nada" } });
        var w = new WebhookNotificador(new FabricaHttp(), opt, NullLogger<WebhookNotificador>.Instance, TimeProvider.System);
        Assert.False(w.Habilitado);
    }

    [Fact]
    public async Task Webhook_sobrevive_a_excepciones_inesperadas()
    {
        var opt = Options.Create(new ArcaOptions { Cuit = Util.CuitEmisor, CertificadoPath = "x", WebhookUrl = "https://receptor.example/h", WebhookEsperaFactor = 0 });
        var w = new WebhookNotificador(new FabricaHttp(), opt, NullLogger<WebhookNotificador>.Instance, TimeProvider.System);
        using var cts = new CancellationTokenSource();
        await w.StartAsync(cts.Token);
        try
        {
            Assert.True(w.Probar());
            Assert.True(w.Probar());
            for (var i = 0; i < 100 && w.Entregas(10).Count < 2; i++) await Task.Delay(20);
            var e = w.Entregas(10);
            Assert.Equal(2, e.Count);
            Assert.All(e, x => { Assert.False(x.Exitosa); Assert.Equal("InvalidOperationException", x.Detalle); Assert.Equal(3, x.Intentos); });
            Assert.False(w.ExecuteTask!.IsCompleted); // el servicio sigue vivo
        }
        finally { await w.StopAsync(CancellationToken.None); }
    }

    // ---------------------------------------------------------------- 8 y 12: despliegue

    private static string Raiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Arca.Facturacion.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Imagen_define_rutas_de_datos_absolutas_bajo_data()
    {
        var docker = File.ReadAllText(Path.Combine(Raiz(), "Dockerfile"));
        foreach (var v in new[] { "Arca__PlantillasPath=/data/plantillas", "Arca__ReceptoresPath=/data/receptores", "Arca__SuscripcionesPath=/data/suscripciones",
                     "Arca__AuditoriaPath=/data/", "Arca__CacheTicketPath=/data/", "Arca__RegistroEmisionesPath=/data/" })
            Assert.Contains(v, docker);
        // Toda ruta *Path de appsettings.json que sea relativa debe estar sobrescrita en la imagen.
        var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(Raiz(), "src", "Arca.Facturacion.Api", "appsettings.json"))).RootElement.GetProperty("Arca");
        foreach (var p in cfg.EnumerateObject().Where(p => p.Name.EndsWith("Path", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.String
                     && !Path.IsPathRooted(p.Value.GetString()!)))
            Assert.Contains($"Arca__{p.Name}=/", docker);
    }

    [Fact]
    public void K8s_no_scrapea_metrics_por_anotacion_sin_credenciales()
    {
        var yaml = File.ReadAllText(Path.Combine(Raiz(), "deploy", "k8s", "arca-facturacion.yaml"));
        Assert.Contains("prometheus.io/scrape: \"false\"", yaml);
        Assert.Contains("credentials_file", yaml);
        Assert.DoesNotContain("MetricasPublicas, value: \"true\"", yaml);
    }

    // ---------------------------------------------------------------- 9 y 11: firma

    [Theory]
    [InlineData("99999999999999999")]
    [InlineData("-99999999999999999")]
    [InlineData("253402300800")]
    [InlineData("+1700000000")]
    public async Task Marca_de_tiempo_fuera_de_rango_es_401_no_500(string tiempo)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalogos");
        req.Headers.Add("X-Api-Key", "k-firma");
        req.Headers.Add("X-Firma-Tiempo", tiempo);
        req.Headers.Add("X-Firma", new string('a', 64));
        var r = await _f.CreateClient().SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("Marca de tiempo", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cache_anti_replay_se_purga_y_sigue_detectando_replays()
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var firma = ClienteFacturacion.Firmar("s3cr3t", t, "GET", "/api/v1/catalogos", Array.Empty<byte>());
        HttpRequestMessage Req()
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalogos");
            req.Headers.Add("X-Api-Key", "k-firma");
            req.Headers.Add("X-Firma-Tiempo", t);
            req.Headers.Add("X-Firma", firma);
            return req;
        }
        var http = _f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req())).StatusCode);
        var r2 = await http.SendAsync(Req());
        Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);
        Assert.Contains("replay", await r2.Content.ReadAsStringAsync());
        Assert.True(FirmaSolicitudes.FirmasRecordadas >= 1);
        // Purga de entradas vencidas (fuera de 2x la tolerancia).
        FirmaSolicitudes.Purgar(DateTimeOffset.UtcNow + FirmaSolicitudes.Tolerancia * 3);
        Assert.Equal(0, FirmaSolicitudes.FirmasRecordadas);
    }
}
