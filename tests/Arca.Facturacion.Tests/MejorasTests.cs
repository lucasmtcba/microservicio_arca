using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Arca.Facturacion.Api.Infraestructura;
using Arca.Facturacion.Core;
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

/// <summary>WSFE simulado configurable para el decorador resiliente.</summary>
public sealed class WsfeContador : WsfeBase
{
    public int Llamadas;
    public Queue<Exception?> Fallas = new();
    private Task<T> Paso<T>(T v)
    {
        Interlocked.Increment(ref Llamadas);
        if (Fallas.Count > 0 && Fallas.Dequeue() is { } e) return Task.FromException<T>(e);
        return Task.FromResult(v);
    }
    public override Task<IReadOnlyList<ParametroItem>> FEParamGetTiposIvaAsync(CancellationToken ct) => Paso<IReadOnlyList<ParametroItem>>(new[] { new ParametroItem { Id = "5" } });
    public override Task<Cotizacion> FEParamGetCotizacionAsync(string m, string? f, CancellationToken ct) => Paso(new Cotizacion { MonId = m, MonCotiz = 1350.25m });
    public override Task<RespuestaCae> FECAESolicitarAsync(SolicitudCae s, CancellationToken ct) => Paso(new RespuestaCae());
    public override Task<ComprobanteConsultado?> FECompConsultarAsync(int pv, int t, long n, CancellationToken ct) => Paso<ComprobanteConsultado?>(null);
}

