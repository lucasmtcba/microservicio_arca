using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsfe;

namespace Arca.Facturacion.Core.Servicios;

// ============================================================ Plantillas

/// <summary>Plantilla de comprobante reutilizable (receptor, concepto, moneda, condición IVA, opcionales, etc.).</summary>
public sealed class PlantillaComprobante
{
    /// <summary>Identificador (letras, dígitos, guion y guion bajo; máx. 64).</summary>
    public string Id { get; set; } = string.Empty;
    public string? Descripcion { get; set; }
    public int PtoVta { get; set; }
    public int CbteTipo { get; set; }
    public ComprobanteDetalle Comprobante { get; set; } = new();
    public DateTimeOffset Actualizada { get; set; }
}

public interface IRepositorioPlantillas
{
    IReadOnlyList<PlantillaComprobante> Listar(string cliente);
    PlantillaComprobante? Obtener(string cliente, string id);
    void Guardar(string cliente, PlantillaComprobante p);
    bool Eliminar(string cliente, string id);
}

/// <summary>Plantillas aisladas por cliente; en disco (un JSON por plantilla) o en memoria.</summary>
public sealed partial class RepositorioPlantillas : IRepositorioPlantillas
{
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };
    private readonly string? _dir;
    private readonly ConcurrentDictionary<string, PlantillaComprobante> _mem = new();

    public RepositorioPlantillas(string? dir)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? null : dir;
        if (_dir != null) Directory.CreateDirectory(_dir);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdValido();

    public static bool EsIdValido(string? id) => id != null && IdValido().IsMatch(id);

    private static string Clave(string cliente, string id) => SinSeparadores(cliente) + "__" + id;
    /// <summary>Prefijo por cliente basado en hash (sin colisiones entre "acme-1" y "acme1").</summary>
    private static string SinSeparadores(string s) => RepositorioJson<PlantillaComprobante>.PrefijoCliente(s);

    private string? Ruta(string cliente, string id) => _dir == null ? null : Path.Combine(_dir, Clave(cliente, id) + ".json");

    public IReadOnlyList<PlantillaComprobante> Listar(string cliente)
    {
        var prefijo = SinSeparadores(cliente) + "__";
        if (_dir == null) return _mem.Where(kv => kv.Key.StartsWith(prefijo, StringComparison.Ordinal)).Select(kv => kv.Value).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        var l = new List<PlantillaComprobante>();
        foreach (var f in Directory.EnumerateFiles(_dir, prefijo + "*.json"))
        {
            // Un archivo dañado no impide listar el resto.
            try { if (JsonSerializer.Deserialize<PlantillaComprobante>(File.ReadAllText(f), Opt) is { } p) l.Add(p); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return l.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
    }

    public PlantillaComprobante? Obtener(string cliente, string id)
    {
        if (!EsIdValido(id)) return null;
        if (_dir == null) return _mem.TryGetValue(Clave(cliente, id), out var p) ? p : null;
        var r = Ruta(cliente, id)!;
        return File.Exists(r) ? JsonSerializer.Deserialize<PlantillaComprobante>(File.ReadAllText(r), Opt) : null;
    }

    public void Guardar(string cliente, PlantillaComprobante p)
    {
        if (!EsIdValido(p.Id)) throw new ValidacionException(new[] { "Id de plantilla inválido (letras, dígitos, - y _; máx. 64)." });
        if (_dir == null) { _mem[Clave(cliente, p.Id)] = p; return; }
        var r = Ruta(cliente, p.Id)!;
        var tmp = r + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(p, Opt));
        File.Move(tmp, r, overwrite: true);
    }

    public bool Eliminar(string cliente, string id)
    {
        if (!EsIdValido(id)) return false;
        if (_dir == null) return _mem.TryRemove(Clave(cliente, id), out _);
        var r = Ruta(cliente, id)!;
        if (!File.Exists(r)) return false;
        File.Delete(r);
        return true;
    }
}

// ============================================================ Auditoría

