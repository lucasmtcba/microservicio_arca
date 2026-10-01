using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Arca.Facturacion.Api.Infraestructura;
using Arca.Facturacion.Cliente;
using Arca.Facturacion.Core;
using Arca.Facturacion.Core.Configuracion;
using Arca.Facturacion.Core.Resiliencia;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Arca.Facturacion.Tests;

public class CalculadoraAvanzadaTests
{
    [Fact]
    public void Cantidad_por_precio_con_bonificacion()
    {
        var d = CalculadoraImportes.Calcular(new SolicitudCalculo
        {
            CbteTipo = 6,
            Lineas = { new LineaImporte { Cantidad = 3, PrecioUnitario = 100, BonificacionPorcentaje = 10, AlicuotaIvaId = 5 } }
        });
        Assert.Equal(270m, d.ImpNeto);
        Assert.Equal(56.7m, d.ImpIVA);
        Assert.Equal(326.7m, d.ImpTotal);
    }

    [Fact]
    public void Precios_con_IVA_incluido_se_desglosan()
    {
        var d = CalculadoraImportes.Calcular(new SolicitudCalculo
        {
            CbteTipo = 6, PreciosConIvaIncluido = true,
            Lineas = { new LineaImporte { Cantidad = 2, PrecioUnitario = 1210, AlicuotaIvaId = 5 } }
        });
        Assert.Equal(2000m, d.ImpNeto);
        Assert.Equal(420m, d.ImpIVA);
        Assert.Equal(2420m, d.ImpTotal);
    }

    [Fact]
    public void IVA_incluido_en_clase_C_no_desglosa()
    {
        var d = CalculadoraImportes.Calcular(new SolicitudCalculo
        {
            CbteTipo = 11, PreciosConIvaIncluido = true, Lineas = { new LineaImporte { PrecioUnitario = 1210 } }
        });
        Assert.Equal(1210m, d.ImpTotal);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, -1, 0)]
    [InlineData(1, 1, 120)]
    public void Valida_cantidad_precio_y_bonificacion(decimal cant, decimal precio, decimal bonif)
        => Assert.Throws<ValidacionException>(() => CalculadoraImportes.Calcular(new SolicitudCalculo
            { CbteTipo = 6, Lineas = { new LineaImporte { Cantidad = cant, PrecioUnitario = precio, BonificacionPorcentaje = bonif } } }));
}

public class FormatoYCatalogosTests
{
    [Fact]
    public void Numero_y_descripcion()
    {
        Assert.Equal("00003-00000042", FormatoComprobante.Numero(3, 42));
        Assert.Equal("Factura B 00003-00000042", FormatoComprobante.Descripcion(3, 6, 42));
        Assert.Equal("Comprobante 999 00001-00000001", FormatoComprobante.Descripcion(1, 999, 1));
        var r = new ResultadoEmision { PtoVta = 1, CbteTipo = 13, CbteNro = 7 };
        Assert.Equal("Nota de Crédito C 00001-00000007", r.Descripcion);
    }

    [Fact]
    public void Catalogos_cubren_los_tipos_validados()
    {
        foreach (var t in TiposComprobante.ClaseA.Concat(TiposComprobante.ClaseB).Concat(TiposComprobante.ClaseC).Concat(TiposComprobante.ClaseM))
            Assert.True(Catalogos.TiposComprobante.ContainsKey(t), $"Falta el tipo {t}");
        foreach (var c in ComprobanteValidator.CondicionesIva) Assert.True(Catalogos.CondicionesIva.ContainsKey(c));
    }
}

public class Validacion2Tests
{
    private static readonly ComprobanteValidator V = new(TimeProvider.System);
    private static readonly DateOnly Hoy = V.HoyArgentina();

    [Fact]
    public void DNI_fuera_de_rango()
    {
        var c = Util.FacturaBValida(Hoy); c.DocTipo = 96; c.DocNro = 123456789;
        Assert.Contains(V.Validar(1, 6, c), e => e.Contains("DNI"));
        c.DocNro = 30111222;
        Assert.DoesNotContain(V.Validar(1, 6, c), e => e.Contains("DNI"));
    }