/// <summary>Base con todos los métodos lanzando, para sobreescribir sólo lo necesario.</summary>
public abstract class WsfeBase : IWsfeClient
{
    public virtual Task<EstadoServidores> FEDummyAsync(CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<RespuestaCae> FECAESolicitarAsync(SolicitudCae s, CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<UltimoComprobante> FECompUltimoAutorizadoAsync(int pv, int t, CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<ComprobanteConsultado?> FECompConsultarAsync(int pv, int t, long n, CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<int> FECompTotXRequestAsync(CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<Caea> FECAEASolicitarAsync(int p, short o, CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<Caea?> FECAEAConsultarAsync(int p, short o, CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<RespuestaCae> FECAEARegInformativoAsync(InformeCaea i, CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<CaeaSinMovimiento> FECAEASinMovimientoInformarAsync(int pv, string c, CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<CaeaSinMovimiento>> FECAEASinMovimientoConsultarAsync(string c, int pv, CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<ParametroItem>> FEParamGetTiposCbteAsync(CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<ParametroItem>> FEParamGetTiposConceptoAsync(CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<ParametroItem>> FEParamGetTiposDocAsync(CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<ParametroItem>> FEParamGetTiposIvaAsync(CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<ParametroItem>> FEParamGetTiposMonedasAsync(CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<ParametroItem>> FEParamGetTiposOpcionalAsync(CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<ParametroItem>> FEParamGetTiposTributosAsync(CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<ParametroItem>> FEParamGetTiposPaisesAsync(CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<ParametroItem>> FEParamGetPtosVentaAsync(CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<ParametroItem>> FEParamGetActividadesAsync(CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<ParametroItem>> FEParamGetCondicionIvaReceptorAsync(string? c, CancellationToken ct) => throw new NotSupportedException();
    public virtual Task<Cotizacion> FEParamGetCotizacionAsync(string m, string? f, CancellationToken ct) => throw new NotSupportedException();
}

public class ResilienciaTests
{
    private static (WsfeClienteResiliente, WsfeContador, CircuitBreaker, FakeTimeProvider) Crear(int umbral = 3)
    {
        var reloj = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var inner = new WsfeContador();
        var cb = new CircuitBreaker(umbral, TimeSpan.FromSeconds(30), reloj);
        var r = new WsfeClienteResiliente(inner, new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 }), cb, Util.Opciones(), reloj,
            NullLogger<WsfeClienteResiliente>.Instance, new[] { TimeSpan.Zero, TimeSpan.Zero });
        return (r, inner, cb, reloj);
    }

    [Fact]
    public async Task Parametros_se_cachean()
    {
        var (r, inner, _, _) = Crear();
        await r.FEParamGetTiposIvaAsync(default);
        await r.FEParamGetTiposIvaAsync(default);
        Assert.Equal(1, inner.Llamadas);
    }

    [Fact]
    public async Task Cotizacion_se_cachea_por_moneda()
    {
        var (r, inner, _, _) = Crear();
        await r.FEParamGetCotizacionAsync("DOL", null, default);
        await r.FEParamGetCotizacionAsync("DOL", null, default);
        await r.FEParamGetCotizacionAsync("060", null, default);
        Assert.Equal(2, inner.Llamadas);
    }

    [Fact]
    public async Task Lecturas_se_reintentan_ante_falla_transitoria()
    {
        var (r, inner, _, _) = Crear();
        inner.Fallas.Enqueue(new ArcaComunicacionException("x"));
        inner.Fallas.Enqueue(new ArcaComunicacionException("x"));
        Assert.Null(await r.FECompConsultarAsync(1, 6, 1, default));
        Assert.Equal(3, inner.Llamadas);
    }

    [Fact]
    public async Task Escrituras_nunca_se_reintentan()
    {
        var (r, inner, _, _) = Crear();
        inner.Fallas.Enqueue(new ArcaComunicacionException("x"));
        await Assert.ThrowsAsync<ArcaComunicacionException>(() => r.FECAESolicitarAsync(new SolicitudCae(), default));
        Assert.Equal(1, inner.Llamadas);
    }

    [Fact]
    public async Task Errores_de_negocio_no_se_reintentan_ni_abren_el_circuito()
    {
        var (r, inner, cb, _) = Crear(umbral: 1);
        inner.Fallas.Enqueue(new ArcaException("negocio"));
        await Assert.ThrowsAsync<ArcaException>(() => r.FECompConsultarAsync(1, 6, 1, default));
        Assert.Equal(1, inner.Llamadas);
        Assert.Equal("Cerrado", cb.Estado);
    }

    [Fact]
    public async Task Circuito_se_abre_falla_rapido_y_se_recupera()
    {
        var (r, inner, cb, reloj) = Crear(umbral: 3);
        for (var i = 0; i < 3; i++) inner.Fallas.Enqueue(new ArcaComunicacionException("x"));
        await Assert.ThrowsAsync<ArcaComunicacionException>(() => r.FECompConsultarAsync(1, 6, 1, default));
        Assert.Equal("Abierto", cb.Estado);
        var llamadas = inner.Llamadas;
        await Assert.ThrowsAsync<CircuitoAbiertoException>(() => r.FECompConsultarAsync(1, 6, 1, default));
        Assert.Equal(llamadas, inner.Llamadas); // no llamó a ARCA
        reloj.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("SemiAbierto", cb.Estado);
        await r.FECompConsultarAsync(1, 6, 1, default);
        Assert.Equal("Cerrado", cb.Estado);
    }

    [Fact]
    public void Estado_registra_desfasaje_de_reloj()
    {
        var e = new EstadoArca();
        var ahora = DateTimeOffset.UtcNow;
        e.RegistrarRespuesta(ahora, ahora.AddMinutes(-7));
        Assert.Equal(7, Math.Round(e.DesfasajeReloj.TotalMinutes));
        Assert.NotNull(e.UltimoExito);
    }
}

public class UtilidadesTests
{
    [Fact]
    public void QR_fiscal_codifica_JSON_en_base64()
    {
        var url = QrFiscal.GenerarUrl(20111111112, 3, 6, 42, "20260930", 121m, "PES", 1m, 99, 0, "76123456789012")!;
        Assert.StartsWith(QrFiscal.UrlBase, url);
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(url[QrFiscal.UrlBase.Length..])))).RootElement;
        Assert.Equal(1, json.GetProperty("ver").GetInt32());
        Assert.Equal("2026-09-30", json.GetProperty("fecha").GetString());
        Assert.Equal(76123456789012, json.GetProperty("codAut").GetInt64());
        Assert.Equal("E", json.GetProperty("tipoCodAut").GetString());
        Assert.False(json.TryGetProperty("tipoDocRec", out _)); // consumidor final sin identificar
    }

    [Fact]
    public void QR_sin_CAE_es_null() => Assert.Null(QrFiscal.GenerarUrl(1, 1, 6, 1, "20260930", 1, "PES", 1, 99, 0, null));

    [Fact]
    public void Catalogo_de_errores_da_ayuda() => Assert.Contains("punto de venta", CatalogoErroresArca.AyudasPara(new[] { new ArcaMensaje(10016, "x") })[0]);

    [Fact]
    public void Calculadora_agrupa_por_alicuota_y_redondea()
    {
        var d = CalculadoraImportes.Calcular(new SolicitudCalculo
        {
            CbteTipo = 6,
            Lineas = { new() { Neto = 33.335m, AlicuotaIvaId = 5 }, new() { Neto = 66.665m, AlicuotaIvaId = 5 }, new() { Neto = 10, AlicuotaIvaId = 4 } },
            Exento = 5
        });
        Assert.Equal(2, d.Iva!.Count);
        Assert.Equal(100m, d.Iva.Single(a => a.Id == 5).BaseImp);
        Assert.Equal(21m, d.Iva.Single(a => a.Id == 5).Importe);
        Assert.Equal(1.05m, d.Iva.Single(a => a.Id == 4).Importe);
        Assert.Equal(110m + 22.05m + 5m, d.ImpTotal);
        // El resultado pasa la validación de importes.
        d.Concepto = 1; d.DocTipo = 99; d.CondicionIVAReceptorId = 5;
        Assert.Empty(new ComprobanteValidator(TimeProvider.System).Validar(1, 6, d));
    }

    [Fact]
    public void Calculadora_clase_C_no_discrimina()
    {
        var d = CalculadoraImportes.Calcular(new SolicitudCalculo { CbteTipo = 11, Lineas = { new() { Neto = 100 } } });
        Assert.Null(d.Iva);
        Assert.Equal(100m, d.ImpTotal);
    }

    [Fact]
    public void Calculadora_rechaza_alicuota_inexistente()
        => Assert.Throws<ValidacionException>(() => CalculadoraImportes.Calcular(new SolicitudCalculo { CbteTipo = 6, Lineas = { new() { Neto = 1, AlicuotaIvaId = 7 } } }));

    [Fact]
    public void Firma_webhook_es_HMAC_SHA256_hex()
        => Assert.Equal("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8", WebhookNotificador.Firmar("The quick brown fox jumps over the lazy dog", "key"));
}

public class ValidacionAdicionalTests
{
    private static readonly DateOnly Hoy = new ComprobanteValidator(TimeProvider.System).HoyArgentina();

    [Fact]
    public void Clase_A_rechaza_consumidor_final_como_condicion()
    {
        var c = Util.FacturaAValida(Hoy); c.CondicionIVAReceptorId = 5;
        Assert.Contains(new ComprobanteValidator(TimeProvider.System).Validar(1, 1, c), e => e.Contains("clase A/M"));
    }

    [Fact]
    public void Condicion_inexistente()
    {
        var c = Util.FacturaBValida(Hoy); c.CondicionIVAReceptorId = 99;
        Assert.Contains(new ComprobanteValidator(TimeProvider.System).Validar(1, 6, c), e => e.Contains("inexistente"));
    }

    [Fact]
    public void Tope_consumidor_final_configurable()
    {
        var v = new ComprobanteValidator(TimeProvider.System) { MontoTopeConsumidorFinal = 100m };
        Assert.Contains(v.Validar(1, 6, Util.FacturaBValida(Hoy)), e => e.Contains("consumidor final"));
        Assert.Empty(new ComprobanteValidator(TimeProvider.System).Validar(1, 6, Util.FacturaBValida(Hoy)));
    }

    [Fact]
    public void FCE_requiere_CBU()
    {
        var c = Util.FacturaAValida(Hoy);
        var v = new ComprobanteValidator(TimeProvider.System);
        Assert.Contains(v.Validar(1, 201, c), e => e.Contains("2101"));
        c.Opcionales = new() { new Opcional { Id = "2101", Valor = "0110599500000000000017" } };
        Assert.DoesNotContain(v.Validar(1, 201, c), e => e.Contains("2101"));
    }
}

public class FacturacionMejorasTests
{
    private readonly WsfeSimulado _w = new();
    private readonly RegistroEmisionesMemoria _reg = new();
    private readonly List<ResultadoEmision> _notificados = new();
    private readonly DateOnly _hoy = new ComprobanteValidator(TimeProvider.System).HoyArgentina();

    private sealed class Notif : INotificadorEmisiones
    {
        private readonly List<ResultadoEmision> _l;
        public Notif(List<ResultadoEmision> l) => _l = l;
        public void Notificar(ResultadoEmision r) { lock (_l) _l.Add(r); }
    }

    private FacturacionService Servicio(TimeProvider? reloj = null, OpcionesEmision? op = null, IWsfeClient? w = null) =>
        new(w ?? _w, new ComprobanteValidator(reloj ?? TimeProvider.System), _reg, NullLogger<FacturacionService>.Instance, reloj ?? TimeProvider.System,
            new[] { TimeSpan.Zero }, op ?? new OpcionesEmision(20111111112, true, true, false), new Notif(_notificados));

    [Fact]
    public async Task Emision_aprobada_trae_QR_y_notifica()
    {
        var r = await Servicio().EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "q1", "c", default);
        Assert.StartsWith(QrFiscal.UrlBase, r.QrUrl);
        Assert.Single(_notificados);
    }

    [Fact]
    public async Task Completa_fecha_y_la_persiste_para_reintentos()
    {
        var d = Util.FacturaBValida(_hoy); d.CbteFch = null;
        await Servicio().EmitirAsync(1, 6, d, "f1", "c", default);
        Assert.Equal(_hoy.ToString("yyyyMMdd"), _w.Emitidos[(1, 6, 1)].CbteFch);
        Assert.Equal(_hoy.ToString("yyyyMMdd"), _reg.Obtener("c:f1")!.Detalle!.CbteFch);
        Assert.Null(d.CbteFch); // no muta la entrada
    }

    [Fact]
    public async Task Cotizacion_automatica_para_moneda_extranjera()
    {
        var d = Util.FacturaBValida(_hoy); d.MonId = "DOL"; d.MonCotiz = 0; d.CanMisMonExt = "N";
        await Servicio().EmitirAsync(1, 6, d, "m1", "c", default);
        Assert.Equal(1m, _w.Emitidos[(1, 6, 1)].MonCotiz); // WsfeSimulado cotiza 1
    }

    private sealed class WsfeTablas : WsfeBase
    {
        public override Task<IReadOnlyList<ParametroItem>> FEParamGetPtosVentaAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ParametroItem>>(new[]
            {
                new ParametroItem { Id = "1", Atributos = { ["Bloqueado"] = "N", ["FchBaja"] = "NULL" } },
                new ParametroItem { Id = "2", Atributos = { ["Bloqueado"] = "S", ["FchBaja"] = "NULL" } }
            });
    }

    [Theory]
    [InlineData(2, "bloqueado")]
    [InlineData(9, "no está habilitado")]
    public async Task Valida_punto_de_venta_contra_ARCA(int pv, string error)
    {
        var s = Servicio(op: new OpcionesEmision(20111111112, false, false, ValidarContraTablas: true), w: new WsfeTablas());
        var ex = await Assert.ThrowsAsync<ValidacionException>(() => s.EmitirAsync(pv, 6, Util.FacturaBValida(_hoy), "t" + pv, "c", default));
        Assert.Contains(ex.Errores, e => e.Contains(error));
    }

    [Fact]
    public async Task Reconciliador_resuelve_pendientes_autorizados_y_libera_los_inexistentes()
    {
        var reloj = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var s = Servicio(reloj);
        // Pendiente 1: ARCA lo autorizó pero la respuesta se perdió.
        _w.FallaDespues = _ => new ArcaComunicacionException("timeout");
        ComprobanteNumerado? tardio = null;
        _w.FallaDespues = x => { _w.Emitidos.TryRemove((x.PtoVta, x.CbteTipo, x.Comprobantes[0].CbteDesde), out tardio); return new ArcaComunicacionException("t"); };
        await Assert.ThrowsAsync<ArcaComunicacionException>(() => s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "p1", "c", default));
        _w.Emitidos[(1, 6, 1)] = tardio!;
        // Pendiente 2: nunca llegó.
        _w.FallaDespues = null;
        _w.FallaAntes = _ => new ArcaComunicacionException("t");
        await Assert.ThrowsAsync<ArcaComunicacionException>(() => s.EmitirAsync(2, 6, Util.FacturaBValida(_hoy), "p2", "c", default));
        _w.FallaAntes = null;

        Assert.Equal(2, s.Pendientes().Count);
        Assert.Equal(0, await s.ReconciliarPendientesAsync(TimeSpan.FromMinutes(2), default)); // todavía son recientes
        reloj.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(2, await s.ReconciliarPendientesAsync(TimeSpan.FromMinutes(2), default));
        Assert.Empty(s.Pendientes());
        Assert.Equal(EstadoEmision.Autorizado, s.ObtenerEmision("p1", "c")!.Estado);
        Assert.Null(s.ObtenerEmision("p2", "c"));
        Assert.Single(_notificados);
    }

    [Fact]
    public async Task Purga_y_estadisticas()
    {
        var reloj = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var s = Servicio(reloj);
        await s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "e1", "c", default);
        await s.EmitirAsync(1, 11, new ComprobanteDetalle { Concepto = 1, DocTipo = 99, CbteFch = _hoy.ToString("yyyyMMdd"), ImpNeto = 50, ImpTotal = 50, CondicionIVAReceptorId = 5 }, "e2", "c", default);
        var est = s.Estadisticas();
        Assert.Equal(2, est.Autorizadas);
        Assert.Equal(2, est.AutorizadasHoy);
        Assert.Equal(171m, est.ImporteHoy);
        Assert.Equal(1, est.PorTipo["11"]);
        Assert.Equal(0, s.Purgar(TimeSpan.FromDays(1)));
        reloj.Advance(TimeSpan.FromDays(2));
        Assert.Equal(2, s.Purgar(TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task Nota_de_credito_anula_con_comprobante_asociado()
    {
        var s = Servicio();
        await s.EmitirAsync(4, 6, Util.FacturaBValida(_hoy), "f", "c", default);
        var nc = new NotaCreditoService(_w, s, new ComprobanteValidator(TimeProvider.System));
        var r = await nc.AnularAsync(4, 6, 1, "nc", "c", default);
        Assert.True(r.Aprobado);
        Assert.Equal(8, r.CbteTipo);
        var emitida = _w.Emitidos[(4, 8, 1)];
        Assert.Equal(121m, emitida.ImpTotal);
        Assert.Equal(1, emitida.CbtesAsoc!.Single().Nro);
    }

    [Fact]
    public async Task Nota_de_credito_de_comprobante_inexistente_o_FCE()
    {
        var nc = new NotaCreditoService(_w, Servicio(), new ComprobanteValidator(TimeProvider.System));
        await Assert.ThrowsAsync<ValidacionException>(() => nc.AnularAsync(4, 6, 99, "x", "c", default));
        await Assert.ThrowsAsync<ValidacionException>(() => nc.AnularAsync(4, 201, 1, "y", "c", default));
    }
}

public sealed class ApiMejorasFactory : WebApplicationFactory<Program>
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
        b.UseSetting("Seguridad:Clientes:0:Nombre", "erp");
        b.UseSetting("Seguridad:Clientes:0:Claves:0", "clave-erp-vieja");
        b.UseSetting("Seguridad:Clientes:0:Claves:1", "clave-erp-nueva");
        b.UseSetting("Seguridad:Clientes:1:Nombre", "lector");
        b.UseSetting("Seguridad:Clientes:1:Claves:0", "clave-lector");
        b.UseSetting("Seguridad:Clientes:1:Permisos:0", "consultar");
        b.UseSetting("Seguridad:Clientes:2:Nombre", "ops");
        b.UseSetting("Seguridad:Clientes:2:Claves:0", "clave-ops");
        b.UseSetting("Seguridad:Clientes:2:Permisos:0", "admin");
        b.UseSetting("Seguridad:Clientes:2:Permisos:1", "consultar");
        b.ConfigureServices(s =>
        {
            s.RemoveAll<IWsfeClient>();
            s.AddSingleton<IWsfeClient>(Wsfe);
            s.RemoveAll<Arca.Facturacion.Core.Wsaa.ICmsFirmante>();
            s.AddSingleton<Arca.Facturacion.Core.Wsaa.ICmsFirmante>(new FirmanteNulo());
        });
    }
}

public class ApiMejorasTests : IClassFixture<ApiMejorasFactory>
{
    private readonly ApiMejorasFactory _f;
    public ApiMejorasTests(ApiMejorasFactory f) => _f = f;

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

    private static async Task<HttpResponseMessage> Emitir(HttpClient c, string key, int pv = 20)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/comprobantes") { Content = JsonContent.Create(new { ptoVta = pv, cbteTipo = 6, comprobante = Factura() }) };
        req.Headers.Add("Idempotency-Key", key);
        return await c.SendAsync(req);
    }

    [Fact]
    public async Task Sin_permiso_emitir_devuelve_403_pero_puede_consultar()
    {
        var c = C("clave-lector");
        Assert.Equal(HttpStatusCode.Forbidden, (await Emitir(c, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/parametros/tipos-iva")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/admin/estadisticas")).StatusCode);
    }

    [Fact]
    public async Task Rotacion_de_claves_conserva_la_idempotencia()
    {
        var a = await (await Emitir(C("clave-erp-vieja"), "rot", 21)).Content.ReadFromJsonAsync<JsonElement>();
        var b = await (await Emitir(C("clave-erp-nueva"), "rot", 21)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(a.GetProperty("cae").GetString(), b.GetProperty("cae").GetString());
        Assert.StartsWith(QrFiscal.UrlBase, a.GetProperty("qrUrl").GetString());
    }

    [Fact]
    public async Task Correlation_id_y_cabeceras_de_seguridad()
    {
        var c = C("clave-erp-nueva");
        c.DefaultRequestHeaders.Add("X-Correlation-Id", "abc-123");
        var r = await c.GetAsync("/api/v1/info");
        Assert.Equal("abc-123", r.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(r.Headers.Contains("X-Api-Version"));
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("20-*****112", j.GetProperty("cuit").GetString());
        Assert.Equal("Homologacion", j.GetProperty("ambiente").GetString());
    }

    [Fact]
    public async Task Lote_con_resultados_individuales()
    {
        var body = new
        {
            items = new object[]
            {
                new { idempotencyKey = "l1", ptoVta = 22, cbteTipo = 6, comprobante = Factura() },
                new { idempotencyKey = "l2", ptoVta = 22, cbteTipo = 6, comprobante = new { concepto = 1, docTipo = 99, impTotal = 5, condicionIVAReceptorId = 5 } }
            }
        };
        var r = await C("clave-erp-nueva").PostAsJsonAsync("/api/v1/comprobantes/lote", body);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(200, j[0].GetProperty("estado").GetInt32());
        Assert.Equal(400, j[1].GetProperty("estado").GetInt32());
    }

    [Fact]
    public async Task Calcular_importes()
    {
        var r = await C("clave-lector").PostAsJsonAsync("/api/v1/comprobantes/calcular", new { cbteTipo = 6, lineas = new[] { new { neto = 100, alicuotaIvaId = 5 } } });
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(121m, j.GetProperty("impTotal").GetDecimal());
    }

    [Fact]
    public async Task Nota_de_credito_estado_por_clave_y_QR()
    {
        var c = C("clave-erp-nueva");
        Assert.Equal(HttpStatusCode.OK, (await Emitir(c, "base-nc", 23)).StatusCode);
        var st = await c.GetFromJsonAsync<JsonElement>("/api/v1/comprobantes/emisiones/base-nc");
        Assert.Equal("Autorizado", st.GetProperty("estado").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/comprobantes/emisiones/no-existe")).StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/comprobantes/23/6/1/nota-credito");
        req.Headers.Add("Idempotency-Key", "nc-1");
        var nc = await (await c.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(8, nc.GetProperty("cbteTipo").GetInt32());

        var qr = await c.GetFromJsonAsync<JsonElement>("/api/v1/comprobantes/23/6/1/qr");
        Assert.StartsWith(QrFiscal.UrlBase, qr.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Admin_estadisticas_pendientes_y_reconciliar()
    {
        var c = C("clave-ops");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/admin/estadisticas")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/admin/emisiones/pendientes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/v1/admin/emisiones/reconciliar", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Emitir(c, "ops-no")).StatusCode);
    }

    [Fact]
    public async Task Health_live_y_ready()
    {
        var c = _f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/live")).StatusCode);
        var r = await c.GetAsync("/health/ready");
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(j.GetProperty("chequeos").TryGetProperty("arca", out _));
        Assert.Equal("Degraded", j.GetProperty("chequeos").GetProperty("registro").GetProperty("estado").GetString());
    }
}

public class ManejadorErroresTests
{
    [Fact]
    public async Task Circuito_abierto_devuelve_503_con_Retry_After()
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        await new ManejadorErrores(NullLogger<ManejadorErrores>.Instance).TryHandleAsync(ctx, new CircuitoAbiertoException(TimeSpan.FromSeconds(12)), default);
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Equal("12", ctx.Response.Headers.RetryAfter.ToString());
    }
}

public class FirmanteDiferidoTests
{
    [Fact]
    public void No_carga_el_certificado_hasta_usarlo()
    {
        var cargas = 0;
        var f = new Arca.Facturacion.Core.Wsaa.FirmanteDiferido(() =>
        {
            cargas++;
            return Arca.Facturacion.Core.Wsaa.CertificadoCmsFirmante.DesdeArchivo("/no/existe.pfx", null);
        });
        Assert.Equal(0, cargas);
        Assert.Throws<WsaaException>(() => f.Firmar(new byte[] { 1 }));
        Assert.Equal(1, cargas);
    }
}
