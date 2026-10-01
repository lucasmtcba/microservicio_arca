using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Arca.Facturacion.Core.Soap;

/// <summary>Cliente SOAP 1.1 mínimo sobre HttpClient. Construye el sobre con XElement (escapado automático) y
/// parsea la respuesta con DTD deshabilitado (sin XXE).</summary>
public sealed class SoapInvoker
{
    public static readonly XNamespace SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private const int MaxRespuestaBytes = 20 * 1024 * 1024;
    private readonly Func<HttpClient> _http;
    private readonly Action<DateTimeOffset?>? _alResponder;

    /// <param name="alResponder">Callback con la cabecera Date del servidor en cada respuesta recibida (diagnóstico de reloj).</param>
    public SoapInvoker(Func<HttpClient> http, Action<DateTimeOffset?>? alResponder = null)
    {
        _http = http;
        _alResponder = alResponder;
    }

    public async Task<XElement> InvocarAsync(string url, string soapAction, XElement cuerpo, CancellationToken ct)
    {
        var sobre = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(SoapNs + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", SoapNs),
                new XElement(SoapNs + "Body", cuerpo)));

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(sobre.Declaration + sobre.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
        };
        req.Headers.TryAddWithoutValidation("SOAPAction", "\"" + soapAction + "\"");

        HttpResponseMessage resp;
        try
        {
            resp = await _http().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ArcaComunicacionException($"Timeout invocando {soapAction}.");
        }
        catch (HttpRequestException ex)
        {
            throw new ArcaComunicacionException($"Error de red invocando {soapAction}: {ex.Message}", ex);
        }

        using (resp)
        {
            _alResponder?.Invoke(resp.Headers.Date);
            if (resp.Content.Headers.ContentLength > MaxRespuestaBytes)
                throw new ArcaComunicacionException("Respuesta de ARCA demasiado grande.");

            string texto;
            try { texto = await LeerAcotadoAsync(resp.Content, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            { throw new ArcaComunicacionException($"Respuesta interrumpida en {soapAction}.", ex); }

            XDocument doc;
            try { doc = ParsearSeguro(texto); }
            catch (XmlException ex)
            {
                throw new ArcaComunicacionException(
                    $"Respuesta no XML de ARCA ({(int)resp.StatusCode}) en {soapAction}.", ex);
            }

            var body = doc.Root?.Element(SoapNs + "Body")
                       ?? throw new ArcaComunicacionException($"Sobre SOAP sin Body en {soapAction}.");
            var fault = body.Element(SoapNs + "Fault");
            if (fault != null)
            {
                var code = fault.Element("faultcode")?.Value?.Trim();
                var msg = fault.Element("faultstring")?.Value?.Trim() ?? "soap:Fault";
                throw new SoapFaultException(code, msg);
            }
            if (resp.StatusCode != HttpStatusCode.OK)
                throw new ArcaComunicacionException($"HTTP {(int)resp.StatusCode} invocando {soapAction}.");

            return body.Elements().FirstOrDefault()
                   ?? throw new ArcaComunicacionException($"Body SOAP vacío en {soapAction}.");
        }
    }

    private static async Task<string> LeerAcotadoAsync(HttpContent content, CancellationToken ct)
    {
        await using var s = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > MaxRespuestaBytes) throw new ArcaComunicacionException("Respuesta de ARCA demasiado grande.");
            ms.Write(buf, 0, n);
        }
        var charset = content.Headers.ContentType?.CharSet;
        var enc = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
            try { enc = Encoding.GetEncoding(charset.Trim('"')); } catch (ArgumentException) { }
        return enc.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public static XDocument ParsearSeguro(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxRespuestaBytes
        };
        using var sr = new StringReader(xml);
        using var xr = XmlReader.Create(sr, settings);
        return XDocument.Load(xr);
    }
}

public sealed class SoapFaultException : ArcaException
{
    public string? FaultCode { get; }
    public SoapFaultException(string? faultCode, string faultString)
        : base($"SOAP Fault [{faultCode}]: {faultString}") => FaultCode = faultCode;
}