    [Fact]
    public void Responsable_inscripto_requiere_CUIT()
    {
        var c = Util.FacturaBValida(Hoy); c.CondicionIVAReceptorId = 1; c.DocTipo = 96; c.DocNro = 30111222;
        Assert.Contains(V.Validar(1, 6, c), e => e.Contains("Responsable Inscripto"));
    }

    [Fact]
    public void Vencimiento_de_pago_anterior_a_la_fecha()
    {
        var c = Util.FacturaAValida(Hoy); c.FchVtoPago = Hoy.AddDays(-1).ToString("yyyyMMdd");
        Assert.Contains(V.Validar(1, 1, c), e => e.Contains("FchVtoPago no puede ser anterior"));
    }

    [Fact]
    public void Normalizacion_de_codigos()
    {
        var d = Util.FacturaBValida(Hoy); d.MonId = " pes "; d.CbteFch = " " + d.CbteFch + " ";
        var n = FacturacionService.Normalizar(FacturacionService.Clonar(d));
        Assert.Equal("PES", n.MonId);
        Assert.Equal(Hoy.ToString("yyyyMMdd"), n.CbteFch);
        Assert.Empty(V.Validar(1, 6, n));
    }
}

public class RegistroIndiceTests
{
    [Fact]
    public void Indice_se_reconstruye_al_reabrir_y_limpia_temporales()
    {
        var dir = Path.Combine(Path.GetTempPath(), "idx_" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = new RegistroEmisionesArchivo(dir);
            for (var i = 0; i < 20; i++) r.Guardar(new RegistroEmision { Clave = "c:" + i, Hash = "h", CbteNro = i, Estado = EstadoEmision.Autorizado });
            File.WriteAllText(Path.Combine(dir, "x.json.abc.tmp"), "basura");
            var r2 = new RegistroEmisionesArchivo(dir);
            Assert.Equal(20, r2.Cantidad);
            Assert.Equal(7, r2.Obtener("c:7")!.CbteNro);
            Assert.Equal(RegistroEmisionesArchivo.VersionEsquema, r2.Obtener("c:7")!.Version);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            if (!OperatingSystem.IsWindows())
                Assert.All(Directory.GetFiles(dir, "*.json"), f => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f)));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class Resiliencia2Tests
{
    private sealed class WsfeLento : WsfeBase
    {
        public override async Task<ComprobanteConsultado?> FECompConsultarAsync(int pv, int t, long n, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return null;
        }
    }

    [Fact]
    public async Task Timeout_de_lectura_se_convierte_en_falla_de_comunicacion()
    {
        var opt = Util.Opciones(o => { o.TimeoutLecturaSegundos = 1; });
        var r = new WsfeClienteResiliente(new WsfeLento(), new MemoryCache(new MemoryCacheOptions()), new CircuitBreaker(5, TimeSpan.FromSeconds(1), TimeProvider.System),
            opt, TimeProvider.System, NullLogger<WsfeClienteResiliente>.Instance, Array.Empty<TimeSpan>());
        var ex = await Assert.ThrowsAsync<ArcaComunicacionException>(() => r.FECompConsultarAsync(1, 6, 1, default));
        Assert.Contains("Timeout", ex.Message);
        Assert.Equal(0, r.LlamadasEnCurso);
    }

    private sealed class WsfeContadorConcurrencia : WsfeBase
    {
        public int Actual, Maximo;
        public override async Task<ComprobanteConsultado?> FECompConsultarAsync(int pv, int t, long n, CancellationToken ct)
        {
            var a = Interlocked.Increment(ref Actual);
            lock (this) Maximo = Math.Max(Maximo, a);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref Actual);
            return null;
        }
    }

