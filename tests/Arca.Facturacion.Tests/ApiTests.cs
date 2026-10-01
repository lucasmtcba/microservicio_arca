using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsaa;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Arca.Facturacion.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public WsfeSimulado Wsfe { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseSetting("Arca:Cuit", Util.CuitEmisor);
        b.UseSetting("Arca:CertificadoPath", "no-usado.pfx");
        b.UseSetting("Arca:CacheTicketPath", "");
        b.UseSetting("Arca:RegistroEmisionesPath", "");
        b.UseSetting("Arca:RenovacionProactivaTicket", "false");
        b.UseSetting("Arca:ReconciliadorIntervaloSegundos", "0");
        b.UseSetting("Swagger:Habilitado", "true");
        b.UseSetting("Seguridad:ApiKeys:0", "clave-de-prueba");
        b.ConfigureServices(s =>
        {
            s.RemoveAll<IWsfeClient>();
            s.AddSingleton<IWsfeClient>(Wsfe);
            s.RemoveAll<ICmsFirmante>();
            s.AddSingleton<ICmsFirmante>(new FirmanteNulo());
        });
    }
}

internal sealed class FirmanteNulo : ICmsFirmante { public byte[] Firmar(byte[] c) => c; }

public class ApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ApiTests(ApiFactory f) => _f = f;

    private HttpClient Cliente(bool conClave = true)
    {
        var c = _f.CreateClient();
        if (conClave) c.DefaultRequestHeaders.Add("X-Api-Key", "clave-de-prueba");
        return c;
    }

    private static string Hoy() => new ComprobanteValidator(TimeProvider.System).HoyArgentina().ToString("yyyyMMdd");

    [Fact]
    public async Task Sin_clave_devuelve_401()
    {
        var r = await Cliente(false).GetAsync("/api/v1/comprobantes/max-registros");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Clave_incorrecta_devuelve_401()
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", "otra");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/parametros/tipos-iva")).StatusCode);
    }

    [Fact]
    public async Task Health_y_swagger_son_publicos()
    {
        var c = Cliente(false);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
        var sw = await c.GetStringAsync("/swagger/v1/swagger.json");
        Assert.Contains("/api/v1/comprobantes", sw);
        Assert.Contains("X-Api-Key", sw);
    }

    [Fact]
    public async Task Emitir_devuelve_CAE()
    {
        var body = new
        {
            ptoVta = 7, cbteTipo = 6,
            comprobante = new { concepto = 1, docTipo = 99, docNro = 0, cbteFch = Hoy(), impTotal = 121, impNeto = 100, impIVA = 21,
                monId = "PES", monCotiz = 1, condicionIVAReceptorId = 5, iva = new[] { new { id = 5, baseImp = 100, importe = 21 } } }
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/comprobantes") { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var r = await Cliente().SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("A", j.GetProperty("resultado").GetString());
        Assert.Equal(14, j.GetProperty("cae").GetString()!.Length);
    }

    [Fact]
    public async Task Emitir_invalido_devuelve_400_con_errores()
    {
        var body = new { ptoVta = 7, cbteTipo = 6, comprobante = new { concepto = 1, docTipo = 99, docNro = 0, impTotal = 50, impNeto = 100, condicionIVAReceptorId = 5 } };
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/comprobantes") { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var r = await Cliente().SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType!.MediaType);
        Assert.Contains("ImpTotal", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Consultar_inexistente_devuelve_404()
        => Assert.Equal(HttpStatusCode.NotFound, (await Cliente().GetAsync("/api/v1/comprobantes/1/6/999999")).StatusCode);

    [Fact]
    public async Task Parametros_y_salud()
    {
        var c = Cliente();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/parametros/tipos-comprobante")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/salud/arca")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/parametros/condiciones-iva-receptor?claseCmp=Z")).StatusCode);
    }

    [Fact]
    public async Task Solicitar_cae_directo_deshabilitado_por_defecto()
    {
        var r = await Cliente().PostAsJsonAsync("/api/v1/comprobantes/cae", new { ptoVta = 1, cbteTipo = 6, comprobantes = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Emitir_sin_idempotency_key_devuelve_400()
    {
        var r = await Cliente().PostAsJsonAsync("/api/v1/comprobantes", new { ptoVta = 7, cbteTipo = 6, comprobante = new { concepto = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Idempotency-Key", await r.Content.ReadAsStringAsync());
    }
}
