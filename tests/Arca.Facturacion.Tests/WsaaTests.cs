using System.Net;
using System.Security;
using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Xml.Linq;
using Arca.Facturacion.Core;
using Arca.Facturacion.Core.Wsaa;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Arca.Facturacion.Tests;

public class TraBuilderTests
{
    [Fact]
    public void Construye_TRA_con_servicio_y_ventana_de_tiempo()
    {
        var ahora = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var x = XElement.Parse(TraBuilder.Construir("wsfe", ahora));
        Assert.Equal("loginTicketRequest", x.Name.LocalName);
        Assert.Equal("wsfe", x.Element("service")!.Value);
        var gen = DateTimeOffset.Parse(x.Element("header")!.Element("generationTime")!.Value);
        var exp = DateTimeOffset.Parse(x.Element("header")!.Element("expirationTime")!.Value);
        Assert.True(gen < ahora && exp > ahora);
        Assert.Equal(ahora.ToUnixTimeSeconds().ToString(), x.Element("header")!.Element("uniqueId")!.Value);
    }

    [Theory, InlineData(""), InlineData("  ")]
    public void Servicio_vacio_falla(string s) => Assert.Throws<ArgumentException>(() => TraBuilder.Construir(s, DateTimeOffset.UtcNow));
}

public class CmsFirmanteTests
{
    [Fact]
    public void Firma_CMS_verificable_y_con_contenido_embebido()
    {
        using var f = new CertificadoCmsFirmante(Util.CertificadoPrueba());
        var contenido = Encoding.UTF8.GetBytes("<loginTicketRequest/>");
        var cms = new SignedCms();
        cms.Decode(f.Firmar(contenido));
        cms.CheckSignature(verifySignatureOnly: true);
        Assert.Equal(contenido, cms.ContentInfo.Content);
        Assert.Single(cms.Certificates);
    }

    [Fact]
    public void Rechaza_certificado_vencido()
    {
        var cert = Util.CertificadoPrueba(DateTimeOffset.UtcNow.AddDays(-1));
        Assert.Throws<WsaaException>(() => new CertificadoCmsFirmante(cert));
    }

    [Fact]
    public void Rechaza_archivo_inexistente() =>
        Assert.Throws<WsaaException>(() => CertificadoCmsFirmante.DesdeArchivo("/no/existe.pfx", null));
}

public class WsaaClientTests
{
    private sealed class FirmanteFalso : ICmsFirmante { public byte[] Firmar(byte[] c) => c; }

    private static string RespuestaLogin(string token = "T0K", string exp = "2026-09-30T21:00:00-03:00")
    {
        var ta = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><loginTicketResponse version=\"1.0\"><header><source>x</source><destination>y</destination>" +
                 $"<uniqueId>1</uniqueId><generationTime>2026-09-30T09:00:00-03:00</generationTime><expirationTime>{exp}</expirationTime></header>" +
                 $"<credentials><token>{token}</token><sign>S1GN</sign></credentials></loginTicketResponse>";
        return Util.Soap($"<loginCmsResponse xmlns=\"http://wsaa.view.sua.dvadac.desein.afip.gov\"><loginCmsReturn>{SecurityElement.Escape(ta)}</loginCmsReturn></loginCmsResponse>");
    }

    [Fact]
    public async Task LoginCms_devuelve_token_y_sign()
    {
        var h = new FakeHandler { Responder = (_, _) => FakeHandler.Xml(RespuestaLogin()) };
        var c = new WsaaClient(new FakeHttpClientFactory(h), new FirmanteFalso(), Util.Opciones(), TimeProvider.System);
        var ta = await c.LoginCmsAsync("wsfe", default);
        Assert.Equal("T0K", ta.Token);
        Assert.Equal("S1GN", ta.Sign);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 21, 0, 0, TimeSpan.FromHours(-3)), ta.ExpiraEn);
        var body = h.Peticiones.Single().Body;
        Assert.Contains("<in0>", body.Replace(" xmlns=\"http://wsaa.view.sua.dvadac.desein.afip.gov\"", ""));
    }

    [Fact]
    public async Task Fault_alreadyAuthenticated_da_mensaje_claro()
    {
        var h = new FakeHandler { Responder = (_, _) => FakeHandler.Xml(Util.Soap(
            "<soap:Fault><faultcode>ns1:coe.alreadyAuthenticated</faultcode><faultstring>El CEE ya posee un TA valido</faultstring></soap:Fault>"),
            HttpStatusCode.InternalServerError) };
        var c = new WsaaClient(new FakeHttpClientFactory(h), new FirmanteFalso(), Util.Opciones(), TimeProvider.System);
        var ex = await Assert.ThrowsAsync<WsaaException>(() => c.LoginCmsAsync("wsfe", default));
        Assert.Contains("CacheTicketPath", ex.Message);
    }

    [Fact]
    public void ParsearTicket_sin_token_falla() =>
        Assert.Throws<WsaaException>(() => WsaaClient.ParsearTicket("<loginTicketResponse><header><generationTime>2026-01-01T00:00:00Z</generationTime><expirationTime>2026-01-01T00:00:00Z</expirationTime></header><credentials><sign>x</sign></credentials></loginTicketResponse>"));
}