    [Fact]
    public async Task Bulkhead_limita_llamadas_simultaneas()
    {
        var inner = new WsfeContadorConcurrencia();
        var r = new WsfeClienteResiliente(inner, new MemoryCache(new MemoryCacheOptions()), new CircuitBreaker(5, TimeSpan.FromSeconds(1), TimeProvider.System),
            Util.Opciones(o => o.MaxLlamadasConcurrentesArca = 3), TimeProvider.System, NullLogger<WsfeClienteResiliente>.Instance);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => r.FECompConsultarAsync(1, 6, i, default)));
        Assert.True(inner.Maximo <= 3, $"Máximo observado {inner.Maximo}");
    }
}

public class PlantillasYAuditoriaTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Plantillas_aisladas_por_cliente(bool enDisco)
    {
        var dir = enDisco ? Path.Combine(Path.GetTempPath(), "pl_" + Guid.NewGuid().ToString("N")) : null;
        try
        {
            var repo = new RepositorioPlantillas(dir);
            repo.Guardar("erp", new PlantillaComprobante { Id = "abono-mensual", PtoVta = 1, CbteTipo = 6, Comprobante = new() { Concepto = 2 } });
            Assert.Single(repo.Listar("erp"));
            Assert.Empty(repo.Listar("otro"));
            Assert.Equal(2, repo.Obtener("erp", "abono-mensual")!.Comprobante.Concepto);
            Assert.Null(repo.Obtener("otro", "abono-mensual"));
            Assert.Null(repo.Obtener("erp", "../../etc"));
            Assert.Throws<ValidacionException>(() => repo.Guardar("erp", new PlantillaComprobante { Id = "../x" }));
            Assert.True(repo.Eliminar("erp", "abono-mensual"));
            Assert.False(repo.Eliminar("erp", "abono-mensual"));
        }
        finally { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Auditoria_en_archivo_se_agrega_y_consulta()
    {
        var f = Path.Combine(Path.GetTempPath(), "aud_" + Guid.NewGuid().ToString("N"), "auditoria.jsonl");
        try
        {
            var a = new AuditoriaArchivo(f);
            var t0 = DateTimeOffset.UtcNow;
            a.Registrar(new EntradaAuditoria { Fecha = t0, Cliente = "erp", Operacion = "POST x", Resultado = "A" });
            a.Registrar(new EntradaAuditoria { Fecha = t0.AddSeconds(1), Cliente = "ops", Operacion = "POST y" });
            a.Vaciar();
            Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(f)!, "auditoria-*").SelectMany(File.ReadAllLines).Count(l => l.Length > 0));
            Assert.True(a.Verificar().Integra);
            var s = new AuditoriaArchivo(f).Consultar(null, "erp", 10);
            Assert.Equal("POST x", s.Single().Operacion);
            Assert.Equal("POST y", a.Consultar(t0.AddMilliseconds(500), null, 10).Single().Operacion);
        }
        finally { Directory.Delete(Path.GetDirectoryName(f)!, true); }
    }
}

public class ConsultasYReportesTests
{
    private readonly WsfeSimulado _w = new();
    private readonly RegistroEmisionesMemoria _reg = new();
    private readonly DateOnly _hoy = new ComprobanteValidator(TimeProvider.System).HoyArgentina();
    private FacturacionService S() => new(_w, new ComprobanteValidator(TimeProvider.System), _reg, NullLogger<FacturacionService>.Instance, TimeProvider.System, new[] { TimeSpan.Zero });

