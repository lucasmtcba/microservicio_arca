using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using Arca.Facturacion.Api.Infraestructura;
using Arca.Facturacion.Core.Padron;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Validacion;
using Microsoft.Extensions.Time.Testing;
using MySqlConnector;
using Xunit;

namespace Arca.Facturacion.Tests;

/// <summary>Pruebas contra MySQL/MariaDB. Se omiten (pasan sin ejecutar) si no hay servidor en ARCA_TEST_MYSQL o localhost.</summary>
public class MySqlTests
{
    private static readonly string Cs = Environment.GetEnvironmentVariable("ARCA_TEST_MYSQL")
        ?? "Server=localhost;User ID=arca;Password=arca;Database=arca_test;Connection Timeout=3";

    private static bool Disponible()
    {
        try { using var c = new MySqlConnection(Cs); c.Open(); return true; }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException) { return false; }
    }

    private static string Tabla() => "t_" + Guid.NewGuid().ToString("N")[..12];

    private static RegistroEmision Reg(string clave, EstadoEmision e = EstadoEmision.Autorizado, long nro = 1) => new()
    {
        Clave = clave, Hash = "h", PtoVta = 1, CbteTipo = 6, CbteNro = nro, Estado = e, Creado = DateTimeOffset.UtcNow, Cliente = "erp"
    };

    [Fact]
    public void Guardar_obtener_listar_eliminar_y_pendientes()
    {
        if (!Disponible()) return;
        var r = new RegistroEmisionesMySql(Cs, tabla: Tabla());
        r.Guardar(Reg("erp:a"));
        r.Guardar(Reg("erp:b", EstadoEmision.Pendiente, 2));
        r.Guardar(Reg("erp:a", nro: 5)); // upsert
        Assert.Equal(5, r.Obtener("erp:a")!.CbteNro);
        Assert.Equal(2, r.Cantidad);
        Assert.Single(r.Pendientes());
        r.Eliminar("erp:b");
        Assert.Null(r.Obtener("erp:b"));
        Assert.True(r.Ping());
    }

    [Fact]
    public void Cifrado_y_filas_ilegibles_no_rompen_el_listado()
    {
        if (!Disponible()) return;
        var t = Tabla();
        var clave = new byte[32]; clave[0] = 1;
        new RegistroEmisionesMySql(Cs, new CifradorRegistro(clave), t).Guardar(Reg("erp:x"));
        var otra = new byte[32]; otra[0] = 2;
        var r = new RegistroEmisionesMySql(Cs, new CifradorRegistro(otra), t);
        Assert.Empty(r.Listar());
        Assert.Equal(1, r.UltimasIlegibles);
        Assert.Throws<InvalidOperationException>(() => r.Obtener("erp:x")); // nunca se trata como inexistente
    }

    [Fact]
    public void Migracion_desde_otro_registro_no_pisa_claves()
    {
        if (!Disponible()) return;
        var r = new RegistroEmisionesMySql(Cs, tabla: Tabla());
        r.Guardar(Reg("erp:1", nro: 99));
        var mem = new RegistroEmisionesMemoria();
        mem.Guardar(Reg("erp:1", nro: 1));
        mem.Guardar(Reg("erp:2", nro: 2));
        var (cop, omi) = r.MigrarDesde(mem);
        Assert.Equal((1, 1), (cop, omi));
        Assert.Equal(99, r.Obtener("erp:1")!.CbteNro);
    }

    [Fact]
    public void Tabla_con_nombre_peligroso_se_rechaza() =>
        Assert.Throws<ArgumentException>(() => new RegistroEmisionesMySql(Cs, tabla: "x`; DROP TABLE y"));

    [Fact]
    public async Task Get_lock_serializa_entre_instancias()
    {
        if (!Disponible()) return;
        var pref = "t" + Guid.NewGuid().ToString("N")[..8];
        var a = new BloqueoNumeracionMySql(Cs, TimeSpan.FromSeconds(10), pref);
        var b = new BloqueoNumeracionMySql(Cs, TimeSpan.FromSeconds(1), pref);
        await using (await a.AdquirirAsync(1, 6, default))
        {
            await Assert.ThrowsAsync<Arca.Facturacion.Core.ArcaComunicacionException>(() => b.AdquirirAsync(1, 6, default));
            await using (await b.AdquirirAsync(1, 1, default)) { } // otro tipo: no bloquea
        }
        await using (await b.AdquirirAsync(1, 6, default)) { } // liberado
    }
}

