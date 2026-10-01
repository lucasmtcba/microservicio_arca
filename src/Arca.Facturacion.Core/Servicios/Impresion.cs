using System.Globalization;
using System.Net;
using System.Text;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;
using QRCoder;

namespace Arca.Facturacion.Core.Servicios;

/// <summary>Datos del emisor que se imprimen en el comprobante.</summary>
public sealed class DatosEmisor
{
    public string RazonSocial { get; set; } = string.Empty;
    public string? NombreFantasia { get; set; }
    public string? Domicilio { get; set; }
    public string? CondicionIva { get; set; }
    public string? IngresosBrutos { get; set; }
    /// <summary>dd/MM/yyyy</summary>
    public string? InicioActividades { get; set; }
}

/// <summary>Convierte importes a letras en español (formato de facturación argentina).</summary>
public static class NumeroALetras
{
    private static readonly string[] Unidades = { "", "UN", "DOS", "TRES", "CUATRO", "CINCO", "SEIS", "SIETE", "OCHO", "NUEVE", "DIEZ",
        "ONCE", "DOCE", "TRECE", "CATORCE", "QUINCE", "DIECISEIS", "DIECISIETE", "DIECIOCHO", "DIECINUEVE", "VEINTE",
        "VEINTIUN", "VEINTIDOS", "VEINTITRES", "VEINTICUATRO", "VEINTICINCO", "VEINTISEIS", "VEINTISIETE", "VEINTIOCHO", "VEINTINUEVE" };
    private static readonly string[] Decenas = { "", "", "", "TREINTA", "CUARENTA", "CINCUENTA", "SESENTA", "SETENTA", "OCHENTA", "NOVENTA" };
    private static readonly string[] Centenas = { "", "CIENTO", "DOSCIENTOS", "TRESCIENTOS", "CUATROCIENTOS", "QUINIENTOS", "SEISCIENTOS",
        "SETECIENTOS", "OCHOCIENTOS", "NOVECIENTOS" };

    private static string Menor1000(int n)
    {
        if (n == 0) return "";
        if (n == 100) return "CIEN";
        var c = n / 100; var r = n % 100;
        var s = Centenas[c];
        string d;
        if (r < 30) d = Unidades[r];
        else d = Decenas[r / 10] + (r % 10 > 0 ? " Y " + Unidades[r % 10] : "");
        return (s + " " + d).Trim();
    }

    /// <summary>Parte entera en letras (hasta 999.999.999.999).</summary>
    public static string Entero(long n)
    {
        if (n == 0) return "CERO";
        if (n < 0) return "MENOS " + Entero(-n);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, 999_999_999_999L);
        var partes = new List<string>();
        var milesMillones = n / 1_000_000_000; n %= 1_000_000_000;
        var millones = n / 1_000_000; n %= 1_000_000;
        var miles = n / 1000; var resto = (int)(n % 1000);
        long millonesTotal = milesMillones * 1000 + millones;
        if (millonesTotal > 0)
        {
            var mm = millonesTotal >= 1000 ? (millonesTotal / 1000 == 1 ? "MIL" : Menor1000((int)(millonesTotal / 1000)) + " MIL") + (millonesTotal % 1000 > 0 ? " " + Menor1000((int)(millonesTotal % 1000)) : "") : Menor1000((int)millonesTotal);
            partes.Add(millonesTotal == 1 ? "UN MILLON" : mm + " MILLONES");
        }
        if (miles > 0) partes.Add(miles == 1 ? "MIL" : Menor1000((int)miles) + " MIL");
        if (resto > 0) partes.Add(Menor1000(resto));
        var texto = string.Join(" ", partes);
        // Apócope: "UN"/"VEINTIUN" sólo delante de MIL/MILLON(ES); al final del número se escribe "UNO"/"VEINTIUNO".
        return texto.EndsWith("UN", StringComparison.Ordinal) ? texto + "O" : texto;
    }

    /// <summary>"SON PESOS CIENTO VEINTIUNO CON 50/100".</summary>
    public static string Importe(decimal importe, string monId = "PES")
    {
        importe = decimal.Round(Math.Abs(importe), 2, MidpointRounding.AwayFromZero);
        if (importe > 999_999_999_999m) return $"SON {importe.ToString("N2", CultureInfo.GetCultureInfo("es-AR"))} {monId}"; // fuera de rango: en números
        var entero = (long)decimal.Truncate(importe);
        var cent = (int)((importe - entero) * 100);
        var moneda = monId switch { "PES" => "PESOS", "DOL" => "DOLARES ESTADOUNIDENSES", "060" => "EUROS", _ => monId };
        return $"SON {moneda} {Entero(entero)} CON {cent:D2}/100";
    }
}