/// <summary>Entrada de auditoría: quién hizo qué, cuándo, desde dónde y con qué resultado.</summary>
public sealed class EntradaAuditoria
{
    public DateTimeOffset Fecha { get; set; }
    public string Cliente { get; set; } = string.Empty;
    public string Operacion { get; set; } = string.Empty;
    public string? Comprobante { get; set; }
    public string? Resultado { get; set; }
    public string? Cae { get; set; }
    public decimal? Importe { get; set; }
    public string? CorrelationId { get; set; }
    public string? Detalle { get; set; }
    /// <summary>IP de origen.</summary>
    public string? Ip { get; set; }
    /// <summary>User-Agent del llamador (recortado).</summary>
    public string? Agente { get; set; }
    /// <summary>Hash encadenado: SHA-256(hash anterior + entrada). Permite detectar alteraciones o borrados.</summary>
    public string? Hash { get; set; }
}

public interface IAuditoria
{
    void Registrar(EntradaAuditoria e);
    IReadOnlyList<EntradaAuditoria> Consultar(DateTimeOffset? desde, string? cliente, int maximo);
}

/// <summary>Resultado de verificar la cadena de hashes de la auditoría.</summary>
public sealed record VerificacionAuditoria(bool Integra, int Entradas, string? PrimerError);

