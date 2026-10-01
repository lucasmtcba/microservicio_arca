using System.Collections.Concurrent;
using Arca.Facturacion.Core;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arca.Facturacion.Tests;

/// <summary>WSFE en memoria que simula la numeración correlativa de ARCA.</summary>
public sealed class WsfeSimulado : IWsfeClient
{
    public readonly ConcurrentDictionary<(int, int), long> Ultimos = new();
    public readonly ConcurrentDictionary<(int, int, long), ComprobanteNumerado> Emitidos = new();
    public Func<SolicitudCae, Exception?>? FallaAntes;
    public Func<SolicitudCae, Exception?>? FallaDespues;
    public Func<Exception?>? FallaConsulta;
    public int Solicitudes;
    private readonly object _lock = new();

    public Task<UltimoComprobante> FECompUltimoAutorizadoAsync(int pv, int t, CancellationToken ct) =>
        Task.FromResult(new UltimoComprobante { PtoVta = pv, CbteTipo = t, CbteNro = Ultimos.GetValueOrDefault((pv, t)) });

    public async Task<RespuestaCae> FECAESolicitarAsync(SolicitudCae s, CancellationToken ct)
    {
        Interlocked.Increment(ref Solicitudes);
        await Task.Yield();
        if (FallaAntes?.Invoke(s) is { } e1) throw e1;
        var c = s.Comprobantes[0];
        RespuestaCae r;
        lock (_lock)
        {
            var ult = Ultimos.GetValueOrDefault((s.PtoVta, s.CbteTipo));
            if (c.CbteDesde != ult + 1)
                return new RespuestaCae { Cabecera = new() { Resultado = "R" }, Errores = { new(10016, "no es el próximo") },
                    Detalles = { new() { Resultado = "R", CbteDesde = c.CbteDesde } } };
            Ultimos[(s.PtoVta, s.CbteTipo)] = c.CbteDesde;
            Emitidos[(s.PtoVta, s.CbteTipo, c.CbteDesde)] = c;
            r = new RespuestaCae { Cabecera = new() { Resultado = "A" },
                Detalles = { new() { Resultado = "A", CbteDesde = c.CbteDesde, CbteHasta = c.CbteDesde, Cae = "7" + c.CbteDesde.ToString("D13"), CaeFchVto = "20261010" } } };
        }
        if (FallaDespues?.Invoke(s) is { } e2) throw e2;
        return r;
    }

    public Task<ComprobanteConsultado?> FECompConsultarAsync(int pv, int t, long n, CancellationToken ct)
    {
        if (FallaConsulta?.Invoke() is { } e) throw e;
        if (!Emitidos.TryGetValue((pv, t, n), out var c)) return Task.FromResult<ComprobanteConsultado?>(null);
        return Task.FromResult<ComprobanteConsultado?>(new ComprobanteConsultado
        {
            PtoVta = pv, CbteTipo = t, CbteDesde = n, CbteHasta = n, DocTipo = c.DocTipo, DocNro = c.DocNro, ImpTotal = c.ImpTotal,
            Concepto = c.Concepto, ImpNeto = c.ImpNeto, ImpIVA = c.ImpIVA, ImpTrib = c.ImpTrib, ImpOpEx = c.ImpOpEx, ImpTotConc = c.ImpTotConc,
            CbteFch = c.CbteFch, MonId = c.MonId, Iva = c.Iva, CondicionIVAReceptorId = c.CondicionIVAReceptorId,
            Resultado = "A", CodAutorizacion = "7" + n.ToString("D13"), FchVto = "20261010", EmisionTipo = "CAE"
        });
    }