/// <summary>Generación del QR en PNG y de la representación imprimible (HTML) del comprobante.</summary>
public static class Impresion
{
    private const string Css = """
<style>
body{font-family:Arial,Helvetica,sans-serif;font-size:12px;color:#111;margin:24px}
.caja{border:1px solid #000;padding:8px} .fila{display:flex;gap:0} .col{flex:1}
.letra{width:56px;text-align:center;font-size:32px;font-weight:bold;border:1px solid #000;margin:-1px auto 0;background:#fff}
table{width:100%;border-collapse:collapse;margin-top:8px} td,th{border:1px solid #000;padding:4px} .der{text-align:right}
.copia{text-align:center;font-weight:bold;border:1px solid #000;padding:4px} .pie{display:flex;justify-content:space-between;align-items:center;margin-top:12px}
@media print{body{margin:8mm}}
</style>
""";

    public static byte[] QrPng(string url, int pixelesPorModulo = 6)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelesPorModulo);
    }

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string M(decimal v) => v.ToString("N2", CultureInfo.GetCultureInfo("es-AR"));
    private static string Fecha(string? yyyymmdd) =>
        ComprobanteValidator.TryFecha(yyyymmdd, out var f) ? f.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "";

    /// <summary>HTML autocontenido, listo para imprimir o "Guardar como PDF" desde el navegador. Original/Duplicado opcional.</summary>
    public static string Html(ComprobanteConsultado c, DatosEmisor emisor, long cuitEmisor, string? qrUrl, string copia = "ORIGINAL")
    {
        var clase = TiposComprobante.Clase(c.CbteTipo) ?? "";
        var tipo = Catalogos.TiposComprobante.GetValueOrDefault(c.CbteTipo, $"Comprobante {c.CbteTipo}");
        var qrImg = qrUrl == null ? "" : $"<a href=\"{H(qrUrl)}\"><img alt=\"QR ARCA\" width=\"120\" height=\"120\" src=\"data:image/png;base64,{Convert.ToBase64String(QrPng(qrUrl, 4))}\"></a>";
        var discrimina = TiposComprobante.DiscriminaIva(c.CbteTipo) && clase != "B";
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"""
<!doctype html><html lang="es"><head><meta charset="utf-8"><title>{H(tipo)} {FormatoComprobante.Numero(c.PtoVta, c.CbteDesde)}</title>
{Css}</head><body>
<div class="copia">{H(copia)}</div>
<div class="fila caja"><div class="col"><h2>{H(emisor.NombreFantasia ?? emisor.RazonSocial)}</h2>
<div><b>Razón social:</b> {H(emisor.RazonSocial)}</div><div><b>Domicilio:</b> {H(emisor.Domicilio)}</div><div><b>Condición frente al IVA:</b> {H(emisor.CondicionIva)}</div></div>
<div><div class="letra">{H(clase)}</div><div style="text-align:center">Cód. {c.CbteTipo:D2}</div></div>
<div class="col" style="padding-left:12px"><h2>{H(tipo)}</h2>
<div><b>Punto de venta:</b> {c.PtoVta:D5} &nbsp; <b>Comp. Nro:</b> {c.CbteDesde:D8}</div><div><b>Fecha de emisión:</b> {Fecha(c.CbteFch)}</div>
<div><b>CUIT:</b> {cuitEmisor}</div><div><b>Ingresos Brutos:</b> {H(emisor.IngresosBrutos)}</div><div><b>Inicio de actividades:</b> {H(emisor.InicioActividades)}</div></div></div>
""");
        if (c.Concepto is 2 or 3)
            sb.Append(CultureInfo.InvariantCulture, $"<div class=\"caja\"><b>Período facturado desde:</b> {Fecha(c.FchServDesde)} <b>hasta:</b> {Fecha(c.FchServHasta)} &nbsp; <b>Vto. para el pago:</b> {Fecha(c.FchVtoPago)}</div>");
        sb.Append(CultureInfo.InvariantCulture, $"""
<div class="caja"><b>Receptor:</b> {H(Catalogos.TiposDocumento.GetValueOrDefault(c.DocTipo, c.DocTipo.ToString(CultureInfo.InvariantCulture)))} {(c.DocTipo == 99 ? "" : c.DocNro.ToString(CultureInfo.InvariantCulture))}
 &nbsp; <b>Condición frente al IVA:</b> {H(c.CondicionIVAReceptorId is int ci ? Catalogos.CondicionesIva.GetValueOrDefault(ci) : "")}</div>
<table><tr><th>Concepto</th><th class="der">Importe</th></tr>
""");
        if (discrimina)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<tr><td>Importe neto gravado</td><td class=\"der\">{M(c.ImpNeto)}</td></tr>");
            foreach (var a in c.Iva ?? new())
                sb.Append(CultureInfo.InvariantCulture, $"<tr><td>IVA {H(Catalogos.AlicuotasIva.GetValueOrDefault(a.Id))}</td><td class=\"der\">{M(a.Importe)}</td></tr>");
            if (c.ImpTotConc != 0) sb.Append(CultureInfo.InvariantCulture, $"<tr><td>Importe no gravado</td><td class=\"der\">{M(c.ImpTotConc)}</td></tr>");
            if (c.ImpOpEx != 0) sb.Append(CultureInfo.InvariantCulture, $"<tr><td>Importe exento</td><td class=\"der\">{M(c.ImpOpEx)}</td></tr>");
        }
        else sb.Append(CultureInfo.InvariantCulture, $"<tr><td>Subtotal</td><td class=\"der\">{M(c.ImpTotal - c.ImpTrib)}</td></tr>");
        foreach (var t in c.Tributos ?? new())
            sb.Append(CultureInfo.InvariantCulture, $"<tr><td>{H(t.Desc ?? "Tributo " + t.Id)}</td><td class=\"der\">{M(t.Importe)}</td></tr>");
        sb.Append(CultureInfo.InvariantCulture, $"""
<tr><th>Importe total {(c.MonId == "PES" ? "$" : H(c.MonId))}</th><th class="der">{M(c.ImpTotal)}</th></tr></table>
<div style="margin-top:6px"><i>{H(NumeroALetras.Importe(c.ImpTotal, c.MonId))}</i></div>
""");
        if (c.MonId != "PES") sb.Append(CultureInfo.InvariantCulture, $"<div>Tipo de cambio: {c.MonCotiz.ToString("0.######", CultureInfo.InvariantCulture)}</div>");
        if (clase == "B" && TiposComprobante.DiscriminaIva(c.CbteTipo))
            sb.Append("<div>Régimen de Transparencia Fiscal al Consumidor (Ley 27.743): IVA contenido " + M(c.ImpIVA) + "</div>");
        sb.Append(CultureInfo.InvariantCulture, $"""
<div class="pie"><div>{qrImg}</div><div class="der"><div><b>CAE N°:</b> {H(c.CodAutorizacion)}</div><div><b>Fecha de vto. de CAE:</b> {Fecha(c.FchVto)}</div>
<div>Comprobante autorizado</div></div></div>
</body></html>
""");
        return sb.ToString();
    }
}
