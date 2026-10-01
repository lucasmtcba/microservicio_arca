using System.ComponentModel.DataAnnotations;

namespace Arca.Facturacion.Core.Configuracion;

/// <summary>Entorno de ARCA contra el que se opera.</summary>
public enum ArcaAmbiente
{
    /// <summary>Homologación (testing) de ARCA: wsaahomo / wswhomo.</summary>
    Homologacion = 0,
    /// <summary>Entorno real de ARCA: wsaa / servicios1. Los comprobantes tienen validez fiscal.</summary>
    Real = 1
}

/// <summary>Configuración del microservicio (sección "Arca" de appsettings).</summary>
public sealed class ArcaOptions
{
    public const string Seccion = "Arca";

    public ArcaAmbiente Ambiente { get; set; } = ArcaAmbiente.Homologacion;

    /// <summary>CUIT del emisor (11 dígitos, sin guiones).</summary>
    [Required, RegularExpression(@"^\d{11}$")]
    public string Cuit { get; set; } = string.Empty;

    /// <summary>Ruta al certificado PKCS#12 (.pfx/.p12) con clave privada, emitido por ARCA para el alias del servicio.</summary>
    [Required]
    public string CertificadoPath { get; set; } = string.Empty;

    /// <summary>Contraseña del .pfx. Debe venir de un secreto (variable de entorno / vault), nunca del repositorio.</summary>
    public string? CertificadoPassword { get; set; }

    /// <summary>Ruta donde se persiste el Ticket de Acceso (TA) para sobrevivir reinicios. Vacío = sólo memoria.</summary>
    public string? CacheTicketPath { get; set; }

    /// <summary>Directorio del registro durable de emisiones (idempotencia). Vacío = memoria (se pierde al reiniciar; NO recomendado).</summary>
    public string? RegistroEmisionesPath { get; set; }

    /// <summary>Habilita POST /api/v1/comprobantes/cae (FECAESolicitar con numeración del llamador, sin idempotencia ni lock).</summary>
    public bool PermitirSolicitudDirecta { get; set; }

    /// <summary>Tolerancia en pesos para controles de sumas e IVA por alícuota.</summary>
    [Range(typeof(decimal), "0", "1")]
    public decimal ToleranciaImportes { get; set; } = 0.01m;

    /// <summary>Esperas (segundos) entre consultas FECompConsultar al reconciliar un resultado incierto.</summary>
    public int[] ReconciliacionEsperasSegundos { get; set; } = { 0, 3, 10 };

    /// <summary>Esperas (ms) entre reintentos de operaciones de LECTURA ante fallas transitorias.</summary>
    public int[] ReintentosLecturaMs { get; set; } = { 200, 1000 };

    /// <summary>Minutos de caché de las tablas FEParamGet* (0 = sin caché).</summary>
    [Range(0, 10080)] public int CacheParametrosMinutos { get; set; } = 720;

    /// <summary>Minutos de caché de cotizaciones (0 = sin caché).</summary>
    [Range(0, 1440)] public int CacheCotizacionMinutos { get; set; } = 10;

    /// <summary>Fallas de comunicación consecutivas que abren el circuito hacia ARCA.</summary>
    [Range(1, 100)] public int CircuitoUmbralFallas { get; set; } = 5;

    /// <summary>Segundos que el circuito permanece abierto antes de probar de nuevo.</summary>
    [Range(1, 600)] public int CircuitoPausaSegundos { get; set; } = 30;

    /// <summary>Importe desde el cual un comprobante a consumidor final debe identificar al receptor. Null = sin control local.
    /// Ajustarlo al valor vigente fijado por ARCA.</summary>
    public decimal? MontoTopeConsumidorFinalSinIdentificar { get; set; }

    /// <summary>Si es true, antes de emitir se verifica contra ARCA (tablas cacheadas) que el punto de venta esté habilitado y la moneda exista.</summary>
    public bool ValidarContraTablasArca { get; set; }

    /// <summary>Completa CbteFch con la fecha actual de Argentina si no se informa.</summary>
    public bool CompletarFechaAutomatica { get; set; } = true;

    /// <summary>Si la moneda es extranjera y MonCotiz es 0, toma la cotización de ARCA (FEParamGetCotizacion).</summary>
    public bool CotizacionAutomatica { get; set; } = true;

    /// <summary>Intervalo del reconciliador en segundo plano de emisiones pendientes (0 = deshabilitado).</summary>
    [Range(0, 86400)] public int ReconciliadorIntervaloSegundos { get; set; } = 60;

    /// <summary>Antigüedad mínima de una emisión pendiente para que el reconciliador la procese.</summary>
    [Range(0, 86400)] public int ReconciliadorAntiguedadMinimaSegundos { get; set; } = 120;

