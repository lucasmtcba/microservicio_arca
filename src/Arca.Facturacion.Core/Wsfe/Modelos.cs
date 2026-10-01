using System.ComponentModel.DataAnnotations;

namespace Arca.Facturacion.Core.Wsfe;

// ===================== Solicitud de comprobante (FECAEDetRequest) =====================

/// <summary>Comprobante asociado (obligatorio en Notas de Crédito/Débito si no se informa PeriodoAsoc).</summary>
public sealed class ComprobanteAsociado
{
    /// <summary>Tipo de comprobante asociado (FEParamGetTiposCbte).</summary>
    public int Tipo { get; set; }
    public int PtoVta { get; set; }
    public long Nro { get; set; }
    /// <summary>CUIT del emisor del comprobante asociado (opcional).</summary>
    public string? Cuit { get; set; }
    /// <summary>Fecha del comprobante asociado, yyyyMMdd (opcional).</summary>
    public string? CbteFch { get; set; }
}

public sealed class Tributo
{
    /// <summary>Código de tributo (FEParamGetTiposTributos).</summary>
    public int Id { get; set; }
    public string? Desc { get; set; }
    public decimal BaseImp { get; set; }
    public decimal Alic { get; set; }
    public decimal Importe { get; set; }
}

public sealed class AlicuotaIva
{
    /// <summary>Código de alícuota (FEParamGetTiposIva): 3=0%, 4=10.5%, 5=21%, 6=27%, 8=5%, 9=2.5%.</summary>
    public int Id { get; set; }
    public decimal BaseImp { get; set; }
    public decimal Importe { get; set; }
}

public sealed class Opcional
{
    public string Id { get; set; } = string.Empty;
    public string Valor { get; set; } = string.Empty;
}

public sealed class Comprador
{
    public int DocTipo { get; set; }
    public long DocNro { get; set; }
    public decimal Porcentaje { get; set; }
}

public sealed class PeriodoAsociado
{
    /// <summary>yyyyMMdd</summary>
    public string FchDesde { get; set; } = string.Empty;
    /// <summary>yyyyMMdd</summary>
    public string FchHasta { get; set; } = string.Empty;
}

/// <summary>Detalle de un comprobante (campos de FECAEDetRequest salvo la numeración).</summary>
public class ComprobanteDetalle
{
    /// <summary>1=Productos, 2=Servicios, 3=Productos y Servicios.</summary>
    [Range(1, 3)] public int Concepto { get; set; }
    /// <summary>Tipo de documento del receptor (80=CUIT, 86=CUIL, 96=DNI, 99=Consumidor Final sin identificar).</summary>
    public int DocTipo { get; set; }
    public long DocNro { get; set; }
    /// <summary>Fecha del comprobante yyyyMMdd. Si se omite, ARCA usa la fecha de proceso.</summary>
    public string? CbteFch { get; set; }
    public decimal ImpTotal { get; set; }
    public decimal ImpTotConc { get; set; }
    public decimal ImpNeto { get; set; }
    public decimal ImpOpEx { get; set; }
    public decimal ImpTrib { get; set; }
    public decimal ImpIVA { get; set; }
    /// <summary>yyyyMMdd. Obligatorio si Concepto es 2 o 3.</summary>
    public string? FchServDesde { get; set; }
    /// <summary>yyyyMMdd. Obligatorio si Concepto es 2 o 3.</summary>
    public string? FchServHasta { get; set; }
    /// <summary>yyyyMMdd. Obligatorio si Concepto es 2 o 3 (salvo Factura de Crédito Electrónica).</summary>
    public string? FchVtoPago { get; set; }
    /// <summary>Código de moneda (FEParamGetTiposMonedas). PES = pesos.</summary>
    public string MonId { get; set; } = "PES";
    public decimal MonCotiz { get; set; } = 1m;
    /// <summary>"S" si se cancela en la misma moneda extranjera, "N" si no (sólo MonId distinto de PES).</summary>
    public string? CanMisMonExt { get; set; }
    /// <summary>Condición frente al IVA del receptor (FEParamGetCondicionIvaReceptor). Obligatorio (RG 5616).</summary>
    public int? CondicionIVAReceptorId { get; set; }
    public List<ComprobanteAsociado>? CbtesAsoc { get; set; }
    public List<Tributo>? Tributos { get; set; }
    public List<AlicuotaIva>? Iva { get; set; }
    public List<Opcional>? Opcionales { get; set; }
    public List<Comprador>? Compradores { get; set; }
    public PeriodoAsociado? PeriodoAsoc { get; set; }
    public List<int>? Actividades { get; set; }
    /// <summary>Referencia propia del sistema consumidor (p.ej. número de pedido). No se envía a ARCA; sirve para buscar emisiones.</summary>
    [MaxLength(100)]
    public string? ReferenciaExterna { get; set; }
}

