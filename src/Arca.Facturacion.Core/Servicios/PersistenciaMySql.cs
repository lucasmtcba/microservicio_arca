using System.Globalization;
using System.Text.Json;
using MySqlConnector;

namespace Arca.Facturacion.Core.Servicios;

/// <summary>Registro de emisiones en MySQL/MariaDB: permite varios servidores de API sobre la misma base.
/// Reintenta automáticamente ante deadlocks/timeouts de bloqueo y tolera filas ilegibles.</summary>
public sealed class RegistroEmisionesMySql : IRegistroEmisiones
{
    private readonly string _cs;
    private readonly CifradorRegistro? _cif;
    private readonly string _tabla;

    public RegistroEmisionesMySql(string cadenaConexion, CifradorRegistro? cifrador = null, string tabla = "arca_emisiones")
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(tabla, "^[A-Za-z_][A-Za-z0-9_]{0,63}$"))
            throw new ArgumentException("Nombre de tabla inválido.", nameof(tabla));
        _cs = cadenaConexion;
        _cif = cifrador;
        _tabla = tabla;
        Reintentar(c => Ejecutar(c, $"""
            CREATE TABLE IF NOT EXISTS `{_tabla}`(
              clave VARCHAR(191) NOT NULL PRIMARY KEY, hash VARCHAR(128) NOT NULL, pto_vta INT NOT NULL, cbte_tipo INT NOT NULL,
              cbte_nro BIGINT NOT NULL, estado TINYINT NOT NULL, creado VARCHAR(40) NOT NULL, cbte_fch CHAR(8) NULL,
              cliente VARCHAR(191) NULL, referencia VARCHAR(191) NULL, json LONGTEXT NOT NULL,
              actualizado TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
              KEY ix_numero(pto_vta, cbte_tipo, cbte_nro), KEY ix_fecha(cbte_fch), KEY ix_estado(estado), KEY ix_referencia(referencia), KEY ix_cliente(cliente)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin
            """));
    }

    /// <summary>Filas ilegibles (clave de cifrado equivocada o datos dañados) del último listado.</summary>
    public int UltimasIlegibles { get; private set; }

    internal static bool EsTransitorio(MySqlException ex) =>
        ex.ErrorCode is MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout || ex.IsTransient;

    private T Reintentar<T>(Func<MySqlConnection, T> f)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                using var c = new MySqlConnection(_cs);
                c.Open();
                return f(c);
            }
            catch (MySqlException ex) when (EsTransitorio(ex) && intento < 4)
            {
                Thread.Sleep(50 * intento * intento);
            }
        }
    }

    private void Reintentar(Action<MySqlConnection> a) => Reintentar(c => { a(c); return 0; });

    private static void Ejecutar(MySqlConnection c, string sql)
    {
        using var cmd = new MySqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private RegistroEmision? LeerSeguro(string j)
    {
        try { return JsonSerializer.Deserialize(_cif?.Descifrar(j) ?? j, RegistroJsonContext.Default.RegistroEmision); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or FormatException) { return null; }
    }

    public int Cantidad => Reintentar(c =>
    {
        using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM `{_tabla}`", c);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    public RegistroEmision? Obtener(string clave) => Reintentar(c =>
    {
        using var cmd = new MySqlCommand($"SELECT json FROM `{_tabla}` WHERE clave=@c", c);
        cmd.Parameters.AddWithValue("@c", clave);
        if (cmd.ExecuteScalar() is not string j) return null;
        // Fila existente pero ilegible: devolver null reabriría la clave y podría duplicar el comprobante.
        return LeerSeguro(j) ?? throw new InvalidOperationException($"El registro de emisión '{clave}' existe pero no se puede leer (¿clave de cifrado distinta?). Requiere revisión manual.");
    });

    public void Guardar(RegistroEmision r)
    {
        // La columna clave es VARCHAR(191): MySQL truncaría o rechazaría claves más largas y dos claves distintas
        // podrían colisionar. Se rechaza explícitamente (FacturacionService ya lo valida antes de llamar a ARCA).
        if (r.Clave.Length > 191)
            throw new ArgumentException($"La clave de idempotencia supera los 191 caracteres admitidos por el registro MySQL ({r.Clave.Length}).", nameof(r));
        r.Version = RegistroEmisionesArchivo.VersionEsquema;
        var json = JsonSerializer.Serialize(r, RegistroJsonContext.Default.RegistroEmision);
        var guardado = _cif?.Cifrar(json) ?? json;
        Reintentar(c =>
        {
            using var cmd = new MySqlCommand($"""
                INSERT INTO `{_tabla}`(clave, hash, pto_vta, cbte_tipo, cbte_nro, estado, creado, cbte_fch, cliente, referencia, json)
                VALUES(@clave, @hash, @pv, @tipo, @nro, @estado, @creado, @fch, @cliente, @ref, @json)
                ON DUPLICATE KEY UPDATE hash=VALUES(hash), pto_vta=VALUES(pto_vta), cbte_tipo=VALUES(cbte_tipo), cbte_nro=VALUES(cbte_nro),
                  estado=VALUES(estado), cbte_fch=VALUES(cbte_fch), cliente=VALUES(cliente), referencia=VALUES(referencia), json=VALUES(json)
                """, c);
            cmd.Parameters.AddWithValue("@clave", r.Clave);
            cmd.Parameters.AddWithValue("@hash", r.Hash);
            cmd.Parameters.AddWithValue("@pv", r.PtoVta);
            cmd.Parameters.AddWithValue("@tipo", r.CbteTipo);
            cmd.Parameters.AddWithValue("@nro", r.CbteNro);
            cmd.Parameters.AddWithValue("@estado", (int)r.Estado);
            cmd.Parameters.AddWithValue("@creado", r.Creado.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@fch", (object?)(r.Resultado?.CbteFch ?? r.Detalle?.CbteFch) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cliente", (object?)Recortar(r.Cliente) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ref", (object?)Recortar(r.Detalle?.ReferenciaExterna) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@json", guardado);
            cmd.ExecuteNonQuery();
        });
    }

    private static string? Recortar(string? s) => s is { Length: > 191 } ? s[..191] : s;

    public void Eliminar(string clave) => Reintentar(c =>
    {
        using var cmd = new MySqlCommand($"DELETE FROM `{_tabla}` WHERE clave=@c", c);
        cmd.Parameters.AddWithValue("@c", clave);
        cmd.ExecuteNonQuery();
    });

    private List<RegistroEmision> Consultar(string where)
    {
        var (l, ileg) = Reintentar(c =>
        {
            var lista = new List<RegistroEmision>();
            var malos = 0;
            using var cmd = new MySqlCommand($"SELECT json FROM `{_tabla}` {where}", c);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) { if (LeerSeguro(rd.GetString(0)) is { } r) lista.Add(r); else malos++; }
            return (lista, malos);
        });
        UltimasIlegibles = ileg;
        return l;
    }

    public IEnumerable<RegistroEmision> Listar() => Consultar("");

    /// <summary>Pendientes vía índice.</summary>
    public IReadOnlyList<RegistroEmision> Pendientes() => Consultar("WHERE estado=0 ORDER BY creado");

    /// <summary>Copia todos los registros de otro backend (SQLite/JSON) sin pisar claves existentes. Devuelve (copiados, omitidos).</summary>
    public (int Copiados, int Omitidos) MigrarDesde(IRegistroEmisiones origen)
    {
        int cop = 0, omi = 0;
        foreach (var r in origen.Listar())
        {
            if (string.IsNullOrEmpty(r.Clave) || Obtener(r.Clave) != null) { omi++; continue; }
            Guardar(r);
            cop++;
        }
        return (cop, omi);
    }

    /// <summary>Verifica conectividad (para health checks).</summary>
    public bool Ping() => Reintentar(c => { using var cmd = new MySqlCommand("SELECT 1", c); return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1; });
}

