using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Arca.Facturacion.Api.Infraestructura;
using Arca.Facturacion.Cliente;
using Arca.Facturacion.Core;
using Arca.Facturacion.Core.Resiliencia;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Simulacion;
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

public class Validacion3Tests
{
    private static readonly ComprobanteValidator V = new(TimeProvider.System);
    private static readonly DateOnly Hoy = V.HoyArgentina();
    private static string F(DateOnly d) => d.ToString("yyyyMMdd");

    private static void Error(ComprobanteValidator v, int tipo, ComprobanteDetalle c, string frag) => Assert.Contains(v.Validar(1, tipo, c), e => e.Contains(frag));

    [Fact]
    public void Asociado_de_otra_clase()
    {
        var c = Util.FacturaBValida(Hoy); c.CbtesAsoc = new() { new ComprobanteAsociado { Tipo = 1, PtoVta = 1, Nro = 5 } };
        Error(V, 8, c, "misma clase");
        c.CbtesAsoc[0].Tipo = 6;
        Assert.Empty(V.Validar(1, 8, c));
    }

    [Fact]
    public void Asociado_con_datos_invalidos_o_repetido()
    {
        var c = Util.FacturaBValida(Hoy);
        c.CbtesAsoc = new() { new() { Tipo = 6, PtoVta = 0, Nro = 0, Cuit = "123", CbteFch = "2026-01-01" }, new() { Tipo = 6, PtoVta = 0, Nro = 0 } };
        var e = V.Validar(1, 8, c);
        Assert.Contains(e, x => x.Contains("número inválido"));
        Assert.Contains(e, x => x.Contains("CUIT 123"));
        Assert.Contains(e, x => x.Contains("CbteFch debe ser yyyyMMdd"));
        Assert.Contains(e, x => x.Contains("repetidos"));
    }

    [Fact]
    public void Tributos_opcionales_y_actividades()
    {
        var c = Util.FacturaAValida(Hoy);
        c.Tributos![0].Id = 99; c.Tributos[0].Desc = null;
        c.Opcionales = new() { new() { Id = "5", Valor = "x" }, new() { Id = "5", Valor = "y" }, new() { Id = "2101", Valor = "123" } };
        c.Actividades = new() { 1, 1 };
        var e = V.Validar(1, 1, c);
        Assert.Contains(e, x => x.Contains("tributo 99"));
        Assert.Contains(e, x => x.Contains("repetir el Id"));
        Assert.Contains(e, x => x.Contains("CBU válido"));
        Assert.Contains(e, x => x.Contains("Actividades"));
    }

    [Theory]
    [InlineData("0110599500000000000017", true)]
    [InlineData("0110599500000000000018", false)]
    [InlineData("0110599400000000000017", false)]
    [InlineData("011059950000000000001", false)]
    [InlineData("A110599500000000000017", false)]
    public void CBU(string cbu, bool ok) => Assert.Equal(ok, Cbu.EsValido(cbu));

    [Fact]
    public void Compradores_moneda_monto_maximo_periodo_y_documento()
    {
        var v = new ComprobanteValidator(TimeProvider.System) { MontoMaximoComprobante = 100 };
        var c = Util.FacturaBValida(Hoy);
        c.Compradores = new() { new() { DocTipo = 80, DocNro = 123, Porcentaje = 100 } };
        c.MonId = "DOL"; c.MonCotiz = 1000.1234567m; c.CanMisMonExt = "N";
        c.DocTipo = 42;
        var e = v.Validar(1, 6, c);
        Assert.Contains(e, x => x.Contains("Comprador 123"));
        Assert.Contains(e, x => x.Contains("6 decimales"));
        Assert.Contains(e, x => x.Contains("máximo configurado"));
        Assert.Contains(e, x => x.Contains("DocTipo 42 desconocido"));
        var nc = Util.FacturaBValida(Hoy); nc.PeriodoAsoc = new() { FchDesde = F(Hoy), FchHasta = F(Hoy.AddDays(3)) };
        Error(V, 8, nc, "no puede ser futura");
    }

    [Fact]
    public void Receptor_bloqueado_y_condicion_del_emisor()
    {
        var a = Util.FacturaAValida(Hoy);
        Error(new ComprobanteValidator(TimeProvider.System) { ReceptoresBloqueados = new HashSet<long> { a.DocNro } }, 1, a, "bloqueado");
        Error(new ComprobanteValidator(TimeProvider.System) { CondicionEmisor = CondicionEmisor.Monotributo }, 1, a, "sólo emite comprobantes clase C");
        var c = new ComprobanteDetalle { Concepto = 1, DocTipo = 99, CbteFch = F(Hoy), ImpNeto = 10, ImpTotal = 10, CondicionIVAReceptorId = 5 };
        Error(new ComprobanteValidator(TimeProvider.System) { CondicionEmisor = CondicionEmisor.ResponsableInscripto }, 11, c, "no emite comprobantes clase C");
        Assert.Empty(new ComprobanteValidator(TimeProvider.System) { CondicionEmisor = CondicionEmisor.Monotributo }.Validar(1, 11, c));
    }

