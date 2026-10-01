using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Arca.Facturacion.Core.Configuracion;
using Arca.Facturacion.Core.Wsaa;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.Extensions.Options;

namespace Arca.Facturacion.Tests;

/// <summary>Handler HTTP falso: responde según el SOAPAction.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    public ConcurrentQueue<(string Action, string Body)> Peticiones { get; } = new();
    public Func<string, string, HttpResponseMessage> Responder { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.OK);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var action = request.Headers.TryGetValues("SOAPAction", out var v) ? v.First().Trim('"') : "";
        var body = await request.Content!.ReadAsStringAsync(ct);
        Peticiones.Enqueue((action, body));
        return Responder(action, body);
    }

    public static HttpResponseMessage Xml(string xml, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
}

public sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _h;
    public FakeHttpClientFactory(HttpMessageHandler h) => _h = h;
    public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
}

public sealed class FakeTicketProvider : ITicketAccesoProvider
{
    public int Invalidaciones;
    public int Obtenciones;
    public Task<TicketAcceso> ObtenerAsync(string servicio, CancellationToken ct)
    {
        Interlocked.Increment(ref Obtenciones);
        return Task.FromResult(new TicketAcceso("TOKEN" + Invalidaciones, "SIGN", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(12)));
    }
    public void Invalidar(string servicio) => Interlocked.Increment(ref Invalidaciones);
}

public static class Util
{
    public const string CuitEmisor = "20111111112";

    public static IOptions<ArcaOptions> Opciones(Action<ArcaOptions>? cfg = null)
    {
        var o = new ArcaOptions { Cuit = CuitEmisor, CertificadoPath = "x.pfx" };
        cfg?.Invoke(o);
        return Options.Create(o);
    }

    public static string Soap(string cuerpo) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>"
        + cuerpo + "</soap:Body></soap:Envelope>";

    public static string Wsfe(string metodo, string resultado) =>
        Soap($"<{metodo}Response xmlns=\"http://ar.gov.afip.dif.FEV1/\"><{metodo}Result>{resultado}</{metodo}Result></{metodo}Response>");

    public static X509Certificate2 CertificadoPrueba(DateTimeOffset? hasta = null)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=prueba, SERIALNUMBER=CUIT 20111111112", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var desde = (hasta ?? DateTimeOffset.UtcNow.AddYears(1)).AddYears(-2);
        var cert = req.CreateSelfSigned(desde, hasta ?? DateTimeOffset.UtcNow.AddYears(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    public static ComprobanteDetalle FacturaBValida(DateOnly hoy) => new()
    {
        Concepto = 1, DocTipo = 99, DocNro = 0, CbteFch = hoy.ToString("yyyyMMdd"),
        ImpNeto = 100m, ImpIVA = 21m, ImpTotal = 121m, CondicionIVAReceptorId = 5,
        Iva = new() { new AlicuotaIva { Id = 5, BaseImp = 100m, Importe = 21m } }
    };

    public static ComprobanteDetalle FacturaAValida(DateOnly hoy) => new()
    {
        Concepto = 2, DocTipo = 80, DocNro = 30500010912, CbteFch = hoy.ToString("yyyyMMdd"),
        FchServDesde = hoy.AddDays(-30).ToString("yyyyMMdd"), FchServHasta = hoy.ToString("yyyyMMdd"),
        FchVtoPago = hoy.AddDays(10).ToString("yyyyMMdd"),
        ImpNeto = 1000m, ImpIVA = 210m, ImpTrib = 30m, ImpTotal = 1240m, CondicionIVAReceptorId = 1,
        Iva = new() { new AlicuotaIva { Id = 5, BaseImp = 1000m, Importe = 210m } },
        Tributos = new() { new Tributo { Id = 7, Desc = "Percepción IIBB", BaseImp = 1000m, Alic = 3m, Importe = 30m } }
    };
}
