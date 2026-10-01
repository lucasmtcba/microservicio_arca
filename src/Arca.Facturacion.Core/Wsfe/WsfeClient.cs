using System.Globalization;
using System.Xml.Linq;
using Arca.Facturacion.Core.Configuracion;
using Arca.Facturacion.Core.Soap;
using Arca.Facturacion.Core.Wsaa;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arca.Facturacion.Core.Wsfe;

/// <summary>Todos los métodos expuestos por WSFEv1 (Factura Electrónica, RG 4291 y modificatorias).</summary>
public interface IWsfeClient
{
    Task<EstadoServidores> FEDummyAsync(CancellationToken ct);
    Task<RespuestaCae> FECAESolicitarAsync(SolicitudCae solicitud, CancellationToken ct);
    Task<UltimoComprobante> FECompUltimoAutorizadoAsync(int ptoVta, int cbteTipo, CancellationToken ct);
    /// <summary>Devuelve null si el comprobante no existe (error 602).</summary>
    Task<ComprobanteConsultado?> FECompConsultarAsync(int ptoVta, int cbteTipo, long cbteNro, CancellationToken ct);
    Task<int> FECompTotXRequestAsync(CancellationToken ct);
    Task<Caea> FECAEASolicitarAsync(int periodo, short orden, CancellationToken ct);
    Task<Caea?> FECAEAConsultarAsync(int periodo, short orden, CancellationToken ct);
    Task<RespuestaCae> FECAEARegInformativoAsync(InformeCaea informe, CancellationToken ct);
    Task<CaeaSinMovimiento> FECAEASinMovimientoInformarAsync(int ptoVta, string caea, CancellationToken ct);
    Task<IReadOnlyList<CaeaSinMovimiento>> FECAEASinMovimientoConsultarAsync(string caea, int ptoVta, CancellationToken ct);
    Task<IReadOnlyList<ParametroItem>> FEParamGetTiposCbteAsync(CancellationToken ct);
    Task<IReadOnlyList<ParametroItem>> FEParamGetTiposConceptoAsync(CancellationToken ct);
    Task<IReadOnlyList<ParametroItem>> FEParamGetTiposDocAsync(CancellationToken ct);
    Task<IReadOnlyList<ParametroItem>> FEParamGetTiposIvaAsync(CancellationToken ct);
    Task<IReadOnlyList<ParametroItem>> FEParamGetTiposMonedasAsync(CancellationToken ct);
    Task<IReadOnlyList<ParametroItem>> FEParamGetTiposOpcionalAsync(CancellationToken ct);
    Task<IReadOnlyList<ParametroItem>> FEParamGetTiposTributosAsync(CancellationToken ct);
    Task<IReadOnlyList<ParametroItem>> FEParamGetTiposPaisesAsync(CancellationToken ct);
    Task<IReadOnlyList<ParametroItem>> FEParamGetPtosVentaAsync(CancellationToken ct);
    Task<IReadOnlyList<ParametroItem>> FEParamGetActividadesAsync(CancellationToken ct);
    Task<IReadOnlyList<ParametroItem>> FEParamGetCondicionIvaReceptorAsync(string? claseCmp, CancellationToken ct);
    Task<Cotizacion> FEParamGetCotizacionAsync(string monId, string? fchCotiz, CancellationToken ct);
}

public sealed class WsfeClient : IWsfeClient
{
    public const string Servicio = "wsfe";
    public static readonly XNamespace Ns = "http://ar.gov.afip.dif.FEV1/";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly SoapInvoker _soap;
    private readonly ITicketAccesoProvider _tickets;
    private readonly ArcaOptions _opt;
    private readonly ILogger<WsfeClient> _log;

    public const string NombreHttpClient = "arca-wsfe";

    public WsfeClient(IHttpClientFactory http, ITicketAccesoProvider tickets, IOptions<ArcaOptions> opt, ILogger<WsfeClient> log,
        Resiliencia.EstadoArca? estado = null, TimeProvider? reloj = null)
    {
        var r = reloj ?? TimeProvider.System;
        _soap = new SoapInvoker(() => http.CreateClient(NombreHttpClient),
            estado == null ? null : fecha =>
            {
                estado.RegistrarRespuesta(r.GetUtcNow(), fecha);
                if (fecha is { } f && (r.GetUtcNow() - f).Duration() > TimeSpan.FromMinutes(5))
                    log.LogWarning("Desfasaje de reloj con ARCA de {Seg:N0} s: puede invalidar el TA", (r.GetUtcNow() - f).TotalSeconds);
            });
        _tickets = tickets;
        _opt = opt.Value;
        _log = log;
    }

