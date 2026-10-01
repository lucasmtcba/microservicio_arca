using System.Globalization;
using System.Text;
using System.Text.Json;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;

namespace Arca.Facturacion.Core.Servicios;

/// <summary>Código QR fiscal obligatorio en comprobantes electrónicos (RG 4892): URL https://www.afip.gob.ar/fe/qr/?p=BASE64(JSON).</summary>
public static class QrFiscal
{
    public const string UrlBase = "https://www.afip.gob.ar/fe/qr/?p=";

    public static string? GenerarUrl(long cuitEmisor, int ptoVta, int cbteTipo, long cbteNro, string? cbteFch, decimal importe,
        string monId, decimal monCotiz, int docTipo, long docNro, string? cae, string tipoCodAut = "E")
    {
        if (string.IsNullOrWhiteSpace(cae) || !ComprobanteValidator.TryFecha(cbteFch, out var fecha)) return null;
        var datos = new Dictionary<string, object>
        {
            ["ver"] = 1,
            ["fecha"] = fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["cuit"] = cuitEmisor,
            ["ptoVta"] = ptoVta,
            ["tipoCmp"] = cbteTipo,
            ["nroCmp"] = cbteNro,
            ["importe"] = importe,
            ["moneda"] = monId,
            ["ctz"] = monCotiz,
            ["tipoCodAut"] = tipoCodAut,
            ["codAut"] = long.TryParse(cae, out var c) ? c : 0
        };
        if (docTipo != 99 || docNro != 0)
        {
            datos["tipoDocRec"] = docTipo;
            datos["nroDocRec"] = docNro;
        }
        var json = JsonSerializer.Serialize(datos);
        // El base64 puede contener '+', '/' y '=': se escapa para que la URL sea válida.
        return UrlBase + Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
    }

    public static string? GenerarUrl(long cuitEmisor, ComprobanteConsultado c) =>
        GenerarUrl(cuitEmisor, c.PtoVta, c.CbteTipo, c.CbteDesde, c.CbteFch, c.ImpTotal, c.MonId, c.MonCotiz == 0 ? 1 : c.MonCotiz,
            c.DocTipo, c.DocNro, c.CodAutorizacion, c.EmisionTipo == "CAEA" ? "A" : "E");
}

/// <summary>Catálogo de errores frecuentes de WSFEv1 con sugerencia de corrección. Sólo incluye códigos verificados;
/// ampliarlo con los códigos observados en homologación.</summary>
public static class CatalogoErroresArca
{
    private static readonly Dictionary<int, string> Ayudas = new()
    {
        [600] = "Token o firma inválidos/vencidos: el servicio renueva el TA automáticamente; si persiste, revisar reloj y certificado.",
        [601] = "La CUIT del emisor no está autorizada en el certificado: asociar el certificado al servicio wsfe para esa CUIT en ARCA.",
        [602] = "No existen datos para la consulta.",
        [10016] = "El número no es el próximo a autorizar: otro sistema emite en el mismo punto de venta."
    };

    public static string? Ayuda(int codigo) => Ayudas.TryGetValue(codigo, out var a) && a.Length > 0 ? a : null;

    public static List<string> AyudasPara(IEnumerable<ArcaMensaje> mensajes) =>
        mensajes.Select(m => Ayuda(m.Code)).Where(a => a != null).Distinct().ToList()!;
}

/// <summary>Línea para la calculadora de importes.</summary>
public sealed class LineaImporte
{
    /// <summary>Neto gravado de la línea (sin IVA).</summary>
    public decimal Neto { get; set; }
    /// <summary>Alícuota de IVA (3=0%, 4=10.5%, 5=21%, 6=27%, 8=5%, 9=2.5%).</summary>
    public int AlicuotaIvaId { get; set; } = 5;
    /// <summary>Cantidad (se usa junto con PrecioUnitario; si PrecioUnitario es null se toma Neto).</summary>
    public decimal Cantidad { get; set; } = 1m;
    /// <summary>Precio unitario. Si se informa, el importe de la línea es Cantidad × PrecioUnitario.</summary>
    public decimal? PrecioUnitario { get; set; }
    /// <summary>Bonificación porcentual de la línea (0-100).</summary>
    public decimal BonificacionPorcentaje { get; set; }
}