    private async Task<ConsultasService> ConDatosAsync()
    {
        var s = S();
        await s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "a", "c1", default);
        await s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "b", "c2", default);
        var nc = Util.FacturaBValida(_hoy); nc.CbtesAsoc = new() { new ComprobanteAsociado { Tipo = 6, PtoVta = 1, Nro = 1 } };
        await s.EmitirAsync(1, 8, nc, "c", "c1", default);
        return new ConsultasService(_reg, _w);
    }

    [Fact]
    public async Task Busqueda_paginada_y_filtros()
    {
        var q = await ConDatosAsync();
        var p = q.Buscar(new FiltroEmisiones { TamanoPagina = 2 });
        Assert.Equal(3, p.Total);
        Assert.Equal(2, p.Items.Count);
        Assert.Equal(2, q.Buscar(new FiltroEmisiones { Cliente = "c1" }).Total);
        Assert.Equal(1, q.Buscar(new FiltroEmisiones { CbteTipo = 8 }).Total);
    }

    [Fact]
    public async Task Resumen_IVA_resta_notas_de_credito()
    {
        var q = await ConDatosAsync();
        var d = _hoy.ToString("yyyyMMdd");
        var r = q.ResumenIva(d, d);
        Assert.Equal(3, r.Comprobantes);
        Assert.Equal(100m, r.Neto);   // 100 + 100 - 100
        Assert.Equal(21m, r.Iva);
        Assert.Equal(100m, r.Alicuotas.Single().BaseImponible);
    }

    [Fact]
    public async Task Exportacion_CSV()
    {
        var q = await ConDatosAsync();
        var csv = q.ExportarCsv(null, null);
        var lineas = csv.TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lineas.Length);
        Assert.StartsWith("Fecha;Tipo;", lineas[0]);
        Assert.Contains(";Nota de Crédito B;", csv);
        Assert.Contains(";121.00;", csv);
    }

    [Fact]
    public async Task Rango_y_auditoria_de_numeracion()
    {
        var q = await ConDatosAsync();
        Assert.Equal(2, (await q.ConsultarRangoAsync(1, 6, 1, 10, default)).Count);
        await Assert.ThrowsAsync<ValidacionException>(() => q.ConsultarRangoAsync(1, 6, 1, 500, default));
        // Otro sistema emitió el 3 en el mismo punto de venta.
        _w.Ultimos[(1, 6)] = 3;
        _w.Emitidos[(1, 6, 3)] = new ComprobanteNumerado { DocTipo = 99 };
        Assert.Equal(new long[] { 3 }, await q.AuditarNumeracionAsync(1, 6, 1, default));
    }

    [Fact]
    public async Task Nota_de_credito_parcial_y_duplicado()
    {
        var s = S();
        await s.EmitirAsync(5, 6, Util.FacturaBValida(_hoy), "orig", "c", default);
        var nc = new NotaCreditoService(_w, s, new ComprobanteValidator(TimeProvider.System));
        var parcial = await nc.NotaCreditoParcialAsync(5, 6, 1, new SolicitudCalculo { Lineas = { new LineaImporte { Neto = 50 } } }, "p1", "c", default);
        Assert.Equal(8, parcial.CbteTipo);
        Assert.Equal(60.5m, _w.Emitidos[(5, 8, 1)].ImpTotal);
        await Assert.ThrowsAsync<ValidacionException>(() =>
            nc.NotaCreditoParcialAsync(5, 6, 1, new SolicitudCalculo { Lineas = { new LineaImporte { Neto = 500 } } }, "p2", "c", default));
        var dup = await nc.DuplicarAsync(5, 6, 1, "d1", "c", default);
        Assert.Equal(2, dup.CbteNro);
        Assert.Equal(121m, _w.Emitidos[(5, 6, 2)].ImpTotal);
    }
}

public class CaeaTests
{
    [Theory]
    [InlineData(202610, 1, 202610, 2)]
    [InlineData(202610, 2, 202611, 1)]
    [InlineData(202612, 2, 202701, 1)]
    public void Quincena_siguiente(int p, short o, int p2, short o2)
        => Assert.Equal(new QuincenaCaea(p2, o2), new QuincenaCaea(p, o).Siguiente());

    private sealed class WsfeCaea : WsfeBase
    {
        public int Solicitudes;
        public override Task<Caea?> FECAEAConsultarAsync(int p, short o, CancellationToken ct) => Task.FromResult<Caea?>(null);
        public override Task<Caea> FECAEASolicitarAsync(int p, short o, CancellationToken ct)
        {
            Solicitudes++;
            return Task.FromResult(new Caea { Codigo = "31234567890123", Periodo = p, Orden = o });
        }
    }