public class PadronTests
{
    [Theory]
    [InlineData(20111111112, true)]
    [InlineData(30500010912, true)]
    [InlineData(20111111113, false)]
    [InlineData(123, false)]
    public void Valida_digito_verificador(long cuit, bool ok) => Assert.Equal(ok, Cuit.EsValida(cuit.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Parseo_tolerante_de_getPersona()
    {
        var xml = XElement.Parse("""
            <r xmlns:ns2="http://a13.soap.ws.server.puc.sr/"><ns2:getPersonaResponse><personaReturn><persona>
              <idPersona>30500010912</idPersona><tipoPersona>JURIDICA</tipoPersona><estadoClave>ACTIVO</estadoClave>
              <razonSocial>EMPRESA SA</razonSocial>
              <domicilio><tipoDomicilio>LEGAL/REAL</tipoDomicilio><direccion>A 1</direccion></domicilio>
              <domicilio><tipoDomicilio>FISCAL</tipoDomicilio><direccion>B 2</direccion><localidad>CABA</localidad><codigoPostal>1000</codigoPostal></domicilio>
            </persona></personaReturn></ns2:getPersonaResponse></r>
            """);
        var c = PadronA13Client.Parsear(xml, 1)!;
        Assert.Equal(30500010912, c.Cuit);
        Assert.Equal("EMPRESA SA", c.Denominacion);
        Assert.True(c.Activo);
        Assert.Equal("B 2, CABA, 1000", c.DomicilioFiscal);
        Assert.Null(PadronA13Client.Parsear(XElement.Parse("<r/>"), 1));
    }

    [Fact]
    public void Denominacion_de_persona_fisica() =>
        Assert.Equal("PEREZ, JUAN", new Contribuyente { Apellido = "PEREZ", Nombre = "JUAN" }.Denominacion);

    private sealed class Contador : IPadronClient
    {
        public int N;
        public async Task<Contribuyente?> ObtenerAsync(long cuit, CancellationToken ct)
        {
            Interlocked.Increment(ref N);
            await Task.Delay(30, ct);
            return cuit == 1 ? null : new Contribuyente { Cuit = cuit };
        }
    }

    [Fact]
    public async Task Cache_single_flight_y_negativo_con_vencimiento()
    {
        var reloj = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var inner = new Contador();
        var p = new PadronCacheado(inner, reloj, TimeSpan.FromHours(1), TimeSpan.FromMinutes(5));
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => p.ObtenerAsync(20111111112, default)));
        Assert.Equal(1, inner.N);
        Assert.Null(await p.ObtenerAsync(1, default));
        Assert.Null(await p.ObtenerAsync(1, default));
        Assert.Equal(2, inner.N);
        reloj.Advance(TimeSpan.FromMinutes(6));
        await p.ObtenerAsync(1, default);
        await p.ObtenerAsync(20111111112, default);
        Assert.Equal(3, inner.N);
    }

    [Fact]
    public async Task Simulado_informa_inactivas_e_invalidas()
    {
        var s = new PadronSimulado();
        Assert.Null(await s.ObtenerAsync(20111111113, default));
        Assert.True((await s.ObtenerAsync(20111111112, default))!.Activo);
    }
}

public class CorreccionesV14Tests
{
    [Fact]
    public void Prefijo_de_cliente_no_colisiona_entre_nombres_parecidos() =>
        Assert.NotEqual(RepositorioJson<Receptor>.PrefijoCliente("acme-1"), RepositorioJson<Receptor>.PrefijoCliente("acme1"));

    [Fact]
    public void Importe_gigante_no_rompe_numero_a_letras() =>
        Assert.False(string.IsNullOrWhiteSpace(NumeroALetras.Importe(9_999_999_999_999m)));

    [Theory]
    [InlineData(80)]
    [InlineData(96)]
    [InlineData(30)]
    [InlineData(0)]
    public void Catalogo_de_documentos_acepta_tipos_validos(int t) => Assert.Contains(t, Catalogo.TiposDocumento);

    [Fact]
    public void Ipv6_se_agrupa_por_64() =>
        Assert.Equal(ParticionIp.De(IPAddress.Parse("2001:db8:1:2::1")), ParticionIp.De(IPAddress.Parse("2001:db8:1:2:ffff::9")));

    [Fact]
    public void Cuota_reserva_y_devuelve()
    {
        var q = new CuotasEmision(new ComprobanteValidator(TimeProvider.System));
        Assert.True(q.TryReservar("c", 3, 2));
        Assert.False(q.TryReservar("c", 3, 2));
        q.Devolver("c", 2);
        Assert.True(q.TryReservar("c", 3, 3));
    }

    [Fact]
    public void Importacion_rechaza_pendientes()
    {
        var reg = new RegistroEmisionesMemoria();
        var svc = new ReportesService(reg, null!, new ComprobanteValidator(TimeProvider.System));
        var pend = System.Text.Json.JsonSerializer.Serialize(new RegistroEmision { Clave = "a:b", Hash = "h", CbteNro = 1, Estado = EstadoEmision.Pendiente });
        Assert.Equal((0, 1), svc.Importar(pend));
    }
}

public class ApiPadronTests : IClassFixture<ApiV13Factory>
{
    private readonly ApiV13Factory _f;
    public ApiPadronTests(ApiV13Factory f) => _f = f;

    private HttpClient C()
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", "k13");
        return c;
    }

    [Fact]
    public async Task Consulta_valida_invalida_y_alta_en_agenda()
    {
        var c = C();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/contribuyentes/20111111113")).StatusCode);
        var ok = await c.GetFromJsonAsync<Contribuyente>("/api/v1/contribuyentes/20111111112");
        Assert.Equal(20111111112, ok!.Cuit);
        var r = await c.PostAsync("/api/v1/contribuyentes/20111111112/agenda?condicionIva=1", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var rec = await r.Content.ReadFromJsonAsync<Receptor>();
        Assert.Equal((80, 20111111112L), (rec!.DocTipo, rec.DocNro));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/contribuyentes/20111111112/agenda?condicionIva=0", null)).StatusCode);
    }

    [Fact]
    public async Task Sin_clave_es_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync("/api/v1/contribuyentes/20111111112")).StatusCode);
    }
}
