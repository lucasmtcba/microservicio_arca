using Arca.Facturacion.Core;
using Arca.Facturacion.Core.Resiliencia;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Simulacion;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Arca.Facturacion.Tests;

/// <summary>Pruebas de la revisión del núcleo de emisión (numeración, reconciliación, persistencia y resiliencia).</summary>
public class RevisionNucleoTests
{
    private readonly WsfeSimulado _w = new();
    private readonly RegistroEmisionesMemoria _reg = new();
    private readonly ComprobanteValidator _val = new(TimeProvider.System);
    private readonly DateOnly _hoy;

    public RevisionNucleoTests() => _hoy = _val.HoyArgentina();

    private FacturacionService Servicio(IWsfeClient? w = null, IRegistroEmisiones? reg = null, TimeProvider? reloj = null) =>
        new(w ?? _w, _val, reg ?? _reg, NullLogger<FacturacionService>.Instance, reloj ?? TimeProvider.System, new[] { TimeSpan.Zero });

    private static string Tmp()
    {
        var d = Path.Combine(Path.GetTempPath(), "revnucleo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private RegistroEmision Pendiente(string clave, long nro, ComprobanteDetalle? det = null) => new()
    {
        Clave = clave, Hash = "h", PtoVta = 1, CbteTipo = 6, CbteNro = nro, Estado = EstadoEmision.Pendiente,
        Creado = DateTimeOffset.UtcNow.AddHours(-1), Detalle = det ?? Util.FacturaBValida(_hoy), Cliente = "c"
    };

    // ---------------------------------------------------------------- 1. pendientes locales y numeración

    [Fact]
    public async Task Pendiente_sin_confirmar_de_otra_clave_impide_numerar()
    {
        _reg.Guardar(Pendiente("c:otra", 1)); // número 1 reservado, ARCA aún no lo tiene
        var ex = await Assert.ThrowsAsync<ArcaComunicacionException>(() =>
            Servicio().EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "nueva", "c", default));
        Assert.Contains("pendiente", ex.Message);
        Assert.Equal(0, _w.Solicitudes); // no se pidió CAE con un número que podría duplicarse
        Assert.Equal(EstadoEmision.Pendiente, _reg.Obtener("c:otra")!.Estado);
    }

    [Fact]
    public async Task Pendiente_de_otra_clave_que_figura_en_ARCA_se_resuelve_y_se_numera_despues()
    {
        var det = Util.FacturaBValida(_hoy);
        _reg.Guardar(Pendiente("c:otra", 1, det));
        _w.Ultimos[(1, 6)] = 1;
        _w.Emitidos[(1, 6, 1)] = FacturacionService.Numerar(det, 1);
        var r = await Servicio().EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "nueva", "c", default);
        Assert.Equal(2, r.CbteNro);
        Assert.Equal(EstadoEmision.Autorizado, _reg.Obtener("c:otra")!.Estado);
    }

    [Fact]
    public async Task Reconciliacion_no_asigna_un_CAE_ya_asignado_a_otra_clave()
    {
        var det = Util.FacturaBValida(_hoy);
        var s = Servicio();
        var a = await s.EmitirAsync(1, 6, det, "a", "c", default);
        Assert.Equal(1, a.CbteNro);
        // Otra clave con el mismo contenido quedó "pendiente" con el mismo número (estado inconsistente).
        _reg.Guardar(Pendiente("c:b", 1, det));
        await Assert.ThrowsAsync<ArcaException>(() => s.ResolverPendienteAsync("c:b", default));
        Assert.Equal(EstadoEmision.Pendiente, _reg.Obtener("c:b")!.Estado);
        Assert.Equal(a.Cae, _reg.Obtener("c:a")!.Resultado!.Cae);
    }

    // ---------------------------------------------------------------- 2. bloqueo entre procesos en reconciliación