    [Fact]
    public async Task Solicita_el_proximo_CAEA_solo_dentro_de_la_ventana()
    {
        var w = new WsfeCaea();
        var temprano = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero));
        Assert.Null(await new CaeaService(w, new ComprobanteValidator(temprano)).AsegurarProximoAsync(default));
        var enVentana = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 15, 0, 0, TimeSpan.Zero));
        var c = await new CaeaService(w, new ComprobanteValidator(enVentana)).AsegurarProximoAsync(default);
        Assert.Equal(202610, c!.Periodo);
        Assert.Equal(2, c.Orden);
        Assert.Equal(1, w.Solicitudes);
    }
}

public class DiagnosticoTests
{
    [Fact]
    public void Advierte_riesgos_y_no_expone_secretos()
    {
        var o = new ArcaOptions { Cuit = Util.CuitEmisor, Ambiente = ArcaAmbiente.Real, CertificadoPassword = "secreto", WebhookUrl = "http://hook", WebhookSecreto = "s3" };
        var a = DiagnosticoConfiguracion.Advertencias(o, swaggerHabilitado: true, clientesConfigurados: 0);
        Assert.Contains(a, x => x.Contains("401"));
        Assert.Contains(a, x => x.Contains("Swagger"));
        Assert.Contains(a, x => x.Contains("HTTPS"));
        var json = JsonSerializer.Serialize(DiagnosticoConfiguracion.Resumen(o));
        Assert.DoesNotContain("secreto", json);
        Assert.DoesNotContain("s3", json);
    }
}

public sealed class ApiV12Factory : WebApplicationFactory<Program>
{
    public WsfeSimulado Wsfe { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseSetting("Arca:Cuit", Util.CuitEmisor);
        b.UseSetting("Arca:CertificadoPath", "no-usado.pfx");
        b.UseSetting("Arca:CacheTicketPath", "");
        b.UseSetting("Arca:AuditoriaPath", Path.Combine(Path.GetTempPath(), "v12aud_" + Guid.NewGuid().ToString("N"), "auditoria.jsonl"));
        b.UseSetting("Arca:RegistroEmisionesPath", "");
        b.UseSetting("Arca:RenovacionProactivaTicket", "false");
        b.UseSetting("Arca:PrecargarParametros", "false");
        b.UseSetting("Arca:ReconciliadorIntervaloSegundos", "0");
        b.UseSetting("Swagger:Habilitado", "true");
        void Cliente(int i, string nombre, string clave, params (string k, string v)[] extra)
        {
            b.UseSetting($"Seguridad:Clientes:{i}:Nombre", nombre);
            b.UseSetting($"Seguridad:Clientes:{i}:Claves:0", clave);
            foreach (var (k, v) in extra) b.UseSetting($"Seguridad:Clientes:{i}:{k}", v);
        }
        Cliente(0, "erp", "k-erp", ("Permisos:0", "emitir"), ("Permisos:1", "consultar"), ("Permisos:2", "admin"));
        Cliente(1, "cuota", "k-cuota", ("CuotaDiariaEmisiones", "1"));
        Cliente(2, "vencido", "k-vencido", ("VenceEl", "2020-01-01T00:00:00Z"));
        Cliente(3, "red", "k-red", ("IpsPermitidas:0", "10.0.0.0/8"));
        Cliente(4, "lento", "k-lento", ("RateLimitPorMinuto", "2"));
        b.ConfigureServices(s =>
        {
            s.RemoveAll<IWsfeClient>();
            s.AddSingleton<IWsfeClient>(Wsfe);
            s.RemoveAll<Arca.Facturacion.Core.Wsaa.ICmsFirmante>();
            s.AddSingleton<Arca.Facturacion.Core.Wsaa.ICmsFirmante>(new FirmanteNulo());
        });
    }
}

public class ApiV12Tests : IClassFixture<ApiV12Factory>
{
    private readonly ApiV12Factory _f;
    public ApiV12Tests(ApiV12Factory f) => _f = f;

