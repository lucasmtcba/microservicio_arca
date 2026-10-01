using System.Collections.Concurrent;
using System.Globalization;
using Arca.Facturacion.Core.Wsfe;

namespace Arca.Facturacion.Core.Simulacion;

/// <summary>Implementación EN MEMORIA de WSFEv1 para que los sistemas consumidores integren y prueben sin certificado
/// ni conexión con ARCA (Arca:ModoSimulacion=true). Numeración correlativa real, CAE ficticio identificable
/// (empieza con 99) y observación 99999 "SIMULACION" en cada comprobante. Inyección opcional de fallas.
/// NUNCA tiene validez fiscal.</summary>
public sealed class WsfeSimulador : IWsfeClient
{
    public const int CodigoObservacion = 99999;
    private readonly ConcurrentDictionary<(int, int), long> _ultimos = new();
    private readonly ConcurrentDictionary<(int, int, long), ComprobanteConsultado> _emitidos = new();
    private readonly ConcurrentDictionary<(int, short), Caea> _caeas = new();
    private readonly object _lock = new();
    private readonly TimeProvider _reloj;
    private readonly int _fallasPorcentaje;
    private readonly Random _rnd;

    public WsfeSimulador(TimeProvider reloj, int fallasPorcentaje = 0, int? semilla = null)
    {
        _reloj = reloj;
        _fallasPorcentaje = Math.Clamp(fallasPorcentaje, 0, 100);
        _rnd = semilla is int s ? new Random(s) : new Random();
    }

