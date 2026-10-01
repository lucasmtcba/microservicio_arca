using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.Extensions.Logging;

namespace Arca.Facturacion.Core.Servicios;

/// <summary>Repositorio JSON genérico por (cliente, id): un archivo por entidad, escritura atómica; o en memoria.</summary>
public sealed class RepositorioJson<T> where T : class
{
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };
    private readonly string? _dir;
    private readonly ConcurrentDictionary<string, T> _mem = new();
    private readonly object _lock = new();

    public RepositorioJson(string? dir)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? null : dir;
        if (_dir != null) Directory.CreateDirectory(_dir);
    }

    private static bool IdValido(string? id) => RepositorioPlantillas.EsIdValido(id);

    /// <summary>Prefijo por cliente: hash del nombre (evita colisiones como "acme-1" / "acme1" entre clientes).</summary>
    internal static string PrefijoCliente(string cliente) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cliente)), 0, 8);

    private static string Clave(string cliente, string id) => PrefijoCliente(cliente) + "__" + id;

    /// <summary>Archivos ilegibles encontrados en el último listado (se omiten; no frenan al resto).</summary>
    public int UltimosCorruptos { get; private set; }

    public IReadOnlyList<T> Listar(string? cliente)
    {
        var pref = cliente == null ? "" : Clave(cliente, "");
        if (_dir == null) return _mem.Where(k => k.Key.StartsWith(pref, StringComparison.Ordinal)).Select(k => k.Value).ToList();
        lock (_lock)
        {
            var l = new List<T>();
            var corruptos = 0;
            foreach (var f in Directory.EnumerateFiles(_dir, pref + "*.json"))
            {
                try { if (JsonSerializer.Deserialize<T>(File.ReadAllText(f), Opt) is { } v) l.Add(v); }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { corruptos++; }
            }
            UltimosCorruptos = corruptos;
            return l;
        }
    }

    /// <summary>Reemplazo atómico con reintentos (en Windows el antivirus o un editor pueden retener el archivo un instante).</summary>
    private static void Reemplazar(string tmp, string destino)
    {
        for (var i = 0; ; i++)
        {
            try { File.Move(tmp, destino, overwrite: true); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < 4) { Thread.Sleep(50 * (i + 1)); }
        }
    }

    public T? Obtener(string cliente, string id)
    {
        if (!IdValido(id)) return null;
        if (_dir == null) return _mem.TryGetValue(Clave(cliente, id), out var v) ? v : null;
        var p = Path.Combine(_dir, Clave(cliente, id) + ".json");
        lock (_lock)
        {
            if (!File.Exists(p)) return null;
            // Un archivo dañado se trata como inexistente en lugar de propagar una excepción de deserialización.
            try { return JsonSerializer.Deserialize<T>(File.ReadAllText(p), Opt); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
        }
    }

    public void Guardar(string cliente, string id, T valor)
    {
        if (!IdValido(id)) throw new ValidacionException(new[] { "Id inválido (letras, dígitos, - y _; máx. 64)." });
        if (_dir == null) { _mem[Clave(cliente, id)] = valor; return; }
        var p = Path.Combine(_dir, Clave(cliente, id) + ".json");
        lock (_lock)
        {
            File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(valor, Opt));
            Reemplazar(p + ".tmp", p);
        }
    }

    public bool Eliminar(string cliente, string id)
    {
        if (!IdValido(id)) return false;
        if (_dir == null) return _mem.TryRemove(Clave(cliente, id), out _);
        var p = Path.Combine(_dir, Clave(cliente, id) + ".json");
        lock (_lock)
        {
            if (!File.Exists(p)) return false;
            File.Delete(p);
            return true;
        }
    }
}

// ============================================================ Agenda de receptores

