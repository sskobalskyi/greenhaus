using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Greenhaus.MockServer.Pulse;

public class DeviceState
{
    public required Device Device { get; set; }
    public string? ClaimTokenHash { get; set; }
    public DateTimeOffset ClaimTokenExpiresAt { get; set; }
    public string? CredentialHash { get; set; }
    public List<Rule> Rules { get; set; } = [];
    public List<Command> Commands { get; set; } = [];
    public Dictionary<Guid, string> CommandFingerprints { get; set; } = [];
    public Dictionary<Guid, DateTimeOffset> CommandExpiry { get; set; } = [];
    public Dictionary<Guid, int> CommandSeconds { get; set; } = [];
    public bool KillSwitch { get; set; }
}

/// <summary>
/// The device registry of Pulse as the contract describes it: lifecycle, one-time claim token, claim rate
/// limits, rules, commands that expire, and series. Devices are never connected to anything real: a command
/// is carried out by a simulated controller that obeys the hard limits it declared.
/// </summary>
public partial class PulseStore(MockClock clock)
{
    public static readonly string[] States = ["allocated", "shipped", "claimed", "active", "retired"];
    public static readonly string[] Metrics = ["temperature", "air_humidity", "soil_moisture", "ph", "light", "airflow", "tank_level", "overflow", "flow"];
    public static readonly string[] ActuatorKinds = ["pump", "heater", "lamp", "fan", "humidifier"];
    public static readonly string[] CommandTypes = ["run_actuator", "stop_actuator", "set_kill_switch"];

    public static readonly TimeSpan NoDataTimeout = TimeSpan.FromSeconds(60);
    public const int ClaimsPerDevicePerMinute = 5;
    public const int ClaimsPerCallerPerMinute = 30;

    [GeneratedRegex("^[A-Za-z0-9._-]+$")] private static partial Regex IdPattern();
    [GeneratedRegex("^(sha256:)?[0-9A-Fa-f]{64}$")] private static partial Regex HashPattern();

    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceState> _devices = [];
    private readonly Dictionary<string, List<DateTimeOffset>> _claimAttempts = [];

    public object Sync => _gate;

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    // ---- seed ---------------------------------------------------------------------------------------------

    public void Reset()
    {
        lock (_gate)
        {
            _devices.Clear();
            _claimAttempts.Clear();
            var now = clock.UtcNow;

            Add("ghx-pot-kitchen", "Greenhaus Pot Controller P1", "active", "demo-user", Kitchen(), now);
            Add("ghx-bed-balcony", "Greenhaus Bed Controller B4", "active", "demo-user", Balcony(), now);
            Add("ghx-demo-0001", "Greenhaus Pot Controller P1", "shipped", null, Kit(), now, "demo-token", now.AddDays(30));
            Add("ghx-demo-0002", "Greenhaus Pot Controller P1", "allocated", null, Kit(), now, "demo-token-2", now.AddDays(30));
            Add("ghx-demo-0003", "Greenhaus Pot Controller P1", "shipped", null, Kit(), now.AddDays(-40), "expired-token", now.AddDays(-10));
        }
    }

    private static PChannel C(string id, string metric, string unit, string? label = null) => new() { Id = id, Metric = metric, Unit = unit, Label = label };

    private static ActuatorCapability A(string id, string kind, int run, int interval, int cap, bool? cutoff = null) => new()
    {
        Id = id, Kind = kind, ThermalCutoff = cutoff,
        HardLimits = new HardLimits { MaxRunSeconds = run, MinIntervalSeconds = interval, DailyCapSeconds = cap },
    };

    private static Capabilities Kitchen() => new()
    {
        Channels = [C("temperature_z0", "temperature", "°C"), C("air_humidity_z0", "air_humidity", "%"), C("soil_moisture_z0", "soil_moisture", "%"), C("light_z0", "light", "lux")],
        Actuators = [A("pump-1", "pump", 60, 600, 600), A("lamp-1", "lamp", 3600, 0, 43200)],
    };

    private static Capabilities Balcony() => new()
    {
        Channels =
        [
            C("temperature_z1", "temperature", "°C"),
            .. Enumerable.Range(1, 4).Select(i => C($"soil_moisture_z{i}", "soil_moisture", "%", $"Zone {i}")),
            .. Enumerable.Range(1, 4).Select(i => C($"ph_z{i}", "ph", "pH", $"Zone {i}")),
        ],
        Actuators = [A("pump-1", "pump", 60, 600, 600), A("heater-1", "heater", 1800, 300, 14400, cutoff: false)],
    };

    private static Capabilities Kit() => new()
    {
        Channels = [C("soil_moisture_c1", "soil_moisture", "%"), C("temperature_c1", "temperature", "°C")],
        Actuators = [A("pump-1", "pump", 60, 600, 600)],
    };