/// <summary>Auditoría append-only en JSONL: un archivo por día (rotación), escritura asíncrona en cola (no bloquea las
/// solicitudes), hash encadenado contra alteraciones y purga por retención.</summary>
public sealed class AuditoriaArchivo : IAuditoria, IDisposable
{
    private readonly string? _base;
    private readonly ConcurrentQueue<EntradaAuditoria> _mem = new();
    private readonly System.Threading.Channels.Channel<EntradaAuditoria> _cola =
        System.Threading.Channels.Channel.CreateBounded<EntradaAuditoria>(new System.Threading.Channels.BoundedChannelOptions(50_000)
            { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Task? _escritor;
    private readonly object _lock = new();
    private string _ultimoHash = "";
    private int _enCola;
    private AnclaAuditoria _ancla = new();

    /// <summary>Estado de la cadena guardado fuera de los archivos de entradas.</summary>
    public sealed class AnclaAuditoria
    {
        public string UltimoHash { get; set; } = "";
        public long Total { get; set; }
        /// <summary>Hash de la última entrada eliminada por la purga (la primera entrada vigente debe encadenar con él).</summary>
        public string HashCorte { get; set; } = "";
        public long PurgadasTotal { get; set; }
    }

    private string RutaAncla => Path.Combine(Path.GetDirectoryName(_base!)!, Path.GetFileNameWithoutExtension(_base) + ".ancla.json");

    private AnclaAuditoria LeerAncla()
    {
        try { if (File.Exists(RutaAncla)) return JsonSerializer.Deserialize<AnclaAuditoria>(File.ReadAllText(RutaAncla)) ?? new(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        // Sin ancla (versión anterior): se reconstruye desde el último archivo, tolerando una última línea truncada.
        var a = new AnclaAuditoria();
        foreach (var f in Archivos())
            foreach (var l in File.ReadLines(f).Where(l => l.Length > 0))
            {
                try { if (JsonSerializer.Deserialize<EntradaAuditoria>(l)?.Hash is { } h) { a.UltimoHash = h; a.Total++; } }
                catch (JsonException) { }
            }
        return a;
    }

    private void GuardarAncla()
    {
        var tmp = RutaAncla + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_ancla));
        File.Move(tmp, RutaAncla, overwrite: true);
    }

    public AuditoriaArchivo(string? ruta)
    {
        _base = string.IsNullOrWhiteSpace(ruta) ? null : Path.GetFullPath(ruta);
        if (_base == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_base)!);
        // Ancla externa de la cadena (último hash, total escrito y hash de corte de la última purga): permite detectar
        // borrados del final o de archivos completos, no sólo alteraciones.
        _ancla = LeerAncla();
        _ultimoHash = _ancla.UltimoHash;
        _escritor = Task.Run(EscribirAsync);
    }

    private string ArchivoDe(DateTimeOffset f) =>
        Path.Combine(Path.GetDirectoryName(_base!)!, Path.GetFileNameWithoutExtension(_base) + "-" +
            f.ToOffset(TimeSpan.FromHours(-3)).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + (Path.GetExtension(_base) is { Length: > 0 } x ? x : ".jsonl"));

    private IEnumerable<string> Archivos() =>
        Directory.EnumerateFiles(Path.GetDirectoryName(_base!)!, Path.GetFileNameWithoutExtension(_base) + "-*").OrderBy(f => f, StringComparer.Ordinal);

    public static string CalcularHash(string anterior, EntradaAuditoria e)
    {
        var copia = JsonSerializer.Serialize(SinHash(e));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(anterior + copia)));
    }

    private static EntradaAuditoria SinHash(EntradaAuditoria e) => new()
    {
        Fecha = e.Fecha, Cliente = e.Cliente, Operacion = e.Operacion, Comprobante = e.Comprobante, Resultado = e.Resultado, Cae = e.Cae,
        Importe = e.Importe, CorrelationId = e.CorrelationId, Detalle = e.Detalle, Ip = e.Ip, Agente = e.Agente
    };

    public void Registrar(EntradaAuditoria e)
    {
        if (_base == null)
        {
            _mem.Enqueue(e);
            while (_mem.Count > 10_000 && _mem.TryDequeue(out _)) { }
            return;
        }
        Interlocked.Increment(ref _enCola);
        if (!_cola.Writer.TryWrite(e)) { Interlocked.Decrement(ref _enCola); EscribirSync(e); }
    }

    private async Task EscribirAsync()
    {
        await foreach (var e in _cola.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { EscribirSync(e); }
            catch (Exception) { /* nunca debe morir el escritor (p.ej. archivo bloqueado en Windows): se sigue con la próxima */ }
            finally { Interlocked.Decrement(ref _enCola); }
        }
    }

    private void EscribirSync(EntradaAuditoria e)
    {
        lock (_lock)
        {
            e.Hash = CalcularHash(_ultimoHash, e);
            // El archivo se elige por el momento de ESCRITURA (orden de la cadena), no por la fecha del evento.
            File.AppendAllText(ArchivoDe(DateTimeOffset.UtcNow), JsonSerializer.Serialize(e) + "\n", Encoding.UTF8);
            _ultimoHash = e.Hash;
            _ancla.UltimoHash = e.Hash;
            _ancla.Total++;
            GuardarAncla();
        }
    }

    /// <summary>Espera a que la cola se vacíe (para lecturas consistentes).</summary>
    public void Vaciar(TimeSpan? max = null)
    {
        var limite = DateTime.UtcNow + (max ?? TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref _enCola) > 0 && DateTime.UtcNow < limite) Thread.Sleep(5);
    }

    private IEnumerable<EntradaAuditoria> Todas()
    {
        if (_base == null) return _mem.ToArray();
        Vaciar();
        lock (_lock)
        {
            var l = new List<EntradaAuditoria>();
            foreach (var linea in Archivos().SelectMany(File.ReadAllLines).Where(x => x.Length > 0))
            {
                try { if (JsonSerializer.Deserialize<EntradaAuditoria>(linea) is { } e) l.Add(e); }
                catch (JsonException) { l.Add(new EntradaAuditoria { Operacion = "(línea ilegible)", Hash = "?" }); }
            }
            return l;
        }
    }

    public IReadOnlyList<EntradaAuditoria> Consultar(DateTimeOffset? desde, string? cliente, int maximo) =>
        Todas().Where(e => (desde == null || e.Fecha >= desde) && (cliente == null || e.Cliente == cliente))
            .OrderByDescending(e => e.Fecha).Take(Math.Clamp(maximo, 1, 1000)).ToList();

    /// <summary>Recalcula la cadena completa: la primera entrada vigente debe encadenar con el hash de corte de la última
    /// purga, y la última con el ancla (detecta alteraciones, inserciones y borrados al inicio, en el medio o al final).</summary>
    public VerificacionAuditoria Verificar()
    {
        if (_base == null) return new VerificacionAuditoria(true, _mem.Count, null);
        var todas = Todas();
        var previo = _ancla.HashCorte;
        var n = 0;
        foreach (var e in todas)
        {
            n++;
            if (CalcularHash(previo, e) != e.Hash) return new VerificacionAuditoria(false, n, $"Entrada {n} ({e.Fecha:o}) alterada, insertada o falta la anterior.");
            previo = e.Hash!;
        }
        if (previo != _ancla.UltimoHash || n + _ancla.PurgadasTotal != _ancla.Total)
            return new VerificacionAuditoria(false, n, $"Faltan entradas: el ancla registra {_ancla.Total} (purgadas {_ancla.PurgadasTotal}) y hay {n}.");
        return new VerificacionAuditoria(true, n, null);
    }

    /// <summary>Elimina archivos de auditoría más antiguos que la retención. Devuelve cuántos borró.</summary>
    public int Purgar(TimeSpan retencion, DateTimeOffset ahora)
    {
        if (_base == null || retencion <= TimeSpan.Zero) return 0;
        var limite = ArchivoDe(ahora - retencion);
        var n = 0;
        lock (_lock)
        {
            foreach (var f in Archivos().Where(f => string.CompareOrdinal(f, limite) < 0).ToList())
            {
                // Antes de borrar se registra en el ancla el hash de la última entrada del archivo (punto de corte de la cadena).
                foreach (var l in File.ReadLines(f).Where(l => l.Length > 0))
                {
                    try { if (JsonSerializer.Deserialize<EntradaAuditoria>(l)?.Hash is { } h) _ancla.HashCorte = h; }
                    catch (JsonException) { }
                    _ancla.PurgadasTotal++;
                }
                File.Delete(f);
                n++;
            }
            if (n > 0) GuardarAncla();
        }
        return n;
    }

    public void Dispose()
    {
        _cola.Writer.TryComplete();
        _escritor?.Wait(TimeSpan.FromSeconds(5));
    }
}

