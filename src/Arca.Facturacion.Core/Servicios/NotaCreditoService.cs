using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;

namespace Arca.Facturacion.Core.Servicios;

/// <summary>Emite la Nota de Crédito que anula totalmente un comprobante ya autorizado.</summary>
public sealed class NotaCreditoService
{
    /// <summary>Factura → Nota de Crédito de la misma clase.</summary>
    public static readonly IReadOnlyDictionary<int, int> NcPorFactura = new Dictionary<int, int>
    {
        [1] = 3, [6] = 8, [11] = 13, [51] = 53, [2] = 3, [7] = 8, [12] = 13, [52] = 53
    };

    private readonly IWsfeClient _wsfe;
    private readonly IFacturacionService _fact;
    private readonly ComprobanteValidator _val;
    private readonly IRegistroEmisiones? _registro;

    /// <summary>Tipos de nota de crédito (incluidas las FCE MiPyME).</summary>
    private static readonly HashSet<int> TiposNc = new() { 3, 8, 13, 53, 203, 208, 213 };
    /// <summary>Opcionales propios de la factura que no se trasladan a la nota de crédito (CBU/alias y transferencia FCE).</summary>
    private static readonly HashSet<string> OpcionalesNoTrasladables = new() { "2101", "2102", "27" };

    /// <param name="registro">Registro local de emisiones: permite controlar las NC ya emitidas contra un comprobante.
    /// Si es null no se controla el acumulado (sólo el importe de cada NC contra el original).</param>
    public NotaCreditoService(IWsfeClient wsfe, IFacturacionService fact, ComprobanteValidator val, IRegistroEmisiones? registro = null)
    { _wsfe = wsfe; _fact = fact; _val = val; _registro = registro; }

    /// <summary>Suma de las NC registradas localmente (autorizadas o pendientes de confirmar) que asocian el comprobante indicado,
    /// excluyendo la emisión con la misma clave de idempotencia (reintentos).</summary>
    private (decimal Total, int Cantidad) NcPrevias(int ptoVta, int cbteTipo, long cbteNro, string idempotencyKey, string cliente)
    {
        if (_registro == null) return (0m, 0);
        var clave = cliente + ":" + idempotencyKey;
        var previas = _registro.Listar()
            .Where(r => r.Clave != clave && TiposNc.Contains(r.CbteTipo) && r.Detalle?.CbtesAsoc != null
                        && (r.Estado == EstadoEmision.Pendiente || r.Resultado is { Aprobado: true })
                        && r.Detalle.CbtesAsoc.Any(a => a.Tipo == cbteTipo && a.PtoVta == ptoVta && a.Nro == cbteNro))
            .ToList();
        return (previas.Sum(r => r.Detalle!.ImpTotal), previas.Count);
    }

    /// <summary>Opcionales del original válidos para la NC: se quitan CBU/alias (2101/2102) y transferencia (27);
    /// en NC FCE se garantiza el opcional 22 (anulación) con "N" por defecto.</summary>
    internal static List<Opcional>? OpcionalesParaNc(IEnumerable<Opcional>? originales, int tipoNc)
    {
        var l = (originales ?? Enumerable.Empty<Opcional>()).Where(o => !OpcionalesNoTrasladables.Contains(o.Id))
            .Select(o => new Opcional { Id = o.Id, Valor = o.Valor }).ToList();
        if (tipoNc is 203 or 208 or 213 && !l.Any(o => o.Id == "22")) l.Add(new Opcional { Id = "22", Valor = "N" });
        return l.Count > 0 ? l : null;
    }

    /// <summary>Nota de Crédito PARCIAL: devuelve los importes indicados (calculados con la calculadora) asociados al original.
    /// El total no puede superar el del comprobante original.</summary>
    public async Task<ResultadoEmision> NotaCreditoParcialAsync(int ptoVta, int cbteTipo, long cbteNro, SolicitudCalculo importes,
        string idempotencyKey, string cliente, CancellationToken ct)
    {
        var (o, tipoNc) = await OriginalAsync(ptoVta, cbteTipo, cbteNro, ct).ConfigureAwait(false);
        importes.CbteTipo = tipoNc;
        var calc = CalculadoraImportes.Calcular(importes);
        if (calc.ImpTotal <= 0) throw new ValidacionException(new[] { "El importe de la nota de crédito debe ser mayor a 0." });
        if (calc.ImpTotal > o.ImpTotal) throw new ValidacionException(new[] { $"El total de la nota de crédito ({calc.ImpTotal}) supera al del comprobante original ({o.ImpTotal})." });
        var (previo, _) = NcPrevias(ptoVta, cbteTipo, cbteNro, idempotencyKey, cliente);
        if (previo + calc.ImpTotal > o.ImpTotal)
            throw new ValidacionException(new[] { $"Ya se acreditaron {previo} del comprobante original ({o.ImpTotal}); el saldo disponible es {o.ImpTotal - previo} y la nota de crédito es de {calc.ImpTotal}." });
        var nc = Base(o, ptoVta, cbteTipo, cbteNro, tipoNc);
        nc.ImpTotal = calc.ImpTotal; nc.ImpNeto = calc.ImpNeto; nc.ImpIVA = calc.ImpIVA; nc.ImpTrib = calc.ImpTrib;
        nc.ImpOpEx = calc.ImpOpEx; nc.ImpTotConc = calc.ImpTotConc; nc.Iva = calc.Iva; nc.Tributos = calc.Tributos;
        return await _fact.EmitirAsync(ptoVta, tipoNc, nc, idempotencyKey, cliente, ct).ConfigureAwait(false);
    }

