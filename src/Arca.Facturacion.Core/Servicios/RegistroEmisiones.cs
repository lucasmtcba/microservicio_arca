using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arca.Facturacion.Core.Wsfe;

namespace Arca.Facturacion.Core.Servicios;

public enum EstadoEmision
{
    /// <summary>Número reservado y enviado a ARCA; resultado aún no confirmado.</summary>
    Pendiente,
    /// <summary>CAE obtenido.</summary>
    Autorizado
}

/// <summary>Registro durable de una emisión, asociado a su clave de idempotencia.
/// Se escribe ANTES de llamar a FECAESolicitar para que un reintento reutilice el mismo número.</summary>
public sealed class RegistroEmision
{
    /// <summary>Versión del esquema del registro, para migraciones futuras.</summary>
    public int Version { get; set; } = RegistroEmisionesArchivo.VersionEsquema;
    public string Clave { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public int PtoVta { get; set; }
    public int CbteTipo { get; set; }
    public long CbteNro { get; set; }
    public EstadoEmision Estado { get; set; }
    public ResultadoEmision? Resultado { get; set; }
    public DateTimeOffset Creado { get; set; }
    /// <summary>Detalle efectivamente enviado (tras completar fecha/cotización). Garantiza reenvíos idénticos.</summary>
    public ComprobanteDetalle? Detalle { get; set; }
    /// <summary>Clave de idempotencia informada por el cliente (sin el prefijo de cliente).</summary>
    public string? IdempotencyKey { get; set; }
    public string? Cliente { get; set; }
}

public interface IRegistroEmisiones
{
    RegistroEmision? Obtener(string clave);
    void Guardar(RegistroEmision r);
    void Eliminar(string clave);
    IEnumerable<RegistroEmision> Listar();
}

/// <summary>Registro en memoria (sólo para pruebas o despliegues sin disco). Se pierde al reiniciar.</summary>
public sealed class RegistroEmisionesMemoria : IRegistroEmisiones
{
    private readonly ConcurrentDictionary<string, RegistroEmision> _d = new();
    private readonly int _max;
    public RegistroEmisionesMemoria(int max = 100_000) => _max = max;

    public RegistroEmision? Obtener(string clave) => _d.TryGetValue(clave, out var r) ? r : null;
    public void Eliminar(string clave) => _d.TryRemove(clave, out _);
    public IEnumerable<RegistroEmision> Listar() => _d.Values.ToList();

    public void Guardar(RegistroEmision r)
    {
        if (_d.Count >= _max && !_d.ContainsKey(r.Clave))
        {
            // Descarta los autorizados más antiguos; nunca los pendientes (son los que evitan duplicados).
            foreach (var viejo in _d.Values.Where(x => x.Estado == EstadoEmision.Autorizado)
                         .OrderBy(x => x.Creado).Take(Math.Max(1, _max / 10)).ToList())
                _d.TryRemove(viejo.Clave, out _);
        }
        _d[r.Clave] = r;
    }
}

/// <summary>Registro durable en disco: un JSON por clave (nombre = SHA-256 de la clave), escritura atómica,
/// permisos 0600 en Unix e índice en memoria cargado al iniciar (lecturas y listados sin tocar el disco).
/// Supone un único proceso escribiendo el directorio (ver limitación de instancia emisora única).</summary>
public sealed class RegistroEmisionesArchivo : IRegistroEmisiones
{
    public const int VersionEsquema = 2;
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = false };
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, RegistroEmision> _indice = new();

    private readonly CifradorRegistro? _cif;

    public RegistroEmisionesArchivo(string dir, CifradorRegistro? cifrador = null)
    {
        _dir = dir;
        _cif = cifrador;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var f in Directory.EnumerateFiles(_dir, "*.tmp")) TryDelete(f); // restos de escrituras interrumpidas
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var txt = File.ReadAllText(f);
                var r = JsonSerializer.Deserialize(_cif?.Descifrar(txt) ?? txt, RegistroJsonContext.Default.RegistroEmision);
                if (r != null && !string.IsNullOrEmpty(r.Clave)) _indice[r.Clave] = r;
            }
            catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException
                                       or FormatException or UnauthorizedAccessException) { Ilegibles++; }
        }
    }

    public int Cantidad => _indice.Count;

    /// <summary>Archivos que no se pudieron leer al iniciar (se omiten; revisar clave de cifrado o daños).</summary>
    public int Ilegibles { get; private set; }

    private static void TryDelete(string f) { try { File.Delete(f); } catch (IOException) { } }

    private string Ruta(string clave) =>
        Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clave))) + ".json");

    public RegistroEmision? Obtener(string clave) => _indice.TryGetValue(clave, out var r) ? r : null;

    public void Eliminar(string clave)
    {
        var p = Ruta(clave);
        if (File.Exists(p)) File.Delete(p);
        _indice.TryRemove(clave, out _);
    }

    public IEnumerable<RegistroEmision> Listar() => _indice.Values.ToList();

    public void Guardar(RegistroEmision r)
    {
        r.Version = VersionEsquema;
        var p = Ruta(r.Clave);
        var tmp = p + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var fso = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) fso.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, fso))
        {
            var json = JsonSerializer.Serialize(r, RegistroJsonContext.Default.RegistroEmision);
            var bytes = Encoding.UTF8.GetBytes(_cif?.Cifrar(json) ?? json);
            fs.Write(bytes);
        }
        File.Move(tmp, p, overwrite: true);
        _indice[r.Clave] = r;
    }
}

/// <summary>Serialización del registro generada en compilación (sin reflexión en caliente).</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(RegistroEmision))]
internal sealed partial class RegistroJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