    public Task<EstadoServidores> FEDummyAsync(CancellationToken ct) => Task.FromResult(new EstadoServidores { AppServer = "OK", DbServer = "OK", AuthServer = "OK" });
    public Task<int> FECompTotXRequestAsync(CancellationToken ct) => Task.FromResult(250);
    public Task<Caea> FECAEASolicitarAsync(int p, short o, CancellationToken ct) => throw new NotImplementedException();
    public Task<Caea?> FECAEAConsultarAsync(int p, short o, CancellationToken ct) => Task.FromResult<Caea?>(null);
    public Task<RespuestaCae> FECAEARegInformativoAsync(InformeCaea i, CancellationToken ct) => throw new NotImplementedException();
    public Task<CaeaSinMovimiento> FECAEASinMovimientoInformarAsync(int pv, string c, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<CaeaSinMovimiento>> FECAEASinMovimientoConsultarAsync(string c, int pv, CancellationToken ct) => throw new NotImplementedException();
    private static Task<IReadOnlyList<ParametroItem>> P() => Task.FromResult<IReadOnlyList<ParametroItem>>(new[] { new ParametroItem { Id = "1", Desc = "x" } });
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposCbteAsync(CancellationToken ct) => P();
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposConceptoAsync(CancellationToken ct) => P();
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposDocAsync(CancellationToken ct) => P();
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposIvaAsync(CancellationToken ct) => P();
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposMonedasAsync(CancellationToken ct) => P();
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposOpcionalAsync(CancellationToken ct) => P();
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposTributosAsync(CancellationToken ct) => P();
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposPaisesAsync(CancellationToken ct) => P();
    public Task<IReadOnlyList<ParametroItem>> FEParamGetPtosVentaAsync(CancellationToken ct) => P();
    public Task<IReadOnlyList<ParametroItem>> FEParamGetActividadesAsync(CancellationToken ct) => P();
    public Task<IReadOnlyList<ParametroItem>> FEParamGetCondicionIvaReceptorAsync(string? c, CancellationToken ct) => P();
    public Task<Cotizacion> FEParamGetCotizacionAsync(string m, string? f, CancellationToken ct) => Task.FromResult(new Cotizacion { MonId = m, MonCotiz = 1 });
}

public class FacturacionServiceTests
{
    private readonly WsfeSimulado _w = new();
    private readonly RegistroEmisionesMemoria _reg = new();
    private readonly FacturacionService _s;
    private readonly DateOnly _hoy;

    public FacturacionServiceTests()
    {
        var val = new ComprobanteValidator(TimeProvider.System);
        _hoy = val.HoyArgentina();
        _s = new FacturacionService(_w, val, _reg, NullLogger<FacturacionService>.Instance, TimeProvider.System, new[] { TimeSpan.Zero, TimeSpan.Zero });
    }

    [Fact]
    public async Task Numera_con_ultimo_mas_uno_y_devuelve_CAE()
    {
        _w.Ultimos[(3, 6)] = 41;
        var r = await _s.EmitirAsync(3, 6, Util.FacturaBValida(_hoy), Guid.NewGuid().ToString(), "c", default);
        Assert.True(r.Aprobado);
        Assert.Equal(42, r.CbteNro);
        Assert.Equal("70000000000042", r.Cae);
        Assert.False(r.Reconciliado);
    }

    [Fact]
    public async Task Invalido_no_llega_a_ARCA()
    {
        var c = Util.FacturaBValida(_hoy); c.ImpTotal = 1;
        await Assert.ThrowsAsync<ValidacionException>(() => _s.EmitirAsync(3, 6, c, Guid.NewGuid().ToString(), "c", default));
        Assert.Equal(0, _w.Solicitudes);
    }

