using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arca.Facturacion.Core.Soap;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.Extensions.Logging;

namespace Arca.Facturacion.Core.Servicios;

/// <summary>Resultado de la emisión de un comprobante.</summary>
public sealed class ResultadoEmision
{
    public int PtoVta { get; set; }
    public int CbteTipo { get; set; }
    public long CbteNro { get; set; }
    /// <summary>A=Aprobado, R=Rechazado.</summary>
    public string? Resultado { get; set; }
    public string? Cae { get; set; }
    /// <summary>yyyyMMdd</summary>
    public string? CaeFchVto { get; set; }
    public string? CbteFch { get; set; }
    public List<ArcaMensaje> Observaciones { get; set; } = new();
    public List<ArcaMensaje> Errores { get; set; } = new();
    public List<ArcaMensaje> Eventos { get; set; } = new();
    /// <summary>true si el CAE se recuperó mediante FECompConsultar tras un resultado incierto.</summary>
    public bool Reconciliado { get; set; }
    /// <summary>Advertencias de la validación local (no impidieron emitir).</summary>
    public List<string>? Advertencias { get; set; }
    /// <summary>Referencia externa informada por el cliente (p.ej. número de pedido).</summary>
    public string? Referencia { get; set; }
    /// <summary>URL del código QR fiscal (RG 4892) para imprimir en el comprobante.</summary>
    public string? QrUrl { get; set; }
    /// <summary>Sugerencias de corrección para los errores/observaciones devueltos.</summary>
    public List<string> Ayuda { get; set; } = new();
    public bool Aprobado => Resultado == "A";
    /// <summary>true si la respuesta proviene del registro de idempotencia (reintento de una emisión ya resuelta).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Repetido { get; private set; }

    /// <summary>Copia marcada como repetida (no altera la instancia guardada).</summary>
    public ResultadoEmision ComoRepetido()
    {
        var c = (ResultadoEmision)MemberwiseClone();
        c.Repetido = true;
        return c;
    }

    /// <summary>Número fiscal PPPPP-NNNNNNNN.</summary>
    public string NumeroFormateado => FormatoComprobante.Numero(PtoVta, CbteNro);
    /// <summary>Descripción legible, p.ej. "Factura B 00003-00000042".</summary>
    public string Descripcion => FormatoComprobante.Descripcion(PtoVta, CbteTipo, CbteNro);
}

/// <summary>Notificación asíncrona de comprobantes autorizados (p.ej. webhook).</summary>
public interface INotificadorEmisiones
{
    void Notificar(ResultadoEmision resultado);
}

/// <summary>Opciones de comportamiento del servicio de emisión.</summary>
public sealed record OpcionesEmision(long CuitEmisor = 0, bool CompletarFecha = true, bool CotizacionAutomatica = true, bool ValidarContraTablas = false);

/// <summary>Resumen operativo del registro de emisiones.</summary>
public sealed class EstadisticasEmision
{
    public int Autorizadas { get; set; }
    public int Pendientes { get; set; }
    public int AutorizadasHoy { get; set; }
    public decimal ImporteHoy { get; set; }
    public Dictionary<string, int> PorTipo { get; set; } = new();
}

public sealed class IdempotenciaConflictoException : Exception
{
    public IdempotenciaConflictoException() : base("La Idempotency-Key ya se usó con un contenido distinto.") { }
}

public interface IFacturacionService
{
    /// <summary>Emite un comprobante con numeración automática (último autorizado + 1) y obtiene CAE.</summary>
    /// <param name="idempotencyKey">Obligatoria. Reintentar con la misma clave nunca genera un segundo comprobante.</param>
    /// <param name="cliente">Identificador del cliente llamador (aísla las claves de idempotencia entre clientes).</param>
    Task<ResultadoEmision> EmitirAsync(int ptoVta, int cbteTipo, ComprobanteDetalle detalle, string idempotencyKey, string cliente, CancellationToken ct);
    /// <summary>Estado de una emisión por su clave de idempotencia (null si no existe).</summary>
    RegistroEmision? ObtenerEmision(string idempotencyKey, string cliente);
    IReadOnlyList<RegistroEmision> Pendientes();
    /// <summary>Resuelve emisiones pendientes con FECompConsultar. Devuelve cuántas se resolvieron.</summary>
    Task<int> ReconciliarPendientesAsync(TimeSpan antiguedadMinima, CancellationToken ct);
    /// <summary>Elimina registros autorizados más antiguos que la retención. Devuelve cuántos se eliminaron.</summary>
    int Purgar(TimeSpan retencion);
    EstadisticasEmision Estadisticas();
    /// <summary>Resuelve manualmente una emisión pendiente: "autorizado", "liberado" o "no-pendiente".</summary>
    Task<string> ResolverPendienteAsync(string clave, CancellationToken ct);
}