/// <summary>Receptor frecuente (agenda de clientes del emisor).</summary>
public sealed class Receptor
{
    public string Id { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public int DocTipo { get; set; }
    public long DocNro { get; set; }
    public int CondicionIVAReceptorId { get; set; }
    public string? Email { get; set; }
    public string? Domicilio { get; set; }
}

public sealed class AgendaReceptores
{
    private readonly RepositorioJson<Receptor> _repo;
    public AgendaReceptores(string? dir) => _repo = new RepositorioJson<Receptor>(dir);

    public IReadOnlyList<Receptor> Buscar(string cliente, string? texto) =>
        _repo.Listar(cliente).Where(r => string.IsNullOrWhiteSpace(texto)
                || r.Nombre.Contains(texto, StringComparison.OrdinalIgnoreCase)
                || r.DocNro.ToString(CultureInfo.InvariantCulture).Contains(texto, StringComparison.Ordinal)
                || r.Id.Contains(texto, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Nombre, StringComparer.OrdinalIgnoreCase).ToList();

    public Receptor? Obtener(string cliente, string id) => _repo.Obtener(cliente, id);

    public Receptor Guardar(string cliente, Receptor r)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(r.Nombre)) e.Add("Nombre obligatorio.");
        if (r.DocTipo is 80 or 86 && !Cuit.EsValida(r.DocNro)) e.Add("CUIT/CUIL inválida.");
        if (!ComprobanteValidator.CondicionesIva.Contains(r.CondicionIVAReceptorId)) e.Add("CondicionIVAReceptorId inexistente.");
        if (r.CondicionIVAReceptorId == 1 && r.DocTipo != 80) e.Add("Un Responsable Inscripto debe tener CUIT.");
        if (e.Count > 0) throw new ValidacionException(e);
        _repo.Guardar(cliente, r.Id, r);
        return r;
    }

    public bool Eliminar(string cliente, string id) => _repo.Eliminar(cliente, id);

    /// <summary>Aplica los datos del receptor al comprobante.</summary>
    public static void Aplicar(Receptor r, ComprobanteDetalle d)
    {
        d.DocTipo = r.DocTipo;
        d.DocNro = r.DocNro;
        d.CondicionIVAReceptorId = r.CondicionIVAReceptorId;
    }
}

// ============================================================ Suscripciones (facturación recurrente)

public enum Periodicidad { Semanal, Quincenal, Mensual }

public sealed class EjecucionSuscripcion
{
    public DateOnly Fecha { get; set; }
    public string? Resultado { get; set; }
    public long? CbteNro { get; set; }
    public string? Cae { get; set; }
    public string? Error { get; set; }
}

/// <summary>Facturación recurrente: emite automáticamente el comprobante en cada período.</summary>
public sealed class Suscripcion
{
    public string Id { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public string? Descripcion { get; set; }
    public int PtoVta { get; set; }
    public int CbteTipo { get; set; }
    public ComprobanteDetalle Comprobante { get; set; } = new();
    /// <summary>Mensual: mismo día de cada mes (ancla). Quincenal: días 1 y 16. Semanal: cada 7 días.</summary>
    public Periodicidad Periodicidad { get; set; } = Periodicidad.Mensual;
    public DateOnly ProximaEjecucion { get; set; }
    /// <summary>Día ancla para la periodicidad mensual (se toma de la primera ProximaEjecucion). Evita el corrimiento 31→28→28.</summary>
    public int? DiaAncla { get; set; }
    /// <summary>Fecha final opcional (inclusive).</summary>
    public DateOnly? Hasta { get; set; }
    public bool Activa { get; set; } = true;
    /// <summary>Si no es null, la suscripción está retenida y requiere acción (p.ej. confirmar períodos atrasados).</summary>
    public string? Retenida { get; set; }
    /// <summary>Los períodos atrasados hasta esta fecha (de período) fueron confirmados para facturarse.</summary>
    public DateOnly? AtrasosConfirmadosHasta { get; set; }
    /// <summary>Período en curso y su detalle congelado: los reintentos reenvían exactamente lo mismo (idempotencia determinista).</summary>
    public DateOnly? PeriodoEnCurso { get; set; }
    public ComprobanteDetalle? DetalleEnCurso { get; set; }
    public List<EjecucionSuscripcion> Historial { get; set; } = new();
}

public sealed class SuscripcionesService : IDisposable
{
    private readonly RepositorioJson<Suscripcion> _repo;
    private readonly IFacturacionService _fact;
    private readonly ComprobanteValidator _val;
    private readonly ILogger<SuscripcionesService> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    public const int MaxHistorial = 120;