    // ---------------------------------------------------------------- infraestructura

    private async Task<XElement> InvocarAsync(string metodo, Func<XElement?, IEnumerable<object>> args, bool conAuth, CancellationToken ct)
    {
        for (var intento = 0; ; intento++)
        {
            XElement? auth = null;
            if (conAuth)
            {
                var ta = await _tickets.ObtenerAsync(Servicio, ct).ConfigureAwait(false);
                auth = new XElement(Ns + "Auth",
                    new XElement(Ns + "Token", ta.Token),
                    new XElement(Ns + "Sign", ta.Sign),
                    new XElement(Ns + "Cuit", _opt.Cuit));
            }
            var cuerpo = new XElement(Ns + metodo, args(auth));
            var resp = await _soap.InvocarAsync(_opt.WsfeUrlEfectiva, Ns.NamespaceName + metodo, cuerpo, ct).ConfigureAwait(false);
            var result = resp.Element(Ns + (metodo + "Result"))
                         ?? throw new ArcaComunicacionException($"Respuesta de {metodo} sin {metodo}Result.");

            // 600 = token/firma inválidos o vencidos: se invalida el TA y se reintenta UNA vez.
            var errores = Mensajes(result.Element(Ns + "Errors"), "Err");
            if (conAuth && intento == 0 && errores.Any(e => e.Code == 600))
            {
                _log.LogWarning("WSFE rechazó el TA (600) en {Metodo}; se renueva y reintenta", metodo);
                _tickets.Invalidar(Servicio);
                continue;
            }
            return result;
        }
    }

    private Task<XElement> InvocarAsync(string metodo, CancellationToken ct, params object[] extra)
        => InvocarAsync(metodo, auth => new object?[] { auth }.Concat(extra).Where(o => o != null)!, true, ct);

    private static void LanzarSiErrores(XElement result, string metodo, params int[] ignorar)
    {
        var err = Mensajes(result.Element(Ns + "Errors"), "Err").Where(e => !ignorar.Contains(e.Code)).ToList();
        if (err.Count > 0)
            throw new ArcaException($"{metodo}: " + string.Join("; ", err.Select(e => $"[{e.Code}] {e.Msg}")), err);
    }

    internal static List<ArcaMensaje> Mensajes(XElement? cont, string item) =>
        cont?.Elements(Ns + item).Select(e => new ArcaMensaje(
            int.TryParse(V(e, "Code"), NumberStyles.Integer, Inv, out var c) ? c : 0, V(e, "Msg") ?? string.Empty)).ToList()
        ?? new List<ArcaMensaje>();

    private static string? V(XElement? e, string n) => e?.Element(Ns + n)?.Value;
    private static int I(XElement? e, string n) => int.TryParse(V(e, n), NumberStyles.Integer, Inv, out var v) ? v : 0;
    private static long L(XElement? e, string n) => long.TryParse(V(e, n), NumberStyles.Integer, Inv, out var v) ? v : 0;
    private static decimal D(XElement? e, string n) => decimal.TryParse(V(e, n), NumberStyles.Number, Inv, out var v) ? v : 0;
    internal static string F(decimal d) => d.ToString("0.######", Inv);
    private static XElement E(string n, object? v) => new(Ns + n, v is decimal d ? F(d) : Convert.ToString(v, Inv));
    private static XElement? EOpt(string n, string? v) => string.IsNullOrWhiteSpace(v) ? null : E(n, v);

    // ---------------------------------------------------------------- serialización de detalle

