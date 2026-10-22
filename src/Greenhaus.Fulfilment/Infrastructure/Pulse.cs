using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Greenhaus.Fulfilment.Infrastructure;

/// <summary>A refusal from Pulse, with the stable code it gave. A 404, a 5xx and an unreachable Pulse are tried again, the rest are not.</summary>
public sealed class PulseException(int status, string code, string title) : Exception(title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public bool Retriable => Status is 404 or 408 or 429 or >= 500;
}

/// <summary>What fulfilment tells Pulse after it has announced a device: it can be claimed now, or it will never be.</summary>
public interface IPulseDevices
{
    /// <summary>`allocated` to `shipped`. Repeating it is harmless in Pulse.</summary>
    Task ShipAsync(string deviceId, CancellationToken ct);
    /// <summary>Allowed from any state. Repeating it is harmless in Pulse.</summary>
    Task RetireAsync(string deviceId, CancellationToken ct);
}

public sealed class HttpPulseDevices(HttpClient http) : IPulseDevices
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task ShipAsync(string deviceId, CancellationToken ct) => PostAsync($"devices/{Uri.EscapeDataString(deviceId)}/ship", ct);

    public Task RetireAsync(string deviceId, CancellationToken ct) => PostAsync($"devices/{Uri.EscapeDataString(deviceId)}/retire", ct);

    private async Task PostAsync(string path, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await http.PostAsync(path, content: null, ct); }
        catch (HttpRequestException e) { throw new PulseException(503, "pulse_unavailable", e.Message); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new PulseException(408, "pulse_timeout", e.Message); }

        using (response)
        {
            if (response.IsSuccessStatusCode) return;
            ProblemWire? problem = null;
            try { problem = await response.Content.ReadFromJsonAsync<ProblemWire>(Json, ct); }
            catch (JsonException) { /* an empty or foreign body is not a problem document */ }
            var code = problem?.Code ?? (response.StatusCode == HttpStatusCode.NotFound ? "not_found" : "pulse_error");
            throw new PulseException((int)response.StatusCode, code, problem?.Title ?? response.ReasonPhrase ?? "Pulse refused the request");
        }
    }

    private sealed record ProblemWire(string? Title, string? Code);
}

/// <summary>The bearer token Pulse expects: a fixed one for local runs, otherwise a client-credentials token cached until shortly before it expires.</summary>
public sealed class PulseTokens(IOptions<PulseOptions> options, IHttpClientFactory factory, TimeProvider clock)
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
            using var client = factory.CreateClient("pulse-token");
            using var response = await client.PostAsync(o.TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = o.ClientId ?? "", ["client_secret"] = o.ClientSecret ?? "",
            }), ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _token = json.RootElement.GetProperty("access_token").GetString();
            // Renewed a minute early so a token never expires in flight.
            _expires = clock.GetUtcNow().AddSeconds(Math.Max(json.RootElement.GetProperty("expires_in").GetInt32() - 60, 10));
            return _token;
        }
        finally { _gate.Release(); }
    }
}

public sealed class PulseTokenHandler(PulseTokens tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            if (await tokens.GetAsync(ct) is { } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        catch (HttpRequestException e) { throw new PulseException(503, "pulse_token_unavailable", e.Message); }
        return await base.SendAsync(request, ct);
    }
}