    /// <summary>Máximo de suscripciones por cliente.</summary>
    public int MaxPorCliente { get; init; } = 500;
    /// <summary>Períodos atrasados tolerados antes de retener la suscripción y pedir confirmación.</summary>
    public int MaxPeriodosAtrasados { get; init; } = 1;
    /// <summary>Control previo a emitir (mantenimiento, cuota): devuelve un motivo para omitir, o null para seguir.</summary>
    public Func<string, string?>? ControlEmision { get; set; }
    /// <summary>Notificación tras una emisión aprobada (p.ej. sumar a la cuota diaria).</summary>
    public Action<string>? AlEmitir { get; set; }

    public void Dispose() => _lock.Dispose();

    public SuscripcionesService(string? dir, IFacturacionService fact, ComprobanteValidator val, ILogger<SuscripcionesService> log)
    {
        _repo = new RepositorioJson<Suscripcion>(dir);
        _fact = fact; _val = val; _log = log;
    }

    public IReadOnlyList<Suscripcion> Listar(string cliente) =>
        _repo.Listar(cliente).Where(s => s.Cliente == cliente).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();

    public Suscripcion? Obtener(string cliente, string id) => _repo.Obtener(cliente, id) is { } s && s.Cliente == cliente ? s : null;

    public bool Eliminar(string cliente, string id)
    {
        _lock.Wait();
        try { return Obtener(cliente, id) != null && _repo.Eliminar(cliente, id); }
        finally { _lock.Release(); }
    }

    public Suscripcion Guardar(string cliente, Suscripcion s)
    {
        _lock.Wait();
        try
        {
            var hoy = _val.HoyArgentina();
            var previa = Obtener(cliente, s.Id);
            if (previa == null && Listar(cliente).Count >= MaxPorCliente)
                throw new ValidacionException(new[] { $"Se alcanzó el máximo de {MaxPorCliente} suscripciones por cliente." });
            s.Cliente = cliente;
            if (s.ProximaEjecucion == default) s.ProximaEjecucion = hoy;
            if (s.ProximaEjecucion < Anterior(hoy, s.Periodicidad, s.ProximaEjecucion.Day))
                throw new ValidacionException(new[] { "ProximaEjecucion no puede ser anterior a un período atrás: los atrasos se facturan sólo con confirmación." });
            s.DiaAncla ??= s.ProximaEjecucion.Day;
            if (previa != null)
            {
                s.Historial = previa.Historial;
                // Un período ya en curso conserva su detalle congelado.
                s.PeriodoEnCurso = previa.PeriodoEnCurso;
                s.DetalleEnCurso = previa.DetalleEnCurso;
                s.AtrasosConfirmadosHasta = previa.AtrasosConfirmadosHasta;
            }
            var prueba = Preparar(s, hoy, hoy);
            var errores = _val.Validar(s.PtoVta, s.CbteTipo, prueba);
            if (errores.Count > 0) throw new ValidacionException(errores);
            _repo.Guardar(cliente, s.Id, s);
            return s;
        }
        finally { _lock.Release(); }
    }

    private Suscripcion? Modificar(string cliente, string id, Action<Suscripcion> cambio)
    {
        _lock.Wait();
        try
        {
            var s = Obtener(cliente, id);
            if (s == null) return null;
            cambio(s);
            _repo.Guardar(cliente, id, s);
            return s;
        }
        finally { _lock.Release(); }
    }

    public Suscripcion? CambiarEstado(string cliente, string id, bool activa) => Modificar(cliente, id, s => s.Activa = activa);