    private HttpClient C(string clave)
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", clave);
        return c;
    }

    private static string Hoy() => new ComprobanteValidator(TimeProvider.System).HoyArgentina().ToString("yyyyMMdd");
    private static object Factura() => new
    {
        concepto = 1, docTipo = 99, docNro = 0, cbteFch = Hoy(), impTotal = 121, impNeto = 100, impIVA = 21,
        monId = "PES", monCotiz = 1, condicionIVAReceptorId = 5, iva = new[] { new { id = 5, baseImp = 100, importe = 21 } }
    };

    private static Task<HttpResponseMessage> Emitir(HttpClient c, string key, int pv)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/comprobantes") { Content = JsonContent.Create(new { ptoVta = pv, cbteTipo = 6, comprobante = Factura() }) };
        req.Headers.Add("Idempotency-Key", key);
        return c.SendAsync(req);
    }

    [Fact]
    public async Task ETag_y_304_en_catalogos()
    {
        var c = C("k-erp");
        var r1 = await c.GetAsync("/api/v1/catalogos");
        var etag = r1.Headers.ETag!;
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalogos");
        req.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.NotModified, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Respuestas_sin_nulos_y_con_numero_formateado()
    {
        var r = await Emitir(C("k-erp"), "n1", 40);
        var txt = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("null", txt);
        Assert.Contains("\"numeroFormateado\":\"00040-00000001\"", txt);
        Assert.Contains("\"descripcion\":\"Factura B 00040-00000001\"", txt);
    }

    [Fact]
    public async Task Solo_JSON_en_entradas()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/comprobantes/calcular") { Content = new StringContent("cbteTipo=6", Encoding.UTF8, "text/plain") };
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await C("k-erp").SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Cuota_diaria_por_cliente()
    {
        var c = C("k-cuota");
        Assert.Equal(HttpStatusCode.OK, (await Emitir(c, "q1", 41)).StatusCode);
        var r = await Emitir(c, "q2", 41);
        Assert.Equal((HttpStatusCode)429, r.StatusCode);
        Assert.Contains("Cuota", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Clave_vencida_e_IP_no_permitida()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await C("k-vencido").GetAsync("/api/v1/catalogos")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await C("k-red").GetAsync("/api/v1/catalogos")).StatusCode);
    }

    [Fact]
    public async Task Rate_limit_propio_del_cliente()
    {
        var c = C("k-lento");
        var codigos = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++) codigos.Add((await c.GetAsync("/api/v1/catalogos")).StatusCode);
        Assert.Equal((HttpStatusCode)429, codigos[2]);
    }

    [Fact]
    public async Task Plantillas_crud_y_emision()
    {
        var c = C("k-erp");
        var pl = new { ptoVta = 42, cbteTipo = 6, descripcion = "Abono", comprobante = Factura() };
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/v1/plantillas/abono", pl)).StatusCode);
        Assert.Single(await c.GetFromJsonAsync<JsonElement[]>("/api/v1/plantillas") ?? Array.Empty<JsonElement>());
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/plantillas/abono/emitir")
        { Content = JsonContent.Create(new { importes = new { lineas = new[] { new { neto = 200, alicuotaIvaId = 5 } } } }) };
        req.Headers.Add("Idempotency-Key", "pl-1");
        var r = await (await c.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("A", r.GetProperty("resultado").GetString());
        Assert.Equal(242m, _f.Wsfe.Emitidos[(42, 6, 1)].ImpTotal);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/v1/plantillas/abono")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/plantillas/abono")).StatusCode);
    }

    [Fact]
    public async Task Reportes_admin_y_auditoria()
    {
        var c = C("k-erp");
        await Emitir(c, "rep-1", 43);
        var csv = await c.GetAsync("/api/v1/admin/exportar.csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var d = Hoy();
        var iva = await c.GetFromJsonAsync<JsonElement>($"/api/v1/admin/resumen-iva?desde={d}&hasta={d}");
        Assert.True(iva.GetProperty("comprobantes").GetInt32() >= 1);
        var em = await c.GetFromJsonAsync<JsonElement>("/api/v1/admin/emisiones?ptoVta=43");
        Assert.Equal(1, em.GetProperty("total").GetInt32());
        var aud = await c.GetFromJsonAsync<JsonElement[]>("/api/v1/admin/auditoria?cliente=erp");
        Assert.Contains(aud!, e => e.TryGetProperty("comprobante", out var cb) && cb.GetString() == "Factura B 00043-00000001");
        var cfg = await c.GetFromJsonAsync<JsonElement>("/api/v1/admin/config");
        Assert.True(cfg.GetProperty("advertencias").GetArrayLength() > 0);
        var num = await c.GetFromJsonAsync<JsonElement>("/api/v1/admin/auditoria-numeracion/43/6?desde=1");
        Assert.Equal(0, num.GetProperty("cantidad").GetInt32());
    }

    [Fact]
    public async Task CAEA_vigente_rango_y_swagger_documentado()
    {
        var c = C("k-erp");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/caea/vigente")).StatusCode);
        await Emitir(c, "rg-1", 44);
        var rango = await c.GetFromJsonAsync<JsonElement[]>("/api/v1/comprobantes/44/6?desde=1&hasta=5");
        Assert.Single(rango!);
        var sw = await c.GetStringAsync("/swagger/v1/swagger.json");
        Assert.Contains("X-Correlation-Id", sw);
        Assert.Contains("\"example\"", sw);
    }

    private sealed class Falla503UnaVez : DelegatingHandler
    {
        public readonly List<string> Claves = new();
        private int _n;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.Headers.TryGetValues("Idempotency-Key", out var k)) lock (Claves) Claves.Add(k.First());
            if (Interlocked.Increment(ref _n) == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return resp;
            }
            return await base.SendAsync(r, ct);
        }
    }

    [Fact]
    public async Task SDK_reintenta_con_la_misma_clave_y_mapea_errores()
    {
        var falla = new Falla503UnaVez();
        var http = _f.CreateDefaultClient(falla);
        var sdk = new ClienteFacturacion(http, "k-erp", esperaMaxima: TimeSpan.FromMilliseconds(10));
        var hoy = new ComprobanteValidator(TimeProvider.System).HoyArgentina();
        var r = await sdk.EmitirAsync(45, 6, Util.FacturaBValida(hoy));
        Assert.True(r.Aprobado);
        Assert.Equal(2, falla.Claves.Count);
        Assert.Equal(falla.Claves[0], falla.Claves[1]);

        Assert.NotNull(await sdk.ConsultarAsync(45, 6, 1));
        Assert.Null(await sdk.ConsultarAsync(45, 6, 999));
        var mal = Util.FacturaBValida(hoy); mal.ImpTotal = 1;
        var ex = await Assert.ThrowsAsync<ErrorFacturacionException>(() => sdk.EmitirAsync(45, 6, mal));
        Assert.Equal(HttpStatusCode.BadRequest, ex.Estado);
        Assert.Contains(ex.Errores, e => e.Contains("ImpTotal"));
        Assert.Equal(121m, (await sdk.CalcularAsync(new SolicitudCalculo { CbteTipo = 6, Lineas = { new LineaImporte { Neto = 100 } } })).ImpTotal);
    }

    [Fact]
    public async Task Contrato_OpenAPI_publicado_coincide_con_el_servicio()
    {
        // Si falla: regenerar docs/openapi.json (ver README) tras cambiar la API.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Arca.Facturacion.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var publicado = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "docs", "openapi.json"))).RootElement.GetProperty("paths")
            .EnumerateObject().Select(p => p.Name + ":" + string.Join(",", p.Value.EnumerateObject().Select(m => m.Name).OrderBy(x => x))).OrderBy(x => x).ToList();
        var actual = JsonDocument.Parse(await C("k-erp").GetStringAsync("/swagger/v1/swagger.json")).RootElement.GetProperty("paths")
            .EnumerateObject().Select(p => p.Name + ":" + string.Join(",", p.Value.EnumerateObject().Select(m => m.Name).OrderBy(x => x))).OrderBy(x => x).ToList();
        Assert.Equal(publicado, actual);
    }
}