    internal static XElement SerializarDetalle(ComprobanteNumerado c, string nombre)
    {
        var x = new XElement(Ns + nombre,
            E("Concepto", c.Concepto), E("DocTipo", c.DocTipo), E("DocNro", c.DocNro),
            E("CbteDesde", c.CbteDesde), E("CbteHasta", c.CbteHasta),
            EOpt("CbteFch", c.CbteFch),
            E("ImpTotal", c.ImpTotal), E("ImpTotConc", c.ImpTotConc), E("ImpNeto", c.ImpNeto),
            E("ImpOpEx", c.ImpOpEx), E("ImpTrib", c.ImpTrib), E("ImpIVA", c.ImpIVA),
            EOpt("FchServDesde", c.FchServDesde), EOpt("FchServHasta", c.FchServHasta), EOpt("FchVtoPago", c.FchVtoPago),
            E("MonId", c.MonId), E("MonCotiz", c.MonCotiz),
            EOpt("CanMisMonExt", c.CanMisMonExt),
            c.CondicionIVAReceptorId is int civa ? E("CondicionIVAReceptorId", civa) : null);

        if (c.CbtesAsoc is { Count: > 0 })
            x.Add(new XElement(Ns + "CbtesAsoc", c.CbtesAsoc.Select(a => new XElement(Ns + "CbteAsoc",
                E("Tipo", a.Tipo), E("PtoVta", a.PtoVta), E("Nro", a.Nro), EOpt("Cuit", a.Cuit), EOpt("CbteFch", a.CbteFch)))));
        if (c.Tributos is { Count: > 0 })
            x.Add(new XElement(Ns + "Tributos", c.Tributos.Select(t => new XElement(Ns + "Tributo",
                E("Id", t.Id), EOpt("Desc", t.Desc), E("BaseImp", t.BaseImp), E("Alic", t.Alic), E("Importe", t.Importe)))));
        if (c.Iva is { Count: > 0 })
            x.Add(new XElement(Ns + "Iva", c.Iva.Select(i => new XElement(Ns + "AlicIva",
                E("Id", i.Id), E("BaseImp", i.BaseImp), E("Importe", i.Importe)))));
        if (c.Opcionales is { Count: > 0 })
            x.Add(new XElement(Ns + "Opcionales", c.Opcionales.Select(o => new XElement(Ns + "Opcional",
                E("Id", o.Id), E("Valor", o.Valor)))));
        if (c.Compradores is { Count: > 0 })
            x.Add(new XElement(Ns + "Compradores", c.Compradores.Select(o => new XElement(Ns + "Comprador",
                E("DocTipo", o.DocTipo), E("DocNro", o.DocNro), E("Porcentaje", o.Porcentaje)))));
        if (c.PeriodoAsoc != null)
            x.Add(new XElement(Ns + "PeriodoAsoc", E("FchDesde", c.PeriodoAsoc.FchDesde), E("FchHasta", c.PeriodoAsoc.FchHasta)));
        if (c.Actividades is { Count: > 0 })
            x.Add(new XElement(Ns + "Actividades", c.Actividades.Select(a => new XElement(Ns + "Actividad", E("Id", a)))));

        if (c is ComprobanteCaea caea)
        {
            x.Add(E("CAEA", caea.Caea));
            if (!string.IsNullOrWhiteSpace(caea.CbteFchHsGen)) x.Add(E("CbteFchHsGen", caea.CbteFchHsGen));
        }
        return x;
    }

    private static RespuestaCae ParsearRespuestaCae(XElement r, string detNombre)
    {
        var cab = r.Element(Ns + "FeCabResp");
        return new RespuestaCae
        {
            Cabecera = cab == null ? null : new CabeceraRespuesta
            {
                Cuit = L(cab, "Cuit"), PtoVta = I(cab, "PtoVta"), CbteTipo = I(cab, "CbteTipo"),
                FchProceso = V(cab, "FchProceso"), CantReg = I(cab, "CantReg"),
                Resultado = V(cab, "Resultado"), Reproceso = V(cab, "Reproceso")
            },
            Detalles = r.Element(Ns + "FeDetResp")?.Elements(Ns + detNombre).Select(d => new DetalleRespuesta
            {
                Concepto = I(d, "Concepto"), DocTipo = I(d, "DocTipo"), DocNro = L(d, "DocNro"),
                CbteDesde = L(d, "CbteDesde"), CbteHasta = L(d, "CbteHasta"), CbteFch = V(d, "CbteFch"),
                Resultado = V(d, "Resultado"),
                Cae = V(d, "CAE") ?? V(d, "CAEA"),
                CaeFchVto = V(d, "CAEFchVto"),
                Observaciones = Mensajes(d.Element(Ns + "Observaciones"), "Obs")
            }).ToList() ?? new(),
            Errores = Mensajes(r.Element(Ns + "Errors"), "Err"),
            Eventos = Mensajes(r.Element(Ns + "Events"), "Evt")
        };
    }