    /// <summary>Libera una suscripción retenida por atraso: se facturan los períodos pendientes, uno por ejecución.</summary>
    public Suscripcion? ConfirmarAtrasos(string cliente, string id) => Modificar(cliente, id, s =>
    {
        s.Retenida = null;
        s.AtrasosConfirmadosHasta = _val.HoyArgentina();
    });

    private static DateOnly ConDia(int anio, int mes, int dia) => new(anio, mes, Math.Min(dia, DateTime.DaysInMonth(anio, mes)));

    public static DateOnly Siguiente(DateOnly f, Periodicidad p, int? ancla = null) => p switch
    {
        Periodicidad.Semanal => f.AddDays(7),
        Periodicidad.Quincenal => f.Day < 16 ? new DateOnly(f.Year, f.Month, 16) : new DateOnly(f.Year, f.Month, 1).AddMonths(1),
        _ => ConDia(f.AddMonths(1).Year, f.AddMonths(1).Month, ancla ?? f.Day)
    };

    public static DateOnly Anterior(DateOnly f, Periodicidad p, int? ancla = null) => p switch
    {
        Periodicidad.Semanal => f.AddDays(-7),
        Periodicidad.Quincenal => f.Day >= 16 ? new DateOnly(f.Year, f.Month, 1) : new DateOnly(f.Year, f.Month, 16).AddMonths(-1),
        _ => ConDia(f.AddMonths(-1).Year, f.AddMonths(-1).Month, ancla ?? f.Day)
    };