// ============================================================ Consultas y reportes

public sealed class FiltroEmisiones
{
    public int? PtoVta { get; set; }
    public int? CbteTipo { get; set; }
    /// <summary>yyyyMMdd</summary>
    public string? Desde { get; set; }
    /// <summary>yyyyMMdd</summary>
    public string? Hasta { get; set; }
    public long? DocNro { get; set; }
    public EstadoEmision? Estado { get; set; }
    public string? Cliente { get; set; }
    /// <summary>Referencia externa exacta.</summary>
    public string? Referencia { get; set; }
    public int Pagina { get; set; } = 1;
    public int TamanoPagina { get; set; } = 50;
}

public sealed class Pagina<T>
{
    public int Total { get; set; }
    public int NumeroPagina { get; set; }
    public int TamanoPagina { get; set; }
    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
}

public sealed class ResumenIva
{
    public string Desde { get; set; } = string.Empty;
    public string Hasta { get; set; } = string.Empty;
    public int Comprobantes { get; set; }
    public decimal Neto { get; set; }
    public decimal Iva { get; set; }
    public decimal Tributos { get; set; }
    public decimal Total { get; set; }
    /// <summary>Por alícuota (Id de IVA): base imponible e impuesto.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<int, (decimal Base, decimal Impuesto)> PorAlicuota { get; set; } = new();
    public List<ResumenAlicuota> Alicuotas => PorAlicuota.OrderBy(k => k.Key)
        .Select(k => new ResumenAlicuota(k.Key, Catalogos.AlicuotasIva.GetValueOrDefault(k.Key, "?"), k.Value.Base, k.Value.Impuesto)).ToList();
}

public sealed record ResumenAlicuota(int Id, string Alicuota, decimal BaseImponible, decimal Impuesto);