public sealed class SolicitudCalculo
{
    public int CbteTipo { get; set; }
    public List<LineaImporte> Lineas { get; set; } = new();
    public decimal NoGravado { get; set; }
    public decimal Exento { get; set; }
    public List<Tributo>? Tributos { get; set; }
    /// <summary>true si los importes de las líneas incluyen IVA (típico en ventas a consumidor final): se desglosa el neto.</summary>
    public bool PreciosConIvaIncluido { get; set; }
}

/// <summary>Calcula importes coherentes para WSFEv1 (agrupa por alícuota, redondea a 2 decimales y totaliza).</summary>
public static class CalculadoraImportes
{
    public static readonly IReadOnlyDictionary<int, decimal> Tasas = new Dictionary<int, decimal>
        { [3] = 0m, [4] = 0.105m, [5] = 0.21m, [6] = 0.27m, [8] = 0.05m, [9] = 0.025m };

    public static ComprobanteDetalle Calcular(SolicitudCalculo s)
    {
        var errores = new List<string>();
        if (s.Lineas.Count > 1000) errores.Add("Máximo 1000 líneas.");
        foreach (var l in s.Lineas)
        {
            if (!Tasas.ContainsKey(l.AlicuotaIvaId)) errores.Add($"Alícuota {l.AlicuotaIvaId} inexistente.");
            if (l.Neto < 0) errores.Add("Neto negativo.");
            if (l.Cantidad <= 0) errores.Add("Cantidad debe ser mayor a 0.");
            if (l.PrecioUnitario < 0) errores.Add("PrecioUnitario negativo.");
            if (l.BonificacionPorcentaje is < 0 or > 100) errores.Add("BonificacionPorcentaje debe estar entre 0 y 100.");
        }
        if (errores.Count > 0) throw new ValidacionException(errores.Distinct().ToList());

        static decimal R(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);
        var claseC = TiposComprobante.ClaseC.Contains(s.CbteTipo);
        // Importe neto efectivo de cada línea: cantidad × precio, menos bonificación y, si corresponde, sin IVA.
        s = new SolicitudCalculo
        {
            CbteTipo = s.CbteTipo, PreciosConIvaIncluido = s.PreciosConIvaIncluido, NoGravado = s.NoGravado, Exento = s.Exento, Tributos = s.Tributos,
            Lineas = s.Lineas.Select(l =>
            {
                var importe = (l.PrecioUnitario.HasValue ? l.Cantidad * l.PrecioUnitario.Value : l.Neto) * (1 - l.BonificacionPorcentaje / 100m);
                // Con IVA incluido se conserva el importe bruto: el desglose se hace por alícuota (ver más abajo)
                // para que el total coincida exactamente con lo cobrado.
                return new LineaImporte { Neto = importe, AlicuotaIvaId = l.AlicuotaIvaId };
            }).ToList()
        };
        // Cada tributo se redondea a 2 decimales (WSFEv1 lo exige) y ImpTrib es la suma de los importes ya redondeados.
        var tributos = s.Tributos?.Select(t => new Tributo { Id = t.Id, Desc = t.Desc, BaseImp = R(t.BaseImp), Alic = t.Alic, Importe = R(t.Importe) }).ToList();
        var d = new ComprobanteDetalle { Tributos = tributos is { Count: > 0 } ? tributos : null };
        d.ImpTrib = (tributos ?? new()).Sum(t => t.Importe);

        if (claseC)
        {
            // Clase C: el neto incluye todo; no se discrimina IVA.
            d.ImpNeto = R(s.Lineas.Sum(l => l.Neto) + s.NoGravado + s.Exento);
        }
        else
        {
            d.Iva = s.Lineas.GroupBy(l => l.AlicuotaIvaId).OrderBy(g => g.Key).Select(g =>
            {
                if (s.PreciosConIvaIncluido)
                {
                    // Bruto cobrado por alícuota: base = bruto / (1 + tasa) e IVA = bruto - base (el total no pierde centavos).
                    var bruto = R(g.Sum(l => l.Neto));
                    var bi = R(bruto / (1 + Tasas[g.Key]));
                    return new AlicuotaIva { Id = g.Key, BaseImp = bi, Importe = bruto - bi };
                }
                var b = R(g.Sum(l => l.Neto));
                return new AlicuotaIva { Id = g.Key, BaseImp = b, Importe = R(b * Tasas[g.Key]) };
            }).ToList();
            if (d.Iva.Count == 0) d.Iva = null;
            d.ImpNeto = d.Iva?.Sum(a => a.BaseImp) ?? 0;
            d.ImpIVA = d.Iva?.Sum(a => a.Importe) ?? 0;
            d.ImpTotConc = R(s.NoGravado);
            d.ImpOpEx = R(s.Exento);
        }
        d.ImpTotal = d.ImpTotConc + d.ImpNeto + d.ImpOpEx + d.ImpTrib + d.ImpIVA;
        return d;
    }
}