    private sealed class BloqueoContador : IBloqueoNumeracion
    {
        public int Adquiridos, Liberados;
        public Task<IAsyncDisposable> AdquirirAsync(int ptoVta, int cbteTipo, CancellationToken ct)
        {
            Interlocked.Increment(ref Adquiridos);
            return Task.FromResult<IAsyncDisposable>(new L(this));
        }
        private sealed class L : IAsyncDisposable
        {
            private readonly BloqueoContador _b;
            public L(BloqueoContador b) => _b = b;
            public ValueTask DisposeAsync() { Interlocked.Increment(ref _b.Liberados); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task Reconciliador_y_resolucion_manual_toman_el_bloqueo_entre_procesos()
    {
        var b = new BloqueoContador();
        var s = Servicio();
        s.Bloqueo = b;
        _reg.Guardar(Pendiente("c:p1", 1));
        Assert.Equal(1, await s.ReconciliarPendientesAsync(TimeSpan.Zero, default));
        Assert.Equal(1, b.Adquiridos);
        _reg.Guardar(Pendiente("c:p2", 1));
        Assert.Equal("liberado", await s.ResolverPendienteAsync("c:p2", default));
        Assert.Equal(2, b.Adquiridos);
        Assert.Equal(2, b.Liberados);
    }

    // ---------------------------------------------------------------- 3. migración JSON -> SQLite con cifrado

    [Fact]
    public void Migracion_a_SQLite_descifra_y_cuenta_ilegibles()
    {
        var dir = Tmp();
        try
        {
            var cif = new CifradorRegistro(new byte[32]);
            var json = Path.Combine(dir, "json");
            new RegistroEmisionesArchivo(json, cif).Guardar(new RegistroEmision { Clave = "c:cifrado", Hash = "h", CbteNro = 7, Estado = EstadoEmision.Autorizado });
            File.WriteAllText(Path.Combine(json, "danado.json"), "{no es json");
            var db = new RegistroEmisionesSqlite(Path.Combine(dir, "r.db"), json, cif);
            Assert.Equal(1, db.Migrados);
            Assert.Equal(1, db.MigracionIlegibles);
            Assert.Equal(7, db.Obtener("c:cifrado")!.CbteNro);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------- 4. 10016 en observaciones del detalle

    /// <summary>Usa el simulador del núcleo; antes del primer FECAESolicitar "otro sistema" toma el número.</summary>
    private sealed class SimuladorConIntruso : WsfeBase
    {
        public readonly WsfeSimulador Sim = new(TimeProvider.System);
        private int _intrusiones;
        public override Task<UltimoComprobante> FECompUltimoAutorizadoAsync(int pv, int t, CancellationToken ct) => Sim.FECompUltimoAutorizadoAsync(pv, t, ct);
        public override Task<ComprobanteConsultado?> FECompConsultarAsync(int pv, int t, long n, CancellationToken ct) => Sim.FECompConsultarAsync(pv, t, n, ct);
        public override async Task<RespuestaCae> FECAESolicitarAsync(SolicitudCae s, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _intrusiones) == 1)
                await Sim.FECAESolicitarAsync(new SolicitudCae { PtoVta = s.PtoVta, CbteTipo = s.CbteTipo, Comprobantes =
                    { new ComprobanteNumerado { CbteDesde = s.Comprobantes[0].CbteDesde, CbteHasta = s.Comprobantes[0].CbteDesde, DocTipo = 80, DocNro = 1, ImpTotal = 5 } } }, ct);
            return await Sim.FECAESolicitarAsync(s, ct);
        }
    }

    [Fact]
    public async Task Error_10016_en_observaciones_del_detalle_renumera()
    {
        var w = new SimuladorConIntruso();
        var r = await Servicio(w).EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "k", "c", default);
        Assert.True(r.Aprobado);
        Assert.Equal(2, r.CbteNro);
    }

    [Fact]
    public async Task Simulador_informa_10016_en_detalle_actualiza_ultimo_por_CAEA_y_conserva_datos()
    {
        var s = new WsfeSimulador(TimeProvider.System);
        var mal = await s.FECAESolicitarAsync(new SolicitudCae { PtoVta = 2, CbteTipo = 6, Comprobantes = { new ComprobanteNumerado { CbteDesde = 5, CbteHasta = 5 } } }, default);
        Assert.Empty(mal.Errores);
        Assert.Equal("R", mal.Detalles[0].Resultado);
        Assert.Contains(mal.Detalles[0].Observaciones, o => o.Code == 10016);

        await s.FECAEARegInformativoAsync(new InformeCaea { PtoVta = 2, CbteTipo = 6, Comprobantes = { new ComprobanteCaea { CbteDesde = 1, CbteHasta = 3, Caea = "x" } } }, default);
        Assert.Equal(3, (await s.FECompUltimoAutorizadoAsync(2, 6, default)).CbteNro);

        await s.FECAESolicitarAsync(new SolicitudCae { PtoVta = 2, CbteTipo = 6, Comprobantes = { new ComprobanteNumerado
        {
            CbteDesde = 4, CbteHasta = 4, Actividades = new() { 620100 }, PeriodoAsoc = new PeriodoAsociado { FchDesde = "20260901", FchHasta = "20260930" }
        } } }, default);
        var c = await s.FECompConsultarAsync(2, 6, 4, default);
        Assert.Equal(620100, c!.Actividades![0]);
        Assert.Equal("20260901", c.PeriodoAsoc!.FchDesde);
    }

    // ---------------------------------------------------------------- 5. single-flight sin token del primer llamador

