using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Arca.Facturacion.Core.Servicios;

/// <summary>Registro de emisiones en SQLite: transaccional, con índices y modo WAL. Seguro entre varios procesos
/// del MISMO servidor que compartan el archivo (combinado con <see cref="BloqueoNumeracionArchivo"/>).
/// Al crearse vacío, migra automáticamente los registros JSON de un directorio previo.</summary>
public sealed class RegistroEmisionesSqlite : IRegistroEmisiones
{
    private readonly string _cs;

    private readonly CifradorRegistro? _cif;

    public RegistroEmisionesSqlite(string archivo, string? migrarDesdeDirectorio = null, CifradorRegistro? cifrador = null)
    {
        _cif = cifrador;
        var dir = Path.GetDirectoryName(Path.GetFullPath(archivo));
        if (dir != null) Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = archivo, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, DefaultTimeout = 30 }.ToString();
        using var c = Abrir();
        Ejecutar(c, """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS emisiones(
              clave TEXT PRIMARY KEY, hash TEXT NOT NULL, pto_vta INTEGER NOT NULL, cbte_tipo INTEGER NOT NULL, cbte_nro INTEGER NOT NULL,
              estado INTEGER NOT NULL, creado TEXT NOT NULL, cbte_fch TEXT, cliente TEXT, referencia TEXT, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_emisiones_numero ON emisiones(pto_vta, cbte_tipo, cbte_nro);
            CREATE INDEX IF NOT EXISTS ix_emisiones_fecha ON emisiones(cbte_fch);
            CREATE INDEX IF NOT EXISTS ix_emisiones_estado ON emisiones(estado);
            CREATE INDEX IF NOT EXISTS ix_emisiones_referencia ON emisiones(referencia);
            """);
        if (migrarDesdeDirectorio != null && Directory.Exists(migrarDesdeDirectorio) && Cantidad == 0)
            Migrados = Migrar(migrarDesdeDirectorio);
    }

    private string Leer(string j) => _cif?.Descifrar(j) ?? j;

    /// <summary>Filas que no se pudieron descifrar/leer en el último listado (clave equivocada o datos dañados).</summary>
    public int UltimasIlegibles { get; private set; }

    private RegistroEmision? LeerSeguro(string j)
    {
        try { return JsonSerializer.Deserialize(Leer(j), RegistroJsonContext.Default.RegistroEmision); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or FormatException) { return null; }
    }

    /// <summary>Registros migrados desde JSON al crear la base.</summary>
    public int Migrados { get; }

    private SqliteConnection Abrir()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static void Ejecutar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public int Cantidad
    {
        get
        {
            using var c = Abrir();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM emisiones";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Archivos JSON que no se pudieron migrar (ilegibles, clave de cifrado equivocada o datos dañados).
    /// Si es mayor que cero, revisar el directorio de origen: esos registros NO están en la base.</summary>
    public int MigracionIlegibles { get; private set; }

    private int Migrar(string dir)
    {
        var n = 0;
        var ilegibles = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                // El registro de archivos puede estar cifrado: se descifra igual que al leerlo desde su propio backend.
                var r = JsonSerializer.Deserialize(Leer(File.ReadAllText(f)), RegistroJsonContext.Default.RegistroEmision);
                if (r == null || string.IsNullOrEmpty(r.Clave)) { ilegibles++; continue; }
                Guardar(r);
                n++;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or System.Security.Cryptography.CryptographicException or FormatException or ArgumentException)
            {
                ilegibles++;
            }
        }
        MigracionIlegibles = ilegibles;
        if (ilegibles > 0)
            Console.Error.WriteLine($"ADVERTENCIA: {ilegibles} registro(s) JSON de {dir} no se pudieron migrar a SQLite (ilegibles o cifrados con otra clave).");
        return n;
    }

    public RegistroEmision? Obtener(string clave)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM emisiones WHERE clave = $c";
        cmd.Parameters.AddWithValue("$c", clave);
        if (cmd.ExecuteScalar() is not string j) return null;
        // Una fila ilegible NO se trata como inexistente: eso reabriría la clave y podría duplicar el comprobante.
        return LeerSeguro(j) ?? throw new InvalidOperationException(
            "El registro de emisión de esta Idempotency-Key existe pero no se puede leer (clave de cifrado equivocada o datos dañados). Revisar manualmente.");
    }

    public void Guardar(RegistroEmision r)
    {
        r.Version = RegistroEmisionesArchivo.VersionEsquema;
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO emisiones(clave, hash, pto_vta, cbte_tipo, cbte_nro, estado, creado, cbte_fch, cliente, referencia, json)
            VALUES($clave, $hash, $pv, $tipo, $nro, $estado, $creado, $fch, $cliente, $ref, $json)
            ON CONFLICT(clave) DO UPDATE SET hash=excluded.hash, pto_vta=excluded.pto_vta, cbte_tipo=excluded.cbte_tipo, cbte_nro=excluded.cbte_nro,
              estado=excluded.estado, cbte_fch=excluded.cbte_fch, cliente=excluded.cliente, referencia=excluded.referencia, json=excluded.json
            """;
        cmd.Parameters.AddWithValue("$clave", r.Clave);
        cmd.Parameters.AddWithValue("$hash", r.Hash);
        cmd.Parameters.AddWithValue("$pv", r.PtoVta);
        cmd.Parameters.AddWithValue("$tipo", r.CbteTipo);
        cmd.Parameters.AddWithValue("$nro", r.CbteNro);
        cmd.Parameters.AddWithValue("$estado", (int)r.Estado);
        cmd.Parameters.AddWithValue("$creado", r.Creado.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$fch", (object?)(r.Resultado?.CbteFch ?? r.Detalle?.CbteFch) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cliente", (object?)r.Cliente ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ref", (object?)r.Detalle?.ReferenciaExterna ?? DBNull.Value);
        var json = JsonSerializer.Serialize(r, RegistroJsonContext.Default.RegistroEmision);
        cmd.Parameters.AddWithValue("$json", _cif?.Cifrar(json) ?? json);
        cmd.ExecuteNonQuery();
    }

    public void Eliminar(string clave)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM emisiones WHERE clave = $c";
        cmd.Parameters.AddWithValue("$c", clave);
        cmd.ExecuteNonQuery();
    }

    public IEnumerable<RegistroEmision> Listar()
    {
        var l = new List<RegistroEmision>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM emisiones";
        using var rd = cmd.ExecuteReader();
        var ilegibles = 0;
        while (rd.Read()) { if (LeerSeguro(rd.GetString(0)) is { } r) l.Add(r); else ilegibles++; }
        UltimasIlegibles = ilegibles;
        return l;
    }

    /// <summary>Pendientes vía índice (sin deserializar toda la tabla).</summary>
    public IReadOnlyList<RegistroEmision> Pendientes()
    {
        var l = new List<RegistroEmision>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM emisiones WHERE estado = 0 ORDER BY creado";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) if (LeerSeguro(rd.GetString(0)) is { } r) l.Add(r);
        return l;
    }
}

/// <summary>Bloqueo de numeración entre procesos del mismo servidor: un archivo por (punto de venta, tipo) abierto en
/// modo exclusivo mientras se numera y emite. Complementa al semáforo en memoria.</summary>
public interface IBloqueoNumeracion
{
    Task<IAsyncDisposable> AdquirirAsync(int ptoVta, int cbteTipo, CancellationToken ct);
}

public sealed class BloqueoNumeracionArchivo : IBloqueoNumeracion
{
    private readonly string _dir;
    private readonly TimeSpan _espera;

    public BloqueoNumeracionArchivo(string dir, TimeSpan? esperaMaxima = null)
    {
        _dir = dir;
        _espera = esperaMaxima ?? TimeSpan.FromSeconds(120);
        Directory.CreateDirectory(dir);
    }

    public async Task<IAsyncDisposable> AdquirirAsync(int ptoVta, int cbteTipo, CancellationToken ct)
    {
        var ruta = Path.Combine(_dir, $"numeracion_{ptoVta}_{cbteTipo}.lock");
        var limite = DateTime.UtcNow + _espera;
        while (true)
        {
            try
            {
                var fs = new FileStream(ruta, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                return new Liberador(fs);
            }
            catch (IOException) when (DateTime.UtcNow < limite)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new ArcaComunicacionException($"No se pudo obtener el bloqueo de numeración {ptoVta}-{cbteTipo} en {_espera.TotalSeconds:N0} s.", ex);
            }
        }
    }

    private sealed class Liberador : IAsyncDisposable
    {
        private readonly FileStream _fs;
        public Liberador(FileStream fs) => _fs = fs;
        public ValueTask DisposeAsync() => _fs.DisposeAsync();
    }
}

/// <summary>Cifrado en reposo (AES-256-GCM) del contenido del registro de emisiones. Formato: "ENC1:" + base64(nonce|tag|datos).
/// Lee indistintamente registros cifrados y en claro (migración gradual).</summary>
public sealed class CifradorRegistro
{
    private const string Prefijo = "ENC1:";
    private readonly byte[] _clave;

    public CifradorRegistro(byte[] clave)
    {
        if (clave.Length != 32) throw new ArgumentException("La clave de cifrado debe tener 32 bytes (256 bits).", nameof(clave));
        _clave = clave;
    }

    public static CifradorRegistro? DesdeBase64(string? b64) =>
        string.IsNullOrWhiteSpace(b64) ? null : new CifradorRegistro(Convert.FromBase64String(b64));

    public string Cifrar(string texto)
    {
        var datos = System.Text.Encoding.UTF8.GetBytes(texto);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cifrado = new byte[datos.Length];
        using (var aes = new System.Security.Cryptography.AesGcm(_clave, 16)) aes.Encrypt(nonce, datos, cifrado, tag);
        return Prefijo + Convert.ToBase64String([.. nonce, .. tag, .. cifrado]);
    }

    public string Descifrar(string texto)
    {
        if (!texto.StartsWith(Prefijo, StringComparison.Ordinal)) return texto;
        var b = Convert.FromBase64String(texto[Prefijo.Length..]);
        var plano = new byte[b.Length - 28];
        using (var aes = new System.Security.Cryptography.AesGcm(_clave, 16)) aes.Decrypt(b.AsSpan(0, 12), b.AsSpan(28), b.AsSpan(12, 16), plano);
        return System.Text.Encoding.UTF8.GetString(plano);
    }
}