    private string Hoy => _reloj.GetUtcNow().ToOffset(TimeSpan.FromHours(-3)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private void QuizasFallar()
    {
        if (_fallasPorcentaje == 0) return;
        int n;
        lock (_rnd) n = _rnd.Next(100);
        if (n < _fallasPorcentaje) throw new ArcaComunicacionException("Falla simulada (Arca:SimulacionFallasPorcentaje).");
    }

    private static ParametroItem P(object id, string desc) => new() { Id = Convert.ToString(id, CultureInfo.InvariantCulture), Desc = desc, FchDesde = "20100101", FchHasta = "NULL" };

    public Task<EstadoServidores> FEDummyAsync(CancellationToken ct) => Task.FromResult(new EstadoServidores { AppServer = "OK", DbServer = "OK", AuthServer = "OK" });

    public Task<RespuestaCae> FECAESolicitarAsync(SolicitudCae s, CancellationToken ct)
    {
        QuizasFallar();
        var r = new RespuestaCae { Cabecera = new CabeceraRespuesta { PtoVta = s.PtoVta, CbteTipo = s.CbteTipo, CantReg = s.Comprobantes.Count, FchProceso = Hoy } };
        lock (_lock)
        {
            foreach (var c in s.Comprobantes)
            {
                var ult = _ultimos.GetValueOrDefault((s.PtoVta, s.CbteTipo));
                if (c.CbteDesde != ult + 1)
                {
                    // Como ARCA real: el 10016 llega como observación del detalle, con Resultado R.
                    r.Detalles.Add(new DetalleRespuesta
                    {
                        Concepto = c.Concepto, DocTipo = c.DocTipo, DocNro = c.DocNro, CbteDesde = c.CbteDesde, CbteHasta = c.CbteHasta,
                        CbteFch = c.CbteFch ?? Hoy, Resultado = "R",
                        Observaciones = { new ArcaMensaje(10016, $"El número {c.CbteDesde} no es el próximo a autorizar ({ult + 1}).") }
                    });
                    continue;
                }
                _ultimos[(s.PtoVta, s.CbteTipo)] = c.CbteHasta;
                var cae = "99" + (s.PtoVta % 1000).ToString("D3", CultureInfo.InvariantCulture) + c.CbteDesde.ToString("D9", CultureInfo.InvariantCulture)[^9..];
                var vto = _reloj.GetUtcNow().AddDays(10).ToOffset(TimeSpan.FromHours(-3)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                var obs = new List<ArcaMensaje> { new(CodigoObservacion, "SIMULACION: comprobante sin validez fiscal.") };
                _emitidos[(s.PtoVta, s.CbteTipo, c.CbteDesde)] = new ComprobanteConsultado
                {
                    PtoVta = s.PtoVta, CbteTipo = s.CbteTipo, CbteDesde = c.CbteDesde, CbteHasta = c.CbteHasta, Concepto = c.Concepto,
                    DocTipo = c.DocTipo, DocNro = c.DocNro, CbteFch = c.CbteFch ?? Hoy, ImpTotal = c.ImpTotal, ImpTotConc = c.ImpTotConc,
                    ImpNeto = c.ImpNeto, ImpOpEx = c.ImpOpEx, ImpTrib = c.ImpTrib, ImpIVA = c.ImpIVA, FchServDesde = c.FchServDesde,
                    FchServHasta = c.FchServHasta, FchVtoPago = c.FchVtoPago, MonId = c.MonId, MonCotiz = c.MonCotiz, CanMisMonExt = c.CanMisMonExt,
                    CondicionIVAReceptorId = c.CondicionIVAReceptorId, Iva = c.Iva, Tributos = c.Tributos, CbtesAsoc = c.CbtesAsoc,
                    Opcionales = c.Opcionales, Compradores = c.Compradores, PeriodoAsoc = c.PeriodoAsoc, Actividades = c.Actividades, Resultado = "A", CodAutorizacion = cae, EmisionTipo = "CAE", FchVto = vto, FchProceso = Hoy,
                    Observaciones = obs
                };
                r.Detalles.Add(new DetalleRespuesta
                {
                    Concepto = c.Concepto, DocTipo = c.DocTipo, DocNro = c.DocNro, CbteDesde = c.CbteDesde, CbteHasta = c.CbteHasta,
                    CbteFch = c.CbteFch ?? Hoy, Resultado = "A", Cae = cae, CaeFchVto = vto, Observaciones = obs
                });
            }
        }
        r.Cabecera.Resultado = r.Detalles.All(d => d.Resultado == "A") ? "A" : r.Detalles.Any(d => d.Resultado == "A") ? "P" : "R";
        return Task.FromResult(r);
    }

    public Task<UltimoComprobante> FECompUltimoAutorizadoAsync(int pv, int t, CancellationToken ct)
    {
        QuizasFallar();
        return Task.FromResult(new UltimoComprobante { PtoVta = pv, CbteTipo = t, CbteNro = _ultimos.GetValueOrDefault((pv, t)) });
    }

    public Task<ComprobanteConsultado?> FECompConsultarAsync(int pv, int t, long n, CancellationToken ct)
        => Task.FromResult(_emitidos.TryGetValue((pv, t, n), out var c) ? c : null);

    public Task<int> FECompTotXRequestAsync(CancellationToken ct) => Task.FromResult(250);

    public Task<Caea> FECAEASolicitarAsync(int p, short o, CancellationToken ct) => Task.FromResult(_caeas.GetOrAdd((p, o), k => new Caea
    {
        Codigo = "99" + k.Item1.ToString(CultureInfo.InvariantCulture) + k.Item2.ToString("D2", CultureInfo.InvariantCulture) + "0000",
        Periodo = k.Item1, Orden = k.Item2, FchProceso = Hoy,
        FchVigDesde = $"{k.Item1}{(k.Item2 == 1 ? "01" : "16")}",
        FchVigHasta = $"{k.Item1}{(k.Item2 == 1 ? "15" : DateTime.DaysInMonth(k.Item1 / 100, k.Item1 % 100).ToString(CultureInfo.InvariantCulture))}"
    }));

    public Task<Caea?> FECAEAConsultarAsync(int p, short o, CancellationToken ct) => Task.FromResult(_caeas.TryGetValue((p, o), out var c) ? c : null);

    public Task<RespuestaCae> FECAEARegInformativoAsync(InformeCaea i, CancellationToken ct)
    {
        // Los comprobantes informados con CAEA consumen numeración: se actualiza el último autorizado.
        lock (_lock)
            foreach (var c in i.Comprobantes)
                _ultimos.AddOrUpdate((i.PtoVta, i.CbteTipo), c.CbteHasta, (_, u) => Math.Max(u, c.CbteHasta));
        return Task.FromResult(new RespuestaCae
        {
            Cabecera = new CabeceraRespuesta { Resultado = "A", PtoVta = i.PtoVta, CbteTipo = i.CbteTipo, CantReg = i.Comprobantes.Count },
            Detalles = i.Comprobantes.Select(c => new DetalleRespuesta { CbteDesde = c.CbteDesde, CbteHasta = c.CbteHasta, Resultado = "A", Cae = c.Caea }).ToList()
        });
    }

    public Task<CaeaSinMovimiento> FECAEASinMovimientoInformarAsync(int pv, string caea, CancellationToken ct)
        => Task.FromResult(new CaeaSinMovimiento { Caea = caea, PtoVta = pv, Resultado = "A", FchProceso = Hoy });

    public Task<IReadOnlyList<CaeaSinMovimiento>> FECAEASinMovimientoConsultarAsync(string caea, int pv, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CaeaSinMovimiento>>(Array.Empty<CaeaSinMovimiento>());

    private static Task<IReadOnlyList<ParametroItem>> L(params ParametroItem[] i) => Task.FromResult<IReadOnlyList<ParametroItem>>(i);

    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposCbteAsync(CancellationToken ct) =>
        L(Servicios.Catalogos.TiposComprobante.Select(k => P(k.Key, k.Value)).ToArray());
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposConceptoAsync(CancellationToken ct) =>
        L(P(1, "Producto"), P(2, "Servicios"), P(3, "Productos y Servicios"));
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposDocAsync(CancellationToken ct) =>
        L(Servicios.Catalogos.TiposDocumento.Select(k => P(k.Key, k.Value)).ToArray());
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposIvaAsync(CancellationToken ct) =>
        L(Servicios.Catalogos.AlicuotasIva.Select(k => P(k.Key, k.Value)).ToArray());
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposMonedasAsync(CancellationToken ct) =>
        L(P("PES", "Pesos Argentinos"), P("DOL", "Dólar Estadounidense"), P("060", "Euro"));
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposOpcionalAsync(CancellationToken ct) =>
        L(P("2101", "Excepcion computo IVA Credito Fiscal - CBU"), P("2102", "Alias"));
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposTributosAsync(CancellationToken ct) =>
        L(P(1, "Impuestos nacionales"), P(2, "Impuestos provinciales"), P(3, "Impuestos municipales"), P(4, "Impuestos Internos"), P(99, "Otro"));
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposPaisesAsync(CancellationToken ct) => L(P(200, "ARGENTINA"));
    public Task<IReadOnlyList<ParametroItem>> FEParamGetPtosVentaAsync(CancellationToken ct) =>
        L(Enumerable.Range(1, 10).Select(i => new ParametroItem { Id = i.ToString(CultureInfo.InvariantCulture), Atributos = { ["EmisionTipo"] = "CAE", ["Bloqueado"] = "N", ["FchBaja"] = "NULL" } }).ToArray());
    public Task<IReadOnlyList<ParametroItem>> FEParamGetActividadesAsync(CancellationToken ct) => L(P(1, "Actividad simulada"));
    public Task<IReadOnlyList<ParametroItem>> FEParamGetCondicionIvaReceptorAsync(string? clase, CancellationToken ct) =>
        L(Servicios.Catalogos.CondicionesIva.Select(k => P(k.Key, k.Value)).ToArray());
    public Task<Cotizacion> FEParamGetCotizacionAsync(string monId, string? f, CancellationToken ct) =>
        Task.FromResult(new Cotizacion { MonId = monId, MonCotiz = monId == "PES" ? 1 : 1000, FchCotiz = f ?? Hoy });
}
