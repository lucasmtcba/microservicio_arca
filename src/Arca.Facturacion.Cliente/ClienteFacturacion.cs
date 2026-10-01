using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Wsfe;

namespace Arca.Facturacion.Cliente;

/// <summary>Error devuelto por el microservicio (ProblemDetails).</summary>
public sealed class ErrorFacturacionException : Exception
{
    public HttpStatusCode Estado { get; }
    public IReadOnlyList<string> Errores { get; }
    public string? TraceId { get; }

    public ErrorFacturacionException(HttpStatusCode estado, string mensaje, IReadOnlyList<string> errores, string? traceId)
        : base($"[{(int)estado}] {mensaje}") { Estado = estado; Errores = errores; TraceId = traceId; }
}

/// <summary>Cliente .NET del microservicio de facturación ARCA.
/// Garantiza la regla de oro: una misma emisión lógica siempre viaja con la MISMA Idempotency-Key,
/// incluso en los reintentos automáticos ante 503/429/timeouts, de modo que nunca se duplican comprobantes.</summary>
public sealed class ClienteFacturacion
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly int _reintentos;
    private readonly TimeSpan _esperaMaxima;

    private readonly string? _secretoFirma;
    private readonly TimeProvider _reloj;

    /// <summary>Identificador de correlación a enviar en cada solicitud (X-Correlation-Id). Si es null se usa el de la traza actual.</summary>
    public string? CorrelationId { get; set; }

    /// <param name="http">HttpClient con BaseAddress del servicio.</param>
    /// <param name="apiKey">Clave de API (cabecera X-Api-Key).</param>
    /// <param name="reintentos">Reintentos ante 503/429/errores de red (con la misma Idempotency-Key).</param>
    /// <param name="esperaMaxima">Tope para la espera indicada por Retry-After.</param>
    /// <param name="reloj">Reloj (para pruebas).</param>
    /// <param name="secretoFirma">Si el cliente tiene SecretoFirma configurado en el servicio, firma cada solicitud (HMAC-SHA256).</param>
    public ClienteFacturacion(HttpClient http, string apiKey, int reintentos = 3, TimeSpan? esperaMaxima = null, string? secretoFirma = null, TimeProvider? reloj = null)
    {
        _secretoFirma = secretoFirma;
        _reloj = reloj ?? TimeProvider.System;
        _http = http;
        _reintentos = reintentos;
        _esperaMaxima = esperaMaxima ?? TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Remove("X-Api-Key");
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    }

    /// <summary>Emite un comprobante. Si no se informa <paramref name="idempotencyKey"/> se genera una y se reutiliza en los reintentos.
    /// Guardar la clave junto a la venta permite reintentar más tarde sin riesgo.</summary>
    public Task<ResultadoEmision> EmitirAsync(int ptoVta, int cbteTipo, ComprobanteDetalle comprobante, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var clave = idempotencyKey ?? Guid.NewGuid().ToString();
        return EnviarAsync<ResultadoEmision>(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/comprobantes")
            { Content = JsonContent.Create(new { ptoVta, cbteTipo, comprobante }, options: Json) };
            req.Headers.Add("Idempotency-Key", clave);
            return req;
        }, ct)!;
    }

    /// <summary>Consulta un comprobante (null si no existe).</summary>
    public async Task<ComprobanteConsultado?> ConsultarAsync(int ptoVta, int cbteTipo, long cbteNro, CancellationToken ct = default)
    {
        try
        {
            return await EnviarAsync<ComprobanteConsultado>(() => new HttpRequestMessage(HttpMethod.Get, $"api/v1/comprobantes/{ptoVta}/{cbteTipo}/{cbteNro}"), ct).ConfigureAwait(false);
        }
        catch (ErrorFacturacionException e) when (e.Estado == HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>Estado de una emisión por su Idempotency-Key (null si no existe).</summary>
    public async Task<RegistroEmision?> EstadoEmisionAsync(string idempotencyKey, CancellationToken ct = default)
    {
        try
        {
            return await EnviarAsync<RegistroEmision>(() => new HttpRequestMessage(HttpMethod.Get, $"api/v1/comprobantes/emisiones/{Uri.EscapeDataString(idempotencyKey)}"), ct).ConfigureAwait(false);
        }
        catch (ErrorFacturacionException e) when (e.Estado == HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>Valida un comprobante sin emitirlo (errores y advertencias).</summary>
    public Task<JsonElement> ValidarAsync(int ptoVta, int cbteTipo, ComprobanteDetalle comprobante, CancellationToken ct = default) =>
        EnviarAsync<JsonElement>(() => new HttpRequestMessage(HttpMethod.Post, "api/v1/comprobantes/validar")
            { Content = JsonContent.Create(new { ptoVta, cbteTipo, comprobante }, options: Json) }, ct)!;

    /// <summary>Anula un comprobante con su nota de crédito (misma Idempotency-Key en reintentos).</summary>
    public Task<ResultadoEmision> AnularAsync(int ptoVta, int cbteTipo, long cbteNro, string? idempotencyKey = null, CancellationToken ct = default)
        => ConClave($"api/v1/comprobantes/{ptoVta}/{cbteTipo}/{cbteNro}/nota-credito", null, idempotencyKey, ct);

    /// <summary>Duplica un comprobante con fecha de hoy.</summary>
    public Task<ResultadoEmision> DuplicarAsync(int ptoVta, int cbteTipo, long cbteNro, string? idempotencyKey = null, CancellationToken ct = default)
        => ConClave($"api/v1/comprobantes/{ptoVta}/{cbteTipo}/{cbteNro}/duplicar", null, idempotencyKey, ct);

    /// <summary>Emite un lote; cada ítem lleva su propia clave (se generan si faltan) y su resultado individual.</summary>
    public Task<JsonElement> EmitirLoteAsync(IEnumerable<(int PtoVta, int CbteTipo, ComprobanteDetalle Comprobante, string? IdempotencyKey)> items, CancellationToken ct = default)
    {
        var lista = items.Select(i => new { idempotencyKey = i.IdempotencyKey ?? Guid.NewGuid().ToString(), ptoVta = i.PtoVta, cbteTipo = i.CbteTipo, comprobante = i.Comprobante }).ToList();
        return EnviarAsync<JsonElement>(() => new HttpRequestMessage(HttpMethod.Post, "api/v1/comprobantes/lote")
            { Content = JsonContent.Create(new { items = lista }, options: Json) }, ct)!;
    }

    private Task<ResultadoEmision> ConClave(string url, object? cuerpo, string? idempotencyKey, CancellationToken ct)
    {
        var clave = idempotencyKey ?? Guid.NewGuid().ToString();
        return EnviarAsync<ResultadoEmision>(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (cuerpo != null) req.Content = JsonContent.Create(cuerpo, options: Json);
            req.Headers.Add("Idempotency-Key", clave);
            return req;
        }, ct)!;
    }

    /// <summary>Calcula importes coherentes a partir de líneas.</summary>
    public Task<ComprobanteDetalle> CalcularAsync(SolicitudCalculo s, CancellationToken ct = default) =>
        EnviarAsync<ComprobanteDetalle>(() => new HttpRequestMessage(HttpMethod.Post, "api/v1/comprobantes/calcular")
            { Content = JsonContent.Create(s, options: Json) }, ct)!;

    private async Task<T?> EnviarAsync<T>(Func<HttpRequestMessage> crear, CancellationToken ct)
    {
        for (var intento = 0; ; intento++)
        {
            HttpResponseMessage resp;
            try
            {
                using var req = crear();
                var corr = CorrelationId ?? System.Diagnostics.Activity.Current?.TraceId.ToString();
                if (!string.IsNullOrEmpty(corr)) req.Headers.TryAddWithoutValidation("X-Correlation-Id", corr);
                if (_secretoFirma != null)
                {
                    var cuerpo = req.Content == null ? Array.Empty<byte>() : await req.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    var t = _reloj.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    req.Headers.Add("X-Firma-Tiempo", t);
                    // Se firma exactamente el destino que viaja en la línea de solicitud HTTP: ruta + query ya codificadas
                    // (Uri.PathAndQuery de la URI absoluta, que es lo que envía HttpClient), incluido el prefijo de BaseAddress.
                    // El servicio verifica contra el destino crudo recibido, sin decodificar.
                    var destino = req.RequestUri!.IsAbsoluteUri ? req.RequestUri
                        : _http.BaseAddress is { } b ? new Uri(b, req.RequestUri) : null;
                    var ruta = destino?.PathAndQuery ?? "/" + req.RequestUri.OriginalString.TrimStart('/');
                    req.Headers.Add("X-Firma", Firmar(_secretoFirma, t, req.Method.Method, ruta, cuerpo));
                }
                resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested)) && intento < _reintentos)
            {
                await Task.Delay(Espera(intento, null), ct).ConfigureAwait(false);
                continue;
            }

            using (resp)
            {
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);

                if ((resp.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests or HttpStatusCode.GatewayTimeout)
                    && intento < _reintentos)
                {
                    await Task.Delay(Espera(intento, resp.Headers.RetryAfter?.Delta), ct).ConfigureAwait(false);
                    continue;
                }
                throw await ErrorAsync(resp, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Firma HMAC-SHA256 v2 compatible con el servicio: hex(HMAC(secreto, "v2\n" + tiempo + "\n" + MÉTODO + "\n" + ruta?query + "\n" + cuerpo)).</summary>
    public static string Firmar(string secreto, string tiempo, string metodo, string rutaYQuery, byte[] cuerpo)
    {
        var datos = System.Text.Encoding.UTF8.GetBytes($"v2\n{tiempo}\n{metodo.ToUpperInvariant()}\n{rutaYQuery}\n").Concat(cuerpo).ToArray();
        return Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secreto), datos)).ToLowerInvariant();
    }

    private TimeSpan Espera(int intento, TimeSpan? retryAfter)
    {
        var t = retryAfter ?? TimeSpan.FromMilliseconds(500 * Math.Pow(2, intento));
        return t > _esperaMaxima ? _esperaMaxima : t;
    }

    private static async Task<ErrorFacturacionException> ErrorAsync(HttpResponseMessage r, CancellationToken ct)
    {
        string titulo = r.ReasonPhrase ?? "Error";
        var errores = new List<string>();
        string? trace = null;
        try
        {
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) titulo = d.GetString()!;
            else if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) titulo = t.GetString()!;
            if (root.TryGetProperty("errores", out var e) && e.ValueKind == JsonValueKind.Array)
                errores.AddRange(e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.ToString()));
            if (root.TryGetProperty("traceId", out var tr)) trace = tr.GetString();
        }
        catch (JsonException) { }
        return new ErrorFacturacionException(r.StatusCode, titulo, errores, trace);
    }
}