    /// <summary>Días que se conservan los registros de emisiones autorizadas (0 = indefinido).</summary>
    [Range(0, 3650)] public int RetencionRegistroDias { get; set; } = 90;

    /// <summary>Renueva el TA en segundo plano antes de que venza y lo precarga al iniciar.</summary>
    public bool RenovacionProactivaTicket { get; set; } = true;

    /// <summary>URL a la que se notifica (POST JSON firmado con HMAC-SHA256) cada comprobante autorizado. Vacío = deshabilitado.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Secreto para la cabecera X-Firma-Arca (HMAC-SHA256 del cuerpo, hex).</summary>
    public string? WebhookSecreto { get; set; }

    /// <summary>Días de vigencia del certificado por debajo de los cuales el health check se degrada.</summary>
    [Range(1, 365)] public int AlertaVencimientoCertificadoDias { get; set; } = 30;

    /// <summary>Timeout de operaciones de lectura (consultas, parámetros), en segundos.</summary>
    [Range(1, 300)] public int TimeoutLecturaSegundos { get; set; } = 20;

    /// <summary>Timeout de operaciones que generan comprobantes (FECAESolicitar, CAEA), en segundos.</summary>
    [Range(5, 300)] public int TimeoutEmisionSegundos { get; set; } = 60;

    /// <summary>Máximo de llamadas simultáneas a ARCA (bulkhead): protege a ARCA y al servicio ante picos.</summary>
    [Range(1, 256)] public int MaxLlamadasConcurrentesArca { get; set; } = 8;

    /// <summary>Proxy HTTP saliente para llegar a ARCA (p.ej. http://proxy:3128). Vacío = conexión directa.</summary>
    public string? ProxySaliente { get; set; }

    /// <summary>Minutos de vida de una conexión HTTP agrupada antes de renovarse (toma cambios de DNS).</summary>
    [Range(1, 120)] public int VidaConexionMinutos { get; set; } = 5;

    /// <summary>Solicita automáticamente el CAEA de la próxima quincena (contingencia). Requiere régimen CAEA habilitado.</summary>
    public bool SolicitarCaeaAutomatico { get; set; }

    /// <summary>Directorio de plantillas de comprobantes. Vacío = plantillas en memoria.</summary>
    public string? PlantillasPath { get; set; }

    /// <summary>Archivo JSONL de auditoría (una línea por operación). Vacío = auditoría deshabilitada.</summary>
    public string? AuditoriaPath { get; set; }

    /// <summary>Precarga las tablas de parámetros al iniciar.</summary>
    public bool PrecargarParametros { get; set; } = true;

    /// <summary>Modo simulación: no llama a ARCA; numeración y CAE ficticios (empiezan con 99). Para integrar sistemas consumidores.</summary>
    public bool ModoSimulacion { get; set; }

    /// <summary>Porcentaje de fallas de comunicación simuladas (0-100) para probar la resiliencia de los consumidores.</summary>
    [Range(0, 100)] public int SimulacionFallasPorcentaje { get; set; }

    /// <summary>Importe máximo por comprobante (protección contra errores de carga). Null = sin tope.</summary>
    public decimal? MontoMaximoComprobante { get; set; }

    /// <summary>Importe desde el cual se emite la advertencia "importe alto".</summary>
    public decimal? UmbralAdvertenciaImporte { get; set; }

    /// <summary>Condición frente al IVA del emisor: ResponsableInscripto, Monotributo o Exento. Restringe las clases que puede emitir.</summary>
    public Validacion.CondicionEmisor? CondicionIvaEmisor { get; set; }

    /// <summary>Documentos (CUIT/DNI) de receptores bloqueados para facturación.</summary>
    public long[] ReceptoresBloqueados { get; set; } = Array.Empty<long>();

    /// <summary>Archivo SQLite para el registro de emisiones (recomendado). Si se define, tiene prioridad sobre RegistroEmisionesPath,
    /// cuyos JSON se migran automáticamente la primera vez.</summary>
    public string? RegistroEmisionesSqlite { get; set; }

    /// <summary>Cadena de conexión MySQL/MariaDB para el registro de emisiones (varios servidores). Tiene prioridad sobre SQLite y JSON.</summary>
    public string? RegistroEmisionesMySql { get; set; }

    /// <summary>Si es true y hay RegistroEmisionesMySql, la numeración usa GET_LOCK (bloqueo distribuido entre servidores).</summary>
    public bool BloqueoDistribuidoMySql { get; set; } = true;

    /// <summary>Al iniciar con MySQL vacío, copia los registros existentes de SQLite/JSON configurados.</summary>
    public bool MigrarRegistroAMySql { get; set; } = true;

