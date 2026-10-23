using System.Collections.Concurrent;
using Greenhaus.Api.Infrastructure;

namespace Greenhaus.Api.Tests;

/// <summary>An in-memory Pulse that knows the few rules the service relies on: claim needs a shipped device and the right token.</summary>
public sealed class FakePulse : IPulseClient
{
    public sealed record Entry(PulseDevice Device, string Token, string? Owner = null);

    private readonly ConcurrentDictionary<string, Entry> _devices = new();
    private readonly ConcurrentDictionary<(string, string), (double, DateTimeOffset)> _readings = new();

    public ConcurrentDictionary<string, IReadOnlyList<PulseRule>> Rules { get; } = new();
    public ConcurrentBag<(string Device, string Actuator, int Seconds)> Runs { get; } = [];
    public ConcurrentBag<(string Device, bool Enabled)> KillSwitches { get; } = [];
    public ConcurrentDictionary<(string, string), (double, double)> Calibrations { get; } = new();
    public bool Down { get; set; }

    private readonly ConcurrentDictionary<(string, string), IReadOnlyList<PulsePoint>> _series = new();

    /// <summary>What the series of a channel returns, whatever the window.</summary>
    public void Series(string device, string channel, params PulsePoint[] points) => _series[(device, channel)] = points;

    public static PulseDevice Kit(string id, string state = "shipped") => new(id, "greenhaus-pot-v1", state, null, "online", DateTimeOffset.UtcNow, "1.0.0",
        [new("soil", "soil_moisture", null, "%"), new("temp", "temperature", null, "C"), new("tank", "tank_level", null, "%")],
        [new("pump", "pump", new PulseHardLimits(60, 600, 300), null), new("heater", "heater", new PulseHardLimits(1800, 300, 14400), true)]);

    public void Add(PulseDevice device, string token) => _devices[device.Id] = new Entry(device, token);

    public void Reading(string device, string channel, double value, DateTimeOffset at) => _readings[(device, channel)] = (value, at);

    private void Check() { if (Down) throw new PulseException(503, "pulse_unavailable", "down"); }

    public Task<PulseDevice?> GetDeviceAsync(string deviceId, CancellationToken ct)
    {
        Check();
        return Task.FromResult(_devices.TryGetValue(deviceId, out var e) ? e.Device : null);
    }

    public Task<PulseDevice> ClaimAsync(string deviceId, string token, string ownerId, CancellationToken ct)
    {
        Check();
        if (!_devices.TryGetValue(deviceId, out var e)) throw new PulseException(404, "not_found", "Not found");
        if (e.Device.State != "shipped") throw new PulseException(409, "invalid_state", "The device is not shipped");
        if (e.Token != token) throw new PulseException(409, "token_invalid", "The token does not match");
        var claimed = e.Device with { State = "claimed", OwnerId = ownerId };
        _devices[deviceId] = e with { Device = claimed, Owner = ownerId };
        return Task.FromResult(claimed);
    }

    public Task SetRulesAsync(string deviceId, IReadOnlyList<PulseRule> rules, CancellationToken ct)
    {
        Check();
        Rules[deviceId] = rules;
        return Task.CompletedTask;
    }

    public Task CalibrateAsync(string deviceId, string channelId, double dryRaw, double wetRaw, CancellationToken ct)
    {
        Check();
        Calibrations[(deviceId, channelId)] = (dryRaw, wetRaw);
        return Task.CompletedTask;
    }

    public Task<PulseCommand> RunActuatorAsync(string deviceId, string actuatorId, int seconds, Guid commandId, DateTimeOffset expiresAt, CancellationToken ct)
    {
        Check();
        Runs.Add((deviceId, actuatorId, seconds));
        return Task.FromResult(new PulseCommand(commandId, "queued"));
    }

    public Task SetKillSwitchAsync(string deviceId, string actuatorId, bool enabled, Guid commandId, DateTimeOffset expiresAt, CancellationToken ct)
    {
        Check();
        KillSwitches.Add((deviceId, enabled));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PulsePoint>> SeriesAsync(string deviceId, string channelId, DateTimeOffset from, DateTimeOffset to, string resolution, CancellationToken ct)
    {
        Check();
        return Task.FromResult(_series.TryGetValue((deviceId, channelId), out var points) ? points : []);
    }

    public Task<(double Value, DateTimeOffset At)?> LatestAsync(string deviceId, string channelId, DateTimeOffset since, CancellationToken ct)
    {
        Check();
        return Task.FromResult(_readings.TryGetValue((deviceId, channelId), out var r) && r.Item2 >= since ? ((double, DateTimeOffset)?)(r.Item1, r.Item2) : null);
    }
}