    [Fact]
    public void Advertencias()
    {
        var v = new ComprobanteValidator(TimeProvider.System) { CuitEmisor = 30500010912, UmbralAdvertenciaImporte = 1000, MontoTopeConsumidorFinal = 130 };
        var a = Util.FacturaAValida(Hoy); a.CbteFch = F(Hoy.AddDays(-1));
        var adv = v.Advertencias(1, a);
        Assert.Contains(adv, x => x.Contains("propio emisor"));
        Assert.Contains(adv, x => x.Contains("no es la de hoy"));
        Assert.Contains(adv, x => x.Contains("Importe alto"));
        Assert.Contains(v.Advertencias(6, Util.FacturaBValida(Hoy)), x => x.Contains("cerca del tope"));
        Assert.Empty(new ComprobanteValidator(TimeProvider.System).Advertencias(6, Util.FacturaBValida(Hoy)));
    }
}

public class SimuladorTests
{
    [Fact]
    public async Task Numera_autoriza_con_CAE_ficticio_y_consulta()
    {
        var s = new WsfeSimulador(TimeProvider.System);
        var hoy = new ComprobanteValidator(TimeProvider.System).HoyArgentina();
        var svc = new FacturacionService(s, new ComprobanteValidator(TimeProvider.System), new RegistroEmisionesMemoria(), NullLogger<FacturacionService>.Instance, TimeProvider.System, new[] { TimeSpan.Zero });
        var r1 = await svc.EmitirAsync(3, 6, Util.FacturaBValida(hoy), "s1", "c", default);
        var r2 = await svc.EmitirAsync(3, 6, Util.FacturaBValida(hoy), "s2", "c", default);
        Assert.Equal(2, r2.CbteNro);
        Assert.StartsWith("99", r1.Cae);
        Assert.Equal(14, r1.Cae!.Length);
        Assert.Contains(r1.Observaciones, o => o.Code == WsfeSimulador.CodigoObservacion);
        Assert.Equal(121m, (await s.FECompConsultarAsync(3, 6, 1, default))!.ImpTotal);
        var mal = await s.FECAESolicitarAsync(new SolicitudCae { PtoVta = 3, CbteTipo = 6, Comprobantes = { new ComprobanteNumerado { CbteDesde = 9, CbteHasta = 9 } } }, default);
        Assert.Contains(mal.Detalles[0].Observaciones, e => e.Code == 10016); // como ARCA real: observación del detalle con Resultado R
        Assert.Equal("R", mal.Detalles[0].Resultado);
        Assert.NotEmpty(await s.FEParamGetTiposCbteAsync(default));
        Assert.Equal("202610", (await s.FECAEASolicitarAsync(202610, 1, default)).Codigo![2..8]);
    }

    [Fact]
    public async Task Inyeccion_de_fallas()
    {
        var s = new WsfeSimulador(TimeProvider.System, fallasPorcentaje: 100, semilla: 1);
        await Assert.ThrowsAsync<ArcaComunicacionException>(() => s.FECompUltimoAutorizadoAsync(1, 6, default));
    }
}

public class SuscripcionesTests
{
    private readonly WsfeSimulado _w = new();

    private (SuscripcionesService, FakeTimeProvider) Crear(DateTimeOffset ahora)
    {
        var reloj = new FakeTimeProvider(ahora);
        var val = new ComprobanteValidator(reloj);
        var fact = new FacturacionService(_w, val, new RegistroEmisionesMemoria(), NullLogger<FacturacionService>.Instance, reloj, new[] { TimeSpan.Zero });
        return (new SuscripcionesService(null, fact, val, NullLogger<SuscripcionesService>.Instance), reloj);
    }

    private static Suscripcion Abono(DateOnly proxima) => new()
    {
        Id = "abono", PtoVta = 7, CbteTipo = 11, ProximaEjecucion = proxima, Periodicidad = Periodicidad.Mensual,
        Comprobante = new ComprobanteDetalle { Concepto = 2, DocTipo = 99, ImpNeto = 5000, ImpTotal = 5000, CondicionIVAReceptorId = 5 }
    };

    [Fact]
    public async Task Emite_el_periodo_vencido_avanza_y_es_idempotente()
    {
        var (s, reloj) = Crear(new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero));
        s.Guardar("erp", Abono(new DateOnly(2026, 10, 1)));
        Assert.Equal(1, await s.EjecutarVencidasAsync(default));
        Assert.Equal(0, await s.EjecutarVencidasAsync(default)); // mismo día: nada nuevo
        var sus = s.Obtener("erp", "abono")!;
        Assert.Equal(new DateOnly(2026, 11, 1), sus.ProximaEjecucion);
        Assert.Single(sus.Historial);
        var emitido = _w.Emitidos[(7, 11, 1)];
        Assert.Equal("20260901", emitido.FchServDesde);
        Assert.Equal("20260930", emitido.FchServHasta);
        Assert.Equal("suscripcion:abono:20261001", emitido.ReferenciaExterna);
        reloj.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, await s.EjecutarVencidasAsync(default));
        Assert.Equal(2, _w.Ultimos[(7, 11)]);
    }

    [Fact]
    public async Task Pausada_no_emite_y_valida_al_guardar()
    {
        var (s, _) = Crear(new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero));
        s.Guardar("erp", Abono(new DateOnly(2026, 10, 1)));
        s.CambiarEstado("erp", "abono", false);
        Assert.Equal(0, await s.EjecutarVencidasAsync(default));
        var mala = Abono(new DateOnly(2026, 10, 1)); mala.Comprobante.ImpTotal = 1;
        Assert.Throws<ValidacionException>(() => s.Guardar("erp", mala));
    }

    [Theory]
    [InlineData(Periodicidad.Semanal, 7)]
    [InlineData(Periodicidad.Quincenal, 15)]
    public void Periodicidades(Periodicidad p, int dias)
        => Assert.Equal(new DateOnly(2026, 1, 1).AddDays(dias), SuscripcionesService.Siguiente(new DateOnly(2026, 1, 1), p));
}

