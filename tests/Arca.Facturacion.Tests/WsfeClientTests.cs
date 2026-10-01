using System.Globalization;
using System.Net;
using System.Xml.Linq;
using Arca.Facturacion.Core;
using Arca.Facturacion.Core.Soap;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arca.Facturacion.Tests;

public class WsfeClientTests
{
    private static readonly XNamespace Ns = "http://ar.gov.afip.dif.FEV1/";
    private readonly FakeHandler _h = new();
    private readonly FakeTicketProvider _tp = new();
    private WsfeClient Cliente() => new(new FakeHttpClientFactory(_h), _tp, Util.Opciones(), NullLogger<WsfeClient>.Instance);
    private static readonly DateOnly Hoy = new(2026, 9, 30);

    private const string RespuestaCaeOk =
        "<FeCabResp><Cuit>20111111112</Cuit><PtoVta>3</PtoVta><CbteTipo>6</CbteTipo><FchProceso>20260930120000</FchProceso><CantReg>1</CantReg><Resultado>A</Resultado><Reproceso>N</Reproceso></FeCabResp>" +
        "<FeDetResp><FECAEDetResponse><Concepto>1</Concepto><DocTipo>99</DocTipo><DocNro>0</DocNro><CbteDesde>11</CbteDesde><CbteHasta>11</CbteHasta><CbteFch>20260930</CbteFch><Resultado>A</Resultado>" +
        "<Observaciones><Obs><Code>10217</Code><Msg>Aviso</Msg></Obs></Observaciones><CAE>76123456789012</CAE><CAEFchVto>20261010</CAEFchVto></FECAEDetResponse></FeDetResp>";

    [Fact]
    public async Task FECAESolicitar_envia_auth_y_detalle_en_orden_de_esquema_y_parsea_CAE()
    {
        _h.Responder = (_, _) => FakeHandler.Xml(Util.Wsfe("FECAESolicitar", RespuestaCaeOk));
        var c = Util.FacturaAValida(Hoy);
        var n = new ComprobanteNumerado();
        foreach (var p in typeof(ComprobanteDetalle).GetProperties()) p.SetValue(n, p.GetValue(c));
        n.CbteDesde = n.CbteHasta = 11;

        var r = await Cliente().FECAESolicitarAsync(new SolicitudCae { PtoVta = 3, CbteTipo = 1, Comprobantes = { n } }, default);

        Assert.True(r.Aprobado);
        Assert.Equal("76123456789012", r.Detalles[0].Cae);
        Assert.Equal("20261010", r.Detalles[0].CaeFchVto);
        Assert.Equal(10217, r.Detalles[0].Observaciones[0].Code);

        var (action, body) = _h.Peticiones.Single();
        Assert.Equal("http://ar.gov.afip.dif.FEV1/FECAESolicitar", action);
        var x = XDocument.Parse(body);
        var auth = x.Descendants(Ns + "Auth").Single();
        Assert.Equal("TOKEN0", auth.Element(Ns + "Token")!.Value);
        Assert.Equal(Util.CuitEmisor, auth.Element(Ns + "Cuit")!.Value);
        var det = x.Descendants(Ns + "FECAEDetRequest").Single();
        var orden = det.Elements().Select(e => e.Name.LocalName).ToList();
        var esquema = new[] { "Concepto", "DocTipo", "DocNro", "CbteDesde", "CbteHasta", "CbteFch", "ImpTotal", "ImpTotConc", "ImpNeto",
            "ImpOpEx", "ImpTrib", "ImpIVA", "FchServDesde", "FchServHasta", "FchVtoPago", "MonId", "MonCotiz", "CondicionIVAReceptorId",
            "Tributos", "Iva" };
        Assert.Equal(esquema, orden);
        Assert.Equal("1240", det.Element(Ns + "ImpTotal")!.Value);
        Assert.Equal("1", x.Descendants(Ns + "CantReg").Single().Value);
    }