/// <summary>Bloqueo de numeración distribuido con GET_LOCK de MySQL/MariaDB: varios servidores pueden emitir sobre la misma
/// CUIT sin pisar números. El bloqueo vive en la conexión, que se mantiene abierta hasta liberar.</summary>
public sealed class BloqueoNumeracionMySql : IBloqueoNumeracion
{
    private readonly string _cs;
    private readonly int _esperaSegundos;
    private readonly string _prefijo;

    public BloqueoNumeracionMySql(string cadenaConexion, TimeSpan? esperaMaxima = null, string prefijo = "arca")
    {
        _cs = cadenaConexion;
        _esperaSegundos = (int)Math.Clamp((esperaMaxima ?? TimeSpan.FromSeconds(120)).TotalSeconds, 1, 3600);
        _prefijo = prefijo;
    }

    public static string NombreBloqueo(string prefijo, int pv, int tipo) => $"{prefijo}:num:{pv}:{tipo}";

    public async Task<IAsyncDisposable> AdquirirAsync(int ptoVta, int cbteTipo, CancellationToken ct)
    {
        var nombre = NombreBloqueo(_prefijo, ptoVta, cbteTipo);
        var c = new MySqlConnection(_cs);
        try
        {
            await c.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new MySqlCommand("SELECT GET_LOCK(@n, @t)", c);
            cmd.Parameters.AddWithValue("@n", nombre);
            cmd.Parameters.AddWithValue("@t", _esperaSegundos);
            var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (r is null or DBNull || Convert.ToInt32(r, CultureInfo.InvariantCulture) != 1)
                throw new ArcaComunicacionException($"No se pudo obtener el bloqueo distribuido de numeración {ptoVta}-{cbteTipo} en {_esperaSegundos} s.");
            return new Liberador(c, nombre);
        }
        catch
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Liberador : IAsyncDisposable
    {
        private readonly MySqlConnection _c;
        private readonly string _n;
        private int _liberado;
        public Liberador(MySqlConnection c, string n) { _c = c; _n = n; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _liberado, 1) == 1) return;
            try
            {
                await using var cmd = new MySqlCommand("SELECT RELEASE_LOCK(@n)", _c);
                cmd.Parameters.AddWithValue("@n", _n);
                await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            }
            catch (MySqlException) { /* al cerrar la conexión el servidor libera el bloqueo igual */ }
            finally { await _c.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