public class AgendaTests
{
    [Fact]
    public void Busca_valida_y_aplica()
    {
        var a = new AgendaReceptores(null);
        a.Guardar("erp", new Receptor { Id = "acme", Nombre = "ACME SA", DocTipo = 80, DocNro = 30500010912, CondicionIVAReceptorId = 1 });
        Assert.Single(a.Buscar("erp", "acme"));
        Assert.Single(a.Buscar("erp", "3050001"));
        Assert.Empty(a.Buscar("otro", null));
        Assert.Throws<ValidacionException>(() => a.Guardar("erp", new Receptor { Id = "x", Nombre = "X", DocTipo = 96, DocNro = 1, CondicionIVAReceptorId = 1 }));
        var d = new ComprobanteDetalle();
        AgendaReceptores.Aplicar(a.Obtener("erp", "acme")!, d);
        Assert.Equal(30500010912, d.DocNro);
        Assert.Equal(1, d.CondicionIVAReceptorId);
    }
}

public class ImpresionTests
{
    [Theory]
    [InlineData(0, "CERO")]
    [InlineData(1, "UNO")]
    [InlineData(21, "VEINTIUNO")]
    [InlineData(100, "CIEN")]
    [InlineData(101, "CIENTO UNO")]
    [InlineData(45, "CUARENTA Y CINCO")]
    [InlineData(1000, "MIL")]
    [InlineData(2021, "DOS MIL VEINTIUNO")]
    [InlineData(1_000_000, "UN MILLON")]
    [InlineData(1_500_000, "UN MILLON QUINIENTOS MIL")]
    [InlineData(2_000_000_000, "DOS MIL MILLONES")]
    public void Numero_a_letras(long n, string esperado) => Assert.Equal(esperado, NumeroALetras.Entero(n));

    [Fact]
    public void Importe_con_centavos() => Assert.Equal("SON PESOS CIENTO VEINTIUNO CON 50/100", NumeroALetras.Importe(121.5m));

    [Fact]
    public void QR_PNG_y_HTML()
    {
        var png = Impresion.QrPng("https://www.afip.gob.ar/fe/qr/?p=abc");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        var c = new ComprobanteConsultado
        {
            PtoVta = 3, CbteTipo = 6, CbteDesde = 42, CbteHasta = 42, CbteFch = "20261001", Concepto = 1, DocTipo = 99, ImpTotal = 121, ImpNeto = 100, ImpIVA = 21,
            MonId = "PES", MonCotiz = 1, CodAutorizacion = "76123456789012", FchVto = "20261011", CondicionIVAReceptorId = 5,
            Iva = new() { new AlicuotaIva { Id = 5, BaseImp = 100, Importe = 21 } }
        };
        var html = Impresion.Html(c, new DatosEmisor { RazonSocial = "Prueba <SA>" }, 20111111112, "https://www.afip.gob.ar/fe/qr/?p=abc", "DUPLICADO");
        Assert.Contains("76123456789012", html);
        Assert.Contains("00003", html);
        Assert.Contains("Factura B", html);
        Assert.Contains("CIENTO VEINTIUNO", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("Prueba &lt;SA&gt;", html);
        Assert.Contains("DUPLICADO", html);
        Assert.Contains("Ley 27.743", html);
    }
}

public class OperacionTests
{
    private readonly WsfeSimulado _w = new();
    private readonly RegistroEmisionesMemoria _reg = new();
    private readonly DateOnly _hoy = new ComprobanteValidator(TimeProvider.System).HoyArgentina();
    private FacturacionService S(RegistroRechazos? rech = null) =>
        new(_w, new ComprobanteValidator(TimeProvider.System), _reg, NullLogger<FacturacionService>.Instance, TimeProvider.System, new[] { TimeSpan.Zero }) { Rechazos = rech };

    [Fact]
    public async Task Reportes_cierre_y_respaldo()
    {
        var s = S();
        await s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "a", "c", default);
        await s.EmitirAsync(2, 1, Util.FacturaAValida(_hoy), "b", "c", default);
        var rep = new ReportesService(_reg, _w, new ComprobanteValidator(TimeProvider.System));
        Assert.Equal(1240m, rep.PorReceptor(null, null, 5)[0].Total);
        Assert.Equal(1361m, rep.PorDia(null, null).Single().Total);
        Assert.Equal(2, rep.PorPuntoVenta(null, null).Count);
        var cierre = await rep.CierreAsync(null, default);
        Assert.True(cierre.Consistente);
        Assert.Equal(2, cierre.Tipos.Count);
        _w.Ultimos[(1, 6)] = 5; // otro sistema emitió
        Assert.False((await rep.CierreAsync(null, default)).Consistente);
        Assert.Equal(6, await rep.ProximoNumeroAsync(1, 6, default));

        var respaldo = rep.Exportar();
        var otro = new RegistroEmisionesMemoria();
        var rep2 = new ReportesService(otro, _w, new ComprobanteValidator(TimeProvider.System));
        Assert.Equal((2, 0), rep2.Importar(respaldo));
        Assert.Equal((0, 2), rep2.Importar(respaldo));
        Assert.Equal((0, 1), rep2.Importar("{basura"));
    }

