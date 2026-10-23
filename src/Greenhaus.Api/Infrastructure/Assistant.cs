using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Greenhaus.Api.Infrastructure;

public sealed class DocsBrainOptions
{
    /// <summary>Base address of Docs-Brain. Nothing is called until a token can be had (static, or client credentials).</summary>
    public string BaseUrl { get; set; } = "http://host.docker.internal:5090";
    /// <summary>A fixed bearer token, for local runs. Otherwise the service asks Keycloak with client credentials.</summary>
    public string? StaticToken { get; set; }
    public string? TokenUrl { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

/// <summary>Docs-Brain cannot be reached, refused the call or broke off. The assistant is then unavailable, never a server error.</summary>
public sealed class AssistantUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The assistant that answers questions. Docs-Brain in production, a fake in tests.</summary>
public interface IAssistantClient
{
    /// <summary>
    /// Asks a question for one tenant with facts about the pot. Yields the JSON of each server-sent event as Docs-Brain sent it
    /// (sources, token, done). Throws <see cref="AssistantUnavailableException"/> when it cannot answer, before or during the stream.
    /// </summary>
    IAsyncEnumerable<string> AskAsync(string tenantId, string question, string context, CancellationToken ct);
}

/// <summary>
/// The bearer token Docs-Brain expects from this service: a fixed one for local runs, otherwise a client-credentials token of
/// the shared realm, cached until shortly before it expires. It follows <see cref="PulseTokens"/>, which does the same for Pulse.
/// </summary>
public sealed class DocsBrainTokens(IOptions<DocsBrainOptions> options, IHttpClientFactory factory, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    public async Task<string?> GetAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!string.IsNullOrEmpty(o.StaticToken)) return o.StaticToken;
        if (string.IsNullOrEmpty(o.TokenUrl)) return null;

        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && clock.GetUtcNow() < _expires) return _token;
            using var client = factory.CreateClient("docsbrain-token");
            using var response = await client.PostAsync(o.TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = o.ClientId ?? "", ["client_secret"] = o.ClientSecret ?? "",
            }), ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _token = json.RootElement.GetProperty("access_token").GetString();
            _expires = clock.GetUtcNow().AddSeconds(Math.Max(json.RootElement.GetProperty("expires_in").GetInt32() - 60, 10));
            return _token;
        }
        finally { _gate.Release(); }
    }
}

public sealed class HttpAssistantClient(HttpClient http, DocsBrainTokens tokens) : IAssistantClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async IAsyncEnumerable<string> AskAsync(string tenantId, string question, string context, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendAsync(tenantId, question, context, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (true)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch (Exception e) when (e is IOException or HttpRequestException) { throw new AssistantUnavailableException("The answer broke off", e); }
            if (line is null) yield break;
            if (line.StartsWith("data:", StringComparison.Ordinal)) yield return line["data:".Length..].Trim();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string tenantId, string question, string context, CancellationToken ct)
    {
        string? token;
        try { token = await tokens.GetAsync(ct); }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException)
        {
            throw new AssistantUnavailableException("No token for the assistant could be had", e);
        }
        if (token is null) throw new AssistantUnavailableException("The assistant is not configured");

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = JsonContent.Create(new { question, context }, options: Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Tenant-Id", tenantId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AssistantUnavailableException("The assistant cannot be reached", e);
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new AssistantUnavailableException($"The assistant refused the call ({status})");
        }
        return response;
    }
}