    [Fact]
    public async Task Emisiones_concurrentes_no_duplican_ni_saltean_numeros()
    {
        var rs = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), Guid.NewGuid().ToString(), "c", default)));
        Assert.All(rs, r => Assert.True(r.Aprobado));
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), rs.Select(r => r.CbteNro).OrderBy(n => n));
    }

    [Fact]
    public async Task Timeout_tras_autorizar_se_reconcilia_con_FECompConsultar()
    {
        _w.FallaDespues = _ => new ArcaComunicacionException("timeout");
        var r = await _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), Guid.NewGuid().ToString(), "c", default);
        Assert.True(r.Reconciliado);
        Assert.Equal("70000000000001", r.Cae);
        Assert.Equal(1, _w.Solicitudes);
    }

    [Fact]
    public async Task Timeout_sin_autorizar_propaga_la_falla()
    {
        _w.FallaAntes = _ => new ArcaComunicacionException("timeout");
        await Assert.ThrowsAsync<ArcaComunicacionException>(() => _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), Guid.NewGuid().ToString(), "c", default));
        Assert.Empty(_w.Emitidos);
    }

    [Fact]
    public async Task Si_la_reconciliacion_falla_el_estado_es_desconocido()
    {
        _w.FallaDespues = _ => new ArcaComunicacionException("timeout");
        _w.FallaConsulta = () => new ArcaComunicacionException("otra vez");
        var ex = await Assert.ThrowsAsync<ArcaComunicacionException>(() => _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), Guid.NewGuid().ToString(), "c", default));
        Assert.Contains("DESCONOCIDO", ex.Message);
    }

    [Fact]
    public async Task Numero_tomado_por_otro_con_datos_distintos_se_reporta()
    {
        _w.FallaAntes = s =>
        {
            // Otro sistema emite ese mismo número con otro importe y la red falla.
            var n = s.Comprobantes[0].CbteDesde;
            _w.Emitidos[(s.PtoVta, s.CbteTipo, n)] = new ComprobanteNumerado { DocTipo = 99, ImpTotal = 999 };
            return new ArcaComunicacionException("timeout");
        };
        var ex = await Assert.ThrowsAsync<ArcaException>(() => _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), Guid.NewGuid().ToString(), "c", default));
        Assert.Contains("datos distintos", ex.Message);
    }

    [Fact]
    public async Task Error_10016_reintenta_una_vez_con_nuevo_numero()
    {
        var primera = true;
        _w.FallaAntes = s =>
        {
            if (primera) { primera = false; _w.Ultimos[(s.PtoVta, s.CbteTipo)] = 5; } // otro emisor avanzó
            return null;
        };
        var r = await _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), Guid.NewGuid().ToString(), "c", default);
        Assert.Equal(6, r.CbteNro);
        Assert.True(r.Aprobado);
    }

    [Fact]
    public async Task Idempotencia_devuelve_el_mismo_resultado_sin_reemitir()
    {
        var a = await _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k1", "c", default);
        var b = await _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k1", "c", default);
        Assert.Equal(a.Cae, b.Cae);
        Assert.True(b.Repetido);
        Assert.False(a.Repetido);
        Assert.Equal(1, _w.Solicitudes);
    }

    [Fact]
    public async Task Idempotencia_concurrente_emite_una_sola_vez()
    {
        var rs = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k2", "c", default)));
        Assert.Equal(1, _w.Solicitudes);
        Assert.All(rs, r => Assert.Equal(1, r.CbteNro));
    }

    [Fact]
    public async Task Idempotencia_con_otro_contenido_es_conflicto()
    {
        await _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k3", "c", default);
        var otro = Util.FacturaBValida(_hoy); otro.ImpNeto = 200; otro.ImpIVA = 42; otro.ImpTotal = 242; otro.Iva![0].BaseImp = 200; otro.Iva[0].Importe = 42;
        await Assert.ThrowsAsync<IdempotenciaConflictoException>(() => _s.EmitirAsync(1, 6, otro, "k3", "c", default));
    }

    [Fact]
    public async Task Idempotencia_tras_falla_permite_reintentar()
    {
        _w.FallaAntes = _ => new ArcaComunicacionException("timeout");
        await Assert.ThrowsAsync<ArcaComunicacionException>(() => _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k4", "c", default));
        _w.FallaAntes = null;
        var r = await _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k4", "c", default);
        Assert.True(r.Aprobado);
    }

    [Fact]
    public async Task Timeout_con_grabacion_tardia_y_reintento_misma_clave_emite_una_sola_vez()
    {
        // ARCA autoriza pero la respuesta se pierde, y FECompConsultar todavía no lo ve.
        ComprobanteNumerado? tardio = null;
        _w.FallaDespues = s =>
        {
            var n = s.Comprobantes[0].CbteDesde;
            _w.Emitidos.TryRemove((s.PtoVta, s.CbteTipo, n), out tardio);
            return new ArcaComunicacionException("timeout");
        };
        await Assert.ThrowsAsync<ArcaComunicacionException>(() => _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k5", "c", default));
        _w.FallaDespues = null;
        _w.Emitidos[(1, 6, 1)] = tardio!; // ahora sí figura en ARCA
        var r = await _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k5", "c", default);
        Assert.Equal(1, r.CbteNro);
        Assert.True(r.Reconciliado);
        Assert.Equal(1, _w.Solicitudes);
        Assert.Single(_w.Emitidos);
    }

    [Fact]
    public async Task Reintento_pendiente_no_encontrado_reenvia_el_mismo_numero()
    {
        _w.Ultimos[(1, 6)] = 9;
        _w.FallaAntes = _ => new ArcaComunicacionException("timeout");
        await Assert.ThrowsAsync<ArcaComunicacionException>(() => _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k6", "c", default));
        _w.FallaAntes = null;
        var r = await _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k6", "c", default);
        Assert.Equal(10, r.CbteNro);
        Assert.False(r.Reconciliado);
    }

    [Fact]
    public async Task SoapFault_en_FECAESolicitar_se_reconcilia()
    {
        _w.FallaDespues = _ => new Arca.Facturacion.Core.Soap.SoapFaultException("soap:Server", "error interno");
        var r = await _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k7", "c", default);
        Assert.True(r.Reconciliado);
        Assert.True(r.Aprobado);
    }

    [Fact]
    public async Task Idempotency_key_obligatoria()
        => await Assert.ThrowsAsync<ValidacionException>(() => _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "", "c", default));

    [Fact]
    public async Task Claves_aisladas_por_cliente()
    {
        var a = await _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k8", "clienteA", default);
        var b = await _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k8", "clienteB", default);
        Assert.NotEqual(a.CbteNro, b.CbteNro);
    }

    [Fact]
    public async Task Reintento_al_dia_siguiente_devuelve_lo_emitido_sin_revalidar()
    {
        var d = Util.FacturaBValida(_hoy);
        var a = await _s.EmitirAsync(1, 6, d, "k9", "c", default);
        var reloj = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(30));
        var s2 = new FacturacionService(_w, new ComprobanteValidator(reloj), _reg, NullLogger<FacturacionService>.Instance, reloj, new[] { TimeSpan.Zero });
        Assert.Equal(a.Cae, (await s2.EmitirAsync(1, 6, d, "k9", "c", default)).Cae);
    }

    [Fact]
    public async Task Doble_10016_requiere_revision_manual()
    {
        // 10016 dos veces (renumera una vez y vuelve a chocar) => error de revisión manual, no se cachea
        _w.FallaAntes = s => { _w.Ultimos[(s.PtoVta, s.CbteTipo)] = _w.Ultimos.GetValueOrDefault((s.PtoVta, s.CbteTipo)) + 1; return null; };
        await Assert.ThrowsAsync<ArcaException>(() => _s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k10", "c", default));
    }

    [Fact]
    public void Coincide_detecta_fecha_o_alicuota_distinta()
    {
        var e = FacturacionService.Numerar(Util.FacturaBValida(_hoy), 1);
        var c = new ComprobanteConsultado { Concepto = 1, DocTipo = 99, ImpTotal = 121, ImpNeto = 100, ImpIVA = 21, CbteFch = e.CbteFch, MonId = "PES",
            Iva = new() { new AlicuotaIva { Id = 5, BaseImp = 100, Importe = 21 } } };
        Assert.True(FacturacionService.Coincide(c, e));
        c.CbteFch = "20000101";
        Assert.False(FacturacionService.Coincide(c, e));
        c.CbteFch = e.CbteFch; c.Iva[0].Id = 4;
        Assert.False(FacturacionService.Coincide(c, e));
    }

    [Fact]
    public void Registro_en_archivo_roundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reg_" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = new RegistroEmisionesArchivo(dir);
            r.Guardar(new RegistroEmision { Clave = "c:k/../x", Hash = "h", CbteNro = 7, Estado = EstadoEmision.Pendiente });
            Assert.Equal(7, new RegistroEmisionesArchivo(dir).Obtener("c:k/../x")!.CbteNro);
            r.Eliminar("c:k/../x");
            Assert.Null(r.Obtener("c:k/../x"));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Numerar_no_modifica_el_original()
    {
        var d = Util.FacturaBValida(_hoy);
        var n = FacturacionService.Numerar(d, 9);
        Assert.Equal(9, n.CbteDesde);
        n.Iva![0].Importe = 0;
        Assert.Equal(21m, d.Iva![0].Importe);
    }
}