    private sealed class WsfeRechaza : WsfeBase
    {
        public override Task<UltimoComprobante> FECompUltimoAutorizadoAsync(int pv, int t, CancellationToken ct) => Task.FromResult(new UltimoComprobante());
        public override Task<RespuestaCae> FECAESolicitarAsync(SolicitudCae s, CancellationToken ct) => Task.FromResult(new RespuestaCae
        {
            Cabecera = new() { Resultado = "R" },
            Detalles = { new() { Resultado = "R", Observaciones = { new(10063, "Factura individual") } } }
        });
    }

    [Fact]
    public async Task Rechazos_se_registran_y_agrupan()
    {
        var rech = new RegistroRechazos(10);
        var s = new FacturacionService(new WsfeRechaza(), new ComprobanteValidator(TimeProvider.System), _reg, NullLogger<FacturacionService>.Instance, TimeProvider.System) { Rechazos = rech };
        await s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "r1", "c", default);
        await s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "r1", "c", default); // rechazado no se cachea: reintenta
        Assert.Equal(2, rech.Ultimos(10).Count);
        Assert.Equal((10063, "Factura individual", 2), rech.Frecuentes()[0]);
    }

    [Fact]
    public async Task Repetido_advertencias_referencia_y_resolver_pendiente()
    {
        var s = S();
        var d = Util.FacturaBValida(_hoy); d.ReferenciaExterna = "PED-1"; d.CbteFch = _hoy.AddDays(-1).ToString("yyyyMMdd");
        var r = await s.EmitirAsync(1, 6, d, "ref", "c", default);
        Assert.Equal("PED-1", r.Referencia);
        Assert.Contains(r.Advertencias!, a => a.Contains("no es la de hoy"));
        Assert.True((await s.EmitirAsync(1, 6, d, "ref", "c", default)).Repetido);
        Assert.Equal(1, new ConsultasService(_reg, _w).Buscar(new FiltroEmisiones { Referencia = "PED-1" }).Total);

        _w.FallaAntes = _ => new ArcaComunicacionException("t");
        await Assert.ThrowsAsync<ArcaComunicacionException>(() => s.EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "pend", "c", default));
        _w.FallaAntes = null;
        Assert.Equal("liberado", await s.ResolverPendienteAsync("c:pend", default));
        Assert.Equal("no-pendiente", await s.ResolverPendienteAsync("c:pend", default));
    }

    [Theory]
    [InlineData("con espacio")]
    [InlineData("ñandú")]
    public async Task Idempotency_key_ascii(string clave)
        => await Assert.ThrowsAsync<ValidacionException>(() => S().EmitirAsync(1, 6, Util.FacturaBValida(_hoy), clave, "c", default));
}