    [Fact]
    public async Task Importes_se_serializan_con_punto_aunque_la_cultura_sea_es_AR()
    {
        var prev = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("es-AR");
        try
        {
            _h.Responder = (_, _) => FakeHandler.Xml(Util.Wsfe("FECAESolicitar", RespuestaCaeOk));
            var n = new ComprobanteNumerado { Concepto = 1, DocTipo = 99, ImpNeto = 1234.56m, ImpIVA = 259.26m, ImpTotal = 1493.82m, MonCotiz = 1, CbteDesde = 1, CbteHasta = 1 };
            await Cliente().FECAESolicitarAsync(new SolicitudCae { PtoVta = 1, CbteTipo = 6, Comprobantes = { n } }, default);
            var body = _h.Peticiones.Single().Body;
            Assert.Contains(">1493.82<", body);
            Assert.DoesNotContain("1493,82", body);
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    [Fact]
    public async Task Texto_con_caracteres_especiales_se_escapa()
    {
        _h.Responder = (_, _) => FakeHandler.Xml(Util.Wsfe("FECAESolicitar", RespuestaCaeOk));
        var n = new ComprobanteNumerado { Concepto = 1, DocTipo = 99, ImpTotal = 1, ImpTotConc = 1, MonCotiz = 1, CbteDesde = 1, CbteHasta = 1,
            Tributos = new() { new Tributo { Id = 99, Desc = "</Desc><Inyectado>1</Inyectado>&", Importe = 0 } } };
        await Cliente().FECAESolicitarAsync(new SolicitudCae { PtoVta = 1, CbteTipo = 6, Comprobantes = { n } }, default);
        var x = XDocument.Parse(_h.Peticiones.Single().Body);
        Assert.Empty(x.Descendants(Ns + "Inyectado"));
        Assert.Equal("</Desc><Inyectado>1</Inyectado>&", x.Descendants(Ns + "Desc").Single().Value);
    }

    [Fact]
    public async Task Error_600_invalida_TA_y_reintenta_una_vez()
    {
        var llamadas = 0;
        _h.Responder = (_, _) => ++llamadas == 1
            ? FakeHandler.Xml(Util.Wsfe("FECompUltimoAutorizado", "<Errors><Err><Code>600</Code><Msg>ValidacionDeToken: expirado</Msg></Err></Errors>"))
            : FakeHandler.Xml(Util.Wsfe("FECompUltimoAutorizado", "<PtoVta>3</PtoVta><CbteTipo>6</CbteTipo><CbteNro>41</CbteNro>"));
        var r = await Cliente().FECompUltimoAutorizadoAsync(3, 6, default);
        Assert.Equal(41, r.CbteNro);
        Assert.Equal(1, _tp.Invalidaciones);
        Assert.Equal(2, llamadas);
    }

    [Fact]
    public async Task Error_600_persistente_no_entra_en_bucle()
    {
        _h.Responder = (_, _) => FakeHandler.Xml(Util.Wsfe("FECompUltimoAutorizado", "<Errors><Err><Code>600</Code><Msg>x</Msg></Err></Errors>"));
        var ex = await Assert.ThrowsAsync<ArcaException>(() => Cliente().FECompUltimoAutorizadoAsync(3, 6, default));
        Assert.Equal(600, ex.Errores.Single().Code);
        Assert.Equal(2, _h.Peticiones.Count);
    }

    [Fact]
    public async Task FECompConsultar_602_devuelve_null()
    {
        _h.Responder = (_, _) => FakeHandler.Xml(Util.Wsfe("FECompConsultar", "<Errors><Err><Code>602</Code><Msg>Sin Resultados</Msg></Err></Errors>"));
        Assert.Null(await Cliente().FECompConsultarAsync(1, 6, 99, default));
    }

    [Fact]
    public async Task FECompConsultar_parsea_comprobante()
    {
        _h.Responder = (_, _) => FakeHandler.Xml(Util.Wsfe("FECompConsultar",
            "<ResultGet><Concepto>1</Concepto><DocTipo>80</DocTipo><DocNro>30500010912</DocNro><CbteDesde>7</CbteDesde><CbteHasta>7</CbteHasta>" +
            "<CbteFch>20260930</CbteFch><ImpTotal>121.5</ImpTotal><ImpTotConc>0</ImpTotConc><ImpNeto>100.41</ImpNeto><ImpOpEx>0</ImpOpEx><ImpTrib>0</ImpTrib><ImpIVA>21.09</ImpIVA>" +
            "<MonId>PES</MonId><MonCotiz>1</MonCotiz><CondicionIVAReceptorId>1</CondicionIVAReceptorId><Iva><AlicIva><Id>5</Id><BaseImp>100.41</BaseImp><Importe>21.09</Importe></AlicIva></Iva>" +
            "<Resultado>A</Resultado><CodAutorizacion>76000000000001</CodAutorizacion><EmisionTipo>CAE</EmisionTipo><FchVto>20261010</FchVto><FchProceso>20260930101010</FchProceso><PtoVta>2</PtoVta><CbteTipo>1</CbteTipo></ResultGet>"));
        var c = (await Cliente().FECompConsultarAsync(2, 1, 7, default))!;
        Assert.Equal(121.5m, c.ImpTotal);
        Assert.Equal("76000000000001", c.CodAutorizacion);
        Assert.Equal(1, c.CondicionIVAReceptorId);
        Assert.Equal(5, c.Iva!.Single().Id);
        var req = XDocument.Parse(_h.Peticiones.Single().Body).Descendants(Ns + "FeCompConsReq").Single();
        Assert.Equal("7", req.Element(Ns + "CbteNro")!.Value);
    }

    [Fact]
    public async Task FEDummy_no_requiere_auth()
    {
        _h.Responder = (_, _) => FakeHandler.Xml(Util.Wsfe("FEDummy", "<AppServer>OK</AppServer><DbServer>OK</DbServer><AuthServer>OK</AuthServer>"));
        var r = await Cliente().FEDummyAsync(default);
        Assert.True(r.TodoOk);
        Assert.Equal(0, _tp.Obtenciones);
        Assert.DoesNotContain("Auth", _h.Peticiones.Single().Body);
    }

    [Fact]
    public async Task Parametros_se_parsean_con_atributos_extra()
    {
        _h.Responder = (_, _) => FakeHandler.Xml(Util.Wsfe("FEParamGetPtosVenta",
            "<ResultGet><PtoVenta><Nro>3</Nro><EmisionTipo>CAE</EmisionTipo><Bloqueado>N</Bloqueado><FchBaja>NULL</FchBaja></PtoVenta></ResultGet>"));
        var r = await Cliente().FEParamGetPtosVentaAsync(default);
        Assert.Equal("3", r[0].Id);
        Assert.Equal("N", r[0].Atributos["Bloqueado"]);
    }

    [Fact]
    public async Task CondicionIvaReceptor_envia_clase()
    {
        _h.Responder = (_, _) => FakeHandler.Xml(Util.Wsfe("FEParamGetCondicionIvaReceptor",
            "<ResultGet><CondicionIvaReceptor><Id>1</Id><Desc>IVA Responsable Inscripto</Desc><Cmp_Clase>A/M/C</Cmp_Clase></CondicionIvaReceptor></ResultGet>"));
        var r = await Cliente().FEParamGetCondicionIvaReceptorAsync("A", default);
        Assert.Equal("A/M/C", r[0].Atributos["Cmp_Clase"]);
        Assert.Contains("<ClaseCmp>A</ClaseCmp>", _h.Peticiones.Single().Body);
    }

    [Theory]
    [InlineData("FEParamGetTiposCbte")] [InlineData("FEParamGetTiposConcepto")] [InlineData("FEParamGetTiposDoc")]
    [InlineData("FEParamGetTiposIva")] [InlineData("FEParamGetTiposMonedas")] [InlineData("FEParamGetTiposOpcional")]
    [InlineData("FEParamGetTiposTributos")] [InlineData("FEParamGetTiposPaises")] [InlineData("FEParamGetActividades")]
    public async Task Todos_los_metodos_de_parametros_invocan_su_SOAPAction(string metodo)
    {
        _h.Responder = (_, _) => FakeHandler.Xml(Util.Wsfe(metodo, "<ResultGet><X><Id>1</Id><Desc>d</Desc><FchDesde>20100101</FchDesde><FchHasta>NULL</FchHasta></X></ResultGet>"));
        var c = Cliente();
        var m = typeof(WsfeClient).GetMethod(metodo + "Async")!;
        var r = await (Task<IReadOnlyList<ParametroItem>>)m.Invoke(c, new object[] { CancellationToken.None })!;
        Assert.Equal("1", r.Single().Id);
        Assert.Equal("http://ar.gov.afip.dif.FEV1/" + metodo, _h.Peticiones.Single().Action);
    }

    [Fact]
    public async Task Cotizacion_y_errores_de_negocio()
    {
        _h.Responder = (_, _) => FakeHandler.Xml(Util.Wsfe("FEParamGetCotizacion", "<ResultGet><MonId>DOL</MonId><MonCotiz>1350.5</MonCotiz><FchCotiz>20260929</FchCotiz></ResultGet>"));
        Assert.Equal(1350.5m, (await Cliente().FEParamGetCotizacionAsync("DOL", "20260929", default)).MonCotiz);
        _h.Responder = (_, _) => FakeHandler.Xml(Util.Wsfe("FEParamGetCotizacion", "<Errors><Err><Code>1001</Code><Msg>Moneda inexistente</Msg></Err></Errors>"));
        var ex = await Assert.ThrowsAsync<ArcaException>(() => Cliente().FEParamGetCotizacionAsync("ZZZ", null, default));
        Assert.Equal(1001, ex.Errores[0].Code);
    }

    [Fact]
    public async Task CAEA_solicitar_consultar_informar_sin_movimiento()
    {
        _h.Responder = (a, _) => a.EndsWith("FECAEASolicitar") || a.EndsWith("FECAEAConsultar")
            ? FakeHandler.Xml(Util.Wsfe(a.Split('/').Last(), "<ResultGet><CAEA>31234567890123</CAEA><Periodo>202610</Periodo><Orden>1</Orden><FchVigDesde>20261001</FchVigDesde><FchVigHasta>20261015</FchVigHasta><FchTopeInf>20261023</FchTopeInf><FchProceso>20260925</FchProceso></ResultGet>"))
            : a.EndsWith("FECAEASinMovimientoInformar")
                ? FakeHandler.Xml(Util.Wsfe("FECAEASinMovimientoInformar", "<CAEA>31234567890123</CAEA><FchProceso>20261024</FchProceso><PtoVta>5</PtoVta><Resultado>A</Resultado>"))
                : FakeHandler.Xml(Util.Wsfe("FECAEASinMovimientoConsultar", "<ResultGet><FECAEASinMov><CAEA>31234567890123</CAEA><FchProceso>20261024</FchProceso><PtoVta>5</PtoVta></FECAEASinMov></ResultGet>"));
        var c = Cliente();
        Assert.Equal("31234567890123", (await c.FECAEASolicitarAsync(202610, 1, default)).Codigo);
        Assert.Equal("20261023", (await c.FECAEAConsultarAsync(202610, 1, default))!.FchTopeInf);
        Assert.Equal("A", (await c.FECAEASinMovimientoInformarAsync(5, "31234567890123", default)).Resultado);
        Assert.Single(await c.FECAEASinMovimientoConsultarAsync("31234567890123", 5, default));
    }

    [Fact]
    public async Task FECAEARegInformativo_incluye_CAEA_al_final_del_detalle()
    {
        _h.Responder = (_, _) => FakeHandler.Xml(Util.Wsfe("FECAEARegInformativo",
            RespuestaCaeOk.Replace("FECAEDetResponse", "FECAEADetResponse").Replace("<CAE>", "<CAEA>").Replace("</CAE>", "</CAEA>")));
        var r = await Cliente().FECAEARegInformativoAsync(new InformeCaea { PtoVta = 5, CbteTipo = 6, Comprobantes = {
            new ComprobanteCaea { Concepto = 1, DocTipo = 99, ImpTotal = 1, ImpTotConc = 1, MonCotiz = 1, CbteDesde = 1, CbteHasta = 1, Caea = "31234567890123" } } }, default);
        Assert.Equal("76123456789012", r.Detalles[0].Cae);
        var det = XDocument.Parse(_h.Peticiones.Single().Body).Descendants(Ns + "FECAEADetRequest").Single();
        Assert.Equal("CAEA", det.Elements().Last().Name.LocalName);
    }

    [Fact]
    public async Task FECompTotXRequest()
    {
        _h.Responder = (_, _) => FakeHandler.Xml(Util.Wsfe("FECompTotXRequest", "<RegXReq>250</RegXReq>"));
        Assert.Equal(250, await Cliente().FECompTotXRequestAsync(default));
    }

    [Fact]
    public async Task SoapFault_se_propaga()
    {
        _h.Responder = (_, _) => FakeHandler.Xml(Util.Soap("<soap:Fault><faultcode>soap:Server</faultcode><faultstring>boom</faultstring></soap:Fault>"), HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<SoapFaultException>(() => Cliente().FECompTotXRequestAsync(default));
    }

    [Fact]
    public async Task Respuesta_HTML_de_error_es_falla_de_comunicacion()
    {
        _h.Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html><body>502</body>") };
        await Assert.ThrowsAsync<ArcaComunicacionException>(() => Cliente().FECompTotXRequestAsync(default));
    }

    [Fact]
    public async Task Timeout_es_falla_de_comunicacion()
    {
        var h = new ThrowingHandler(new TaskCanceledException("timeout"));
        var c = new WsfeClient(new FakeHttpClientFactory(h), _tp, Util.Opciones(), NullLogger<WsfeClient>.Instance);
        await Assert.ThrowsAsync<ArcaComunicacionException>(() => c.FECompTotXRequestAsync(default));
    }

    [Fact]
    public async Task Cancelacion_del_llamador_no_se_disfraza_de_timeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var h = new ThrowingHandler(new TaskCanceledException("cancel"));
        var c = new WsfeClient(new FakeHttpClientFactory(h), _tp, Util.Opciones(), NullLogger<WsfeClient>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.FEDummyAsync(cts.Token));
    }

    [Fact]
    public void Parser_rechaza_DTD_XXE()
    {
        const string xxe = "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><r>&x;</r>";
        Assert.ThrowsAny<System.Xml.XmlException>(() => SoapInvoker.ParsearSeguro(xxe));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) => _ex = ex;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromException<HttpResponseMessage>(_ex);
    }
}
