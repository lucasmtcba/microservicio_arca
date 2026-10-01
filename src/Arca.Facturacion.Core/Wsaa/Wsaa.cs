using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Arca.Facturacion.Core.Configuracion;
using Arca.Facturacion.Core.Soap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arca.Facturacion.Core.Wsaa;

/// <summary>Ticket de Acceso (TA) devuelto por WSAA.</summary>
public sealed record TicketAcceso(string Token, string Sign, DateTimeOffset GeneradoEn, DateTimeOffset ExpiraEn)
{
    public bool EsValido(DateTimeOffset ahora, TimeSpan margen) => ahora + margen < ExpiraEn;
}

/// <summary>Construye el TRA (LoginTicketRequest).</summary>
public static class TraBuilder
{
    public static string Construir(string servicio, DateTimeOffset ahora)
    {
        if (string.IsNullOrWhiteSpace(servicio)) throw new ArgumentException("Servicio requerido", nameof(servicio));
        var utc = ahora.ToUniversalTime();
        // Margen de ±10 min para tolerar desfasaje de reloj con WSAA (generationTime no puede ser futuro).
        var doc = new XElement("loginTicketRequest", new XAttribute("version", "1.0"),
            new XElement("header",
                new XElement("uniqueId", ((uint)utc.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture)),
                new XElement("generationTime", utc.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture)),
                new XElement("expirationTime", utc.AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture))),
            new XElement("service", servicio));
        return doc.ToString(SaveOptions.DisableFormatting);
    }
}

/// <summary>Firma CMS (PKCS#7) del TRA.</summary>
public interface ICmsFirmante
{
    byte[] Firmar(byte[] contenido);
}

/// <summary>Firmante basado en un certificado X.509 con clave privada.</summary>
public sealed class CertificadoCmsFirmante : ICmsFirmante, IDisposable
{
    private readonly X509Certificate2 _cert;