/// <summary>Formatos legibles de comprobantes.</summary>
public static class FormatoComprobante
{
    /// <summary>Número en formato fiscal PPPPP-NNNNNNNN.</summary>
    public static string Numero(int ptoVta, long nro) =>
        ptoVta.ToString("D5", CultureInfo.InvariantCulture) + "-" + nro.ToString("D8", CultureInfo.InvariantCulture);

    /// <summary>Descripción completa, p.ej. "Factura B 00003-00000042".</summary>
    public static string Descripcion(int ptoVta, int cbteTipo, long nro) =>
        (Catalogos.TiposComprobante.TryGetValue(cbteTipo, out var d) ? d : $"Comprobante {cbteTipo}") + " " + Numero(ptoVta, nro);
}

/// <summary>Catálogos locales (no requieren llamar a ARCA). Para validaciones y presentación; la fuente de verdad son las tablas FEParamGet*.</summary>
public static class Catalogos
{
    public static readonly IReadOnlyDictionary<int, string> TiposComprobante = new Dictionary<int, string>
    {
        [1] = "Factura A", [2] = "Nota de Débito A", [3] = "Nota de Crédito A", [4] = "Recibo A", [5] = "Nota de Venta al contado A",
        [6] = "Factura B", [7] = "Nota de Débito B", [8] = "Nota de Crédito B", [9] = "Recibo B", [10] = "Nota de Venta al contado B",
        [11] = "Factura C", [12] = "Nota de Débito C", [13] = "Nota de Crédito C", [15] = "Recibo C",
        [34] = "Comprobante A del Anexo I, Apartado A, inc. f), RG 1415", [35] = "Comprobante B del Anexo I, Apartado A, inc. f), RG 1415",
        [39] = "Otros comprobantes A (RG 1415)", [40] = "Otros comprobantes B (RG 1415)",
        [60] = "Cuenta de Venta y Líquido Producto A", [61] = "Cuenta de Venta y Líquido Producto B",
        [63] = "Liquidación A", [64] = "Liquidación B",
        [49] = "Comprobante de Compra de Bienes Usados", [51] = "Factura M", [52] = "Nota de Débito M", [53] = "Nota de Crédito M", [54] = "Recibo M",
        [201] = "Factura de Crédito Electrónica MiPyME A", [202] = "Nota de Débito Electrónica MiPyME A", [203] = "Nota de Crédito Electrónica MiPyME A",
        [206] = "Factura de Crédito Electrónica MiPyME B", [207] = "Nota de Débito Electrónica MiPyME B", [208] = "Nota de Crédito Electrónica MiPyME B",
        [211] = "Factura de Crédito Electrónica MiPyME C", [212] = "Nota de Débito Electrónica MiPyME C", [213] = "Nota de Crédito Electrónica MiPyME C"
    };

    public static readonly IReadOnlyDictionary<int, string> TiposDocumento = new Dictionary<int, string>
    {
        [80] = "CUIT", [86] = "CUIL", [87] = "CDI", [89] = "LE", [90] = "LC", [91] = "CI Extranjera", [94] = "Pasaporte", [96] = "DNI", [99] = "Consumidor Final sin identificar"
    };

    public static readonly IReadOnlyDictionary<int, string> CondicionesIva = new Dictionary<int, string>
    {
        [1] = "IVA Responsable Inscripto", [4] = "IVA Sujeto Exento", [5] = "Consumidor Final", [6] = "Responsable Monotributo",
        [7] = "Sujeto No Categorizado", [8] = "Proveedor del Exterior", [9] = "Cliente del Exterior", [10] = "IVA Liberado – Ley N° 19.640",
        [13] = "Monotributista Social", [15] = "IVA No Alcanzado", [16] = "Monotributo Trabajador Independiente Promovido"
    };

    public static readonly IReadOnlyDictionary<int, string> AlicuotasIva = new Dictionary<int, string>
    {
        [3] = "0%", [4] = "10,5%", [5] = "21%", [6] = "27%", [8] = "5%", [9] = "2,5%"
    };
}
