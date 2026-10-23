using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.Extensions.Options;

namespace Greenhaus.Api.Infrastructure;

public sealed class PulseOptions
{
    /// <summary>Base address of the Pulse device API, for example http://pulse:8080/api/pulse/v2. Empty means Pulse is not configured.</summary>
    public string? BaseUrl { get; set; }
    /// <summary>A fixed bearer token, for local runs against a mock. Otherwise the service uses client credentials.</summary>
    public string? StaticToken { get; set; }
    public string? TokenUrl { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

public sealed record PulseChannel(string Id, string Metric, string? Label, string? Unit);
public sealed record PulseHardLimits(int MaxRunSeconds, int MinIntervalSeconds, int DailyCapSeconds);
public sealed record PulseActuator(string Id, string Kind, PulseHardLimits HardLimits, bool? ThermalCutoff);
public sealed record PulseDevice(string Id, string Model, string State, string? OwnerId, string? Connection, DateTimeOffset? LastSeenAt, string? Firmware,
    IReadOnlyList<PulseChannel> Channels, IReadOnlyList<PulseActuator> Actuators);
public sealed record PulseRule(string ChannelId, string Kind, double? Min, double? Max, double? Margin, int? TimeoutSeconds);
public sealed record PulseCommand(Guid CommandId, string Status);
/// <summary>One point of a series: the reading for raw, the average of the bucket (with its minimum and maximum) for 1m and 1h.</summary>
public sealed record PulsePoint(DateTimeOffset At, double Value, double? Min, double? Max);

/// <summary>A refusal from Pulse, with the stable code it gave. Callers translate it for the user.</summary>
public sealed class PulseException(int status, string code, string title) : Exception(title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>What Greenhaus needs from the device platform. The HTTP client talks to Pulse, tests use an in-memory fake.</summary>
public interface IPulseClient
{
    /// <summary>Null when Pulse does not know the device.</summary>
    Task<PulseDevice?> GetDeviceAsync(string deviceId, CancellationToken ct);
    Task<PulseDevice> ClaimAsync(string deviceId, string token, string ownerId, CancellationToken ct);
    Task SetRulesAsync(string deviceId, IReadOnlyList<PulseRule> rules, CancellationToken ct);
    Task CalibrateAsync(string deviceId, string channelId, double dryRaw, double wetRaw, CancellationToken ct);
    Task<PulseCommand> RunActuatorAsync(string deviceId, string actuatorId, int seconds, Guid commandId, DateTimeOffset expiresAt, CancellationToken ct);
    Task SetKillSwitchAsync(string deviceId, string actuatorId, bool enabled, Guid commandId, DateTimeOffset expiresAt, CancellationToken ct);
    /// <summary>The newest reading of a channel since the given time, or null when there is none.</summary>
    Task<(double Value, DateTimeOffset At)?> LatestAsync(string deviceId, string channelId, DateTimeOffset since, CancellationToken ct);
    /// <summary>The readings of one channel between two moments at a resolution (raw, 1m or 1h), oldest first. Empty when there are none.</summary>
    Task<IReadOnlyList<PulsePoint>> SeriesAsync(string deviceId, string channelId, DateTimeOffset from, DateTimeOffset to, string resolution, CancellationToken ct);
}

public sealed class HttpPulseClient(HttpClient http) : IPulseClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public async Task<PulseDevice?> GetDeviceAsync(string deviceId, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Get, $"devices/{Uri.EscapeDataString(deviceId)}", null, ct, allowNotFound: true);
        return response is null ? null : Map(await Read<DeviceWire>(response, ct));
    }

    public async Task<PulseDevice> ClaimAsync(string deviceId, string token, string ownerId, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Post, $"devices/{Uri.EscapeDataString(deviceId)}/claim", new { token, ownerId }, ct);
        return Map(await Read<DeviceWire>(response!, ct));
    }

    public async Task SetRulesAsync(string deviceId, IReadOnlyList<PulseRule> rules, CancellationToken ct)
    {
        using var _ = await Send(HttpMethod.Put, $"devices/{Uri.EscapeDataString(deviceId)}/rules", rules, ct);
    }

    public async Task CalibrateAsync(string deviceId, string channelId, double dryRaw, double wetRaw, CancellationToken ct)
    {
        using var _ = await Send(HttpMethod.Put, $"devices/{Uri.EscapeDataString(deviceId)}/channels/{Uri.EscapeDataString(channelId)}/calibration", new { dryRaw, wetRaw }, ct);
    }