    /// <summary>Arma el comprobante del período <paramref name="periodo"/>: el servicio facturado SIEMPRE es el del período
    /// (nunca se corre con la fecha de hoy). La fecha del comprobante es la del período si cae dentro de la ventana
    /// admitida por ARCA; si no, la de hoy.</summary>
    internal static ComprobanteDetalle Preparar(Suscripcion s, DateOnly periodo, DateOnly hoy)
    {
        static string F(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var d = FacturacionService.Clonar(s.Comprobante);
        var ventana = d.Concepto == 1 ? 5 : 10;
        var fecha = Math.Abs(periodo.DayNumber - hoy.DayNumber) <= ventana ? periodo : hoy;
        d.CbteFch = F(fecha);
        if (d.Concepto is 2 or 3)
        {
            d.FchServDesde = F(Anterior(periodo, s.Periodicidad, s.DiaAncla));
            d.FchServHasta = F(periodo.AddDays(-1));
            d.FchVtoPago = F(fecha.AddDays(10));
        }
        d.ReferenciaExterna ??= $"suscripcion:{s.Id}:{F(periodo)}";
        return d;
    }

    /// <summary>Emite, a lo sumo, UN período por suscripción vencida. Idempotente (clave = suscripción + período) y
    /// determinista (el detalle del período se congela antes del primer intento). Serializado: no corre en paralelo
    /// consigo mismo ni con altas/bajas/cambios.</summary>
    public async Task<int> EjecutarVencidasAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var hoy = _val.HoyArgentina();
            var emitidas = 0;
            foreach (var s in _repo.Listar(null).Where(s => s.Activa && s.Retenida == null && s.ProximaEjecucion <= hoy))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (await ProcesarAsync(s, hoy, ct).ConfigureAwait(false)) emitidas++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Suscripción {Id}: error inesperado; se continúa con las demás", s.Id);
                }
            }
            if (_repo.UltimosCorruptos > 0) _log.LogError("Suscripciones: {N} archivos ilegibles fueron omitidos", _repo.UltimosCorruptos);
            return emitidas;
        }
        finally { _lock.Release(); }
    }

    private async Task<bool> ProcesarAsync(Suscripcion s, DateOnly hoy, CancellationToken ct)
    {
        if (s.Hasta is { } h && s.ProximaEjecucion > h) { s.Activa = false; _repo.Guardar(s.Cliente, s.Id, s); return false; }

        // Atraso: más de N períodos sin facturar => se retiene para confirmación manual (evita ráfagas de facturas).
        var atrasados = 0;
        for (var f = s.ProximaEjecucion; f <= hoy && atrasados <= MaxPeriodosAtrasados + 1; f = Siguiente(f, s.Periodicidad, s.DiaAncla)) atrasados++;
        if (atrasados > MaxPeriodosAtrasados + 1 && s.PeriodoEnCurso == null
            && !(s.AtrasosConfirmadosHasta is { } conf && s.ProximaEjecucion <= conf))
        {
            s.Retenida = $"Hay {atrasados - 1}+ períodos atrasados desde {s.ProximaEjecucion:yyyy-MM-dd}. Confirmar con POST /api/v1/suscripciones/{s.Id}/confirmar-atrasos.";
            _repo.Guardar(s.Cliente, s.Id, s);
            _log.LogWarning("Suscripción {Id} retenida por atraso", s.Id);
            return false;
        }

        if (ControlEmision?.Invoke(s.Cliente) is { } motivo)
        {
            _log.LogInformation("Suscripción {Id} omitida: {Motivo}", s.Id, motivo);
            return false;
        }

        var periodo = s.ProximaEjecucion;
        if (s.PeriodoEnCurso != periodo || s.DetalleEnCurso == null)
        {
            s.PeriodoEnCurso = periodo;
            s.DetalleEnCurso = Preparar(s, periodo, hoy);
            _repo.Guardar(s.Cliente, s.Id, s); // congelado ANTES de emitir
        }
        var clave = $"sus-{s.Id}-{periodo.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
        var ej = new EjecucionSuscripcion { Fecha = periodo };
        var ok = false;
        try
        {
            var r = await _fact.EmitirAsync(s.PtoVta, s.CbteTipo, s.DetalleEnCurso, clave, s.Cliente, ct).ConfigureAwait(false);
            ej.Resultado = r.Resultado; ej.CbteNro = r.CbteNro; ej.Cae = r.Cae;
            if (r.Aprobado)
            {
                ok = !r.Repetido;
                Avanzar(s);
                if (ok) AlEmitir?.Invoke(s.Cliente);
            }
            else
            {
                ej.Error = string.Join("; ", r.Errores.Concat(r.Observaciones).Select(m => $"[{m.Code}] {m.Msg}"));
                s.PeriodoEnCurso = null; s.DetalleEnCurso = null; // rechazado: se rearma (p.ej. si se corrigió la plantilla)
            }
        }
        catch (IdempotenciaConflictoException)
        {
            if (_fact.ObtenerEmision(clave, s.Cliente) is { Estado: EstadoEmision.Autorizado, Resultado: { } pr })
            {
                ej.Resultado = pr.Resultado; ej.CbteNro = pr.CbteNro; ej.Cae = pr.Cae;
                Avanzar(s);
            }
            else
            {
                var res = await _fact.ResolverPendienteAsync(s.Cliente + ":" + clave, ct).ConfigureAwait(false);
                ej.Resultado = "pendiente";
                ej.Error = $"Emisión del período en conflicto; resolución: {res}.";
            }
        }
        catch (Exception ex) when (ex is ArcaException or ValidacionException)
        {
            ej.Resultado = "error";
            ej.Error = ex.Message;
            _log.LogWarning(ex, "Suscripción {Id}: no se pudo emitir el período {Fecha}", s.Id, periodo);
        }
        s.Historial.Add(ej);
        if (s.Historial.Count > MaxHistorial) s.Historial.RemoveRange(0, s.Historial.Count - MaxHistorial);
        _repo.Guardar(s.Cliente, s.Id, s);
        return ok;
    }

    private static void Avanzar(Suscripcion s)
    {
        s.ProximaEjecucion = Siguiente(s.ProximaEjecucion, s.Periodicidad, s.DiaAncla);
        s.PeriodoEnCurso = null;
        s.DetalleEnCurso = null;
    }
}
