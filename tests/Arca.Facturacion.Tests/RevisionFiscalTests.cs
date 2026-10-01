using System.Text;
using System.Text.Json;
using Arca.Facturacion.Core;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arca.Facturacion.Tests;

/// <summary>Pruebas de la revisión fiscal (RG 5616, FCE, NC acumuladas, IVA incluido, importes en letras, QR).</summary>
public class RevisionFiscalTests
{
    private static readonly DateOnly Hoy = new ComprobanteValidator(TimeProvider.System).HoyArgentina();
    private static ComprobanteValidator V() => new(TimeProvider.System);
    private const string CbuValido = "0110599500000000000017";

    private static ComprobanteDetalle Fce(int tipo)
    {
        var c = Util.FacturaAValida(Hoy);
        if (tipo == 211)
        {
            c.ImpIVA = 0; c.Iva = null; c.ImpTrib = 0; c.Tributos = null; c.ImpTotal = 1000m;
        }
        c.Opcionales = new() { new Opcional { Id = "2101", Valor = CbuValido }, new Opcional { Id = "27", Valor = "SCA" } };
        return c;
    }

    // 1. RG 5616: clase B no admite RI ni monotributistas.
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(13)]
    [InlineData(16)]
    public void ClaseB_rechaza_condiciones_de_clase_A(int condicion)
    {
        var c = Util.FacturaBValida(Hoy);
        c.DocTipo = 80; c.DocNro = 30500010912; c.CondicionIVAReceptorId = condicion;
        Assert.Contains(V().Validar(1, 6, c), e => e.Contains("clase B no admite"));
    }

    [Fact]
    public void ClaseB_admite_consumidor_final_y_exento()
    {
        var c = Util.FacturaBValida(Hoy);
        Assert.Empty(V().Validar(1, 6, c));
        c.DocTipo = 80; c.DocNro = 30500010912; c.CondicionIVAReceptorId = 4;
        Assert.Empty(V().Validar(1, 6, c));
    }

    // 2. Precios con IVA incluido: el total es exactamente lo cobrado.
    [Theory]
    [InlineData(100, 5)]
    [InlineData(99.99, 5)]
    [InlineData(1234.57, 4)]
    [InlineData(10, 6)]
    [InlineData(0.01, 5)]
    public void Iva_incluido_conserva_el_total(decimal precio, int alicuota)
    {
        var d = CalculadoraImportes.Calcular(new SolicitudCalculo
        {
            CbteTipo = 6, PreciosConIvaIncluido = true, Lineas = { new LineaImporte { PrecioUnitario = precio, AlicuotaIvaId = alicuota } }
        });
        Assert.Equal(precio, d.ImpTotal);
        Assert.Equal(d.ImpTotal, d.ImpNeto + d.ImpIVA);
        d.CbteFch = Hoy.ToString("yyyyMMdd"); d.Concepto = 1; d.DocTipo = 99; d.CondicionIVAReceptorId = 5; d.MonId = "PES"; d.MonCotiz = 1;
        Assert.Empty(V().Validar(1, 6, d));
    }

    [Fact]
    public void Iva_incluido_100_al_21()
    {
        var d = CalculadoraImportes.Calcular(new SolicitudCalculo
        {
            CbteTipo = 6, PreciosConIvaIncluido = true, Lineas = { new LineaImporte { PrecioUnitario = 100m } }
        });
        Assert.Equal(100m, d.ImpTotal);
        Assert.Equal(82.64m, d.ImpNeto);
        Assert.Equal(17.36m, d.ImpIVA);
    }

    // 3. NC parciales acumuladas y anulación con NC previas.
    private static (NotaCreditoService Nc, WsfeSimulado W) Escenario()
    {
        var w = new WsfeSimulado();
        var reg = new RegistroEmisionesMemoria();
        var s = new FacturacionService(w, V(), reg, NullLogger<FacturacionService>.Instance, TimeProvider.System,
            new[] { TimeSpan.Zero }, new OpcionesEmision(20111111112, true, true, false));
        return (new NotaCreditoService(w, s, V(), reg), w);
    }

    [Fact]
    public async Task NC_parciales_no_superan_el_original_en_acumulado()
    {
        var (nc, w) = Escenario();
        var fact = Util.FacturaBValida(Hoy);
        await new FacturacionService(w, V(), new RegistroEmisionesMemoria(), NullLogger<FacturacionService>.Instance, TimeProvider.System,
            new[] { TimeSpan.Zero }, new OpcionesEmision(20111111112)).EmitirAsync(7, 6, fact, "f", "c", default);
        SolicitudCalculo Neto(decimal n) => new() { Lineas = { new LineaImporte { Neto = n } } };
        await nc.NotaCreditoParcialAsync(7, 6, 1, Neto(60), "p1", "c", default);   // 72.60
        // Reintento con la misma clave: no cuenta contra sí misma.
        await nc.NotaCreditoParcialAsync(7, 6, 1, Neto(60), "p1", "c", default);
        var ex = await Assert.ThrowsAsync<ValidacionException>(() => nc.NotaCreditoParcialAsync(7, 6, 1, Neto(50), "p2", "c", default)); // 60.50
        Assert.Contains(ex.Errores, e => e.Contains("saldo disponible"));
        await nc.NotaCreditoParcialAsync(7, 6, 1, Neto(40), "p3", "c", default);   // 48.40 → total 121
        await Assert.ThrowsAsync<ValidacionException>(() => nc.NotaCreditoParcialAsync(7, 6, 1, Neto(0.01m), "p4", "c", default));
        Assert.Equal(2, w.Ultimos[(7, 8)]);
    }

    [Fact]
    public async Task Anular_rechaza_si_ya_hay_NC_previa()
    {
        var (nc, w) = Escenario();
        await new FacturacionService(w, V(), new RegistroEmisionesMemoria(), NullLogger<FacturacionService>.Instance, TimeProvider.System,
            new[] { TimeSpan.Zero }, new OpcionesEmision(20111111112)).EmitirAsync(7, 6, Util.FacturaBValida(Hoy), "f", "c", default);
        await nc.NotaCreditoParcialAsync(7, 6, 1, new SolicitudCalculo { Lineas = { new LineaImporte { Neto = 10 } } }, "p1", "c", default);
        var ex = await Assert.ThrowsAsync<ValidacionException>(() => nc.AnularAsync(7, 6, 1, "anu", "c", default));
        Assert.Contains(ex.Errores, e => e.Contains("parcial por el saldo"));
    }

    [Fact]
    public async Task Anular_sin_NC_previa_funciona_y_luego_bloquea_otra()
    {
        var (nc, w) = Escenario();
        await new FacturacionService(w, V(), new RegistroEmisionesMemoria(), NullLogger<FacturacionService>.Instance, TimeProvider.System,
            new[] { TimeSpan.Zero }, new OpcionesEmision(20111111112)).EmitirAsync(7, 6, Util.FacturaBValida(Hoy), "f", "c", default);
        Assert.True((await nc.AnularAsync(7, 6, 1, "anu", "c", default)).Aprobado);
        Assert.True((await nc.AnularAsync(7, 6, 1, "anu", "c", default)).Aprobado); // reintento idempotente
        await Assert.ThrowsAsync<ValidacionException>(() => nc.AnularAsync(7, 6, 1, "anu2", "c", default));
        await Assert.ThrowsAsync<ValidacionException>(() =>
            nc.NotaCreditoParcialAsync(7, 6, 1, new SolicitudCalculo { Lineas = { new LineaImporte { Neto = 1 } } }, "p", "c", default));
    }

    // 4. Opcionales trasladados a la NC.
    [Fact]
    public void Opcionales_de_NC_descartan_CBU_alias_y_transferencia()
    {
        var o = new List<Opcional>
        {
            new() { Id = "2101", Valor = CbuValido }, new() { Id = "2102", Valor = "alias.x" }, new() { Id = "27", Valor = "SCA" }, new() { Id = "10", Valor = "1" }
        };
        var nc = NotaCreditoService.OpcionalesParaNc(o, 8)!;
        Assert.Equal(new[] { "10" }, nc.Select(x => x.Id));
        Assert.Null(NotaCreditoService.OpcionalesParaNc(new List<Opcional> { new() { Id = "2101", Valor = CbuValido } }, 3));
        var fce = NotaCreditoService.OpcionalesParaNc(o, 203)!;
        Assert.Contains(fce, x => x.Id == "22" && x.Valor == "N");
        Assert.DoesNotContain(fce, x => x.Id is "2101" or "27");
        var conAnulacion = NotaCreditoService.OpcionalesParaNc(new List<Opcional> { new() { Id = "22", Valor = "S" } }, 208)!;
        Assert.Equal("S", Assert.Single(conAnulacion).Valor);
    }

    // 5. FCE requiere opcional 27 con SCA o ADC.
    [Theory]
    [InlineData(201)]
    [InlineData(206)]
    [InlineData(211)]
    public void FCE_requiere_opcional_27(int tipo)
    {
        var c = Fce(tipo);
        if (tipo == 206) c.CondicionIVAReceptorId = 4;
        Assert.DoesNotContain(V().Validar(1, tipo, c), e => e.Contains("opcional 27"));
        c.Opcionales![1].Valor = "XXX";
        Assert.Contains(V().Validar(1, tipo, c), e => e.Contains("opcional 27"));
        c.Opcionales.RemoveAt(1);
        Assert.Contains(V().Validar(1, tipo, c), e => e.Contains("opcional 27"));
        c.Opcionales.Add(new Opcional { Id = "27", Valor = "ADC" });
        Assert.DoesNotContain(V().Validar(1, tipo, c), e => e.Contains("opcional 27"));
    }

    // 6. Duplicar en moneda extranjera consulta la cotización aunque la emisión no la complete automáticamente.
    [Fact]
    public async Task Duplicar_moneda_extranjera_toma_cotizacion()
    {
        var w = new WsfeSimulado();
        var reg = new RegistroEmisionesMemoria();
        var s = new FacturacionService(w, V(), reg, NullLogger<FacturacionService>.Instance, TimeProvider.System,
            new[] { TimeSpan.Zero }, new OpcionesEmision(20111111112, true, CotizacionAutomatica: false));
        var f = Util.FacturaBValida(Hoy);
        f.MonId = "DOL"; f.MonCotiz = 950m; f.CanMisMonExt = "N";
        await s.EmitirAsync(2, 6, f, "f", "c", default);
        var nc = new NotaCreditoService(w, s, V(), reg);
        var r = await nc.DuplicarAsync(2, 6, 1, "d", "c", default);
        Assert.True(r.Aprobado);
        Assert.True(w.Emitidos[(2, 6, 2)].MonCotiz > 0);
    }

    // 7. Clase M requiere CUIT.
    [Fact]
    public void ClaseM_requiere_CUIT()
    {
        var c = Util.FacturaAValida(Hoy);
        Assert.DoesNotContain(V().Validar(1, 51, c), e => e.Contains("DocTipo 80"));
        c.DocTipo = 96; c.DocNro = 12345678; c.CondicionIVAReceptorId = 6;
        Assert.Contains(V().Validar(1, 51, c), e => e.Contains("clase A/M requieren receptor identificado con CUIT"));
    }

    // 8. QR: el base64 va escapado en la URL.
    [Fact]
    public void QR_escapa_el_base64()
    {
        string? url = null;
        // Se busca un caso cuyo base64 tenga '+', '/' o '=' para comprobar el escape.
        for (long n = 1; n < 500 && url == null; n++)
        {
            var u = QrFiscal.GenerarUrl(20111111112, 3, 6, n, "20260930", 121.5m + n, "PES", 1m, 80, 30500010912, "76123456789012")!;
            var b64 = Uri.UnescapeDataString(u[QrFiscal.UrlBase.Length..]);
            if (b64.IndexOfAny(new[] { '+', '/', '=' }) >= 0) url = u;
        }
        Assert.NotNull(url);
        var p = url![QrFiscal.UrlBase.Length..];
        Assert.DoesNotContain("+", p);
        Assert.DoesNotContain("/", p);
        Assert.DoesNotContain("=", p);
        Assert.True(Uri.IsWellFormedUriString(url, UriKind.Absolute));
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(p)))).RootElement;
        Assert.Equal(1, json.GetProperty("ver").GetInt32());
    }

    // 9. NC FCE debe asociar una FCE de su clase.
    [Theory]
    [InlineData(203, 201, true)]
    [InlineData(203, 1, false)]
    [InlineData(208, 206, true)]
    [InlineData(208, 6, false)]
    [InlineData(213, 211, true)]
    [InlineData(213, 11, false)]
    public void NC_FCE_asocia_FCE(int tipoNc, int tipoAsoc, bool ok)
    {
        var c = Util.FacturaAValida(Hoy);
        c.CbtesAsoc = new() { new ComprobanteAsociado { Tipo = tipoAsoc, PtoVta = 1, Nro = 1, CbteFch = Hoy.ToString("yyyyMMdd") } };
        var errores = V().Validar(1, tipoNc, c);
        if (ok) Assert.DoesNotContain(errores, e => e.Contains("FCE MiPyME"));
        else Assert.Contains(errores, e => e.Contains("debe asociar una Factura de Crédito Electrónica"));
    }

    [Fact]
    public void NC_FCE_sin_asociado_rechaza()
    {
        var c = Util.FacturaAValida(Hoy);
        c.PeriodoAsoc = new PeriodoAsociado { FchDesde = Hoy.AddDays(-10).ToString("yyyyMMdd"), FchHasta = Hoy.ToString("yyyyMMdd") };
        Assert.Contains(V().Validar(1, 203, c), e => e.Contains("requieren CbtesAsoc con la factura FCE"));
    }

    // 10. Tributo con más de 2 decimales.
    [Fact]
    public void Tributo_con_mas_de_dos_decimales_se_rechaza()
    {
        var c = Util.FacturaAValida(Hoy);
        c.Tributos![0].Importe = 30.004m; c.ImpTrib = 30.004m; c.ImpTotal = 1240.004m;
        Assert.Contains(V().Validar(1, 1, c), e => e.Contains("Tributo 7: Importe admite como máximo 2 decimales"));
    }

    [Fact]
    public void Calculadora_redondea_cada_tributo()
    {
        var d = CalculadoraImportes.Calcular(new SolicitudCalculo
        {
            CbteTipo = 1, Lineas = { new LineaImporte { Neto = 1000 } },
            Tributos = new() { new Tributo { Id = 7, Desc = "IIBB", BaseImp = 1000, Alic = 3.333m, Importe = 33.335m }, new Tributo { Id = 7, Desc = "IIBB 2", BaseImp = 1000, Alic = 1, Importe = 10.004m } }
        });
        Assert.Equal(new[] { 33.34m, 10.00m }, d.Tributos!.Select(t => t.Importe));
        Assert.Equal(43.34m, d.ImpTrib);
        Assert.Equal(1253.34m, d.ImpTotal);
    }

    // 11. Importe en letras: UNO / VEINTIUNO al final.
    [Theory]
    [InlineData(1, "UNO")]
    [InlineData(21, "VEINTIUNO")]
    [InlineData(31, "TREINTA Y UNO")]
    [InlineData(21_000, "VEINTIUN MIL")]
    [InlineData(21_021, "VEINTIUN MIL VEINTIUNO")]
    [InlineData(1_000_000, "UN MILLON")]
    [InlineData(21_000_000, "VEINTIUN MILLONES")]
    [InlineData(1_000_001, "UN MILLON UNO")]
    public void Numero_a_letras_apocope(long n, string esperado) => Assert.Equal(esperado, NumeroALetras.Entero(n));

    [Fact]
    public void Importe_uno() => Assert.Equal("SON PESOS UNO CON 00/100", NumeroALetras.Importe(1m));

    // 12. Clases A/B sin detalle de IVA: ImpIVA debe ser 0.
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void Sin_Iva_ImpIVA_debe_ser_cero(int tipo)
    {
        var c = tipo == 1 ? Util.FacturaAValida(Hoy) : Util.FacturaBValida(Hoy);
        c.Iva = null; c.ImpNeto = 0; c.ImpTrib = 0; c.Tributos = null; c.ImpOpEx = 100; c.ImpIVA = 21; c.ImpTotal = 121;
        Assert.Contains(V().Validar(1, tipo, c), e => e.Contains("Sin detalle de Iva, ImpIVA debe ser 0"));
        c.ImpIVA = 0; c.ImpTotal = 100;
        Assert.DoesNotContain(V().Validar(1, tipo, c), e => e.Contains("ImpIVA"));
    }
}