/// <summary>Orquesta la emisión segura:
/// 1) idempotencia durable (el número se reserva y persiste ANTES de llamar a ARCA);
/// 2) numeración serializada por (PtoVta, CbteTipo);
/// 3) ante resultado incierto (timeout, 5xx, soap:Fault) se reconcilia con FECompConsultar con esperas;
/// 4) un reintento con la misma clave reenvía el MISMO número, nunca uno nuevo.
/// Limitación: el semáforo es por proceso. Con varias réplicas, una sola debe emitir por punto de venta.</summary>
public sealed class FacturacionService : IFacturacionService
{
    private const int ErrorNumeroNoCorrelativo = 10016;
    private static readonly JsonSerializerOptions JsonOpt = new() { PropertyNamingPolicy = null };

    private readonly IWsfeClient _wsfe;
    private readonly ComprobanteValidator _validator;
    private readonly IRegistroEmisiones _registro;
    private readonly ILogger<FacturacionService> _log;
    private readonly IReadOnlyList<TimeSpan> _esperas;
    private readonly TimeProvider _reloj;
    private readonly ConcurrentDictionary<(int, int), SemaphoreSlim> _locks = new();
    private readonly OpcionesEmision _op;
    private readonly INotificadorEmisiones? _notif;

    public FacturacionService(IWsfeClient wsfe, ComprobanteValidator validator, IRegistroEmisiones registro,
        ILogger<FacturacionService> log, TimeProvider reloj, IReadOnlyList<TimeSpan>? esperasReconciliacion = null,
        OpcionesEmision? opciones = null, INotificadorEmisiones? notificador = null)
    {
        _wsfe = wsfe; _validator = validator; _registro = registro; _log = log; _reloj = reloj;
        _op = opciones ?? new OpcionesEmision(CompletarFecha: false, CotizacionAutomatica: false);
        _notif = notificador;
        _esperas = esperasReconciliacion ?? new[] { TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10) };
    }

    public async Task<ResultadoEmision> EmitirAsync(int ptoVta, int cbteTipo, ComprobanteDetalle detalle, string idempotencyKey, string cliente, CancellationToken ct)
    {
        var inicio = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return await EmitirMedidoAsync(ptoVta, cbteTipo, detalle, idempotencyKey, cliente, ct).ConfigureAwait(false); }
        finally
        {
            Arca.Facturacion.Core.Resiliencia.ArcaTelemetria.DuracionEmision.Record(
                System.Diagnostics.Stopwatch.GetElapsedTime(inicio).TotalMilliseconds, new KeyValuePair<string, object?>("cbteTipo", cbteTipo));
        }
    }

    private async Task<ResultadoEmision> EmitirMedidoAsync(int ptoVta, int cbteTipo, ComprobanteDetalle detalle, string idempotencyKey, string cliente, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(detalle);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100 || idempotencyKey.Any(ch => ch is < '!' or > '~'))
            throw new ValidacionException(new[] { "La cabecera Idempotency-Key es obligatoria: 1 a 100 caracteres ASCII imprimibles, sin espacios." });
        if (idempotencyKey.Contains(':', StringComparison.Ordinal))
            throw new ValidacionException(new[] { "La cabecera Idempotency-Key no puede contener el carácter ':' (se reserva como separador interno)." });

        var clave = ClaveDe(cliente, idempotencyKey);
        if (clave.Length > LargoMaximoClave)
            throw new ValidacionException(new[] { $"El identificador de cliente más la Idempotency-Key superan los {LargoMaximoClave} caracteres admitidos por el registro. Usar una clave más corta." });
        var hash = Hash(ptoVta, cbteTipo, detalle);

        // La búsqueda idempotente va ANTES de validar: un reintento fuera de la ventana de fecha debe devolver lo ya emitido.
        var previo = _registro.Obtener(clave);
        if (previo != null && previo.Hash != hash) throw new IdempotenciaConflictoException();
        if (previo is { Estado: EstadoEmision.Autorizado, Resultado: not null }) return previo.Resultado.ComoRepetido();

        var teniaPrevio = previo != null;
        ComprobanteDetalle efectivo = previo?.Detalle ?? detalle;
        if (previo == null)
        {
            efectivo = await EnriquecerAsync(detalle, ct).ConfigureAwait(false);
            var errores = _validator.Validar(ptoVta, cbteTipo, efectivo);
            if (errores.Count > 0) throw new ValidacionException(errores);
            if (_op.ValidarContraTablas) await ValidarContraTablasAsync(ptoVta, efectivo, ct).ConfigureAwait(false);
        }

        var sem = _locks.GetOrAdd((ptoVta, cbteTipo), _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var bloqueo = Bloqueo == null ? null : await Bloqueo.AdquirirAsync(ptoVta, cbteTipo, ct).ConfigureAwait(false);
            // A partir de aquí no se cancela: abortar a mitad deja el estado incierto.
            previo = _registro.Obtener(clave);
            if (previo != null && previo.Hash != hash) throw new IdempotenciaConflictoException();
            if (previo is { Estado: EstadoEmision.Autorizado, Resultado: not null }) return previo.Resultado.ComoRepetido();

            if (teniaPrevio && previo == null)
            {
                // El pendiente desapareció entre la primera lectura y la relectura bajo bloqueo (lo liberó el reconciliador):
                // el detalle leído antes no está validado ni enriquecido; se reinicia el flujo completo fuera del bloqueo.
                _log.LogInformation("La emisión pendiente {Clave} fue liberada mientras se esperaba el bloqueo; se reinicia", clave);
            }
            else if (previo is { Estado: EstadoEmision.Pendiente })
            {
                _log.LogWarning("Reintento de emisión pendiente {Pv}-{Tipo}-{Nro}: se verifica en ARCA antes de reenviar", ptoVta, cbteTipo, previo.CbteNro);
                var detPrevio = previo.Detalle ?? detalle;
                var cbtePrevio = Numerar(detPrevio, previo.CbteNro);
                var recon = await ConsultarAsync(ptoVta, cbteTipo, cbtePrevio, conEsperas: false).ConfigureAwait(false);
                if (recon != null) return Autorizar(previo, recon);
                return await EnviarAsync(previo, detPrevio, permitirRenumerar: false).ConfigureAwait(false);
            }
            else
            {
            var ultimo = await ProximoNumeroBaseAsync(ptoVta, cbteTipo, clave).ConfigureAwait(false);
            var reg = new RegistroEmision
            {
                Clave = clave, Hash = hash, PtoVta = ptoVta, CbteTipo = cbteTipo, CbteNro = ultimo + 1,
                Estado = EstadoEmision.Pendiente, Creado = _reloj.GetUtcNow(),
                Detalle = efectivo, IdempotencyKey = idempotencyKey, Cliente = cliente
            };
            _registro.Guardar(reg);
            var res = await EnviarAsync(reg, efectivo, permitirRenumerar: true).ConfigureAwait(false);
            var adv = _validator.Advertencias(cbteTipo, efectivo);
            if (adv.Count > 0)
            {
                res.Advertencias = adv.ToList();
                Arca.Facturacion.Core.Resiliencia.ArcaTelemetria.Advertencias.Add(adv.Count);
            }
            return res;
            }
        }
        finally { sem.Release(); }
        // Sólo se llega aquí si hubo que reiniciar el flujo.
        return await EmitirMedidoAsync(ptoVta, cbteTipo, detalle, idempotencyKey, cliente, ct).ConfigureAwait(false);
    }

    /// <summary>Largo máximo de la clave compuesta (cliente:Idempotency-Key): límite de la columna del registro MySQL.</summary>
    public const int LargoMaximoClave = 191;

    private static string ClaveDe(string cliente, string idempotencyKey) => cliente + ":" + idempotencyKey;

    /// <summary>Último número "ocupado" del punto de venta/tipo antes de reservar uno nuevo. Primero intenta resolver las
    /// emisiones pendientes locales del mismo pv/tipo (FECompConsultar): si alguna sigue sin confirmar, se rechaza la
    /// emisión (503) en lugar de arriesgar dos claves con el mismo número. Debe llamarse con el bloqueo tomado.</summary>
    private async Task<long> ProximoNumeroBaseAsync(int pv, int tipo, string claveActual)
    {
        var pendientes = Pendientes().Where(p => p.PtoVta == pv && p.CbteTipo == tipo && p.Clave != claveActual).ToList();
        foreach (var p in pendientes)
        {
            var actual = _registro.Obtener(p.Clave);
            if (actual is not { Estado: EstadoEmision.Pendiente }) continue;
            ComprobanteConsultado? c = null;
            if (actual.Detalle != null)
            {
                try { c = await ConsultarAsync(pv, tipo, Numerar(actual.Detalle, actual.CbteNro), conEsperas: false).ConfigureAwait(false); }
                catch (ArcaException ex) { _log.LogWarning(ex, "No se pudo verificar la pendiente {Pv}-{Tipo}-{Nro}", pv, tipo, actual.CbteNro); }
            }
            if (c != null) { Autorizar(actual, c); continue; }
            throw new ArcaComunicacionException(
                $"Hay una emisión pendiente sin confirmar en {pv}-{tipo} (número {actual.CbteNro}). No se asigna un número nuevo hasta " +
                "resolverla (reconciliación automática o resolución manual). Reintentar más tarde.");
        }
        var ultimo = await _wsfe.FECompUltimoAutorizadoAsync(pv, tipo, CancellationToken.None).ConfigureAwait(false);
        return ultimo.CbteNro;
    }

    /// <summary>Toma el semáforo en memoria y, si está configurado, el bloqueo entre procesos de (pv, tipo).</summary>
    private async Task<IAsyncDisposable> BloquearAsync(int pv, int tipo, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd((pv, tipo), _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var externo = Bloqueo == null ? null : await Bloqueo.AdquirirAsync(pv, tipo, ct).ConfigureAwait(false);
            return new Liberador(sem, externo);
        }
        catch
        {
            sem.Release();
            throw;
        }
    }

    private sealed class Liberador : IAsyncDisposable
    {
        private readonly SemaphoreSlim _sem;
        private readonly IAsyncDisposable? _externo;
        public Liberador(SemaphoreSlim sem, IAsyncDisposable? externo) { _sem = sem; _externo = externo; }
        public async ValueTask DisposeAsync()
        {
            try { if (_externo != null) await _externo.DisposeAsync().ConfigureAwait(false); }
            finally { _sem.Release(); }
        }
    }

    private async Task<ResultadoEmision> EnviarAsync(RegistroEmision reg, ComprobanteDetalle detalle, bool permitirRenumerar)
    {
        var cbte = Numerar(detalle, reg.CbteNro);
        RespuestaCae resp;
        try
        {
            resp = await _wsfe.FECAESolicitarAsync(new SolicitudCae
                { PtoVta = reg.PtoVta, CbteTipo = reg.CbteTipo, Comprobantes = { cbte } }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ArcaException ex) when (ex is ArcaComunicacionException or SoapFaultException)
        {
            _log.LogWarning(ex, "Resultado incierto en FECAESolicitar {Pv}-{Tipo}-{Nro}; se reconcilia", reg.PtoVta, reg.CbteTipo, reg.CbteNro);
            var r = await ConsultarAsync(reg.PtoVta, reg.CbteTipo, cbte, conEsperas: true).ConfigureAwait(false);
            if (r != null) return Autorizar(reg, r);
            throw new ArcaComunicacionException(
                $"Resultado no confirmado para {reg.PtoVta}-{reg.CbteTipo}-{reg.CbteNro}. Reintentar con la MISMA Idempotency-Key: " +
                "se verificará y, si no existe, se reenviará el mismo número.", ex);
        }

        // ARCA informa 10016 como observación del detalle (Resultado R); algunas versiones lo devuelven en Errores.
        var mensajes10016 = resp.Errores.Concat(resp.Detalles.SelectMany(d => d.Observaciones ?? new List<ArcaMensaje>())).ToList();
        if (mensajes10016.Any(e => e.Code == ErrorNumeroNoCorrelativo))
        {
            // ¿Lo autorizó un intento anterior nuestro? Si no (inexistente o con datos de otro emisor), otro emisor tomó el número.
            var r = await ConsultarAsync(reg.PtoVta, reg.CbteTipo, cbte, conEsperas: false, distintoEsAjeno: true).ConfigureAwait(false);
            if (r != null) return Autorizar(reg, r);
            if (permitirRenumerar)
            {
                var ultimo = await ProximoNumeroBaseAsync(reg.PtoVta, reg.CbteTipo, reg.Clave).ConfigureAwait(false);
                _log.LogWarning("Número {Nro} no correlativo; otro emisor usa {Pv}-{Tipo}. Se renumera a {Nuevo}", reg.CbteNro, reg.PtoVta, reg.CbteTipo, ultimo + 1);
                reg.CbteNro = ultimo + 1;
                _registro.Guardar(reg);
                return await EnviarAsync(reg, detalle, permitirRenumerar: false).ConfigureAwait(false);
            }
            throw new ArcaException($"El número {reg.CbteNro} no es correlativo y no figura autorizado: otro sistema emite en el punto de venta " +
                                    $"{reg.PtoVta}. Revisar manualmente.", mensajes10016);
        }

        var det = resp.Detalles.FirstOrDefault();
        var res = new ResultadoEmision
        {
            PtoVta = reg.PtoVta, CbteTipo = reg.CbteTipo, CbteNro = reg.CbteNro,
            Resultado = det?.Resultado ?? resp.Cabecera?.Resultado ?? "R",
            Cae = string.IsNullOrWhiteSpace(det?.Cae) ? null : det!.Cae,
            CaeFchVto = string.IsNullOrWhiteSpace(det?.CaeFchVto) ? null : det!.CaeFchVto,
            CbteFch = det?.CbteFch ?? cbte.CbteFch,
            Observaciones = det?.Observaciones ?? new(),
            Errores = resp.Errores, Eventos = resp.Eventos
        };
        res.Ayuda = CatalogoErroresArca.AyudasPara(res.Errores.Concat(res.Observaciones));
        res.Referencia = detalle.ReferenciaExterna;
        Arca.Facturacion.Core.Resiliencia.ArcaTelemetria.Emisiones.Add(1, new KeyValuePair<string, object?>("resultado", res.Resultado));
        if (res.Aprobado && res.Cae != null)
        {
            res.QrUrl = Qr(cbte, res);
            reg.Estado = EstadoEmision.Autorizado;
            reg.Resultado = res;
            _registro.Guardar(reg);
            Notificar(res);
        }
        else
        {
            // Rechazado: ARCA no consumió el número. Se libera la clave para permitir reintentar (mismo contenido).
            _registro.Eliminar(reg.Clave);
            Rechazos?.Registrar(reg.Cliente ?? "", res, _reloj.GetUtcNow());
            Notificar(res);
        }
        Logs.ComprobanteEmitido(_log, reg.PtoVta, reg.CbteTipo, reg.CbteNro, res.Resultado, res.Cae);
        return res;
    }

    /// <summary>Busca el número en ARCA. Devuelve el consultado si existe y coincide; null si no existe;
    /// lanza si existe con otros datos o si no se pudo consultar.</summary>
    /// <param name="distintoEsAjeno">true: si existe con otros datos se devuelve null (es de otro emisor) en lugar de lanzar.</param>
    private async Task<ComprobanteConsultado?> ConsultarAsync(int pv, int tipo, ComprobanteNumerado enviado, bool conEsperas, bool distintoEsAjeno = false)
    {
        var esperas = conEsperas ? _esperas : new[] { TimeSpan.Zero };
        Exception? ultimoError = null;
        foreach (var espera in esperas)
        {
            if (espera > TimeSpan.Zero) await Task.Delay(espera, _reloj).ConfigureAwait(false);
            ComprobanteConsultado? c;
            try { c = await _wsfe.FECompConsultarAsync(pv, tipo, enviado.CbteDesde, CancellationToken.None).ConfigureAwait(false); }
            catch (ArcaException ex) { ultimoError = ex; continue; }
            ultimoError = null;
            if (c == null) continue;
            if (!Coincide(c, enviado))
            {
                if (distintoEsAjeno) return null;
                throw new ArcaException(
                    $"El número {pv}-{tipo}-{enviado.CbteDesde} figura autorizado con datos distintos a los enviados. " +
                    "Otro sistema emitió en el mismo punto de venta. Revisar manualmente.");
            }
            return c;
        }
        if (ultimoError != null)
            throw new ArcaComunicacionException(
                $"Estado DESCONOCIDO del comprobante {pv}-{tipo}-{enviado.CbteDesde}: no se pudo confirmar con FECompConsultar. " +
                "Reintentar con la MISMA Idempotency-Key.", ultimoError);
        return null;
    }

    internal static bool Coincide(ComprobanteConsultado c, ComprobanteNumerado e)
    {
        if (c.Concepto != 0 && c.Concepto != e.Concepto) return false;
        if (c.DocTipo != e.DocTipo || c.DocNro != e.DocNro) return false;
        if (c.ImpTotal != e.ImpTotal || c.ImpNeto != e.ImpNeto || c.ImpIVA != e.ImpIVA || c.ImpTrib != e.ImpTrib
            || c.ImpOpEx != e.ImpOpEx || c.ImpTotConc != e.ImpTotConc) return false;
        if (!string.IsNullOrEmpty(e.CbteFch) && c.CbteFch != e.CbteFch) return false;
        if (!string.IsNullOrEmpty(c.MonId) && c.MonId != e.MonId) return false;
        if (c.CondicionIVAReceptorId is int ci && e.CondicionIVAReceptorId is int ei && ci != ei) return false;
        static string Iva(IEnumerable<AlicuotaIva>? l) => string.Join(";", (l ?? Enumerable.Empty<AlicuotaIva>())
            .OrderBy(a => a.Id).Select(a => $"{a.Id}:{a.BaseImp:0.00}:{a.Importe:0.00}"));
        if (c.Iva != null && Iva(c.Iva) != Iva(e.Iva)) return false;
        return true;
    }

    private ResultadoEmision Autorizar(RegistroEmision reg, ComprobanteConsultado c)
    {
        // Un comprobante de ARCA corresponde a UNA sola clave: si otro registro ya lo tiene asignado, no se duplica el CAE.
        if (_registro.Listar().Any(o => o.Clave != reg.Clave && o.Estado == EstadoEmision.Autorizado && o.PtoVta == reg.PtoVta
                                        && o.CbteTipo == reg.CbteTipo && o.CbteNro == reg.CbteNro))
            throw new ArcaException(
                $"El comprobante {reg.PtoVta}-{reg.CbteTipo}-{reg.CbteNro} ya está asignado a otra Idempotency-Key. Revisar manualmente.");
        var res = new ResultadoEmision
        {
            PtoVta = reg.PtoVta, CbteTipo = reg.CbteTipo, CbteNro = reg.CbteNro,
            Resultado = c.Resultado ?? "A", Cae = c.CodAutorizacion, CaeFchVto = c.FchVto, CbteFch = c.CbteFch,
            Observaciones = c.Observaciones, Reconciliado = true
        };
        res.Referencia = reg.Detalle?.ReferenciaExterna;
        if (reg.Detalle != null) res.QrUrl = Qr(Numerar(reg.Detalle, reg.CbteNro), res);
        else res.QrUrl = QrFiscal.GenerarUrl(_op.CuitEmisor, c);
        reg.Estado = EstadoEmision.Autorizado;
        reg.Resultado = res;
        _registro.Guardar(reg);
        Arca.Facturacion.Core.Resiliencia.ArcaTelemetria.Reconciliaciones.Add(1);
        Arca.Facturacion.Core.Resiliencia.ArcaTelemetria.Emisiones.Add(1, new KeyValuePair<string, object?>("resultado", res.Resultado));
        Notificar(res);
        Logs.ComprobanteReconciliado(_log, reg.PtoVta, reg.CbteTipo, reg.CbteNro, res.Cae);
        return res;
    }

    private string? Qr(ComprobanteNumerado c, ResultadoEmision r) => _op.CuitEmisor == 0 ? null :
        QrFiscal.GenerarUrl(_op.CuitEmisor, r.PtoVta, r.CbteTipo, r.CbteNro, r.CbteFch ?? c.CbteFch, c.ImpTotal, c.MonId, c.MonCotiz,
            c.DocTipo, c.DocNro, r.Cae);

    private void Notificar(ResultadoEmision r)
    {
        try { _notif?.Notificar(r); }
        catch (Exception ex) { _log.LogWarning(ex, "No se pudo encolar la notificación de {Nro}", r.CbteNro); }
    }

    /// <summary>Completa datos derivables: fecha del día (hora argentina) y cotización de ARCA para moneda extranjera.</summary>
    private async Task<ComprobanteDetalle> EnriquecerAsync(ComprobanteDetalle d, CancellationToken ct)
    {
        var c = Normalizar(Clonar(d));
        if (_op.CompletarFecha && string.IsNullOrWhiteSpace(c.CbteFch))
            c.CbteFch = _validator.HoyArgentina().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        if (_op.CotizacionAutomatica && !string.IsNullOrWhiteSpace(c.MonId) && c.MonId != "PES" && c.MonCotiz <= 0)
        {
            var cot = await _wsfe.FEParamGetCotizacionAsync(c.MonId, null, ct).ConfigureAwait(false);
            if (cot.MonCotiz <= 0) throw new ValidacionException(new[] { $"ARCA no informó cotización para {c.MonId}." });
            c.MonCotiz = cot.MonCotiz;
        }
        return c;
    }

    private async Task ValidarContraTablasAsync(int ptoVta, ComprobanteDetalle d, CancellationToken ct)
    {
        var errores = new List<string>();
        var pvs = await _wsfe.FEParamGetPtosVentaAsync(ct).ConfigureAwait(false);
        var pv = pvs.FirstOrDefault(p => p.Id == ptoVta.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (pv == null) errores.Add($"El punto de venta {ptoVta} no está habilitado para web services en ARCA.");
        else
        {
            if (pv.Atributos.TryGetValue("Bloqueado", out var b) && b == "S") errores.Add($"El punto de venta {ptoVta} está bloqueado.");
            if (pv.Atributos.TryGetValue("FchBaja", out var f) && !string.IsNullOrWhiteSpace(f) && f != "NULL")
                errores.Add($"El punto de venta {ptoVta} está dado de baja ({f}).");
        }
        if (d.MonId != "PES")
        {
            var monedas = await _wsfe.FEParamGetTiposMonedasAsync(ct).ConfigureAwait(false);
            if (monedas.Count > 0 && monedas.All(m => m.Id != d.MonId)) errores.Add($"La moneda {d.MonId} no existe en ARCA.");
        }
        if (errores.Count > 0) throw new ValidacionException(errores);
    }

    // ------------------------------------------------------------ operación

    /// <summary>Bloqueo opcional de numeración entre procesos (mismo servidor).</summary>
    public IBloqueoNumeracion? Bloqueo { get; set; }

    /// <summary>Registro opcional de rechazos recientes (diagnóstico).</summary>
    public RegistroRechazos? Rechazos { get; set; }

    /// <summary>Resuelve manualmente UNA emisión pendiente: si figura en ARCA la marca autorizada; si no, la libera.
    /// Devuelve el estado final ("autorizado", "liberado", "no-pendiente").</summary>
    public async Task<string> ResolverPendienteAsync(string clave, CancellationToken ct)
    {
        var p = _registro.Obtener(clave);
        if (p is not { Estado: EstadoEmision.Pendiente } || p.Detalle == null) return "no-pendiente";
        await using (await BloquearAsync(p.PtoVta, p.CbteTipo, ct).ConfigureAwait(false))
        {
            p = _registro.Obtener(clave);
            if (p is not { Estado: EstadoEmision.Pendiente } || p.Detalle == null) return "no-pendiente";
            var c = await ConsultarAsync(p.PtoVta, p.CbteTipo, Numerar(p.Detalle, p.CbteNro), conEsperas: false).ConfigureAwait(false);
            if (c != null) { Autorizar(p, c); return "autorizado"; }
            _registro.Eliminar(clave);
            return "liberado";
        }
    }

    public RegistroEmision? ObtenerEmision(string idempotencyKey, string cliente) => _registro.Obtener(ClaveDe(cliente, idempotencyKey));

    public IReadOnlyList<RegistroEmision> Pendientes() => _registro is RegistroEmisionesSqlite sql ? sql.Pendientes() :
        _registro.Listar().Where(r => r.Estado == EstadoEmision.Pendiente).OrderBy(r => r.Creado).ToList();

    public async Task<int> ReconciliarPendientesAsync(TimeSpan antiguedadMinima, CancellationToken ct)
    {
        var limite = _reloj.GetUtcNow() - antiguedadMinima;
        var resueltas = 0;
        foreach (var p in Pendientes().Where(r => r.Creado <= limite))
        {
            ct.ThrowIfCancellationRequested();
            var bloqueo = await BloquearAsync(p.PtoVta, p.CbteTipo, ct).ConfigureAwait(false);
            try
            {
                var actual = _registro.Obtener(p.Clave);
                if (actual is not { Estado: EstadoEmision.Pendiente } || actual.Detalle == null) continue;
                var c = await ConsultarAsync(actual.PtoVta, actual.CbteTipo, Numerar(actual.Detalle, actual.CbteNro), conEsperas: false).ConfigureAwait(false);
                if (c != null) Autorizar(actual, c);
                else
                {
                    // Nunca llegó a ARCA: se libera la clave; un reintento del cliente obtendrá número nuevo.
                    _registro.Eliminar(actual.Clave);
                    _log.LogWarning("Emisión pendiente {Pv}-{Tipo}-{Nro} no figura en ARCA: se libera", actual.PtoVta, actual.CbteTipo, actual.CbteNro);
                }
                resueltas++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "No se pudo reconciliar {Pv}-{Tipo}-{Nro}; requiere revisión", p.PtoVta, p.CbteTipo, p.CbteNro);
            }
            finally { await bloqueo.DisposeAsync().ConfigureAwait(false); }
        }
        return resueltas;
    }

    public int Purgar(TimeSpan retencion)
    {
        if (retencion <= TimeSpan.Zero) return 0;
        var limite = _reloj.GetUtcNow() - retencion;
        var n = 0;
        foreach (var r in _registro.Listar().Where(r => r.Estado == EstadoEmision.Autorizado && r.Creado < limite && r.Detalle != null).ToList())
        {
            // Se conserva una "lápida" liviana (clave, hash, número y CAE) en lugar de borrar: un reintento tardío con la
            // misma Idempotency-Key devuelve el resultado original (o conflicto) y nunca re-emite.
            r.Detalle = null;
            if (r.Resultado is { } res)
                r.Resultado = new ResultadoEmision
                {
                    PtoVta = res.PtoVta, CbteTipo = res.CbteTipo, CbteNro = res.CbteNro, Resultado = res.Resultado, Cae = res.Cae,
                    CaeFchVto = res.CaeFchVto, CbteFch = res.CbteFch, Reconciliado = res.Reconciliado, Referencia = res.Referencia, QrUrl = res.QrUrl
                };
            _registro.Guardar(r);
            n++;
        }
        return n;
    }

    public EstadisticasEmision Estadisticas()
    {
        var hoy = _validator.HoyArgentina().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var e = new EstadisticasEmision();
        foreach (var r in _registro.Listar())
        {
            if (r.Estado == EstadoEmision.Pendiente) { e.Pendientes++; continue; }
            e.Autorizadas++;
            var k = r.CbteTipo.ToString(System.Globalization.CultureInfo.InvariantCulture);
            e.PorTipo[k] = e.PorTipo.GetValueOrDefault(k) + 1;
            if (r.Resultado?.CbteFch == hoy)
            {
                e.AutorizadasHoy++;
                e.ImporteHoy += r.Detalle?.ImpTotal ?? 0;
            }
        }
        return e;
    }

    /// <summary>Normaliza la entrada: recorta espacios y pasa a mayúsculas códigos (MonId, CanMisMonExt, Opcionales).</summary>
    public static ComprobanteDetalle Normalizar(ComprobanteDetalle c)
    {
        static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        c.MonId = (N(c.MonId) ?? "PES").ToUpperInvariant();
        c.CanMisMonExt = N(c.CanMisMonExt)?.ToUpperInvariant();
        c.CbteFch = N(c.CbteFch);
        c.FchServDesde = N(c.FchServDesde);
        c.FchServHasta = N(c.FchServHasta);
        c.FchVtoPago = N(c.FchVtoPago);
        if (c.Opcionales != null)
            foreach (var o in c.Opcionales) { o.Id = o.Id.Trim(); o.Valor = o.Valor.Trim(); }
        if (c.Tributos != null)
            foreach (var t in c.Tributos) t.Desc = N(t.Desc);
        return c;
    }

    public static ComprobanteDetalle Clonar(ComprobanteDetalle d) =>
        JsonSerializer.Deserialize<ComprobanteDetalle>(JsonSerializer.Serialize(d, typeof(ComprobanteDetalle), JsonOpt), JsonOpt)!;

    internal static ComprobanteNumerado Numerar(ComprobanteDetalle d, long nro)
    {
        var c = JsonSerializer.Deserialize<ComprobanteNumerado>(JsonSerializer.Serialize(d, d.GetType(), JsonOpt), JsonOpt)!;
        c.CbteDesde = nro;
        c.CbteHasta = nro;
        return c;
    }

    private static string Hash(int pv, int tipo, ComprobanteDetalle d)
    {
        var json = $"{pv}|{tipo}|" + JsonSerializer.Serialize(d, typeof(ComprobanteDetalle), JsonOpt);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}

/// <summary>Mensajes de log de alto volumen generados en compilación (sin boxing ni parseo de plantillas en caliente).</summary>
internal static partial class Logs
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Comprobante {Pv}-{Tipo}-{Nro}: {Resultado} CAE {Cae}")]
    public static partial void ComprobanteEmitido(ILogger log, int pv, int tipo, long nro, string? resultado, string? cae);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Comprobante {Pv}-{Tipo}-{Nro} reconciliado: CAE {Cae}")]
    public static partial void ComprobanteReconciliado(ILogger log, int pv, int tipo, long nro, string? cae);
}