/// <summary>Consultas operativas sobre el registro de emisiones y sobre ARCA.</summary>
public sealed class ConsultasService
{
    private readonly IRegistroEmisiones _reg;
    private readonly IWsfeClient _wsfe;
    public const int MaxRango = 100;

    public ConsultasService(IRegistroEmisiones reg, IWsfeClient wsfe) { _reg = reg; _wsfe = wsfe; }

    private static bool EsNotaCredito(int tipo) => tipo is 3 or 8 or 13 or 53 or 203 or 208 or 213;

    private IEnumerable<RegistroEmision> Autorizadas(string? desde, string? hasta) =>
        _reg.Listar().Where(r => r.Estado == EstadoEmision.Autorizado && r.Resultado?.CbteFch is { } f
            && (desde == null || string.CompareOrdinal(f, desde) >= 0) && (hasta == null || string.CompareOrdinal(f, hasta) <= 0));

    public Pagina<RegistroEmision> Buscar(FiltroEmisiones f)
    {
        var tam = Math.Clamp(f.TamanoPagina, 1, 500);
        var pag = Math.Max(1, f.Pagina);
        var q = _reg.Listar().Where(r =>
            (f.PtoVta == null || r.PtoVta == f.PtoVta) && (f.CbteTipo == null || r.CbteTipo == f.CbteTipo) &&
            (f.Estado == null || r.Estado == f.Estado) && (f.Cliente == null || r.Cliente == f.Cliente) &&
            (f.DocNro == null || r.Detalle?.DocNro == f.DocNro) &&
            (f.Referencia == null || r.Detalle?.ReferenciaExterna == f.Referencia) &&
            (f.Desde == null || string.CompareOrdinal(r.Resultado?.CbteFch ?? r.Detalle?.CbteFch ?? "", f.Desde) >= 0) &&
            (f.Hasta == null || string.CompareOrdinal(r.Resultado?.CbteFch ?? r.Detalle?.CbteFch ?? "", f.Hasta) <= 0))
            .OrderByDescending(r => r.Creado).ToList();
        return new Pagina<RegistroEmision> { Total = q.Count, NumeroPagina = pag, TamanoPagina = tam, Items = q.Skip((pag - 1) * tam).Take(tam).ToList() };
    }

    // Índice (pv, tipo, nro) -> cliente de los comprobantes autorizados. Un comprobante autorizado no cambia de dueño,
    // así que un acierto del índice es definitivo; ante un fallo se reconstruye (como máximo una vez por intervalo).
    private volatile Dictionary<(int, int, long), string?> _duenos = new();
    private long _ultimaReconstruccion;
    private readonly object _lockIndice = new();
    /// <summary>Intervalo mínimo entre reconstrucciones del índice disparadas por números desconocidos.</summary>
    public static readonly TimeSpan IntervaloReconstruccion = TimeSpan.FromSeconds(2);

    private Dictionary<(int, int, long), string?> Reconstruir()
    {
        lock (_lockIndice)
        {
            var ahora = Environment.TickCount64;
            if (ahora - Interlocked.Read(ref _ultimaReconstruccion) < IntervaloReconstruccion.TotalMilliseconds) return _duenos;
            var d = new Dictionary<(int, int, long), string?>();
            foreach (var r in _reg.Listar())
                if (r.Estado == EstadoEmision.Autorizado) d[(r.PtoVta, r.CbteTipo, r.CbteNro)] = r.Cliente;
            _duenos = d;
            Interlocked.Exchange(ref _ultimaReconstruccion, ahora);
            return d;
        }
    }

    /// <summary>Cliente que emitió el comprobante a través del servicio (null si no lo emitió el servicio o no está autorizado).
    /// Búsqueda por índice: no recorre el registro en cada llamada.</summary>
    public string? ClienteDe(int pv, int tipo, long nro)
    {
        var k = (pv, tipo, nro);
        if (_duenos.TryGetValue(k, out var c)) return c;
        // Número desconocido para el índice: puede ser una emisión reciente. Se reconstruye (a lo sumo una vez por intervalo);
        // si la reconstrucción está limitada, se busca directamente en el registro para no dar un falso negativo.
        if (Reconstruir().TryGetValue(k, out c)) return c;
        return _reg.Listar().FirstOrDefault(r => r.PtoVta == pv && r.CbteTipo == tipo && r.CbteNro == nro && r.Estado == EstadoEmision.Autorizado)?.Cliente;
    }

