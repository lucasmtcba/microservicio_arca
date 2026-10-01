using System.Collections.Concurrent;
using System.Xml.Linq;
using Arca.Facturacion.Core.Configuracion;
using Arca.Facturacion.Core.Soap;
using Arca.Facturacion.Core.Wsaa;
using Microsoft.Extensions.Options;

namespace Arca.Facturacion.Core.Padron;

/// <summary>Domicilio informado por el padrón.</summary>
public sealed record DomicilioPadron(string? Tipo, string? Direccion, string? Localidad, string? CodigoPostal, string? Provincia);

/// <summary>Datos de un contribuyente según ws_sr_padron_a13 (getPersona).</summary>
public sealed class Contribuyente
{
    public long Cuit { get; set; }
    public string? TipoPersona { get; set; }
    public string? EstadoClave { get; set; }
    public string? Nombre { get; set; }
    public string? Apellido { get; set; }
    public string? RazonSocial { get; set; }
    public List<DomicilioPadron> Domicilios { get; set; } = new();
    /// <summary>Nombre para mostrar: razón social o "Apellido, Nombre".</summary>
    public string Denominacion => !string.IsNullOrWhiteSpace(RazonSocial) ? RazonSocial!
        : string.Join(", ", new[] { Apellido, Nombre }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public bool Activo => string.Equals(EstadoClave, "ACTIVO", StringComparison.OrdinalIgnoreCase);
    /// <summary>Domicilio fiscal en una línea (o el primero disponible).</summary>
    public string? DomicilioFiscal => (Domicilios.FirstOrDefault(d => d.Tipo?.Contains("FISCAL", StringComparison.OrdinalIgnoreCase) == true) ?? Domicilios.FirstOrDefault())
        is { } d ? string.Join(", ", new[] { d.Direccion, d.Localidad, d.CodigoPostal, d.Provincia }.Where(s => !string.IsNullOrWhiteSpace(s))) : null;
}

public interface IPadronClient
{
    /// <summary>Devuelve el contribuyente o null si no existe en el padrón.</summary>
    Task<Contribuyente?> ObtenerAsync(long cuit, CancellationToken ct);
}

/// <summary>Cliente SOAP de ws_sr_padron_a13 con parseo tolerante (ignora espacios de nombres y campos faltantes).</summary>
public sealed class PadronA13Client : IPadronClient
{
    public const string Servicio = "ws_sr_padron_a13";
    public const string NombreHttpClient = "arca-padron";
    private static readonly XNamespace Ns = "http://a13.soap.ws.server.puc.sr/";
    private readonly SoapInvoker _soap;
    private readonly ITicketAccesoProvider _ta;
    private readonly ArcaOptions _opt;

    public PadronA13Client(IHttpClientFactory http, ITicketAccesoProvider ta, IOptions<ArcaOptions> opt)
    {
        _soap = new SoapInvoker(() => http.CreateClient(NombreHttpClient));
        _ta = ta;
        _opt = opt.Value;
    }

    public async Task<Contribuyente?> ObtenerAsync(long cuit, CancellationToken ct)
    {
        var ta = await _ta.ObtenerAsync(Servicio, ct).ConfigureAwait(false);
        XElement resp;
        try
        {
            resp = await _soap.InvocarAsync(_opt.PadronUrlEfectiva, "", new XElement(Ns + "getPersona",
                new XElement("token", ta.Token), new XElement("sign", ta.Sign),
                new XElement("cuitRepresentada", _opt.Cuit), new XElement("idPersona", cuit)), ct).ConfigureAwait(false);
        }
        catch (SoapFaultException f) when (f.Message.Contains("No existe persona", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return Parsear(resp, cuit);
    }

    internal static Contribuyente? Parsear(XElement resp, long cuit)
    {
        var p = resp.Descendants().FirstOrDefault(e => e.Name.LocalName == "persona");
        if (p == null) return null;
        string? V(XElement x, string n) => x.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value?.Trim() is { Length: > 0 } v ? v : null;
        return new Contribuyente
        {
            Cuit = long.TryParse(V(p, "idPersona"), out var id) ? id : cuit,
            TipoPersona = V(p, "tipoPersona"),
            EstadoClave = V(p, "estadoClave"),
            Nombre = V(p, "nombre"),
            Apellido = V(p, "apellido"),
            RazonSocial = V(p, "razonSocial"),
            Domicilios = p.Elements().Where(e => e.Name.LocalName == "domicilio")
                .Select(d => new DomicilioPadron(V(d, "tipoDomicilio"), V(d, "direccion"), V(d, "localidad"), V(d, "codigoPostal") ?? V(d, "codPostal"), V(d, "descripcionProvincia")))
                .ToList()
        };
    }
}

/// <summary>Padrón ficticio para modo simulación: cualquier CUIT válida existe; las terminadas en 0 se informan inactivas.</summary>
public sealed class PadronSimulado : IPadronClient
{
    public Task<Contribuyente?> ObtenerAsync(long cuit, CancellationToken ct) => Task.FromResult<Contribuyente?>(!Validacion.Cuit.EsValida(cuit.ToString(System.Globalization.CultureInfo.InvariantCulture)) ? null : new Contribuyente
    {
        Cuit = cuit,
        TipoPersona = cuit.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith('3') ? "JURIDICA" : "FISICA",
        EstadoClave = cuit % 10 == 0 ? "INACTIVO" : "ACTIVO",
        RazonSocial = $"CONTRIBUYENTE SIMULADO {cuit}",
        Domicilios = { new DomicilioPadron("FISCAL", "CALLE FALSA 123", "CIUDAD AUTONOMA BUENOS AIRES", "1000", "CIUDAD AUTONOMA BUENOS AIRES") }
    });
}

/// <summary>Caché (incluye resultados negativos, por menos tiempo) y single-flight sobre un IPadronClient.</summary>
public sealed class PadronCacheado : IPadronClient
{
    private readonly IPadronClient _interno;
    private readonly TimeProvider _reloj;
    private readonly TimeSpan _ttl, _ttlNegativo;
    private readonly ConcurrentDictionary<long, (Contribuyente? Valor, DateTimeOffset Vence)> _cache = new();
    private readonly ConcurrentDictionary<long, Lazy<Task<Contribuyente?>>> _enCurso = new();

    public PadronCacheado(IPadronClient interno, TimeProvider reloj, TimeSpan? ttl = null, TimeSpan? ttlNegativo = null)
    {
        _interno = interno; _reloj = reloj;
        _ttl = ttl ?? TimeSpan.FromHours(24);
        _ttlNegativo = ttlNegativo ?? TimeSpan.FromMinutes(10);
    }

    public int Llamadas { get; private set; }

    public async Task<Contribuyente?> ObtenerAsync(long cuit, CancellationToken ct)
    {
        if (_cache.TryGetValue(cuit, out var e) && e.Vence > _reloj.GetUtcNow()) return e.Valor;
        var lazy = _enCurso.GetOrAdd(cuit, c => new Lazy<Task<Contribuyente?>>(async () =>
        {
            try
            {
                Llamadas++;
                var v = await _interno.ObtenerAsync(c, CancellationToken.None).ConfigureAwait(false);
                _cache[c] = (v, _reloj.GetUtcNow() + (v == null ? _ttlNegativo : _ttl));
                if (_cache.Count > 50_000) _cache.Clear();
                return v;
            }
            finally { _enCurso.TryRemove(c, out _); }
        }));
        return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
    }
}