    /// <summary>Directorio para bloqueos de numeración entre procesos del mismo servidor. Vacío = sólo bloqueo en memoria.</summary>
    public string? BloqueoEntreProcesosPath { get; set; }

    /// <summary>Tamaño máximo de la caché en memoria (entradas).</summary>
    [Range(100, 1_000_000)] public int CacheTamanoMaximo { get; set; } = 10_000;

    /// <summary>Horas que se cachean los comprobantes autorizados consultados (son inmutables).</summary>
    [Range(0, 720)] public int CacheComprobantesHoras { get; set; } = 24;

    /// <summary>Directorio de suscripciones (facturación recurrente). Vacío = memoria.</summary>
    public string? SuscripcionesPath { get; set; }

    /// <summary>Directorio de la agenda de receptores. Vacío = memoria.</summary>
    public string? ReceptoresPath { get; set; }

    /// <summary>Datos del emisor para la representación impresa.</summary>
    public Servicios.DatosEmisor Emisor { get; set; } = new();

    /// <summary>Clave AES-256 en base64 (32 bytes) para cifrar en reposo el registro de emisiones. Debe venir de un secreto.</summary>
    public string? ClaveCifradoRegistro { get; set; }

    /// <summary>Huellas SHA-256 (hex) admitidas del certificado TLS de ARCA (pinning). Vacío = validación TLS estándar.
    /// Si se usa, actualizarlas antes de que ARCA renueve su certificado.</summary>
    public string[] HuellasTlsArca { get; set; } = Array.Empty<string>();

    /// <summary>URLs adicionales de webhook (se suman a WebhookUrl).</summary>
    public string[] WebhookUrls { get; set; } = Array.Empty<string>();

    /// <summary>Notificar también los comprobantes rechazados (evento comprobante.rechazado).</summary>
    public bool WebhookNotificarRechazos { get; set; }

    /// <summary>Factor de espera entre reintentos del webhook (1 = 3 s, 9 s). Para pruebas puede ser 0.</summary>
    [Range(0, 10)] public double WebhookEsperaFactor { get; set; } = 1;

    /// <summary>Días de retención de los archivos de auditoría (0 = indefinido).</summary>
    [Range(0, 3650)] public int RetencionAuditoriaDias { get; set; } = 365;

    /// <summary>Intervalo (minutos) del ejecutor de suscripciones (0 = deshabilitado).</summary>
    [Range(0, 1440)] public int SuscripcionesIntervaloMinutos { get; set; } = 15;

    /// <summary>Períodos atrasados que una suscripción puede facturar sola; más que eso requiere confirmación.</summary>
    [Range(0, 12)] public int SuscripcionesMaxPeriodosAtrasados { get; set; } = 1;

    /// <summary>Override opcional de la URL de WSAA.</summary>
    public string? WsaaUrl { get; set; }

    /// <summary>Override opcional de la URL de WSFEv1.</summary>
    public string? WsfeUrl { get; set; }

    /// <summary>Override opcional de la URL de ws_sr_padron_a13.</summary>
    public string? PadronUrl { get; set; }

    /// <summary>Horas de caché de consultas al padrón.</summary>
    [Range(0, 720)] public int PadronCacheHoras { get; set; } = 24;

    public string PadronUrlEfectiva => !string.IsNullOrWhiteSpace(PadronUrl) ? PadronUrl! :
        Ambiente == ArcaAmbiente.Real
            ? "https://aws.afip.gov.ar/sr-padron/webservices/personaServiceA13"
            : "https://awshomo.afip.gov.ar/sr-padron/webservices/personaServiceA13";

    /// <summary>Timeout HTTP por llamada a ARCA, en segundos.</summary>
    [Range(5, 300)]
    public int TimeoutSegundos { get; set; } = 60;

    /// <summary>Minutos antes del vencimiento del TA en los que se considera vencido y se renueva.</summary>
    [Range(1, 120)]
    public int MargenRenovacionTicketMinutos { get; set; } = 10;

    public string WsaaUrlEfectiva => !string.IsNullOrWhiteSpace(WsaaUrl) ? WsaaUrl! :
        Ambiente == ArcaAmbiente.Real
            ? "https://wsaa.afip.gov.ar/ws/services/LoginCms"
            : "https://wsaahomo.afip.gov.ar/ws/services/LoginCms";

    public string WsfeUrlEfectiva => !string.IsNullOrWhiteSpace(WsfeUrl) ? WsfeUrl! :
        Ambiente == ArcaAmbiente.Real
            ? "https://servicios1.afip.gov.ar/wsfev1/service.asmx"
            : "https://wswhomo.afip.gov.ar/wsfev1/service.asmx";

    public long CuitNumerico => long.Parse(Cuit, System.Globalization.CultureInfo.InvariantCulture);
}
