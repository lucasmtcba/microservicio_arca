using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.Extensions.Time.Testing;

namespace Arca.Facturacion.Tests;

public class CuitTests
{
    [Theory]
    [InlineData("20111111112", true)]
    [InlineData("30500010912", true)]
    [InlineData("33693450239", true)]
    [InlineData("20-11111111-2", true)]
    [InlineData("20111111113", false)]
    [InlineData("99111111112", false)]
    [InlineData("2011111111", false)]
    [InlineData("2011111111A", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Valida_digito_verificador(string? cuit, bool esperado) => Assert.Equal(esperado, Cuit.EsValida(cuit));
}

public class ComprobanteValidatorTests
{
    // 30/09/2026 15:00 UTC = 12:00 en Argentina
    private static readonly FakeTimeProvider Reloj = new(new DateTimeOffset(2026, 9, 30, 15, 0, 0, TimeSpan.Zero));
    private static readonly ComprobanteValidator V = new(Reloj);
    private static readonly DateOnly Hoy = new(2026, 9, 30);

    [Fact] public void Factura_B_valida() => Assert.Empty(V.Validar(1, 6, Util.FacturaBValida(Hoy)));
    [Fact] public void Factura_A_servicios_con_tributo_valida() => Assert.Empty(V.Validar(1, 1, Util.FacturaAValida(Hoy)));

    [Fact]
    public void Hoy_se_calcula_en_hora_argentina()
    {
        var r = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero)); // 30/09 23:00 AR
        Assert.Equal(new DateOnly(2026, 9, 30), new ComprobanteValidator(r).HoyArgentina());
    }

    private static void Error(int tipo, ComprobanteDetalle c, string fragmento) =>
        Assert.Contains(V.Validar(1, tipo, c), e => e.Contains(fragmento));

    [Fact] public void Total_que_no_cierra() { var c = Util.FacturaBValida(Hoy); c.ImpTotal = 120; Error(6, c, "ImpTotal"); }
    [Fact] public void Mas_de_dos_decimales() { var c = Util.FacturaBValida(Hoy); c.ImpNeto = 100.001m; c.ImpTotal = 121.001m; Error(6, c, "2 decimales"); }
    [Fact] public void Importe_negativo() { var c = Util.FacturaBValida(Hoy); c.ImpOpEx = -1; c.ImpTotal = 120; Error(6, c, "negativo"); }
    [Fact] public void Iva_mal_calculado() { var c = Util.FacturaBValida(Hoy); c.Iva![0].Importe = 20; c.ImpIVA = 20; c.ImpTotal = 120; Error(6, c, "no corresponde"); }
    [Fact] public void Base_iva_distinta_de_neto() { var c = Util.FacturaBValida(Hoy); c.Iva![0].BaseImp = 90; Error(6, c, "BaseImp"); }
    [Fact] public void Alicuota_inexistente() { var c = Util.FacturaBValida(Hoy); c.Iva![0].Id = 7; Error(6, c, "inexistente"); }
    [Fact] public void Alicuota_repetida() { var c = Util.FacturaBValida(Hoy); c.Iva = new() { new() { Id = 5, BaseImp = 50, Importe = 10.5m }, new() { Id = 5, BaseImp = 50, Importe = 10.5m } }; Error(6, c, "repetir"); }
    [Fact] public void Neto_sin_iva() { var c = Util.FacturaBValida(Hoy); c.Iva = null; c.ImpIVA = 0; c.ImpTotal = 100; Error(6, c, "detalle de Iva"); }

    [Fact]
    public void Factura_C_no_discrimina_iva()
    {
        var c = Util.FacturaBValida(Hoy);
        Error(11, c, "clase C");
        c.Iva = null; c.ImpIVA = 0; c.ImpTotal = 100;
        Assert.Empty(V.Validar(1, 11, c));
    }