    private static List<ParametroItem> ParsearParametros(XElement r)
    {
        var comunes = new HashSet<string> { "Id", "Desc", "FchDesde", "FchHasta" };
        return r.Element(Ns + "ResultGet")?.Elements().Select(e => new ParametroItem
        {
            Id = V(e, "Id") ?? V(e, "Nro"),
            Desc = V(e, "Desc"),
            FchDesde = V(e, "FchDesde"),
            FchHasta = V(e, "FchHasta"),
            Atributos = e.Elements().Where(c => !comunes.Contains(c.Name.LocalName))
                .GroupBy(c => c.Name.LocalName).ToDictionary(g => g.Key, g => g.First().Value)
        }).ToList() ?? new();
    }

    private static Caea ParsearCaea(XElement rg) => new()
    {
        Codigo = V(rg, "CAEA"), Periodo = I(rg, "Periodo"), Orden = I(rg, "Orden"),
        FchVigDesde = V(rg, "FchVigDesde"), FchVigHasta = V(rg, "FchVigHasta"),
        FchTopeInf = V(rg, "FchTopeInf"), FchProceso = V(rg, "FchProceso"),
        Observaciones = Mensajes(rg.Element(Ns + "Observaciones"), "Obs")
    };

    // ---------------------------------------------------------------- métodos

    public async Task<EstadoServidores> FEDummyAsync(CancellationToken ct)
    {
        var r = await InvocarAsync("FEDummy", _ => Array.Empty<object>(), false, ct).ConfigureAwait(false);
        return new EstadoServidores { AppServer = V(r, "AppServer"), DbServer = V(r, "DbServer"), AuthServer = V(r, "AuthServer") };
    }

    public async Task<RespuestaCae> FECAESolicitarAsync(SolicitudCae s, CancellationToken ct)
    {
        var req = new XElement(Ns + "FeCAEReq",
            new XElement(Ns + "FeCabReq", E("CantReg", s.Comprobantes.Count), E("PtoVta", s.PtoVta), E("CbteTipo", s.CbteTipo)),
            new XElement(Ns + "FeDetReq", s.Comprobantes.Select(c => SerializarDetalle(c, "FECAEDetRequest"))));
        var r = await InvocarAsync("FECAESolicitar", ct, req).ConfigureAwait(false);
        return ParsearRespuestaCae(r, "FECAEDetResponse");
    }

    public async Task<UltimoComprobante> FECompUltimoAutorizadoAsync(int ptoVta, int cbteTipo, CancellationToken ct)
    {
        var r = await InvocarAsync("FECompUltimoAutorizado", ct, E("PtoVta", ptoVta), E("CbteTipo", cbteTipo)).ConfigureAwait(false);
        LanzarSiErrores(r, "FECompUltimoAutorizado");
        return new UltimoComprobante { PtoVta = I(r, "PtoVta"), CbteTipo = I(r, "CbteTipo"), CbteNro = L(r, "CbteNro") };
    }