    /// <summary>¿El comprobante fue emitido por este cliente a través del servicio?</summary>
    public bool EsDelCliente(int pv, int tipo, long nro, string cliente) => ClienteDe(pv, tipo, nro) is { } c && c == cliente;

    /// <summary>Totales de IVA por alícuota del período. Las notas de crédito restan.</summary>
    public ResumenIva ResumenIva(string desde, string hasta)
    {
        var r = new ResumenIva { Desde = desde, Hasta = hasta };
        foreach (var e in Autorizadas(desde, hasta))
        {
            var d = e.Detalle;
            if (d == null) continue;
            var signo = EsNotaCredito(e.CbteTipo) ? -1m : 1m;
            var cot = d.MonId == "PES" ? 1m : d.MonCotiz; // en pesos
            r.Comprobantes++;
            r.Neto += signo * d.ImpNeto * cot;
            r.Iva += signo * d.ImpIVA * cot;
            r.Tributos += signo * d.ImpTrib * cot;
            r.Total += signo * d.ImpTotal * cot;
            foreach (var a in d.Iva ?? new())
            {
                var prev = r.PorAlicuota.GetValueOrDefault(a.Id);
                r.PorAlicuota[a.Id] = (prev.Base + signo * a.BaseImp * cot, prev.Impuesto + signo * a.Importe * cot);
            }
        }
        return r;
    }

