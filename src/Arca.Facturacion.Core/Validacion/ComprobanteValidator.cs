using System.Globalization;
using Arca.Facturacion.Core.Wsfe;

namespace Arca.Facturacion.Core.Validacion;

public static class Cuit
{
    private static readonly int[] Pesos = { 5, 4, 3, 2, 7, 6, 5, 4, 3, 2 };

    /// <summary>Valida longitud, prefijo y dígito verificador (módulo 11) de una CUIT/CUIL.</summary>
    public static bool EsValida(string? cuit)
    {
        if (cuit is null) return false;
        cuit = cuit.Replace("-", "").Trim();
        if (cuit.Length != 11 || !cuit.All(char.IsAsciiDigit)) return false;
        var prefijo = int.Parse(cuit[..2], CultureInfo.InvariantCulture);
        if (prefijo is not (20 or 23 or 24 or 25 or 26 or 27 or 30 or 33 or 34)) return false;
        var suma = 0;
        for (var i = 0; i < 10; i++) suma += (cuit[i] - '0') * Pesos[i];
        var dv = 11 - suma % 11;
        dv = dv == 11 ? 0 : dv;
        if (dv == 10) return false; // CUIT inválida con este prefijo (ARCA reasigna prefijo 23/33)
        return dv == cuit[10] - '0';
    }