    [Fact] public void Factura_A_requiere_CUIT() { var c = Util.FacturaAValida(Hoy); c.DocTipo = 96; c.DocNro = 12345678; Error(1, c, "clase A"); }
    [Fact] public void CUIT_receptor_invalida() { var c = Util.FacturaAValida(Hoy); c.DocNro = 30500010913; Error(1, c, "CUIT/CUIL válida"); }
    [Fact] public void Consumidor_final_con_numero() { var c = Util.FacturaBValida(Hoy); c.DocNro = 5; Error(6, c, "DocTipo 99"); }
    [Fact] public void Condicion_iva_receptor_obligatoria() { var c = Util.FacturaBValida(Hoy); c.CondicionIVAReceptorId = null; Error(6, c, "RG 5616"); }

    [Theory]
    [InlineData(1, 6, true)] [InlineData(1, -6, true)] [InlineData(1, 5, false)]
    [InlineData(2, 11, true)] [InlineData(2, 10, false)]
    public void Ventana_de_fecha_segun_concepto(int concepto, int dias, bool falla)
    {
        var c = concepto == 1 ? Util.FacturaBValida(Hoy) : Util.FacturaAValida(Hoy);
        c.CbteFch = Hoy.AddDays(dias).ToString("yyyyMMdd");
        var tipo = concepto == 1 ? 6 : 1;
        Assert.Equal(falla, V.Validar(1, tipo, c).Any(e => e.Contains("CbteFch debe estar dentro")));
    }

    [Fact] public void Fecha_mal_formada() { var c = Util.FacturaBValida(Hoy); c.CbteFch = "2026-09-30"; Error(6, c, "yyyyMMdd"); }
    [Fact] public void Servicios_sin_fechas() { var c = Util.FacturaAValida(Hoy); c.FchServDesde = null; c.FchVtoPago = null; Error(1, c, "FchServDesde"); Error(1, c, "FchVtoPago"); }
    [Fact] public void Servicio_desde_mayor_que_hasta() { var c = Util.FacturaAValida(Hoy); c.FchServDesde = "20261231"; Error(1, c, "posterior"); }
    [Fact] public void Productos_con_fechas_de_servicio() { var c = Util.FacturaBValida(Hoy); c.FchServDesde = "20260901"; Error(6, c, "Concepto 1"); }
    [Fact] public void Tributos_no_suman() { var c = Util.FacturaAValida(Hoy); c.Tributos![0].Importe = 29; Error(1, c, "Tributos"); }

    [Fact]
    public void Nota_de_credito_requiere_asociado()
    {
        var c = Util.FacturaBValida(Hoy);
        Error(8, c, "CbtesAsoc o PeriodoAsoc");
        c.CbtesAsoc = new() { new ComprobanteAsociado { Tipo = 6, PtoVta = 1, Nro = 10 } };
        Assert.Empty(V.Validar(1, 8, c));
        c.PeriodoAsoc = new PeriodoAsociado { FchDesde = "20260901", FchHasta = "20260930" };
        Error(8, c, "excluyentes");
    }

    [Fact]
    public void Moneda_extranjera()
    {
        var c = Util.FacturaBValida(Hoy);
        c.MonId = "DOL"; c.MonCotiz = 1350.5m;
        Error(6, c, "CanMisMonExt");
        c.CanMisMonExt = "N";
        Assert.Empty(V.Validar(1, 6, c));
        c.MonCotiz = 0;
        Error(6, c, "MonCotiz");
    }

    [Fact] public void Pesos_con_cotizacion_distinta_de_1() { var c = Util.FacturaBValida(Hoy); c.MonCotiz = 2; Error(6, c, "cotización debe ser 1"); }
    [Fact] public void Punto_de_venta_y_tipo_fuera_de_rango() { Assert.Contains(V.Validar(0, 999, Util.FacturaBValida(Hoy)), e => e.Contains("PtoVta")); Assert.Contains(V.Validar(1, 999, Util.FacturaBValida(Hoy)), e => e.Contains("CbteTipo")); }
    [Fact] public void Compradores_no_suman_100() { var c = Util.FacturaBValida(Hoy); c.Compradores = new() { new() { DocTipo = 80, DocNro = 1, Porcentaje = 50 } }; Error(6, c, "100"); }
    [Fact] public void Total_cero_en_factura() { var c = new ComprobanteDetalle { Concepto = 1, DocTipo = 99, CondicionIVAReceptorId = 5 }; Error(6, c, "mayor a 0"); }
}