public class TicketAccesoProviderTests
{
    private sealed class WsaaContador : IWsaaClient
    {
        public int Llamadas;
        public TimeProvider Reloj = TimeProvider.System;
        public async Task<TicketAcceso> LoginCmsAsync(string servicio, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Llamadas);
            await Task.Delay(20, ct);
            var ahora = Reloj.GetUtcNow();
            return new TicketAcceso("tok" + n, "sig", ahora, ahora.AddHours(12));
        }
    }

    [Fact]
    public async Task Cachea_el_TA_y_hace_un_solo_login_con_concurrencia()
    {
        var reloj = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var w = new WsaaContador { Reloj = reloj };
        var p = new TicketAccesoProvider(w, Util.Opciones(), reloj, NullLogger<TicketAccesoProvider>.Instance);
        var tas = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => p.ObtenerAsync("wsfe", default)));
        Assert.Equal(1, w.Llamadas);
        Assert.All(tas, t => Assert.Equal("tok1", t.Token));
    }

    [Fact]
    public async Task Renueva_cuando_entra_en_margen_de_vencimiento()
    {
        var reloj = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var w = new WsaaContador { Reloj = reloj };
        var p = new TicketAccesoProvider(w, Util.Opciones(o => o.MargenRenovacionTicketMinutos = 10), reloj, NullLogger<TicketAccesoProvider>.Instance);
        await p.ObtenerAsync("wsfe", default);
        reloj.Advance(TimeSpan.FromHours(11).Add(TimeSpan.FromMinutes(51)));
        var ta = await p.ObtenerAsync("wsfe", default);
        Assert.Equal(2, w.Llamadas);
        Assert.Equal("tok2", ta.Token);
    }

    [Fact]
    public async Task Invalidar_fuerza_nuevo_login()
    {
        var w = new WsaaContador();
        var p = new TicketAccesoProvider(w, Util.Opciones(), TimeProvider.System, NullLogger<TicketAccesoProvider>.Instance);
        await p.ObtenerAsync("wsfe", default);
        p.Invalidar("wsfe");
        await p.ObtenerAsync("wsfe", default);
        Assert.Equal(2, w.Llamadas);
    }

    private sealed class WsaaYaAutenticado : IWsaaClient
    {
        public int Llamadas;
        public Task<TicketAcceso> LoginCmsAsync(string servicio, CancellationToken ct)
        {
            if (++Llamadas == 1) return Task.FromResult(new TicketAcceso("original", "s", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(12)));
            throw new WsaaException("ya autenticado", "ns1:coe.alreadyAuthenticated");
        }
    }

    [Fact]
    public async Task Invalidar_y_alreadyAuthenticated_conserva_el_TA_vigente()
    {
        var w = new WsaaYaAutenticado();
        var p = new TicketAccesoProvider(w, Util.Opciones(), TimeProvider.System, NullLogger<TicketAccesoProvider>.Instance);
        await p.ObtenerAsync("wsfe", default);
        p.Invalidar("wsfe");
        var ta = await p.ObtenerAsync("wsfe", default);
        Assert.Equal("original", ta.Token);
        Assert.Equal(2, w.Llamadas);
        await p.ObtenerAsync("wsfe", default);
        Assert.Equal(2, w.Llamadas); // ya no es sospechoso
    }

    [Fact]
    public async Task Persiste_en_disco_y_sobrevive_reinicio()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ta_" + Guid.NewGuid().ToString("N"));
        try
        {
            var opt = Util.Opciones(o => o.CacheTicketPath = dir);
            var w1 = new WsaaContador();
            await new TicketAccesoProvider(w1, opt, TimeProvider.System, NullLogger<TicketAccesoProvider>.Instance).ObtenerAsync("wsfe", default);
            var w2 = new WsaaContador();
            var ta = await new TicketAccesoProvider(w2, opt, TimeProvider.System, NullLogger<TicketAccesoProvider>.Instance).ObtenerAsync("wsfe", default);
            Assert.Equal(0, w2.Llamadas);
            Assert.Equal("tok1", ta.Token);
            var archivo = Directory.GetFiles(dir).Single();
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(archivo));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Archivo_corrupto_se_ignora()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ta_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var opt = Util.Opciones(o => o.CacheTicketPath = dir);
            File.WriteAllText(Path.Combine(dir, $"ta_{Util.CuitEmisor}_Homologacion_wsfe.json"), "{basura");
            var w = new WsaaContador();
            await new TicketAccesoProvider(w, opt, TimeProvider.System, NullLogger<TicketAccesoProvider>.Instance).ObtenerAsync("wsfe", default);
            Assert.Equal(1, w.Llamadas);
        }
        finally { Directory.Delete(dir, true); }
    }
}