/// <summary>Detalle con numeración explícita (uso directo de FECAESolicitar / FECAEARegInformativo).</summary>
public class ComprobanteNumerado : ComprobanteDetalle
{
    public long CbteDesde { get; set; }
    public long CbteHasta { get; set; }
}

/// <summary>Detalle para informar comprobantes emitidos con CAEA.</summary>
public sealed class ComprobanteCaea : ComprobanteNumerado
{
    public string Caea { get; set; } = string.Empty;
    /// <summary>Fecha y hora de generación yyyyMMddHHmmss (opcional).</summary>
    public string? CbteFchHsGen { get; set; }
}

/// <summary>Lote para FECAESolicitar.</summary>
public sealed class SolicitudCae
{
    public int PtoVta { get; set; }
    public int CbteTipo { get; set; }
    public List<ComprobanteNumerado> Comprobantes { get; set; } = new();
}

/// <summary>Lote para FECAEARegInformativo.</summary>
public sealed class InformeCaea
{
    public int PtoVta { get; set; }
    public int CbteTipo { get; set; }
    public List<ComprobanteCaea> Comprobantes { get; set; } = new();
}

// ===================== Respuestas =====================

public sealed class CabeceraRespuesta
{
    public long Cuit { get; set; }
    public int PtoVta { get; set; }
    public int CbteTipo { get; set; }
    public string? FchProceso { get; set; }
    public int CantReg { get; set; }
    /// <summary>A=Aprobado, R=Rechazado, P=Parcial.</summary>
    public string? Resultado { get; set; }
    public string? Reproceso { get; set; }
}

public sealed class DetalleRespuesta
{
    public int Concepto { get; set; }
    public int DocTipo { get; set; }
    public long DocNro { get; set; }
    public long CbteDesde { get; set; }
    public long CbteHasta { get; set; }
    public string? CbteFch { get; set; }
    /// <summary>A=Aprobado, R=Rechazado.</summary>
    public string? Resultado { get; set; }
    /// <summary>CAE otorgado (o CAEA informado).</summary>
    public string? Cae { get; set; }
    /// <summary>Vencimiento del CAE yyyyMMdd.</summary>
    public string? CaeFchVto { get; set; }
    public List<ArcaMensaje> Observaciones { get; set; } = new();
}

public sealed class RespuestaCae
{
    public CabeceraRespuesta? Cabecera { get; set; }
    public List<DetalleRespuesta> Detalles { get; set; } = new();
    public List<ArcaMensaje> Errores { get; set; } = new();
    public List<ArcaMensaje> Eventos { get; set; } = new();
    public bool Aprobado => Cabecera?.Resultado == "A";
}

public sealed class UltimoComprobante
{
    public int PtoVta { get; set; }
    public int CbteTipo { get; set; }
    public long CbteNro { get; set; }
}

/// <summary>Resultado de FECompConsultar.</summary>
public sealed class ComprobanteConsultado : ComprobanteNumerado
{
    public int PtoVta { get; set; }
    public int CbteTipo { get; set; }
    public string? Resultado { get; set; }
    /// <summary>CAE o CAEA.</summary>
    public string? CodAutorizacion { get; set; }
    /// <summary>CAE / CAEA.</summary>
    public string? EmisionTipo { get; set; }
    public string? FchVto { get; set; }
    public string? FchProceso { get; set; }
    public List<ArcaMensaje> Observaciones { get; set; } = new();
}

public sealed class Caea
{
    public string? Codigo { get; set; }
    public int Periodo { get; set; }
    public int Orden { get; set; }
    public string? FchVigDesde { get; set; }
    public string? FchVigHasta { get; set; }
    public string? FchTopeInf { get; set; }
    public string? FchProceso { get; set; }
    public List<ArcaMensaje> Observaciones { get; set; } = new();
}

public sealed class CaeaSinMovimiento
{
    public string? Caea { get; set; }
    public string? FchProceso { get; set; }
    public int PtoVta { get; set; }
    public string? Resultado { get; set; }
}

/// <summary>Ítem genérico de tabla de parámetros (Id/Desc/vigencia + atributos adicionales).</summary>
public sealed class ParametroItem
{
    public string? Id { get; set; }
    public string? Desc { get; set; }
    public string? FchDesde { get; set; }
    public string? FchHasta { get; set; }
    public Dictionary<string, string> Atributos { get; set; } = new();
}

public sealed class Cotizacion
{
    public string? MonId { get; set; }
    public decimal MonCotiz { get; set; }
    public string? FchCotiz { get; set; }
}

public sealed class EstadoServidores
{
    public string? AppServer { get; set; }
    public string? DbServer { get; set; }
    public string? AuthServer { get; set; }
    public bool TodoOk => AppServer == "OK" && DbServer == "OK" && AuthServer == "OK";
}