    public static bool EsValida(long cuit) => EsValida(cuit.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Clasificación de tipos de comprobante WSFEv1 (tabla FEParamGetTiposCbte).</summary>
public static class TiposComprobante
{
    public static readonly HashSet<int> ClaseA = new() { 1, 2, 3, 4, 5, 34, 39, 60, 63, 201, 202, 203 };
    public static readonly HashSet<int> ClaseB = new() { 6, 7, 8, 9, 10, 35, 40, 61, 64, 206, 207, 208 };
    public static readonly HashSet<int> ClaseC = new() { 11, 12, 13, 15, 211, 212, 213 };
    public static readonly HashSet<int> ClaseM = new() { 51, 52, 53, 54 };
    public static readonly HashSet<int> NotasCreditoDebito = new() { 2, 3, 7, 8, 12, 13, 52, 53, 202, 203, 207, 208, 212, 213 };
    public static readonly HashSet<int> FacturaCreditoElectronica = new() { 201, 202, 203, 206, 207, 208, 211, 212, 213 };

    public static bool Conocido(int t) => ClaseA.Contains(t) || ClaseB.Contains(t) || ClaseC.Contains(t) || ClaseM.Contains(t) || t == 49;
    /// <summary>Clase del comprobante: "A", "B", "C" o "M" (null si no aplica).</summary>
    public static string? Clase(int t) => ClaseA.Contains(t) ? "A" : ClaseB.Contains(t) ? "B" : ClaseC.Contains(t) ? "C" : ClaseM.Contains(t) ? "M" : null;
    public static bool DiscriminaIva(int t) => ClaseA.Contains(t) || ClaseM.Contains(t) || ClaseB.Contains(t);
}

/// <summary>Validación local previa al envío. No sustituye la validación de ARCA; evita rechazos evitables
/// y, sobre todo, evita consumir numeración con datos obviamente erróneos.</summary>
public sealed class ComprobanteValidator
{
    private static readonly Dictionary<int, decimal> AlicuotasIva = new()
    {
        [3] = 0m, [4] = 0.105m, [5] = 0.21m, [6] = 0.27m, [8] = 0.05m, [9] = 0.025m
    };
    public const int MaxItemsPorLista = 50;
    /// <summary>Condiciones frente al IVA del receptor (FEParamGetCondicionIvaReceptor, RG 5616).</summary>
    public static readonly HashSet<int> CondicionesIva = new() { 1, 4, 5, 6, 7, 8, 9, 10, 13, 15, 16 };
    /// <summary>Condiciones admitidas para comprobantes clase A/M: RI y monotributistas.</summary>
    public static readonly HashSet<int> CondicionesIvaClaseA = new() { 1, 6, 13, 16 };
    /// <summary>Importe desde el cual el consumidor final debe identificarse (null = sin control).</summary>
    public decimal? MontoTopeConsumidorFinal { get; init; }
    /// <summary>Importe máximo admitido por comprobante (protección contra errores de tipeo). Null = sin tope.</summary>
    public decimal? MontoMaximoComprobante { get; init; }
    /// <summary>Condición frente al IVA del EMISOR: restringe las clases de comprobante que puede emitir.</summary>
    public CondicionEmisor? CondicionEmisor { get; init; }
    /// <summary>Documentos de receptores a los que no se debe facturar (p.ej. clientes dados de baja).</summary>
    public IReadOnlySet<long> ReceptoresBloqueados { get; init; } = new HashSet<long>();
    /// <summary>CUIT del emisor (para advertir autofacturación).</summary>
    public long CuitEmisor { get; init; }
    /// <summary>Importe a partir del cual se advierte "importe alto".</summary>
    public decimal? UmbralAdvertenciaImporte { get; init; }
    private readonly decimal Tolerancia;
    private static readonly TimeZoneInfo ZonaAr = BuscarZona();
    private readonly TimeProvider _reloj;

    public ComprobanteValidator(TimeProvider reloj, decimal tolerancia = 0.01m)
    {
        _reloj = reloj;
        Tolerancia = tolerancia;
    }

    private static TimeZoneInfo BuscarZona()
    {
        foreach (var id in new[] { "America/Argentina/Buenos_Aires", "Argentina Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        return TimeZoneInfo.CreateCustomTimeZone("ART", TimeSpan.FromHours(-3), "ART", "ART");
    }

    public DateOnly HoyArgentina() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_reloj.GetUtcNow(), ZonaAr).DateTime);

    /// <summary>NC/ND FCE → tipos FCE que puede asociar (factura y nota de débito FCE de la misma clase).</summary>
    private static readonly Dictionary<int, int[]> FceAsociables = new()
    {
        [202] = new[] { 201, 202 }, [203] = new[] { 201, 202 },
        [207] = new[] { 206, 207 }, [208] = new[] { 206, 207 },
        [212] = new[] { 211, 212 }, [213] = new[] { 211, 212 }
    };

    private static void ValidarAsociados(int cbteTipo, ComprobanteDetalle c, List<string> e)
    {
        var asoc = c.CbtesAsoc ?? new();
        foreach (var a in asoc)
        {
            if (a.PtoVta is < 1 or > 99998 || a.Nro < 1) e.Add($"Comprobante asociado {a.Tipo}-{a.PtoVta}-{a.Nro}: punto de venta o número inválido.");
            if (a.CbteFch != null && !TryFecha(a.CbteFch, out _)) e.Add("Comprobante asociado: CbteFch debe ser yyyyMMdd.");
            if (a.Cuit != null && !Cuit.EsValida(a.Cuit)) e.Add($"Comprobante asociado: CUIT {a.Cuit} inválida.");
            if (TiposComprobante.NotasCreditoDebito.Contains(cbteTipo) && TiposComprobante.Clase(a.Tipo) is { } ca
                && TiposComprobante.Clase(cbteTipo) is { } cn && ca != cn)
                e.Add($"El comprobante asociado (clase {ca}) debe ser de la misma clase que la nota (clase {cn}).");
            // Las NC/ND FCE MiPyME sólo pueden asociar comprobantes FCE de su misma clase (factura o nota de débito FCE).
            if (FceAsociables.TryGetValue(cbteTipo, out var admitidos) && !admitidos.Contains(a.Tipo))
                e.Add($"La nota {cbteTipo} (FCE MiPyME) debe asociar una Factura de Crédito Electrónica ({string.Join("/", admitidos)}), no el tipo {a.Tipo}.");
        }
        if (FceAsociables.ContainsKey(cbteTipo) && asoc.Count == 0)
            e.Add("Las notas de crédito/débito FCE MiPyME requieren CbtesAsoc con la factura FCE original.");
        if (asoc.GroupBy(a => (a.Tipo, a.PtoVta, a.Nro)).Any(g => g.Count() > 1)) e.Add("Comprobantes asociados repetidos.");
    }

    /// <summary>Advertencias: no impiden emitir, pero conviene revisarlas.</summary>
    public IReadOnlyList<string> Advertencias(int cbteTipo, ComprobanteDetalle c)
    {
        var a = new List<string>();
        if (CuitEmisor != 0 && c.DocTipo == 80 && c.DocNro == CuitEmisor) a.Add("El receptor es el propio emisor.");
        if (TryFecha(c.CbteFch, out var f) && f != HoyArgentina()) a.Add($"La fecha del comprobante ({c.CbteFch}) no es la de hoy.");
        if (UmbralAdvertenciaImporte is decimal u && c.ImpTotal >= u) a.Add($"Importe alto: {c.ImpTotal:N2}.");
        if (MontoTopeConsumidorFinal is decimal t && c.DocTipo == 99 && c.ImpTotal >= t * 0.9m && c.ImpTotal < t)
            a.Add("El importe está cerca del tope para consumidor final sin identificar.");
        if (c.MonId != "PES" && c.CanMisMonExt == "S") a.Add("Se cancela en moneda extranjera (CanMisMonExt=S).");
        return a;
    }

    public static bool TryFecha(string? s, out DateOnly f) =>
        DateOnly.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out f);

    /// <param name="validarVentanaFecha">false para comprobantes CAEA informados a posteriori.</param>
    public IReadOnlyList<string> Validar(int ptoVta, int cbteTipo, ComprobanteDetalle c, bool validarVentanaFecha = true)
    {
        var e = new List<string>();
        foreach (var (n, cant) in new[] { ("CbtesAsoc", c.CbtesAsoc?.Count), ("Tributos", c.Tributos?.Count), ("Iva", c.Iva?.Count),
                     ("Opcionales", c.Opcionales?.Count), ("Compradores", c.Compradores?.Count), ("Actividades", c.Actividades?.Count) })
            if (cant > MaxItemsPorLista) e.Add($"{n} admite como máximo {MaxItemsPorLista} elementos.");
        if (e.Count > 0) return e;
        if (ptoVta is < 1 or > 99998) e.Add("PtoVta debe estar entre 1 y 99998.");
        if (!TiposComprobante.Conocido(cbteTipo)) e.Add($"CbteTipo {cbteTipo} no es un tipo WSFEv1 soportado.");
        if (c.Concepto is < 1 or > 3) e.Add("Concepto debe ser 1, 2 o 3.");

        // Receptor
        if (c.DocNro < 0) e.Add("DocNro no puede ser negativo.");
        if (c.DocTipo is 80 or 86 && !Cuit.EsValida(c.DocNro)) e.Add($"DocNro {c.DocNro} no es una CUIT/CUIL válida.");
        if (c.DocTipo == 99 && c.DocNro != 0) e.Add("Con DocTipo 99 (sin identificar) DocNro debe ser 0.");
        if (c.DocTipo == 96 && c.DocNro is < 1 or > 99_999_999) e.Add("El DNI debe tener entre 1 y 8 dígitos.");
        if (c.CondicionIVAReceptorId == 1 && c.DocTipo != 80) e.Add("Un receptor Responsable Inscripto debe identificarse con CUIT (DocTipo 80).");
        if ((TiposComprobante.ClaseA.Contains(cbteTipo) || TiposComprobante.ClaseM.Contains(cbteTipo)) && c.DocTipo != 80)
            e.Add("Los comprobantes clase A/M requieren receptor identificado con CUIT (DocTipo 80).");
        if (c.CondicionIVAReceptorId is null) e.Add("CondicionIVAReceptorId es obligatorio (RG 5616).");
        else if (!CondicionesIva.Contains(c.CondicionIVAReceptorId.Value))
            e.Add($"CondicionIVAReceptorId {c.CondicionIVAReceptorId} inexistente.");
        else if ((TiposComprobante.ClaseA.Contains(cbteTipo) || TiposComprobante.ClaseM.Contains(cbteTipo))
                 && !CondicionesIvaClaseA.Contains(c.CondicionIVAReceptorId.Value))
            e.Add("Los comprobantes clase A/M sólo admiten receptores Responsables Inscriptos o Monotributistas.");
        else if (TiposComprobante.ClaseB.Contains(cbteTipo) && CondicionesIvaClaseA.Contains(c.CondicionIVAReceptorId.Value))
            e.Add("Los comprobantes clase B no admiten receptores Responsables Inscriptos o Monotributistas: corresponde clase A (RG 5616).");
        if (MontoTopeConsumidorFinal is decimal tope && c.DocTipo == 99 && c.ImpTotal >= tope)
            e.Add($"Desde {tope:N2} el consumidor final debe identificarse (DocTipo distinto de 99).");
        if (TiposComprobante.FacturaCreditoElectronica.Contains(cbteTipo) && cbteTipo is 201 or 206 or 211
            && !(c.Opcionales ?? new()).Any(o => o.Id is "2101" or "2102"))
            e.Add("Las Facturas de Crédito Electrónica requieren el opcional 2101 (CBU) o 2102 (alias).");
        if (cbteTipo is 201 or 206 or 211 && !(c.Opcionales ?? new()).Any(o => o.Id == "27" && o.Valor is "SCA" or "ADC"))
            e.Add("Las Facturas de Crédito Electrónica requieren el opcional 27 (transferencia) con valor SCA o ADC.");

        // Fechas
        var hoy = HoyArgentina();
        if (!string.IsNullOrEmpty(c.CbteFch))
        {
            if (!TryFecha(c.CbteFch, out var f)) e.Add("CbteFch debe tener formato yyyyMMdd.");
            else if (validarVentanaFecha)
            {
                var dias = c.Concepto == 1 ? 5 : 10;
                if (Math.Abs(f.DayNumber - hoy.DayNumber) > dias)
                    e.Add($"CbteFch debe estar dentro de ±{dias} días de la fecha actual para Concepto {c.Concepto}.");
            }
        }
        if (c.Concepto is 2 or 3)
        {
            var okD = TryFecha(c.FchServDesde, out var d);
            var okH = TryFecha(c.FchServHasta, out var h);
            if (!okD) e.Add("FchServDesde (yyyyMMdd) es obligatoria para servicios.");
            if (!okH) e.Add("FchServHasta (yyyyMMdd) es obligatoria para servicios.");
            if (okD && okH && d > h) e.Add("FchServDesde no puede ser posterior a FchServHasta.");
            if (TryFecha(c.FchVtoPago, out var vto) && TryFecha(c.CbteFch, out var fc) && vto < fc)
                e.Add("FchVtoPago no puede ser anterior a CbteFch.");
            if (!TryFecha(c.FchVtoPago, out _) && !TiposComprobante.FacturaCreditoElectronica.Contains(cbteTipo))
                e.Add("FchVtoPago (yyyyMMdd) es obligatoria para servicios.");
        }
        else if (c.FchServDesde != null || c.FchServHasta != null || c.FchVtoPago != null)
        {
            if (!TiposComprobante.FacturaCreditoElectronica.Contains(cbteTipo))
                e.Add("Con Concepto 1 (productos) no deben informarse FchServDesde/FchServHasta/FchVtoPago.");
        }

        // Importes
        foreach (var (n, v) in new[] { ("ImpTotal", c.ImpTotal), ("ImpTotConc", c.ImpTotConc), ("ImpNeto", c.ImpNeto),
                     ("ImpOpEx", c.ImpOpEx), ("ImpTrib", c.ImpTrib), ("ImpIVA", c.ImpIVA) })
        {
            if (v < 0) e.Add($"{n} no puede ser negativo.");
            if (decimal.Round(v, 2) != v) e.Add($"{n} admite como máximo 2 decimales.");
        }
        var suma = c.ImpTotConc + c.ImpNeto + c.ImpOpEx + c.ImpTrib + c.ImpIVA;
        if (Math.Abs(suma - c.ImpTotal) > Tolerancia)
            e.Add($"ImpTotal ({c.ImpTotal}) debe ser igual a ImpTotConc+ImpNeto+ImpOpEx+ImpTrib+ImpIVA ({suma}).");
        if (c.ImpTotal == 0 && !TiposComprobante.NotasCreditoDebito.Contains(cbteTipo))
            e.Add("ImpTotal debe ser mayor a 0.");

        // IVA
        var iva = c.Iva ?? new List<AlicuotaIva>();
        if (TiposComprobante.ClaseC.Contains(cbteTipo))
        {
            if (c.ImpIVA != 0 || iva.Count > 0) e.Add("Los comprobantes clase C no discriminan IVA (ImpIVA=0, sin Iva).");
            if (c.ImpTotConc != 0) e.Add("Los comprobantes clase C no admiten ImpTotConc.");
            if (c.ImpOpEx != 0) e.Add("Los comprobantes clase C no admiten ImpOpEx.");
        }
        else if (TiposComprobante.DiscriminaIva(cbteTipo))
        {
            if (c.ImpNeto > 0 && iva.Count == 0) e.Add("Con ImpNeto > 0 debe informarse el detalle de Iva.");
            if (iva.Count == 0 && c.ImpIVA != 0) e.Add("Sin detalle de Iva, ImpIVA debe ser 0.");
            if (iva.Count > 0)
            {
                if (Math.Abs(iva.Sum(i => i.BaseImp) - c.ImpNeto) > Tolerancia)
                    e.Add("La suma de Iva.BaseImp debe ser igual a ImpNeto.");
                if (Math.Abs(iva.Sum(i => i.Importe) - c.ImpIVA) > Tolerancia)
                    e.Add("La suma de Iva.Importe debe ser igual a ImpIVA.");
                if (iva.GroupBy(i => i.Id).Any(g => g.Count() > 1)) e.Add("No se puede repetir la misma alícuota de IVA.");
                foreach (var a in iva)
                {
                    if (!AlicuotasIva.TryGetValue(a.Id, out var tasa)) { e.Add($"Alícuota de IVA {a.Id} inexistente."); continue; }
                    if (a.BaseImp < 0 || a.Importe < 0) e.Add($"Alícuota {a.Id}: importes negativos.");
                    if (Math.Abs(decimal.Round(a.BaseImp * tasa, 2, MidpointRounding.AwayFromZero) - a.Importe) > Tolerancia)
                        e.Add($"Alícuota {a.Id}: Importe {a.Importe} no corresponde a BaseImp {a.BaseImp} × {tasa:P1}.");
                }
            }
        }

        // Tributos
        var trib = c.Tributos ?? new List<Tributo>();
        if (Math.Abs(trib.Sum(t => t.Importe) - c.ImpTrib) > Tolerancia)
            e.Add("La suma de Tributos.Importe debe ser igual a ImpTrib.");

        // Moneda
        if (string.IsNullOrWhiteSpace(c.MonId) || c.MonId.Length != 3) e.Add("MonId debe tener 3 caracteres.");
        else if (c.MonId == "PES")
        {
            if (c.MonCotiz != 1) e.Add("Para MonId PES la cotización debe ser 1.");
            if (c.CanMisMonExt != null) e.Add("CanMisMonExt sólo se informa para moneda extranjera.");
        }
        else
        {
            if (c.MonCotiz <= 0) e.Add("MonCotiz debe ser mayor a 0 para moneda extranjera.");
            if (c.CanMisMonExt is not ("S" or "N")) e.Add("CanMisMonExt debe ser 'S' o 'N' para moneda extranjera.");
        }

        // Asociados
        if (TiposComprobante.NotasCreditoDebito.Contains(cbteTipo) && (c.CbtesAsoc is not { Count: > 0 }) && c.PeriodoAsoc == null)
            e.Add("Las notas de crédito/débito requieren CbtesAsoc o PeriodoAsoc.");
        if (c.CbtesAsoc is { Count: > 0 } && c.PeriodoAsoc != null)
            e.Add("CbtesAsoc y PeriodoAsoc son excluyentes.");
        if (c.PeriodoAsoc != null && (!TryFecha(c.PeriodoAsoc.FchDesde, out var pd) || !TryFecha(c.PeriodoAsoc.FchHasta, out var ph) || pd > ph))
            e.Add("PeriodoAsoc debe tener FchDesde <= FchHasta en formato yyyyMMdd.");

        if (c.Compradores is { Count: > 0 } && c.Compradores.Sum(x => x.Porcentaje) != 100m)
            e.Add("La suma de porcentajes de Compradores debe ser 100.");

        // ---------------- reglas adicionales (v1.3)
        ValidarAsociados(cbteTipo, c, e);
        foreach (var t in c.Tributos ?? new())
        {
            if (t.Id <= 0) e.Add("Tributo con Id inválido.");
            if (t.BaseImp < 0 || t.Alic < 0) e.Add($"Tributo {t.Id}: BaseImp y Alic no pueden ser negativos.");
            if (decimal.Round(t.Importe, 2) != t.Importe) e.Add($"Tributo {t.Id}: Importe admite como máximo 2 decimales.");
            if (t.Id == 99 && string.IsNullOrWhiteSpace(t.Desc)) e.Add("El tributo 99 (Otros) requiere descripción.");
        }
        var opc = c.Opcionales ?? new();
        if (opc.Any(o => string.IsNullOrWhiteSpace(o.Id) || string.IsNullOrWhiteSpace(o.Valor))) e.Add("Opcionales: Id y Valor son obligatorios.");
        if (opc.GroupBy(o => o.Id).Any(g => g.Count() > 1)) e.Add("Opcionales: no se puede repetir el Id.");
        foreach (var o in opc.Where(o => o.Id == "2101"))
            if (!Cbu.EsValido(o.Valor)) e.Add("El opcional 2101 debe ser un CBU válido de 22 dígitos.");
        foreach (var co in c.Compradores ?? new())
        {
            if (co.Porcentaje <= 0) e.Add("Compradores: el porcentaje debe ser mayor a 0.");
            if (co.DocTipo is 80 or 86 && !Cuit.EsValida(co.DocNro)) e.Add($"Comprador {co.DocNro}: CUIT/CUIL inválida.");
        }
        if (decimal.Round(c.MonCotiz, 6) != c.MonCotiz) e.Add("MonCotiz admite como máximo 6 decimales.");
        if (MontoMaximoComprobante is decimal max && c.ImpTotal > max) e.Add($"ImpTotal supera el máximo configurado ({max:N2}).");
        if (c.PeriodoAsoc != null && TryFecha(c.PeriodoAsoc.FchHasta, out var pHasta) && pHasta > hoy)
            e.Add("PeriodoAsoc.FchHasta no puede ser futura.");
        var acts = c.Actividades ?? new();
        if (acts.Any(a => a <= 0)) e.Add("Actividades: Id inválido.");
        if (acts.Distinct().Count() != acts.Count) e.Add("Actividades: no se puede repetir el Id.");
        if (!Validacion.Catalogo.TiposDocumento.Contains(c.DocTipo)) e.Add($"DocTipo {c.DocTipo} desconocido.");
        if (ReceptoresBloqueados.Contains(c.DocNro) && c.DocNro != 0) e.Add($"El receptor {c.DocNro} está bloqueado para facturación.");
        if (CondicionEmisor is { } ce)
        {
            var esC = TiposComprobante.ClaseC.Contains(cbteTipo);
            if (ce == Validacion.CondicionEmisor.ResponsableInscripto && esC)
                e.Add("Un emisor Responsable Inscripto no emite comprobantes clase C.");
            if (ce != Validacion.CondicionEmisor.ResponsableInscripto && !esC)
                e.Add("Un emisor Monotributista o Exento sólo emite comprobantes clase C.");
        }
        return e;
    }
}

/// <summary>Condición frente al IVA del emisor.</summary>
public enum CondicionEmisor { ResponsableInscripto, Monotributo, Exento }

/// <summary>Catálogos mínimos usados por la validación.</summary>
public static class Catalogo
{
    /// <summary>Tabla de tipos de documento de ARCA: cédulas provinciales 0-24, 30, y 80-99.</summary>
    public static readonly HashSet<int> TiposDocumento = new(Enumerable.Range(0, 25).Concat(new[] { 30, 80, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 99 }));
}

/// <summary>Validación de CBU (Clave Bancaria Uniforme): 22 dígitos y dos dígitos verificadores.</summary>
public static class Cbu
{
    private static readonly int[] PesosBloque1 = { 7, 1, 3, 9, 7, 1, 3 };
    private static readonly int[] PesosBloque2 = { 3, 9, 7, 1, 3, 9, 7, 1, 3, 9, 7, 1, 3 };

    public static bool EsValido(string? cbu)
    {
        if (cbu is null || cbu.Length != 22 || !cbu.All(char.IsAsciiDigit)) return false;
        static bool Bloque(string b, int[] pesos)
        {
            var suma = 0;
            for (var i = 0; i < pesos.Length; i++) suma += (b[i] - '0') * pesos[i];
            return (10 - suma % 10) % 10 == b[^1] - '0';
        }
        return Bloque(cbu[..8], PesosBloque1) && Bloque(cbu[8..], PesosBloque2);
    }
}