    public async Task<ComprobanteConsultado?> FECompConsultarAsync(int ptoVta, int cbteTipo, long cbteNro, CancellationToken ct)
    {
        var r = await InvocarAsync("FECompConsultar", ct, new XElement(Ns + "FeCompConsReq",
            E("CbteTipo", cbteTipo), E("CbteNro", cbteNro), E("PtoVta", ptoVta))).ConfigureAwait(false);
        var errs = Mensajes(r.Element(Ns + "Errors"), "Err");
        if (errs.Any(e => e.Code == 602)) return null; // No existen datos
        LanzarSiErrores(r, "FECompConsultar");
        var g = r.Element(Ns + "ResultGet");
        if (g == null) return null;
        return new ComprobanteConsultado
        {
            Concepto = I(g, "Concepto"), DocTipo = I(g, "DocTipo"), DocNro = L(g, "DocNro"),
            CbteDesde = L(g, "CbteDesde"), CbteHasta = L(g, "CbteHasta"), CbteFch = V(g, "CbteFch"),
            ImpTotal = D(g, "ImpTotal"), ImpTotConc = D(g, "ImpTotConc"), ImpNeto = D(g, "ImpNeto"),
            ImpOpEx = D(g, "ImpOpEx"), ImpTrib = D(g, "ImpTrib"), ImpIVA = D(g, "ImpIVA"),
            FchServDesde = V(g, "FchServDesde"), FchServHasta = V(g, "FchServHasta"), FchVtoPago = V(g, "FchVtoPago"),
            MonId = V(g, "MonId") ?? "PES", MonCotiz = D(g, "MonCotiz"),
            CanMisMonExt = V(g, "CanMisMonExt"),
            CondicionIVAReceptorId = g.Element(Ns + "CondicionIVAReceptorId") != null ? I(g, "CondicionIVAReceptorId") : null,
            Iva = g.Element(Ns + "Iva")?.Elements(Ns + "AlicIva").Select(a => new AlicuotaIva
                { Id = I(a, "Id"), BaseImp = D(a, "BaseImp"), Importe = D(a, "Importe") }).ToList(),
            Tributos = g.Element(Ns + "Tributos")?.Elements(Ns + "Tributo").Select(t => new Tributo
                { Id = I(t, "Id"), Desc = V(t, "Desc"), BaseImp = D(t, "BaseImp"), Alic = D(t, "Alic"), Importe = D(t, "Importe") }).ToList(),
            CbtesAsoc = g.Element(Ns + "CbtesAsoc")?.Elements(Ns + "CbteAsoc").Select(a => new ComprobanteAsociado
                { Tipo = I(a, "Tipo"), PtoVta = I(a, "PtoVta"), Nro = L(a, "Nro"), Cuit = V(a, "Cuit"), CbteFch = V(a, "CbteFch") }).ToList(),
            Opcionales = g.Element(Ns + "Opcionales")?.Elements(Ns + "Opcional").Select(o => new Opcional
                { Id = V(o, "Id") ?? "", Valor = V(o, "Valor") ?? "" }).ToList(),
            Compradores = g.Element(Ns + "Compradores")?.Elements(Ns + "Comprador").Select(o => new Comprador
                { DocTipo = I(o, "DocTipo"), DocNro = L(o, "DocNro"), Porcentaje = D(o, "Porcentaje") }).ToList(),
            PeriodoAsoc = g.Element(Ns + "PeriodoAsoc") is { } pa ? new PeriodoAsociado { FchDesde = V(pa, "FchDesde") ?? "", FchHasta = V(pa, "FchHasta") ?? "" } : null,
            Actividades = g.Element(Ns + "Actividades")?.Elements(Ns + "Actividad").Select(a => I(a, "Id")).ToList(),
            Resultado = V(g, "Resultado"), CodAutorizacion = V(g, "CodAutorizacion"), EmisionTipo = V(g, "EmisionTipo"),
            FchVto = V(g, "FchVto"), FchProceso = V(g, "FchProceso"),
            PtoVta = I(g, "PtoVta"), CbteTipo = I(g, "CbteTipo"),
            Observaciones = Mensajes(g.Element(Ns + "Observaciones"), "Obs")
        };
    }

    public async Task<int> FECompTotXRequestAsync(CancellationToken ct)
    {
        var r = await InvocarAsync("FECompTotXRequest", ct).ConfigureAwait(false);
        LanzarSiErrores(r, "FECompTotXRequest");
        return I(r, "RegXReq");
    }

    public async Task<Caea> FECAEASolicitarAsync(int periodo, short orden, CancellationToken ct)
    {
        var r = await InvocarAsync("FECAEASolicitar", ct, E("Periodo", periodo), E("Orden", orden)).ConfigureAwait(false);
        LanzarSiErrores(r, "FECAEASolicitar");
        return ParsearCaea(r.Element(Ns + "ResultGet") ?? throw new ArcaException("FECAEASolicitar sin ResultGet."));
    }

    public async Task<Caea?> FECAEAConsultarAsync(int periodo, short orden, CancellationToken ct)
    {
        var r = await InvocarAsync("FECAEAConsultar", ct, E("Periodo", periodo), E("Orden", orden)).ConfigureAwait(false);
        if (Mensajes(r.Element(Ns + "Errors"), "Err").Any(e => e.Code == 602)) return null;
        LanzarSiErrores(r, "FECAEAConsultar");
        var rg = r.Element(Ns + "ResultGet");
        return rg == null ? null : ParsearCaea(rg);
    }

    public async Task<RespuestaCae> FECAEARegInformativoAsync(InformeCaea s, CancellationToken ct)
    {
        var req = new XElement(Ns + "FeCAEARegInfReq",
            new XElement(Ns + "FeCabReq", E("CantReg", s.Comprobantes.Count), E("PtoVta", s.PtoVta), E("CbteTipo", s.CbteTipo)),
            new XElement(Ns + "FeDetReq", s.Comprobantes.Select(c => SerializarDetalle(c, "FECAEADetRequest"))));
        var r = await InvocarAsync("FECAEARegInformativo", ct, req).ConfigureAwait(false);
        return ParsearRespuestaCae(r, "FECAEADetResponse");
    }