    /// <summary>CSV (separador ;, decimales con punto, UTF-8 con BOM para Excel) de los comprobantes autorizados del período.</summary>
    public string ExportarCsv(string? desde, string? hasta)
    {
        static string C(string? v) => v == null ? "" : (v.Contains(';') || v.Contains('"') || v.Contains('\n')) ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        static string D(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append('﻿');
        sb.AppendLine("Fecha;Tipo;Descripcion;PtoVta;Numero;DocTipo;DocNro;CondicionIVA;Moneda;Cotizacion;Neto;IVA;Tributos;Exento;NoGravado;Total;CAE;VtoCAE;Cliente");
        foreach (var e in Autorizadas(desde, hasta).OrderBy(e => e.Resultado!.CbteFch).ThenBy(e => e.PtoVta).ThenBy(e => e.CbteTipo).ThenBy(e => e.CbteNro))
        {
            var d = e.Detalle ?? new ComprobanteDetalle();
            var r = e.Resultado!;
            sb.AppendLine(string.Join(';', C(r.CbteFch), e.CbteTipo.ToString(CultureInfo.InvariantCulture),
                C(Catalogos.TiposComprobante.GetValueOrDefault(e.CbteTipo)), e.PtoVta.ToString(CultureInfo.InvariantCulture),
                e.CbteNro.ToString(CultureInfo.InvariantCulture), d.DocTipo.ToString(CultureInfo.InvariantCulture),
                d.DocNro.ToString(CultureInfo.InvariantCulture), d.CondicionIVAReceptorId?.ToString(CultureInfo.InvariantCulture) ?? "",
                C(d.MonId), d.MonCotiz.ToString("0.######", CultureInfo.InvariantCulture),
                D(d.ImpNeto), D(d.ImpIVA), D(d.ImpTrib), D(d.ImpOpEx), D(d.ImpTotConc), D(d.ImpTotal), C(r.Cae), C(r.CaeFchVto), C(e.Cliente)));
        }
        return sb.ToString();
    }

    /// <summary>Consulta en ARCA un rango de números (máx. 100) con concurrencia limitada. Los inexistentes se omiten.</summary>
    public async Task<IReadOnlyList<ComprobanteConsultado>> ConsultarRangoAsync(int pv, int tipo, long desde, long hasta, CancellationToken ct)
    {
        if (desde < 1 || hasta < desde || hasta - desde + 1 > MaxRango)
            throw new ValidacionException(new[] { $"Rango inválido: desde ≥ 1, hasta ≥ desde y como máximo {MaxRango} comprobantes." });
        var res = new ConcurrentBag<ComprobanteConsultado>();
        await Parallel.ForEachAsync(Enumerable.Range(0, (int)(hasta - desde + 1)).Select(i => desde + i),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (n, c) => { if (await _wsfe.FECompConsultarAsync(pv, tipo, n, c).ConfigureAwait(false) is { } x) res.Add(x); }).ConfigureAwait(false);
        return res.OrderBy(x => x.CbteDesde).ToList();
    }

    /// <summary>Números autorizados en ARCA (entre desde y el último) que NO figuran en el registro de este servicio:
    /// indican otro sistema emitiendo en el mismo punto de venta.</summary>
    public async Task<IReadOnlyList<long>> AuditarNumeracionAsync(int pv, int tipo, long desde, CancellationToken ct)
    {
        var ultimo = (await _wsfe.FECompUltimoAutorizadoAsync(pv, tipo, ct).ConfigureAwait(false)).CbteNro;
        if (ultimo < desde) return Array.Empty<long>();
        var hasta = Math.Min(ultimo, desde + MaxRango - 1);
        var propios = _reg.Listar().Where(r => r.PtoVta == pv && r.CbteTipo == tipo).Select(r => r.CbteNro).ToHashSet();
        var ajenos = new List<long>();
        for (var n = desde; n <= hasta; n++) if (!propios.Contains(n)) ajenos.Add(n);
        return ajenos;
    }
}

// ============================================================ CAEA

/// <summary>Período (yyyyMM) y orden (1: días 1-15, 2: 16-fin de mes) de una quincena.</summary>
public readonly record struct QuincenaCaea(int Periodo, short Orden)
{
    public static QuincenaCaea De(DateOnly f) => new(f.Year * 100 + f.Month, (short)(f.Day <= 15 ? 1 : 2));
    public QuincenaCaea Siguiente()
    {
        if (Orden == 1) return this with { Orden = 2 };
        var y = Periodo / 100; var m = Periodo % 100 + 1;
        if (m == 13) { m = 1; y++; }
        return new QuincenaCaea(y * 100 + m, 1);
    }
}

public sealed class CaeaService
{
    private readonly IWsfeClient _wsfe;
    private readonly ComprobanteValidator _val;
    public CaeaService(IWsfeClient wsfe, ComprobanteValidator val) { _wsfe = wsfe; _val = val; }

    public QuincenaCaea Actual() => QuincenaCaea.De(_val.HoyArgentina());

    /// <summary>CAEA de la quincena actual (null si no fue solicitado).</summary>
    public Task<Caea?> VigenteAsync(CancellationToken ct)
    {
        var q = Actual();
        return _wsfe.FECAEAConsultarAsync(q.Periodo, q.Orden, ct);
    }

    /// <summary>Asegura el CAEA de la próxima quincena: lo consulta y, si no existe, lo solicita. ARCA permite pedirlo
    /// dentro de los 5 días corridos anteriores al inicio de la quincena.</summary>
    public async Task<Caea?> AsegurarProximoAsync(CancellationToken ct)
    {
        var hoy = _val.HoyArgentina();
        var prox = Actual().Siguiente();
        var inicio = new DateOnly(prox.Periodo / 100, prox.Periodo % 100, prox.Orden == 1 ? 1 : 16);
        if (inicio.DayNumber - hoy.DayNumber > 5) return null; // todavía no se puede pedir
        return await _wsfe.FECAEAConsultarAsync(prox.Periodo, prox.Orden, ct).ConfigureAwait(false)
               ?? await _wsfe.FECAEASolicitarAsync(prox.Periodo, prox.Orden, ct).ConfigureAwait(false);
    }
}