    /// <summary>Emite un comprobante NUEVO idéntico a uno existente (mismo receptor e importes) con fecha de hoy.
    /// Útil para facturación recurrente.</summary>
    public async Task<ResultadoEmision> DuplicarAsync(int ptoVta, int cbteTipo, long cbteNro, string idempotencyKey, string cliente, CancellationToken ct)
    {
        var o = await _wsfe.FECompConsultarAsync(ptoVta, cbteTipo, cbteNro, ct).ConfigureAwait(false)
                ?? throw new ValidacionException(new[] { $"El comprobante {ptoVta}-{cbteTipo}-{cbteNro} no existe en ARCA." });
        var hoy = _val.HoyArgentina();
        var d = new ComprobanteDetalle
        {
            Concepto = o.Concepto, DocTipo = o.DocTipo, DocNro = o.DocNro,
            ImpTotal = o.ImpTotal, ImpTotConc = o.ImpTotConc, ImpNeto = o.ImpNeto, ImpOpEx = o.ImpOpEx, ImpTrib = o.ImpTrib, ImpIVA = o.ImpIVA,
            MonId = o.MonId, MonCotiz = 1, // moneda extranjera: se reemplaza abajo por la cotización del día
            CanMisMonExt = o.MonId == "PES" ? null : (o.CanMisMonExt ?? "N"),
            CondicionIVAReceptorId = o.CondicionIVAReceptorId, Iva = o.Iva, Tributos = o.Tributos,
            Opcionales = o.Opcionales is { Count: > 0 } ? o.Opcionales : null,
            CbtesAsoc = o.CbtesAsoc is { Count: > 0 } ? o.CbtesAsoc : null, PeriodoAsoc = o.PeriodoAsoc,
            CbteFch = hoy.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)
        };
        if (o.MonId != "PES")
        {
            // Se consulta explícitamente: no depende de que la cotización automática esté habilitada en la emisión.
            var cot = await _wsfe.FEParamGetCotizacionAsync(o.MonId, null, ct).ConfigureAwait(false);
            if (cot.MonCotiz <= 0) throw new ValidacionException(new[] { $"ARCA no informó cotización para {o.MonId}." });
            d.MonCotiz = cot.MonCotiz;
        }
        if (o.Concepto is 2 or 3)
        {
            // Servicios: se replica la duración del período facturado, terminando hoy.
            var dur = ComprobanteValidator.TryFecha(o.FchServDesde, out var a) && ComprobanteValidator.TryFecha(o.FchServHasta, out var b) ? b.DayNumber - a.DayNumber : 0;
            d.FchServHasta = d.CbteFch;
            d.FchServDesde = hoy.AddDays(-dur).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
            d.FchVtoPago = hoy.AddDays(10).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        }
        return await _fact.EmitirAsync(ptoVta, cbteTipo, d, idempotencyKey, cliente, ct).ConfigureAwait(false);
    }

    private async Task<(ComprobanteConsultado, int)> OriginalAsync(int ptoVta, int cbteTipo, long cbteNro, CancellationToken ct)
    {
        if (TiposComprobante.FacturaCreditoElectronica.Contains(cbteTipo))
            throw new ValidacionException(new[] { "La anulación de Facturas de Crédito Electrónica MiPyME requiere el circuito FCE (aceptación/rechazo); emitir la NC manualmente." });
        if (!NcPorFactura.TryGetValue(cbteTipo, out var tipoNc))
            throw new ValidacionException(new[] { $"El tipo {cbteTipo} no admite nota de crédito automática (sólo facturas y notas de débito A, B, C y M)." });
        var o = await _wsfe.FECompConsultarAsync(ptoVta, cbteTipo, cbteNro, ct).ConfigureAwait(false)
                ?? throw new ValidacionException(new[] { $"El comprobante {ptoVta}-{cbteTipo}-{cbteNro} no existe en ARCA." });
        if (o.Resultado != "A") throw new ValidacionException(new[] { "Sólo se pueden acreditar comprobantes aprobados." });
        return (o, tipoNc);
    }

    private ComprobanteDetalle Base(ComprobanteConsultado o, int ptoVta, int cbteTipo, long cbteNro, int tipoNc)
    {
        var hoy = _val.HoyArgentina().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        return new ComprobanteDetalle
        {
            Concepto = o.Concepto, DocTipo = o.DocTipo, DocNro = o.DocNro,
            FchServDesde = o.Concepto is 2 or 3 ? o.FchServDesde : null,
            FchServHasta = o.Concepto is 2 or 3 ? o.FchServHasta : null,
            FchVtoPago = o.Concepto is 2 or 3 ? hoy : null,
            MonId = o.MonId, MonCotiz = o.MonCotiz == 0 ? 1 : o.MonCotiz, CanMisMonExt = o.MonId == "PES" ? null : (o.CanMisMonExt ?? "N"),
            CondicionIVAReceptorId = o.CondicionIVAReceptorId,
            Opcionales = OpcionalesParaNc(o.Opcionales, tipoNc),
            CbtesAsoc = new() { new ComprobanteAsociado { Tipo = cbteTipo, PtoVta = ptoVta, Nro = cbteNro, CbteFch = o.CbteFch } },
            CbteFch = hoy
        };
    }

    public async Task<ResultadoEmision> AnularAsync(int ptoVta, int cbteTipo, long cbteNro, string idempotencyKey, string cliente, CancellationToken ct)
    {
        if (TiposComprobante.FacturaCreditoElectronica.Contains(cbteTipo))
            throw new ValidacionException(new[] { "La anulación de Facturas de Crédito Electrónica MiPyME requiere el circuito FCE (aceptación/rechazo); emitir la NC manualmente." });
        if (!NcPorFactura.TryGetValue(cbteTipo, out var tipoNc))
            throw new ValidacionException(new[] { $"El tipo {cbteTipo} no admite anulación automática (sólo facturas y notas de débito A, B, C y M)." });

        var o = await _wsfe.FECompConsultarAsync(ptoVta, cbteTipo, cbteNro, ct).ConfigureAwait(false)
                ?? throw new ValidacionException(new[] { $"El comprobante {ptoVta}-{cbteTipo}-{cbteNro} no existe en ARCA." });
        if (o.Resultado != "A") throw new ValidacionException(new[] { "Sólo se pueden anular comprobantes aprobados." });
        var (previo, cant) = NcPrevias(ptoVta, cbteTipo, cbteNro, idempotencyKey, cliente);
        if (cant > 0)
            throw new ValidacionException(new[] { $"El comprobante ya tiene {cant} nota(s) de crédito por {previo}: no se puede anular por el total. " +
                $"Emitir una nota de crédito parcial por el saldo ({o.ImpTotal - previo})." });

        var nc = new ComprobanteDetalle
        {
            Concepto = o.Concepto, DocTipo = o.DocTipo, DocNro = o.DocNro,
            ImpTotal = o.ImpTotal, ImpTotConc = o.ImpTotConc, ImpNeto = o.ImpNeto, ImpOpEx = o.ImpOpEx, ImpTrib = o.ImpTrib, ImpIVA = o.ImpIVA,
            FchServDesde = o.Concepto is 2 or 3 ? o.FchServDesde : null,
            FchServHasta = o.Concepto is 2 or 3 ? o.FchServHasta : null,
            MonId = o.MonId, MonCotiz = o.MonCotiz == 0 ? 1 : o.MonCotiz, CanMisMonExt = o.MonId == "PES" ? null : (o.CanMisMonExt ?? "N"),
            CondicionIVAReceptorId = o.CondicionIVAReceptorId,
            Iva = o.Iva, Tributos = o.Tributos, Opcionales = OpcionalesParaNc(o.Opcionales, tipoNc),
            CbtesAsoc = new() { new ComprobanteAsociado { Tipo = cbteTipo, PtoVta = ptoVta, Nro = cbteNro, CbteFch = o.CbteFch } }
        };
        var hoy = _val.HoyArgentina().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        nc.CbteFch = hoy;
        if (o.Concepto is 2 or 3) nc.FchVtoPago = hoy;
        return await _fact.EmitirAsync(ptoVta, tipoNc, nc, idempotencyKey, cliente, ct).ConfigureAwait(false);
    }
}