public class PersistenciaTests
{
    private static string Tmp() => Path.Combine(Path.GetTempPath(), "p_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Sqlite_crud_pendientes_y_migracion()
    {
        var dir = Tmp();
        try
        {
            var json = Path.Combine(dir, "json");
            var archivo = new RegistroEmisionesArchivo(json);
            archivo.Guardar(new RegistroEmision { Clave = "c:viejo", Hash = "h", CbteNro = 1, Estado = EstadoEmision.Autorizado });
            var db = new RegistroEmisionesSqlite(Path.Combine(dir, "r.db"), json);
            Assert.Equal(1, db.Migrados);
            db.Guardar(new RegistroEmision { Clave = "c:p", Hash = "h", CbteNro = 2, Estado = EstadoEmision.Pendiente, Detalle = new() { ReferenciaExterna = "X" } });
            Assert.Equal(2, db.Listar().Count());
            Assert.Single(db.Pendientes());
            db.Guardar(new RegistroEmision { Clave = "c:p", Hash = "h", CbteNro = 2, Estado = EstadoEmision.Autorizado });
            Assert.Empty(db.Pendientes());
            db.Eliminar("c:p");
            Assert.Null(db.Obtener("c:p"));
            Assert.Equal(1, new RegistroEmisionesSqlite(Path.Combine(dir, "r.db"), json).Cantidad); // no re-migra
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Sqlite_concurrente()
    {
        var dir = Tmp();
        try
        {
            var db = new RegistroEmisionesSqlite(Path.Combine(dir, "r.db"));
            await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() =>
                db.Guardar(new RegistroEmision { Clave = "c:" + i, Hash = "h", CbteNro = i, Estado = EstadoEmision.Autorizado }))));
            Assert.Equal(200, db.Cantidad);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void Cifrado_en_reposo()
    {
        var clave = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var cif = new CifradorRegistro(clave);
        var c = cif.Cifrar("{\"DocNro\":30500010912}");
        Assert.StartsWith("ENC1:", c);
        Assert.DoesNotContain("30500010912", c);
        Assert.Equal("{\"DocNro\":30500010912}", cif.Descifrar(c));
        Assert.Equal("en claro", cif.Descifrar("en claro"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => new CifradorRegistro(new byte[32]).Descifrar(c));

        var dir = Tmp();
        try
        {
            var r = new RegistroEmisionesArchivo(dir, cif);
            r.Guardar(new RegistroEmision { Clave = "c:x", Hash = "h", Detalle = new() { DocNro = 30500010912 } });
            Assert.DoesNotContain("30500010912", File.ReadAllText(Directory.GetFiles(dir).Single()));
            Assert.Equal(30500010912, new RegistroEmisionesArchivo(dir, cif).Obtener("c:x")!.Detalle!.DocNro);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Bloqueo_entre_procesos_serializa()
    {
        var dir = Tmp();
        try
        {
            var b = new BloqueoNumeracionArchivo(dir, TimeSpan.FromSeconds(5));
            var l1 = await b.AdquirirAsync(1, 6, default);
            var t2 = b.AdquirirAsync(1, 6, default);
            await Task.Delay(150);
            Assert.False(t2.IsCompleted);
            var otro = await b.AdquirirAsync(2, 6, default); // otra numeración no espera
            await otro.DisposeAsync();
            await l1.DisposeAsync();
            await (await t2).DisposeAsync();
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class Resiliencia3Tests
{
    private sealed class Contador : WsfeBase
    {
        public int Consultas, Tablas;
        public override Task<ComprobanteConsultado?> FECompConsultarAsync(int pv, int t, long n, CancellationToken ct)
        {
            Interlocked.Increment(ref Consultas);
            return Task.FromResult<ComprobanteConsultado?>(n == 1 ? new ComprobanteConsultado { Resultado = "A" } : null);
        }
        public override async Task<IReadOnlyList<ParametroItem>> FEParamGetTiposIvaAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Tablas);
            await Task.Delay(50, ct);
            return new[] { new ParametroItem { Id = "5" } };
        }
    }

    private static WsfeClienteResiliente R(Contador c) => new(c, new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 }),
        new CircuitBreaker(5, TimeSpan.FromSeconds(1), TimeProvider.System), Util.Opciones(), TimeProvider.System, NullLogger<WsfeClienteResiliente>.Instance);

    [Fact]
    public async Task Cachea_autorizados_pero_no_inexistentes()
    {
        var c = new Contador(); var r = R(c);
        await r.FECompConsultarAsync(1, 6, 1, default); await r.FECompConsultarAsync(1, 6, 1, default);
        await r.FECompConsultarAsync(1, 6, 2, default); await r.FECompConsultarAsync(1, 6, 2, default);
        Assert.Equal(3, c.Consultas);
    }

    [Fact]
    public async Task Single_flight_de_tablas()
    {
        var c = new Contador(); var r = R(c);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => r.FEParamGetTiposIvaAsync(default)));
        Assert.Equal(1, c.Tablas);
    }
}

public class Seguridad3Tests
{
    [Fact]
    public void Fuerza_bruta_bloquea_y_expira()
    {
        var reloj = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var p = new ProteccionFuerzaBruta(reloj) { MaxFallos = 3 };
        p.RegistrarFallo("1.1.1.1"); p.RegistrarFallo("1.1.1.1");
        Assert.False(p.Bloqueada("1.1.1.1"));
        p.RegistrarFallo("1.1.1.1");
        Assert.True(p.Bloqueada("1.1.1.1"));
        Assert.False(p.Bloqueada("2.2.2.2"));
        reloj.Advance(TimeSpan.FromMinutes(16));
        Assert.False(p.Bloqueada("1.1.1.1"));
    }

    [Fact]
    public void Firma_del_SDK_coincide_con_la_del_servicio()
    {
        var cuerpo = Encoding.UTF8.GetBytes("{\"a\":1}");
        Assert.Equal(FirmaSolicitudes.Calcular("s3cr3t", "1700000000", "POST", "/api/v1/x?a=1", cuerpo),
            ClienteFacturacion.Firmar("s3cr3t", "1700000000", "post", "/api/v1/x?a=1", cuerpo));
        Assert.NotEqual(FirmaSolicitudes.Calcular("s3cr3t", "1700000000", "POST", "/api/v1/x", cuerpo),
            FirmaSolicitudes.Calcular("s3cr3t", "1700000000", "DELETE", "/api/v1/x", cuerpo));
    }

    [Fact]
    public void Utilidades_de_seguridad()
    {
        Assert.Equal("ab", Sanitizar.Log("a\r\nb"));
        Assert.EndsWith("…", Sanitizar.Log(new string('x', 300)));
        var (clave, hash) = DirectorioClientes.GenerarClave();
        Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(clave))));
        Assert.Equal("20111111112", Arca.Facturacion.Core.Wsaa.CertificadoCmsFirmante.ExtraerCuit("CN=x, SERIALNUMBER=CUIT 20111111112, O=y"));
        Assert.Null(Arca.Facturacion.Core.Wsaa.CertificadoCmsFirmante.ExtraerCuit("CN=x"));
        var m = new EstadoMantenimiento();
        m.Activar("");
        Assert.True(m.Activo);
        m.Desactivar();
        Assert.False(m.Activo);
    }

    [Fact]
    public void Secretos_desde_archivo()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, "  clave-secreta\n");
            var r = SecretosArchivo.Leer(new System.Collections.Hashtable { ["Arca__CertificadoPassword_FILE"] = f, ["OTRA"] = "x", ["Falta_FILE"] = "/no/existe" });
            Assert.Equal("clave-secreta", r["Arca:CertificadoPassword"]);
            Assert.Single(r);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void Certificado_con_clave_corta_se_rechaza()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(1024);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=x", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        Assert.Throws<WsaaException>(() => new Arca.Facturacion.Core.Wsaa.CertificadoCmsFirmante(cert));
    }

    [Fact]
    public void Auditoria_detecta_alteraciones_y_purga()
    {
        var dir = Path.Combine(Path.GetTempPath(), "au_" + Guid.NewGuid().ToString("N"));
        try
        {
            var t = DateTimeOffset.UtcNow;
            using (var a = new AuditoriaArchivo(Path.Combine(dir, "aud.jsonl")))
            {
                for (var i = 0; i < 3; i++) a.Registrar(new EntradaAuditoria { Fecha = t, Cliente = "c", Operacion = "op" + i });
                a.Vaciar();
                Assert.True(a.Verificar().Integra);
            }
            // Simula que esas entradas son de hace 400 días: se renombra el archivo y se agregan entradas nuevas.
            var viejo = Directory.GetFiles(dir, "aud-*").Single();
            File.Move(viejo, Path.Combine(dir, "aud-20200101.jsonl"));
            using (var a = new AuditoriaArchivo(Path.Combine(dir, "aud.jsonl")))
            {
                a.Registrar(new EntradaAuditoria { Fecha = t, Cliente = "c", Operacion = "nueva" });
                a.Vaciar();
                Assert.True(a.Verificar().Integra);
                Assert.Equal(1, a.Purgar(TimeSpan.FromDays(365), t));
                Assert.True(a.Verificar().Integra); // la cadena sigue anclada al punto de corte
                var actual = Directory.GetFiles(dir, "aud-*").Single();
                File.WriteAllText(actual, File.ReadAllText(actual).Replace("nueva", "otra"));
                Assert.False(a.Verificar().Integra);           // alteración
                File.WriteAllText(actual, "");
                Assert.False(a.Verificar().Integra);           // borrado del final
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Exportador_Prometheus()
    {
        using var p = new ExportadorPrometheus();
        ArcaTelemetria.Emisiones.Add(1, new KeyValuePair<string, object?>("resultado", "A"));
        ArcaTelemetria.DuracionArca.Record(10, new KeyValuePair<string, object?>("metodo", "X"));
        var txt = p.Exponer();
        Assert.Contains("# TYPE arca_emisiones_total counter", txt);
        Assert.Contains("arca_emisiones_total{resultado=\"A\"}", txt);
        Assert.Contains("arca_duracion_count{metodo=\"X\"}", txt);
    }
}

public sealed class ApiV13Factory : WebApplicationFactory<Program>
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "v13_" + Guid.NewGuid().ToString("N"));
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseSetting("Arca:Cuit", Util.CuitEmisor);
        b.UseSetting("Arca:CertificadoPath", "no-usado.pfx");
        b.UseSetting("Arca:ModoSimulacion", "true");
        b.UseSetting("Arca:CacheTicketPath", "");
        b.UseSetting("Arca:RegistroEmisionesSqlite", Path.Combine(Dir, "registro.db"));
        b.UseSetting("Arca:BloqueoEntreProcesosPath", Path.Combine(Dir, "bloqueos"));
        b.UseSetting("Arca:AuditoriaPath", Path.Combine(Dir, "aud", "auditoria.jsonl"));
        b.UseSetting("Arca:ReceptoresPath", Path.Combine(Dir, "receptores"));
        b.UseSetting("Arca:SuscripcionesPath", Path.Combine(Dir, "suscripciones"));
        b.UseSetting("Arca:SuscripcionesIntervaloMinutos", "0");
        b.UseSetting("Arca:PrecargarParametros", "false");
        b.UseSetting("Arca:ReconciliadorIntervaloSegundos", "0");
        b.UseSetting("Arca:Emisor:RazonSocial", "Gestoría de Prueba SA");
        b.UseSetting("Swagger:Habilitado", "true");
        b.UseSetting("Seguridad:MaxIntentosFallidos", "3");
        b.UseSetting("Seguridad:Clientes:0:Nombre", "erp");
        b.UseSetting("Seguridad:Clientes:0:ClavesHash:0", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("k13"))));
        b.UseSetting("Seguridad:Clientes:0:Permisos:0", "emitir");
        b.UseSetting("Seguridad:Clientes:0:Permisos:1", "consultar");
        b.UseSetting("Seguridad:Clientes:0:Permisos:2", "admin");
        b.UseSetting("Seguridad:Clientes:1:Nombre", "firmado");
        b.UseSetting("Seguridad:Clientes:1:Claves:0", "k-firma");
        b.UseSetting("Seguridad:Clientes:1:SecretoFirma", "s3cr3t");
        b.UseEnvironment("Production");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