    public CertificadoCmsFirmante(X509Certificate2 cert, TimeProvider? reloj = null)
    {
        if (!cert.HasPrivateKey) throw new WsaaException("El certificado configurado no contiene clave privada.");
        var ahora = (reloj ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        if (cert.NotAfter.ToUniversalTime() < ahora)
            throw new WsaaException($"El certificado venció el {cert.NotAfter:yyyy-MM-dd}. Renovarlo en ARCA.");
        using (var rsa = cert.GetRSAPrivateKey())
            if (rsa != null && rsa.KeySize < 2048)
                throw new WsaaException($"La clave privada RSA del certificado tiene {rsa.KeySize} bits; se requieren al menos 2048.");
        _cert = cert;
    }

    public static CertificadoCmsFirmante DesdeArchivo(string path, string? password)
    {
        if (!File.Exists(path)) throw new WsaaException($"No se encuentra el certificado en '{path}'.");
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        try { return new CertificadoCmsFirmante(new X509Certificate2(path, password, flags)); }
        catch (CryptographicException ex) { throw new WsaaException("No se pudo abrir el certificado (¿contraseña incorrecta?).", null, ex); }
    }

    public byte[] Firmar(byte[] contenido)
    {
        var cms = new SignedCms(new ContentInfo(contenido), detached: false);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, _cert)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") // SHA-256
        };
        cms.ComputeSignature(signer, silent: true);
        return cms.Encode();
    }

    public DateTime Vencimiento => _cert.NotAfter;

    /// <summary>CUIT informada en el sujeto del certificado (SERIALNUMBER=CUIT nnnnnnnnnnn), o null.</summary>
    public string? CuitDelCertificado => ExtraerCuit(_cert.Subject);

    public static string? ExtraerCuit(string sujeto)
    {
        var m = System.Text.RegularExpressions.Regex.Match(sujeto, @"SERIALNUMBER=CUIT\s*(\d{11})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
    public void Dispose() => _cert.Dispose();
}

/// <summary>Carga el certificado recién en el primer uso: el servicio arranca (y reporta su estado en /health/ready)
/// aunque el certificado falte o esté mal, en lugar de caerse al iniciar.</summary>
public sealed class FirmanteDiferido : ICmsFirmante
{
    private readonly Lazy<CertificadoCmsFirmante> _f;
    public FirmanteDiferido(Func<CertificadoCmsFirmante> fabrica) => _f = new Lazy<CertificadoCmsFirmante>(fabrica, LazyThreadSafetyMode.PublicationOnly);
    public byte[] Firmar(byte[] contenido) => _f.Value.Firmar(contenido);
    /// <summary>Vencimiento del certificado (carga el certificado si aún no se cargó; puede lanzar WsaaException).</summary>
    public DateTime Vencimiento => _f.Value.Vencimiento;
    /// <summary>CUIT del certificado (carga el certificado si aún no se cargó).</summary>
    public string? CuitDelCertificado => _f.Value.CuitDelCertificado;
}

/// <summary>Cliente del método loginCms de WSAA.</summary>
public interface IWsaaClient
{
    Task<TicketAcceso> LoginCmsAsync(string servicio, CancellationToken ct);
}

public sealed class WsaaClient : IWsaaClient
{
    private static readonly XNamespace Ns = "http://wsaa.view.sua.dvadac.desein.afip.gov";
    private readonly SoapInvoker _soap;
    private readonly ICmsFirmante _firmante;
    private readonly ArcaOptions _opt;
    private readonly TimeProvider _reloj;

    public const string NombreHttpClient = "arca-wsaa";

    public WsaaClient(IHttpClientFactory http, ICmsFirmante firmante, IOptions<ArcaOptions> opt, TimeProvider reloj)
    {
        _soap = new SoapInvoker(() => http.CreateClient(NombreHttpClient));
        _firmante = firmante;
        _opt = opt.Value;
        _reloj = reloj;
    }

    public async Task<TicketAcceso> LoginCmsAsync(string servicio, CancellationToken ct)
    {
        var tra = TraBuilder.Construir(servicio, _reloj.GetUtcNow());
        var cms = Convert.ToBase64String(_firmante.Firmar(Encoding.UTF8.GetBytes(tra)));
        XElement resp;
        try
        {
            resp = await _soap.InvocarAsync(_opt.WsaaUrlEfectiva, "",
                new XElement(Ns + "loginCms", new XElement(Ns + "in0", cms)), ct).ConfigureAwait(false);
        }
        catch (SoapFaultException f)
        {
            throw new WsaaException(f.FaultCode?.Contains("alreadyAuthenticated") == true
                ? "WSAA indica que ya existe un TA válido para este certificado y servicio, pero no está en caché. " +
                  "Esperar a su vencimiento (máx. 12 h) o configurar CacheTicketPath para persistirlo."
                : f.Message, f.FaultCode, f);
        }
        var ret = resp.Descendants().FirstOrDefault(e => e.Name.LocalName == "loginCmsReturn")?.Value
                  ?? throw new WsaaException("Respuesta de WSAA sin loginCmsReturn.");
        return ParsearTicket(ret);
    }

    internal static TicketAcceso ParsearTicket(string xml)
    {
        var doc = SoapInvoker.ParsearSeguro(xml).Root!;
        string Req(XElement? e, string n) => e?.Element(n)?.Value?.Trim() is { Length: > 0 } v ? v
            : throw new WsaaException($"TA sin '{n}'.");
        var header = doc.Element("header");
        var cred = doc.Element("credentials");
        return new TicketAcceso(Req(cred, "token"), Req(cred, "sign"),
            DateTimeOffset.Parse(Req(header, "generationTime"), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(Req(header, "expirationTime"), CultureInfo.InvariantCulture));
    }
}

/// <summary>Obtiene (y cachea) el TA de un servicio.</summary>
public interface ITicketAccesoProvider
{
    Task<TicketAcceso> ObtenerAsync(string servicio, CancellationToken ct);
    void Invalidar(string servicio);
}

/// <summary>Caché de TA en memoria + disco opcional. Un único login concurrente por servicio.
/// WSAA rechaza pedir un TA nuevo mientras haya uno vigente, por eso se persiste.</summary>
public sealed class TicketAccesoProvider : ITicketAccesoProvider, IDisposable
{
    private readonly IWsaaClient _wsaa;
    private readonly ArcaOptions _opt;
    private readonly TimeProvider _reloj;
    private readonly ILogger<TicketAccesoProvider> _log;
    private readonly Dictionary<string, TicketAcceso> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TicketAccesoProvider(IWsaaClient wsaa, IOptions<ArcaOptions> opt, TimeProvider reloj, ILogger<TicketAccesoProvider> log)
    {
        _wsaa = wsaa; _opt = opt.Value; _reloj = reloj; _log = log;
    }

    private TimeSpan Margen => TimeSpan.FromMinutes(_opt.MargenRenovacionTicketMinutos);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _sospechosos = new();

    public async Task<TicketAcceso> ObtenerAsync(string servicio, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ahora = _reloj.GetUtcNow();
            var sospechoso = _sospechosos.ContainsKey(servicio);
            if (!_cache.TryGetValue(servicio, out var actual))
            {
                actual = LeerDisco(servicio);
                if (actual != null) _cache[servicio] = actual;
            }
            if (actual != null && actual.EsValido(ahora, Margen) && !sospechoso) return actual;

            _log.LogInformation("Solicitando nuevo TA a WSAA para {Servicio} ({Ambiente})", servicio, _opt.Ambiente);
            TicketAcceso nuevo;
            try
            {
                nuevo = await _wsaa.LoginCmsAsync(servicio, ct).ConfigureAwait(false);
            }
            catch (WsaaException ex) when (ex.CodigoFault?.Contains("alreadyAuthenticated") == true
                                           && actual != null && actual.EsValido(ahora, TimeSpan.Zero))
            {
                // WSAA confirma que el TA vigente sigue siendo el válido: se conserva (evita bloqueo de hasta 12 h).
                _log.LogWarning("WSAA mantiene vigente el TA actual de {Servicio}; se conserva", servicio);
                _sospechosos.TryRemove(servicio, out _);
                return actual;
            }
            _sospechosos.TryRemove(servicio, out _);
            _cache[servicio] = nuevo;
            GuardarDisco(servicio, nuevo);
            _log.LogInformation("TA obtenido para {Servicio}, vence {Vence:o}", servicio, nuevo.ExpiraEn);
            return nuevo;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Marca el TA como sospechoso (p.ej. error 600). NO lo descarta: si WSAA responde que ya existe
    /// un TA vigente, se sigue usando el actual.</summary>
    public void Invalidar(string servicio) => _sospechosos[servicio] = true;

    public void Dispose() => _lock.Dispose();

    private string? RutaDisco(string servicio)
    {
        if (string.IsNullOrWhiteSpace(_opt.CacheTicketPath)) return null;
        var seguro = new string(servicio.Where(char.IsLetterOrDigit).ToArray());
        return Path.Combine(_opt.CacheTicketPath!, $"ta_{_opt.Cuit}_{_opt.Ambiente}_{seguro}.json");
    }

    private TicketAcceso? LeerDisco(string servicio)
    {
        var p = RutaDisco(servicio);
        if (p == null || !File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<TicketAcceso>(File.ReadAllText(p)); }
        catch (Exception ex) { _log.LogWarning(ex, "TA en disco ilegible, se ignora"); return null; }
    }

    private void GuardarDisco(string servicio, TicketAcceso ta)
    {
        var p = RutaDisco(servicio);
        if (p == null) return;
        try
        {
            var dir = Path.GetDirectoryName(p)!;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var tmp = p + ".tmp";
            if (!OperatingSystem.IsWindows())
            {
                using var fs = new FileStream(tmp, new FileStreamOptions
                {
                    Mode = FileMode.Create, Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
                JsonSerializer.Serialize(fs, ta);
            }
            else File.WriteAllText(tmp, JsonSerializer.Serialize(ta));
            File.Move(tmp, p, overwrite: true);
        }
        catch (Exception ex) { _log.LogWarning(ex, "No se pudo persistir el TA en disco"); }
    }
}