    private sealed class TablasLentas : WsfeBase
    {
        public int Llamadas;
        public override async Task<IReadOnlyList<ParametroItem>> FEParamGetTiposCbteAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Llamadas);
            await Task.Delay(300, ct);
            return new[] { new ParametroItem { Id = "1", Desc = "x" } };
        }
    }

    [Fact]
    public async Task Cancelar_el_primer_llamador_no_cancela_la_llamada_compartida()
    {
        var inner = new TablasLentas();
        using var r = new WsfeClienteResiliente(inner, new MemoryCache(new MemoryCacheOptions()), new CircuitBreaker(5, TimeSpan.FromSeconds(1), TimeProvider.System),
            Util.Opciones(), TimeProvider.System, NullLogger<WsfeClienteResiliente>.Instance, Array.Empty<TimeSpan>());
        using var cts = new CancellationTokenSource(50);
        var primero = r.FEParamGetTiposCbteAsync(cts.Token);
        var segundo = r.FEParamGetTiposCbteAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => primero);
        Assert.Single(await segundo);
        Assert.Equal(1, inner.Llamadas);
    }

    // ---------------------------------------------------------------- 6 y 8. validación de la clave

    [Fact]
    public async Task Idempotency_key_con_dos_puntos_se_rechaza()
    {
        var ex = await Assert.ThrowsAsync<ValidacionException>(() => Servicio().EmitirAsync(1, 6, Util.FacturaBValida(_hoy), "a:b", "c", default));
        Assert.Contains("':'", string.Join(" ", ex.Errores));
        Assert.Equal(0, _w.Solicitudes);
    }

    [Fact]
    public async Task Clave_compuesta_demasiado_larga_se_rechaza_antes_de_ARCA()
    {
        var cliente = new string('x', 150);
        var ex = await Assert.ThrowsAsync<ValidacionException>(() => Servicio().EmitirAsync(1, 6, Util.FacturaBValida(_hoy), new string('k', 50), cliente, default));
        Assert.Contains("191", string.Join(" ", ex.Errores));
        Assert.Equal(0, _w.Solicitudes);
    }

    [Fact]
    public void MySql_rechaza_clave_mayor_a_191()
    {
        const string cs = "Server=localhost;User ID=arca;Password=arca;Database=arca_test;Connection Timeout=3";
        try { using var c = new MySqlConnector.MySqlConnection(cs); c.Open(); }
        catch (Exception ex) when (ex is MySqlConnector.MySqlException or InvalidOperationException) { return; }
        var r = new RegistroEmisionesMySql(cs, tabla: "t_" + Guid.NewGuid().ToString("N")[..12]);
        Assert.Throws<ArgumentException>(() => r.Guardar(new RegistroEmision { Clave = new string('z', 192), Hash = "h" }));
    }

    // ---------------------------------------------------------------- 7. pendiente que desaparece antes del bloqueo

    /// <summary>La primera lectura devuelve un pendiente "viejo" (como si lo liberara el reconciliador justo después).</summary>
    private sealed class RegistroQueLiberaAlReleer : IRegistroEmisiones
    {
        private readonly RegistroEmisionesMemoria _m = new();
        public RegistroEmision? Fantasma;
        public RegistroEmision? Obtener(string clave)
        {
            var f = Interlocked.Exchange(ref Fantasma, null);
            return f ?? _m.Obtener(clave);
        }
        public void Guardar(RegistroEmision r) => _m.Guardar(r);
        public void Eliminar(string clave) => _m.Eliminar(clave);
        public IEnumerable<RegistroEmision> Listar() => _m.Listar();
    }

    [Fact]
    public async Task Si_el_pendiente_desaparece_antes_del_bloqueo_se_reinicia_el_flujo()
    {
        var det = Util.FacturaBValida(_hoy);
        det.ReferenciaExterna = "nuevo";
        // Hash real del contenido: se obtiene de una emisión en otro registro.
        var aux = new RegistroEmisionesMemoria();
        await Servicio(new WsfeSimulado(), aux).EmitirAsync(1, 6, det, "k", "c", default);
        var hash = aux.Obtener("c:k")!.Hash;

        var viejo = Util.FacturaBValida(_hoy);
        viejo.ReferenciaExterna = "viejo";
        var reg = new RegistroQueLiberaAlReleer
        {
            Fantasma = new RegistroEmision { Clave = "c:k", Hash = hash, PtoVta = 1, CbteTipo = 6, CbteNro = 9, Estado = EstadoEmision.Pendiente, Detalle = viejo }
        };
        var r = await Servicio(reg: reg).EmitirAsync(1, 6, det, "k", "c", default);
        Assert.Equal("nuevo", r.Referencia);
        Assert.Equal(1, r.CbteNro);
    }

    // ---------------------------------------------------------------- 9. lecturas tolerantes

    [Fact]
    public void Sqlite_Obtener_fila_ilegible_da_error_claro_y_no_null()
    {
        var dir = Tmp();
        try
        {
            var archivo = Path.Combine(dir, "r.db");
            var db = new RegistroEmisionesSqlite(archivo);
            db.Guardar(new RegistroEmision { Clave = "c:x", Hash = "h", CbteNro = 1, Estado = EstadoEmision.Autorizado });
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + archivo))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE emisiones SET json='{dañado' WHERE clave='c:x'";
                cmd.ExecuteNonQuery();
            }
            var ex = Assert.Throws<InvalidOperationException>(() => db.Obtener("c:x"));
            Assert.Contains("no se puede leer", ex.Message);
            Assert.Empty(db.Listar());
            Assert.Equal(1, db.UltimasIlegibles);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void Plantillas_y_repositorio_JSON_toleran_archivos_danados()
    {
        var dir = Tmp();
        try
        {
            var p = new RepositorioPlantillas(dir);
            p.Guardar("c", new PlantillaComprobante { Id = "ok", PtoVta = 1, CbteTipo = 6 });
            var buena = Directory.GetFiles(dir, "*.json").Single();
            File.WriteAllText(buena.Replace("__ok.json", "__mala.json", StringComparison.Ordinal), "{roto");
            Assert.Equal("ok", Assert.Single(p.Listar("c")).Id);

            var rdir = Path.Combine(dir, "receptores");
            var agenda = new RepositorioJson<Receptor>(rdir);
            agenda.Guardar("c", "r1", new Receptor { Id = "r1", Nombre = "X" });
            File.WriteAllText(Directory.GetFiles(rdir, "*.json").Single(), "{roto");
            Assert.Null(agenda.Obtener("c", "r1"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------- 10. semiabierto: una sola sonda

    [Fact]
    public void Circuito_semiabierto_deja_pasar_una_sola_sonda()
    {
        var reloj = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var b = new CircuitBreaker(1, TimeSpan.FromSeconds(10), reloj);
        b.RegistrarFalla();
        Assert.Throws<CircuitoAbiertoException>(b.Verificar);
        reloj.Advance(TimeSpan.FromSeconds(11));
        b.Verificar(); // la sonda
        Assert.Throws<CircuitoAbiertoException>(b.Verificar); // concurrentes: rechazadas
        b.RegistrarExito();
        b.Verificar();
        b.Verificar();
        Assert.Equal("Cerrado", b.Estado);
    }

    [Fact]
    public void Sonda_colgada_se_reemplaza_tras_la_pausa_y_su_falla_reabre()
    {
        var reloj = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var b = new CircuitBreaker(1, TimeSpan.FromSeconds(10), reloj);
        b.RegistrarFalla();
        reloj.Advance(TimeSpan.FromSeconds(11));
        b.Verificar(); // sonda que nunca informa resultado
        reloj.Advance(TimeSpan.FromSeconds(11));
        b.Verificar(); // nueva sonda permitida
        b.RegistrarFalla();
        Assert.Equal("Abierto", b.Estado);
    }

    // ---------------------------------------------------------------- 11. purga con lápida

    [Fact]
    public async Task Purga_deja_lapida_y_el_reintento_no_reemite()
    {
        var reloj = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var s = Servicio(reloj: reloj);
        var det = Util.FacturaBValida(_hoy);
        var r1 = await s.EmitirAsync(1, 6, det, "k", "c", default);
        reloj.Advance(TimeSpan.FromDays(40));
        Assert.Equal(1, s.Purgar(TimeSpan.FromDays(30)));
        Assert.Equal(0, s.Purgar(TimeSpan.FromDays(30))); // la lápida no se vuelve a purgar
        var lapida = _reg.Obtener("c:k")!;
        Assert.Null(lapida.Detalle);
        Assert.Equal(r1.Cae, lapida.Resultado!.Cae);

        var r2 = await s.EmitirAsync(1, 6, det, "k", "c", default);
        Assert.True(r2.Repetido);
        Assert.Equal(r1.Cae, r2.Cae);
        Assert.Equal(1, _w.Solicitudes);
        var otro = Util.FacturaBValida(_hoy); otro.ImpNeto = 200; otro.ImpIVA = 42; otro.ImpTotal = 242; otro.Iva![0].BaseImp = 200; otro.Iva[0].Importe = 42;
        await Assert.ThrowsAsync<IdempotenciaConflictoException>(() => s.EmitirAsync(1, 6, otro, "k", "c", default));
    }
}