public class ApiV13Tests : IClassFixture<ApiV13Factory>
{
    private readonly ApiV13Factory _f;
    public ApiV13Tests(ApiV13Factory f) => _f = f;

    private HttpClient C(string clave = "k13")
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", clave);
        return c;
    }

    private static string Hoy() => new ComprobanteValidator(TimeProvider.System).HoyArgentina().ToString("yyyyMMdd");
    private static object Factura(string? receptorId = null, int pv = 50) => new
    {
        ptoVta = pv, cbteTipo = 6, receptorId,
        comprobante = new { concepto = 1, docTipo = 99, docNro = 0, cbteFch = Hoy(), impTotal = 121, impNeto = 100, impIVA = 21,
            monId = "PES", monCotiz = 1, condicionIVAReceptorId = 5, iva = new[] { new { id = 5, baseImp = 100, importe = 21 } } }
    };

    private static async Task<HttpResponseMessage> Post(HttpClient c, string url, object body, string? clave = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (clave != null) req.Headers.Add("Idempotency-Key", clave);
        return await c.SendAsync(req);
    }

    [Fact]
    public async Task Emision_simulada_con_receptor_de_agenda_location_y_replay()
    {
        var c = C();
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/v1/receptores/acme",
            new { nombre = "ACME", docTipo = 96, docNro = 30111222, condicionIVAReceptorId = 5 })).StatusCode);
        var r1 = await Post(c, "/api/v1/comprobantes", Factura("acme", 51), "e1");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal("/api/v1/comprobantes/51/6/1", r1.Headers.Location!.ToString());
        var j = await r1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("99", j.GetProperty("cae").GetString());
        var r2 = await Post(c, "/api/v1/comprobantes", Factura("acme", 51), "e1");
        Assert.Equal("true", r2.Headers.GetValues("Idempotency-Replayed").Single());
        var est = await (await Post(c, "/api/v1/comprobantes/emisiones/estado", new { claves = new[] { "e1", "nada" } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Autorizado", est.GetProperty("e1").GetProperty("estado").GetString());
        Assert.Equal(JsonValueKind.Null, est.GetProperty("nada").ValueKind);
        var html = await c.GetStringAsync("/api/v1/comprobantes/51/6/1/html?copia=DUPLICADO");
        Assert.Contains("de Prueba SA", html);
        Assert.Contains("30111222", html);
        var png = await c.GetByteArrayAsync("/api/v1/comprobantes/51/6/1/qr.png");
        Assert.Equal(0x89, png[0]);
        var prox = await c.GetFromJsonAsync<JsonElement>("/api/v1/comprobantes/proximo?ptoVta=51&cbteTipo=6");
        Assert.Equal(2, prox.GetProperty("proximo").GetInt32());
    }

    [Fact]
    public async Task Validar_sin_emitir()
    {
        var r = await (await Post(C(), "/api/v1/comprobantes/validar", Factura())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(r.GetProperty("valido").GetBoolean());
        var mal = new { ptoVta = 1, cbteTipo = 6, comprobante = new { concepto = 1, docTipo = 99, impTotal = 5, condicionIVAReceptorId = 5 } };
        var r2 = await (await Post(C(), "/api/v1/comprobantes/validar", mal)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(r2.GetProperty("valido").GetBoolean());
        Assert.True(r2.GetProperty("errores").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Suscripciones_por_api()
    {
        var c = C();
        var s = new { cbteTipo = 6, ptoVta = 52, periodicidad = "Mensual", proximaEjecucion = DateTime.UtcNow.AddHours(-3).ToString("yyyy-MM-dd"),
            comprobante = new { concepto = 1, docTipo = 99, impTotal = 121, impNeto = 100, impIVA = 21, monId = "PES", monCotiz = 1, condicionIVAReceptorId = 5,
                iva = new[] { new { id = 5, baseImp = 100, importe = 21 } } } };
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/v1/suscripciones/mensual", s)).StatusCode);
        var ej = await (await c.PostAsync("/api/v1/admin/suscripciones/ejecutar", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, ej.GetProperty("emitidas").GetInt32());
        var pausa = await (await c.PostAsync("/api/v1/suscripciones/mensual/pausar", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(pausa.GetProperty("activa").GetBoolean());
        Assert.Single((await c.GetFromJsonAsync<JsonElement>("/api/v1/suscripciones/mensual")).GetProperty("historial").EnumerateArray());
    }

    [Fact]
    public async Task Admin_reportes_cierre_rechazos_respaldo_y_auditoria()
    {
        var c = C();
        await Post(c, "/api/v1/comprobantes", Factura(pv: 53), "adm1");
        foreach (var url in new[] { "reportes/por-receptor", "reportes/por-dia", "reportes/por-punto-venta", "cierre-diario", "puntos-venta", "rechazos", "webhook/entregas", "auditoria/verificar" })
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/admin/" + url)).StatusCode);
        var resp = await c.GetAsync("/api/v1/admin/registro/exportar");
        var jsonl = await resp.Content.ReadAsStringAsync();
        Assert.Contains("adm1", jsonl);
        var imp = await c.PostAsync("/api/v1/admin/registro/importar", new StringContent(jsonl, Encoding.UTF8, "application/x-ndjson"));
        Assert.Equal(0, (await imp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("importados").GetInt32());
        var ver = await c.GetFromJsonAsync<JsonElement>("/api/v1/admin/auditoria/verificar");
        Assert.True(ver.GetProperty("integra").GetBoolean());
        Assert.Equal("no-pendiente", (await (await c.PostAsync("/api/v1/admin/emisiones/resolver?clave=erp:adm1", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("resultado").GetString());
    }

    [Fact]
    public async Task Mantenimiento_bloquea_emisiones_pero_no_consultas()
    {
        var c = C();
        await c.PostAsync("/api/v1/admin/mantenimiento?motivo=Actualizacion", null);
        try
        {
            var r = await Post(c, "/api/v1/comprobantes", Factura(pv: 54), "m1");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.Contains("MANTENIMIENTO", await r.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/catalogos")).StatusCode);
        }
        finally { await c.DeleteAsync("/api/v1/admin/mantenimiento"); }
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/v1/comprobantes", Factura(pv: 54), "m2")).StatusCode);
    }

    [Fact]
    public async Task Firma_de_solicitudes_requerida()
    {
        var sin = C("k-firma");
        Assert.Equal(HttpStatusCode.Unauthorized, (await sin.GetAsync("/api/v1/catalogos")).StatusCode);
        var sdk = new ClienteFacturacion(_f.CreateClient(), "k-firma", secretoFirma: "s3cr3t");
        var calc = await sdk.CalcularAsync(new SolicitudCalculo { CbteTipo = 6, Lineas = { new LineaImporte { Neto = 100 } } });
        Assert.Equal(121m, calc.ImpTotal);
        var malaFirma = new ClienteFacturacion(_f.CreateClient(), "k-firma", secretoFirma: "otra", reintentos: 0);
        await Assert.ThrowsAsync<ErrorFacturacionException>(() => malaFirma.CalcularAsync(new SolicitudCalculo { CbteTipo = 6 }));
    }

    [Fact]
    public async Task Problema_con_codigo_metricas_salud_y_swagger_protegido()
    {
        var c = C();
        var r = await Post(c, "/api/v1/comprobantes", new { ptoVta = 1, cbteTipo = 6, comprobante = new { concepto = 1, docTipo = 99, impTotal = 5, condicionIVAReceptorId = 5 } }, "p1");
        var pd = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDACION", pd.GetProperty("codigo").GetString());
        Assert.Equal("/docs/errores#validacion", pd.GetProperty("type").GetString());

        var anon = _f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/metrics")).StatusCode);
        var mr = await c.GetAsync("/metrics"); var m = await mr.Content.ReadAsStringAsync(); Assert.True(mr.IsSuccessStatusCode, m);
        Assert.Contains("arca_", m);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/health/startup")).StatusCode);
        var ready = await anon.GetFromJsonAsync<JsonElement>("/health/ready");
        Assert.Equal(JsonValueKind.String, ready.GetProperty("chequeos").GetProperty("registro").ValueKind); // sin detalle para anónimos
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/swagger/v1/swagger.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/swagger/v1/swagger.json")).StatusCode);
        var letras = await c.GetFromJsonAsync<JsonElement>("/api/v1/comprobantes/importe-en-letras?importe=1000.5");
        Assert.Equal("SON PESOS MIL CON 50/100", letras.GetProperty("texto").GetString());
    }

    [Fact]
    public async Task Fuerza_bruta_bloquea_la_IP()
    {
        var f = new ApiV13Factory();
        try
        {
            var c = f.CreateClient();
            c.DefaultRequestHeaders.Add("X-Api-Key", "mala");
            for (var i = 0; i < 3; i++) await c.GetAsync("/api/v1/catalogos");
            var bueno = f.CreateClient();
            bueno.DefaultRequestHeaders.Add("X-Api-Key", "k13");
            Assert.Equal((HttpStatusCode)429, (await bueno.GetAsync("/api/v1/catalogos")).StatusCode);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task SDK_nuevos_metodos()
    {
        var sdk = new ClienteFacturacion(_f.CreateClient(), "k13") { CorrelationId = "sdk-test" };
        var hoy = new ComprobanteValidator(TimeProvider.System).HoyArgentina();
        var r = await sdk.EmitirAsync(55, 6, Util.FacturaBValida(hoy));
        var v = await sdk.ValidarAsync(55, 6, Util.FacturaBValida(hoy));
        Assert.True(v.GetProperty("valido").GetBoolean());
        var nc = await sdk.AnularAsync(55, 6, r.CbteNro);
        Assert.Equal(8, nc.CbteTipo);
        var dup = await sdk.DuplicarAsync(55, 6, r.CbteNro);
        Assert.Equal(2, dup.CbteNro);
        var lote = await sdk.EmitirLoteAsync(new[] { (55, 6, Util.FacturaBValida(hoy), (string?)null) });
        Assert.Equal(200, lote[0].GetProperty("estado").GetInt32());
    }
}

public class PropiedadesCalculadoraTests
{
    [Fact]
    public void Resultados_aleatorios_siempre_son_validos_para_ARCA()
    {
        var rnd = new Random(20261001);
        var v = new ComprobanteValidator(TimeProvider.System);
        var hoy = v.HoyArgentina().ToString("yyyyMMdd");
        var alic = new[] { 3, 4, 5, 6, 8, 9 };
        for (var i = 0; i < 500; i++)
        {
            var tipo = new[] { 1, 6, 11 }[rnd.Next(3)];
            var s = new SolicitudCalculo { CbteTipo = tipo, PreciosConIvaIncluido = rnd.Next(2) == 0, Exento = tipo == 11 ? 0 : rnd.Next(0, 3) * 10.01m };
            for (var l = 0; l < rnd.Next(1, 12); l++)
                s.Lineas.Add(new LineaImporte
                {
                    Cantidad = rnd.Next(1, 20), PrecioUnitario = Math.Round((decimal)rnd.NextDouble() * 10000, 2),
                    BonificacionPorcentaje = rnd.Next(0, 4) * 5, AlicuotaIvaId = alic[rnd.Next(alic.Length)]
                });
            var d = CalculadoraImportes.Calcular(s);
            d.Concepto = 1; d.CbteFch = hoy; d.CondicionIVAReceptorId = tipo == 1 ? 1 : 5;
            if (tipo == 1) { d.DocTipo = 80; d.DocNro = 30500010912; } else { d.DocTipo = 99; }
            var errores = v.Validar(1, tipo, d);
            Assert.True(errores.Count == 0, $"Caso {i}: {string.Join(" | ", errores)}");
            Assert.Equal(d.ImpTotal, d.ImpTotConc + d.ImpNeto + d.ImpOpEx + d.ImpTrib + d.ImpIVA);
        }
    }
}