    public async Task<CaeaSinMovimiento> FECAEASinMovimientoInformarAsync(int ptoVta, string caea, CancellationToken ct)
    {
        var r = await InvocarAsync("FECAEASinMovimientoInformar", ct, E("PtoVta", ptoVta), E("CAEA", caea)).ConfigureAwait(false);
        LanzarSiErrores(r, "FECAEASinMovimientoInformar");
        return new CaeaSinMovimiento { Caea = V(r, "CAEA"), FchProceso = V(r, "FchProceso"), PtoVta = I(r, "PtoVta"), Resultado = V(r, "Resultado") };
    }

    public async Task<IReadOnlyList<CaeaSinMovimiento>> FECAEASinMovimientoConsultarAsync(string caea, int ptoVta, CancellationToken ct)
    {
        var r = await InvocarAsync("FECAEASinMovimientoConsultar", ct, E("CAEA", caea), E("PtoVta", ptoVta)).ConfigureAwait(false);
        if (Mensajes(r.Element(Ns + "Errors"), "Err").Any(e => e.Code == 602)) return Array.Empty<CaeaSinMovimiento>();
        LanzarSiErrores(r, "FECAEASinMovimientoConsultar");
        return r.Element(Ns + "ResultGet")?.Elements().Select(e => new CaeaSinMovimiento
            { Caea = V(e, "CAEA"), FchProceso = V(e, "FchProceso"), PtoVta = I(e, "PtoVta") }).ToList()
            ?? new List<CaeaSinMovimiento>();
    }

    private async Task<IReadOnlyList<ParametroItem>> ParamAsync(string metodo, CancellationToken ct, params object[] extra)
    {
        var r = await InvocarAsync(metodo, ct, extra).ConfigureAwait(false);
        // 602 = sin datos (p.ej. FEParamGetPtosVenta sin puntos de venta habilitados para WS)
        if (Mensajes(r.Element(Ns + "Errors"), "Err").Any(e => e.Code == 602)) return Array.Empty<ParametroItem>();
        LanzarSiErrores(r, metodo);
        return ParsearParametros(r);
    }

    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposCbteAsync(CancellationToken ct) => ParamAsync("FEParamGetTiposCbte", ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposConceptoAsync(CancellationToken ct) => ParamAsync("FEParamGetTiposConcepto", ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposDocAsync(CancellationToken ct) => ParamAsync("FEParamGetTiposDoc", ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposIvaAsync(CancellationToken ct) => ParamAsync("FEParamGetTiposIva", ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposMonedasAsync(CancellationToken ct) => ParamAsync("FEParamGetTiposMonedas", ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposOpcionalAsync(CancellationToken ct) => ParamAsync("FEParamGetTiposOpcional", ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposTributosAsync(CancellationToken ct) => ParamAsync("FEParamGetTiposTributos", ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetTiposPaisesAsync(CancellationToken ct) => ParamAsync("FEParamGetTiposPaises", ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetPtosVentaAsync(CancellationToken ct) => ParamAsync("FEParamGetPtosVenta", ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetActividadesAsync(CancellationToken ct) => ParamAsync("FEParamGetActividades", ct);
    public Task<IReadOnlyList<ParametroItem>> FEParamGetCondicionIvaReceptorAsync(string? claseCmp, CancellationToken ct)
        => ParamAsync("FEParamGetCondicionIvaReceptor", ct, string.IsNullOrWhiteSpace(claseCmp) ? Array.Empty<object>() : new object[] { E("ClaseCmp", claseCmp) });

    public async Task<Cotizacion> FEParamGetCotizacionAsync(string monId, string? fchCotiz, CancellationToken ct)
    {
        var args = new List<object> { E("MonId", monId) };
        if (!string.IsNullOrWhiteSpace(fchCotiz)) args.Add(E("FchCotiz", fchCotiz));
        var r = await InvocarAsync("FEParamGetCotizacion", ct, args.ToArray()).ConfigureAwait(false);
        LanzarSiErrores(r, "FEParamGetCotizacion");
        var g = r.Element(Ns + "ResultGet");
        return new Cotizacion { MonId = V(g, "MonId"), MonCotiz = D(g, "MonCotiz"), FchCotiz = V(g, "FchCotiz") };
    }
}