    public async Task<PulseCommand> RunActuatorAsync(string deviceId, string actuatorId, int seconds, Guid commandId, DateTimeOffset expiresAt, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Post, $"devices/{Uri.EscapeDataString(deviceId)}/commands",
            new { commandId, type = "run_actuator", actuatorId, durationSeconds = seconds, expiresAt }, ct);
        var command = await Read<CommandWire>(response!, ct);
        return new PulseCommand(command.CommandId, command.Status);
    }

    public async Task SetKillSwitchAsync(string deviceId, string actuatorId, bool enabled, Guid commandId, DateTimeOffset expiresAt, CancellationToken ct)
    {
        using var _ = await Send(HttpMethod.Post, $"devices/{Uri.EscapeDataString(deviceId)}/commands",
            new { commandId, type = "set_kill_switch", actuatorId, enabled, expiresAt }, ct);
    }

    public async Task<(double Value, DateTimeOffset At)?> LatestAsync(string deviceId, string channelId, DateTimeOffset since, CancellationToken ct)
    {
        var query = $"from={Uri.EscapeDataString(since.UtcDateTime.ToString("O"))}&resolution=raw";
        using var response = await Send(HttpMethod.Get, $"devices/{Uri.EscapeDataString(deviceId)}/channels/{Uri.EscapeDataString(channelId)}/series?{query}", null, ct, allowNotFound: true);
        if (response is null) return null;
        var points = await Read<List<PointWire>>(response, ct);
        return points.Count == 0 ? null : (points[^1].Value, points[^1].Ts);
    }

    public async Task<IReadOnlyList<PulsePoint>> SeriesAsync(string deviceId, string channelId, DateTimeOffset from, DateTimeOffset to, string resolution, CancellationToken ct)
    {
        var query = $"from={Uri.EscapeDataString(from.UtcDateTime.ToString("O"))}&to={Uri.EscapeDataString(to.UtcDateTime.ToString("O"))}&resolution={Uri.EscapeDataString(resolution)}";
        using var response = await Send(HttpMethod.Get, $"devices/{Uri.EscapeDataString(deviceId)}/channels/{Uri.EscapeDataString(channelId)}/series?{query}", null, ct, allowNotFound: true);
        if (response is null) return [];
        return [.. (await Read<List<PointWire>>(response, ct)).Select(p => new PulsePoint(p.Ts, p.Value, p.Min, p.Max))];
    }

    private async Task<HttpResponseMessage?> Send(HttpMethod method, string path, object? body, CancellationToken ct, bool allowNotFound = false)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException e) { throw new PulseException(503, "pulse_unavailable", e.Message); }

        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
            ProblemWire? problem = null;
            try { problem = await response.Content.ReadFromJsonAsync<ProblemWire>(Json, ct); }
            catch (JsonException) { /* an empty or foreign body: not a problem document */ }
            // A 404 without a problem document is a route Pulse does not have (it is older than the contract), not an unknown device.
            if (response.StatusCode == HttpStatusCode.NotFound && problem?.Code is null)
                throw new PulseException(501, "pulse_not_supported", "This Pulse does not offer that operation yet");
            throw new PulseException((int)response.StatusCode, problem?.Code ?? "pulse_error", problem?.Title ?? response.ReasonPhrase ?? "Pulse refused the request");
        }
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new PulseException(502, "pulse_error", "Pulse sent an empty answer");

    private static PulseDevice Map(DeviceWire d) => new(d.Id, d.Model, d.State, d.OwnerId, d.Connection, d.LastSeenAt, d.Firmware,
        [.. d.Capabilities.Channels.Select(c => new PulseChannel(c.Id, c.Metric, c.Label, c.Unit))],
        [.. d.Capabilities.Actuators.Select(a => new PulseActuator(a.Id, a.Kind, new PulseHardLimits(a.HardLimits.MaxRunSeconds, a.HardLimits.MinIntervalSeconds, a.HardLimits.DailyCapSeconds), a.ThermalCutoff))]);

    private sealed record DeviceWire(string Id, string Model, string State, string? OwnerId, string? Connection, DateTimeOffset? LastSeenAt, string? Firmware, CapabilitiesWire Capabilities);
    private sealed record CapabilitiesWire(List<ChannelWire> Channels, List<ActuatorWire> Actuators);
    private sealed record ChannelWire(string Id, string Metric, string? Label, string? Unit);
    private sealed record ActuatorWire(string Id, string Kind, PulseHardLimits HardLimits, bool? ThermalCutoff);
    private sealed record CommandWire(Guid CommandId, string Status);
    private sealed record PointWire(DateTimeOffset Ts, double Value, double? Min = null, double? Max = null);
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
        if (await tokens.GetAsync(ct) is { } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct);
    }
}
/// <summary>Readings from Pulse. A channel that cannot be read is reported as having no reading, not as a failure of the whole status.</summary>
public sealed class PulseReadingSource(IPulseClient pulse, TimeProvider clock, ILogger<PulseReadingSource> log) : IReadingSource
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public async Task<IReadOnlyList<ChannelReading>> LatestAsync(IReadOnlyCollection<ChannelEntity> channels, CancellationToken ct)
    {
        var since = clock.GetUtcNow() - Window;
        var tasks = channels.Select(async channel =>
        {
            try
            {
                return await pulse.LatestAsync(channel.DeviceId, channel.ChannelId, since, ct) is { } latest
                    ? new ChannelReading(channel.DeviceId, channel.ChannelId, latest.Value, latest.At) : null;
            }
            catch (PulseException e)
            {
                log.LogWarning("No reading for {Device}/{Channel}: {Code}", channel.DeviceId, channel.ChannelId, e.Code);
                return null;
            }
        });
        return [.. (await Task.WhenAll(tasks)).Where(r => r is not null).Select(r => r!)];
    }
}

public sealed class PulseCommandSender(IPulseClient pulse, TimeProvider clock) : ICommandSender
{
    public async Task<Guid> RunAsync(string deviceId, string actuatorId, int seconds, CancellationToken ct) =>
        (await pulse.RunActuatorAsync(deviceId, actuatorId, seconds, Guid.NewGuid(), clock.GetUtcNow().AddMinutes(2), ct)).CommandId;
}

/// <summary>Turns what the pot demands into the alert rules a device enforces.</summary>
public static class RuleBuilder
{
    public const int NoDataSeconds = 900;

    public static List<PulseRule> For(IEnumerable<ChannelEntity> channels, IReadOnlyDictionary<Metric, RequirementEntity> requirements)
    {
        var rules = new List<PulseRule>();
        foreach (var channel in channels.Where(c => c.Active))
        {
            if (requirements.TryGetValue(channel.Metric, out var r) && r.Enabled && (r.Min is not null || r.Max is not null))
                rules.Add(new PulseRule(channel.ChannelId, "threshold", r.Min, r.Max, r.Margin, null));
            rules.Add(new PulseRule(channel.ChannelId, "no_data", null, null, null, NoDataSeconds));
        }
        return rules;
    }
}