    private void Add(string id, string model, string state, string? owner, Capabilities capabilities, DateTimeOffset at, string? token = null, DateTimeOffset? tokenExpires = null)
    {
        var device = new Device
        {
            Id = id, Model = model, State = state, OwnerId = owner, Capabilities = capabilities, Firmware = "0.3.1",
            Connection = state == "active" ? "online" : "unknown", LastSeenAt = state == "active" ? at.AddSeconds(-4) : null,
        };
        _devices[id] = new DeviceState
        {
            Device = device,
            ClaimTokenHash = token is null ? null : Hash(token),
            ClaimTokenExpiresAt = tokenExpires ?? at,
            CredentialHash = "$argon2id$v=19$m=65536,t=3,p=4$mock$mock",
        };
    }

    // ---- visibility -----------------------------------------------------------------------------------------

    /// <summary>A device is visible to a service caller always, and to a user only if they own it. Otherwise it does not exist.</summary>
    public DeviceState? Find(string id, Caller caller)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(id, out var state)) return null;
            if (!caller.IsService && state.Device.OwnerId != caller.Subject) return null;
            Refresh(state);
            return state;
        }
    }

    public IReadOnlyList<Device> List(Caller caller, string? state, string? ownerId)
    {
        lock (_gate)
        {
            foreach (var d in _devices.Values) Refresh(d);
            return [.. _devices.Values
                .Where(d => caller.IsService || d.Device.OwnerId == caller.Subject)
                .Where(d => state is null || d.Device.State == state)
                .Where(d => ownerId is null || !caller.IsService || d.Device.OwnerId == ownerId)
                .Select(d => d.Device)
                .OrderBy(d => d.Id, StringComparer.Ordinal)];
        }
    }

    /// <summary>Connection comes from how recently the device reported. Commands that ran out of time expire.</summary>
    private void Refresh(DeviceState state)
    {
        var now = clock.UtcNow;
        var device = state.Device;
        if (device.State == "retired") device.Connection = "offline";
        else if (device.LastSeenAt is { } seen) device.Connection = now - seen <= NoDataTimeout ? "online" : "offline";

        foreach (var command in state.Commands.Where(c => c.Status is "queued" or "sent" or "accepted"))
            Progress(state, command, now);
    }

    // ---- registration and lifecycle -------------------------------------------------------------------------

    public (Device? Device, bool Created, List<string> Problems) Register(DeviceRegistration body)
    {
        var problems = new List<string>();
        if (string.IsNullOrEmpty(body.Id)) problems.Add("id is required");
        else
        {
            if (!IdPattern().IsMatch(body.Id)) problems.Add("id may only contain letters, digits, '.', '_' and '-'");
            if (body.Id.Length > 64) problems.Add("id may have at most 64 characters");
        }
        if (string.IsNullOrWhiteSpace(body.Model)) problems.Add("model is required");
        if (string.IsNullOrWhiteSpace(body.CredentialHash)) problems.Add("credentialHash is required");
        if (string.IsNullOrWhiteSpace(body.ClaimTokenHash) || !HashPattern().IsMatch(body.ClaimTokenHash))
            problems.Add("claimTokenHash must be a SHA-256 as 64 hexadecimal characters, optionally prefixed with 'sha256:'");
        if (body.ClaimTokenExpiresAt is null) problems.Add("claimTokenExpiresAt is required");
        else if (body.ClaimTokenExpiresAt <= clock.UtcNow) problems.Add("claimTokenExpiresAt must be in the future");

        var channels = body.Capabilities?.Channels;
        var actuators = body.Capabilities?.Actuators;
        if (channels is null || actuators is null) problems.Add("capabilities must list channels and actuators");
        foreach (var channel in channels ?? [])
        {
            if (string.IsNullOrEmpty(channel.Id) || !IdPattern().IsMatch(channel.Id)) problems.Add($"channel id '{channel.Id}' may only contain letters, digits, '.', '_' and '-'");
            if (channel.Metric is null || !Metrics.Contains(channel.Metric)) problems.Add($"channel '{channel.Id}' has an unknown metric '{channel.Metric}'");
        }
        foreach (var duplicate in (channels ?? []).GroupBy(c => c.Id).Where(g => g.Count() > 1)) problems.Add($"channel id '{duplicate.Key}' is used more than once");
        foreach (var actuator in actuators ?? [])
        {
            if (string.IsNullOrEmpty(actuator.Id) || !IdPattern().IsMatch(actuator.Id)) problems.Add($"actuator id '{actuator.Id}' may only contain letters, digits, '.', '_' and '-'");
            if (actuator.Kind is null || !ActuatorKinds.Contains(actuator.Kind)) problems.Add($"actuator '{actuator.Id}' has an unknown kind '{actuator.Kind}'");
            var limits = actuator.HardLimits;
            if (limits?.MaxRunSeconds is not > 0 || limits.MinIntervalSeconds is not >= 0 || limits.DailyCapSeconds is not > 0)
                problems.Add($"actuator '{actuator.Id}' needs hardLimits with maxRunSeconds, minIntervalSeconds and dailyCapSeconds");
            else if (limits.MaxRunSeconds > limits.DailyCapSeconds)
                problems.Add($"actuator '{actuator.Id}': maxRunSeconds must not exceed dailyCapSeconds");
        }
        if (problems.Count > 0) return (null, false, problems);

        lock (_gate)
        {
            if (_devices.TryGetValue(body.Id!, out var existing)) return (existing.Device, false, problems);
            var device = new Device
            {
                Id = body.Id!, Model = body.Model!, State = "allocated", Connection = "unknown",
                Capabilities = new Capabilities { Channels = body.Capabilities!.Channels, Actuators = body.Capabilities.Actuators },
            };
            _devices[device.Id] = new DeviceState
            {
                Device = device,
                ClaimTokenHash = body.ClaimTokenHash![(body.ClaimTokenHash.StartsWith("sha256:", StringComparison.Ordinal) ? 7 : 0)..].ToLowerInvariant(),
                ClaimTokenExpiresAt = body.ClaimTokenExpiresAt!.Value,
                CredentialHash = body.CredentialHash,
            };
            return (device, true, problems);
        }
    }

    public (Device? Device, string? Error) Ship(DeviceState state)
    {
        lock (_gate)
        {
            if (state.Device.State == "shipped") return (state.Device, null);
            if (state.Device.State != "allocated") return (null, "invalid_state");
            state.Device.State = "shipped";
            return (state.Device, null);
        }
    }

    public (Device? Device, string? Error, int? RetryAfter) Claim(DeviceState state, ClaimRequest body, Caller caller)
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            // Every attempt counts, whether it succeeds or not.
            var retry = CountAttempt($"device:{state.Device.Id}", ClaimsPerDevicePerMinute, now) ?? CountAttempt($"caller:{caller.Subject}", ClaimsPerCallerPerMinute, now);
            if (retry is not null) return (null, "rate_limited", retry);

            if (state.Device.State != "shipped") return (null, "invalid_state", null);
            var given = Hash(body.Token!);
            if (state.ClaimTokenHash is null || !string.Equals(state.ClaimTokenHash, given, StringComparison.OrdinalIgnoreCase)) return (null, "token_invalid", null);
            if (state.ClaimTokenExpiresAt <= now) return (null, "token_expired", null);

            state.Device.State = "claimed";
            state.Device.OwnerId = body.OwnerId;
            state.ClaimTokenHash = null; // The token works once.
            return (state.Device, null, null);
        }
    }

    private int? CountAttempt(string key, int limit, DateTimeOffset now)
    {
        if (!_claimAttempts.TryGetValue(key, out var attempts)) _claimAttempts[key] = attempts = [];
        attempts.RemoveAll(a => now - a > TimeSpan.FromMinutes(1));
        if (attempts.Count >= limit) return Math.Max(1, (int)Math.Ceiling((attempts[0] + TimeSpan.FromMinutes(1) - now).TotalSeconds));
        attempts.Add(now);
        return null;
    }

    public Device Retire(DeviceState state)
    {
        lock (_gate)
        {
            state.Device.State = "retired";
            state.Device.Connection = "offline";
            state.ClaimTokenHash = null;
            state.CredentialHash = null;
            foreach (var command in state.Commands.Where(c => c.Status is "queued" or "sent" or "accepted"))
            {
                command.Status = "failed";
                command.Reason = "device_retired";
                command.FinishedAt = clock.UtcNow;
            }
            return state.Device;
        }
    }

    // ---- mock-only: the device reports, or goes quiet -----------------------------------------------------------

    /// <summary>The first reading moves a claimed device to active, and any reading makes it online.</summary>
    public Device? SimulateReading(string id)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(id, out var state) || state.Device.State == "retired") return null;
            state.Device.LastSeenAt = clock.UtcNow;
            state.Device.Connection = "online";
            if (state.Device.State == "claimed") state.Device.State = "active";
            return state.Device;
        }
    }

    public Device? SimulateSilence(string id)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(id, out var state)) return null;
            state.Device.LastSeenAt = clock.UtcNow - NoDataTimeout - TimeSpan.FromSeconds(1);
            state.Device.Connection = "offline";
            return state.Device;
        }
    }
}

/// <summary>Who is calling. The mock reads it from X-Mock-Caller: "service" (default) or "user:&lt;subject&gt;".</summary>
public sealed record Caller(bool IsService, string Subject)
{
    public static Caller From(HttpContext context)
    {
        var header = context.Request.Headers["X-Mock-Caller"].FirstOrDefault();
        return header is { } h && h.StartsWith("user:", StringComparison.OrdinalIgnoreCase) && h.Length > 5
            ? new Caller(false, h[5..])
            : new Caller(true, "service");
    }
}
