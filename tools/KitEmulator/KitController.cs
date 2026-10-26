namespace Greenhaus.KitEmulator;

/// <summary>Where the controller puts its messages. MQTT in the emulator, a list in tests.</summary>
public interface IMessageSink
{
    Task PublishAsync(string topic, string payload, bool retain, int qos, CancellationToken ct = default);
}

public sealed class ControllerOptions
{
    public string Firmware { get; set; } = "1.0.0-emulator";
    public TimeSpan TelemetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The temperature at which the thermal cutoff of the heater trips, as the hardware would.</summary>
    public double ThermalCutoffCelsius { get; set; } = 35;

    /// <summary>A pump does not run when the soil is already this wet.</summary>
    public double OverflowSoilMoisture { get; set; } = 98;

    /// <summary>Pump runs in a row that do not wet the soil before the controller stops trying (empty tank, broken pump).</summary>
    public int MaxFruitlessRuns { get; set; } = 3;
    public double FruitlessGain { get; set; } = 0.5;
}

/// <summary>
/// The firmware of a pot controller, without the transport. It is the last line of defence: whatever the cloud asks, it keeps the
/// hard limits of every actuator, honours the kill switch, never runs a command past its expiry and treats a command it has already
/// seen as done (QoS 1 may deliver it twice). It tells the platform what it does in the messages of device-mqtt.yaml.
/// </summary>
public sealed class KitController
{
    private const int RememberedCommands = 2000;

    private readonly KitDescription _kit;
    private readonly string _deviceId;
    private readonly PlantPhysics _physics;
    private readonly TimeProvider _time;
    private readonly IMessageSink _sink;
    private readonly ControllerOptions _options;
    private readonly Topics _topics;
    private readonly List<Runtime> _runtimes;
    private readonly Dictionary<Guid, Seen> _seen = [];
    private readonly Queue<Guid> _seenOrder = new();
    private readonly Dictionary<string, long> _sequence = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string> _log;

    private DateTimeOffset _lastTick;
    private DateTimeOffset _nextTelemetry;
    private string? _lastStateKey;

    public KitController(KitDescription kit, string deviceId, PlantPhysics physics, TimeProvider time, IMessageSink sink, ControllerOptions? options = null, Action<string>? log = null)
    {
        _kit = kit;
        _deviceId = deviceId;
        _physics = physics;
        _time = time;
        _sink = sink;
        _options = options ?? new ControllerOptions();
        _topics = new Topics(deviceId);
        _log = log ?? (_ => { });
        _runtimes = [.. kit.Actuators.Select(a => new Runtime(a))];
        _lastTick = time.GetUtcNow();
        _nextTelemetry = _lastTick;
    }

    public Topics Topics => _topics;
    public PlantPhysics Physics => _physics;
    public bool KillSwitch { get; private set; }

    private sealed class Runtime(ActuatorDef def)
    {
        public ActuatorDef Def { get; } = def;
        public bool Running { get; set; }
        public DateTimeOffset RunStartedAt { get; set; }
        public DateTimeOffset RunEndsAt { get; set; }
        public Guid RunCommand { get; set; }
        public double SoilAtStart { get; set; }
        public DateTimeOffset? LastRunEnd { get; set; }
        public double RunSecondsToday { get; set; }
        public DateOnly Day { get; set; }
        public bool Fault { get; set; }
        public int FruitlessRuns { get; set; }
        public bool FruitlessBlocked { get; set; }
    }

    private sealed record Seen(string Status, string? Reason);

    // ---- connection -----------------------------------------------------------------------------

    /// <summary>What a controller says when it connects: its capabilities (retained), that it is online (retained) and its state.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            var manifest = new Manifest(_kit.Model, _options.Firmware,
                [.. _kit.Channels.Select(c => new ManifestChannel(c.Id, c.Metric, c.Unit))],
                [.. _kit.Actuators.Select(a => new ManifestActuator(a.Id, a.Kind, a.Kind == "heater" ? a.ThermalCutoff : null, a.HardLimits))]);
            await _sink.PublishAsync(_topics.Manifest, Json.Write(manifest), retain: true, qos: 1, ct);
            await _sink.PublishAsync(_topics.Status, Json.Write(new StatusMessage("online", Stamp(now))), retain: true, qos: 1, ct);
            _lastStateKey = null;
            await PublishStateIfChangedAsync(now, ct);
        }
        finally { _gate.Release(); }
    }

    public Task StopAsync(CancellationToken ct = default) =>
        _sink.PublishAsync(_topics.Status, Json.Write(new StatusMessage("offline", Stamp(_time.GetUtcNow()))), retain: true, qos: 1, ct);

    // ---- time -----------------------------------------------------------------------------------

    /// <summary>Lets the pot and the actuators move on to now, ends runs that are due, enforces the safety cutoffs and reports.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await TickCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task TickCoreAsync(CancellationToken ct)
    {
        {
            var now = _time.GetUtcNow();
            var remaining = Math.Max(0, (now - _lastTick).TotalSeconds);
            var cursor = _lastTick;
            while (remaining > 0)
            {
                var running = _runtimes.Where(r => r.Running).ToList();
                // Small steps while something runs, so a run ends when it should. Whole minutes otherwise.
                var step = running.Count > 0 ? Math.Min(remaining, 0.25) : Math.Min(remaining, 60);
                foreach (var r in running) step = Math.Min(step, Math.Max((r.RunEndsAt - cursor).TotalSeconds, 0.001));
                _physics.Step(step, On());
                cursor = cursor.AddSeconds(step);
                remaining -= step;
                await GuardAsync(cursor, ct);
            }
            _lastTick = now;

            foreach (var r in _runtimes)
            {
                var day = DateOnly.FromDateTime(now.UtcDateTime);
                if (r.Day != day) { r.Day = day; r.RunSecondsToday = 0; }
            }

            await PublishStateIfChangedAsync(now, ct);
            if (now >= _nextTelemetry)
            {
                await PublishTelemetryAsync(now, ct);
                _nextTelemetry = now + _options.TelemetryInterval;
            }
        }
    }

    private ActuatorsOn On() => new(
        Pump: IsRunning("pump"), Heater: IsRunning("heater"), Lamp: IsRunning("lamp"), Fan: IsRunning("fan"), Humidifier: IsRunning("humidifier"));

    private bool IsRunning(string kind) => _runtimes.Any(r => r.Running && r.Def.Kind == kind);

    // The controller's own protection, whatever the cloud says: runs end on time, the heater trips at its cutoff,
    // a pump that finds the tank empty stops.
    private async Task GuardAsync(DateTimeOffset at, CancellationToken ct)
    {
        foreach (var r in _runtimes)
        {
            if (r.Running)
            {
                if (r.Def.Kind == "heater" && _physics.Temperature >= _options.ThermalCutoffCelsius)
                {
                    _log($"{r.Def.Id}: thermal cutoff at {_physics.Temperature:0.0} C, stopped");
                    r.Fault = true;
                    await EndRunAsync(r, at, "failed", null, ct);
                }
                else if (r.Def.Kind == "pump" && _physics.TankEmpty)
                {
                    _log($"{r.Def.Id}: the tank is empty, stopped");
                    r.Fault = true;
                    await EndRunAsync(r, at, "failed", "tank_empty", ct);
                }
                else if (at >= r.RunEndsAt.AddMilliseconds(-1))
                {
                    await EndRunAsync(r, r.RunEndsAt, "completed", null, ct);
                }
            }
            else if (r.Fault)
            {
                if (r.Def.Kind == "heater" && _physics.Temperature <= _options.ThermalCutoffCelsius - 5) r.Fault = false;
                if (r.Def.Kind == "pump" && !_physics.TankEmpty) r.Fault = false;
            }
        }
    }

    private async Task EndRunAsync(Runtime r, DateTimeOffset at, string status, string? reason, CancellationToken ct)
    {
        var seconds = Math.Max(0, (at - r.RunStartedAt).TotalSeconds);
        r.Running = false;
        r.RunSecondsToday += seconds;
        r.LastRunEnd = at;

        if (r.Def.Kind == "pump" && status == "completed")
        {
            // A pump that ran and did not wet the soil is wasting water: after a few such runs it stops trying.
            if (_physics.SoilMoisture - r.SoilAtStart < _options.FruitlessGain) r.FruitlessRuns++; else r.FruitlessRuns = 0;
            if (r.FruitlessRuns >= _options.MaxFruitlessRuns) r.FruitlessBlocked = true;
        }

        _log($"{r.Def.Id}: run {status} after {seconds:0.#} s");
        await AckAsync(r.RunCommand, status, reason, ct);
    }

    // ---- commands -------------------------------------------------------------------------------

    /// <summary>A command from the bridge, as the bytes of the message. It is never trusted: it is checked against the limits.</summary>
    public async Task HandleCommandAsync(string json, CancellationToken ct = default)
    {
        var command = Command.TryParse(json);
        if (command is null)
        {
            _log("ignored a command that could not be read");
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Catch up first, so the limits are checked against what the pot and the actuators are doing now.
            await TickCoreAsync(ct);
            var now = _time.GetUtcNow();

            if (_seen.TryGetValue(command.CommandId, out var prior))
            {
                // At-least-once delivery: the same command again is not run again, the platform just hears the answer once more.
                _log($"command {command.CommandId}: seen before, answered again with {prior.Status}");
                await SendAckAsync(command.CommandId, prior.Status, prior.Reason, ct);
                return;
            }

            if (now > command.ExpiresAt)
            {
                _log($"command {command.CommandId}: expired, not run");
                await RememberAndAckAsync(command.CommandId, "expired", "expired", ct);
                return;
            }

            switch (command.Type)
            {
                case "set_kill_switch": await KillSwitchAsync(command, now, ct); break;
                case "stop_actuator": await StopActuatorAsync(command, now, ct); break;
                case "run_actuator": await RunActuatorAsync(command, now, ct); break;
                default:
                    _log($"command {command.CommandId}: unknown type {command.Type}");
                    await RememberAndAckAsync(command.CommandId, "rejected", null, ct);
                    break;
            }

            await PublishStateIfChangedAsync(now, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task KillSwitchAsync(Command command, DateTimeOffset now, CancellationToken ct)
    {
        if (command.Enabled is not { } enabled)
        {
            await RememberAndAckAsync(command.CommandId, "rejected", null, ct);
            return;
        }

        KillSwitch = enabled;
        if (enabled)
        {
            foreach (var r in _runtimes.Where(r => r.Running)) await EndRunAsync(r, now, "failed", "kill_switch", ct);
            _log("kill switch on: every actuator stopped");
        }
        else
        {
            // Switching it off is the person saying that they have looked: a blocked pump may try again.
            foreach (var r in _runtimes) { r.FruitlessBlocked = false; r.FruitlessRuns = 0; }
            _log("kill switch off");
        }
        await RememberAndAckAsync(command.CommandId, "completed", null, ct);
    }

    private async Task StopActuatorAsync(Command command, DateTimeOffset now, CancellationToken ct)
    {
        var r = _runtimes.FirstOrDefault(r => r.Def.Id == command.ActuatorId);
        if (r is null)
        {
            await RememberAndAckAsync(command.CommandId, "rejected", "unknown_actuator", ct);
            return;
        }
        if (r.Running) await EndRunAsync(r, now, "completed", null, ct);
        await RememberAndAckAsync(command.CommandId, "completed", null, ct);
    }

    private async Task RunActuatorAsync(Command command, DateTimeOffset now, CancellationToken ct)
    {
        var r = _runtimes.FirstOrDefault(r => r.Def.Id == command.ActuatorId);
        if (r is null) { await Reject(command, "unknown_actuator", ct); return; }
        if (KillSwitch) { await Reject(command, "kill_switch", ct); return; }

        var limits = r.Def.HardLimits;
        var seconds = command.DurationSeconds ?? Math.Min(10, limits.MaxRunSeconds);
        if (seconds < 1 || seconds > limits.MaxRunSeconds) { await Reject(command, "exceeds_hard_limit", ct); return; }
        if (r.Running) { await Reject(command, "min_interval", ct); return; }
        if (r.LastRunEnd is { } ended && (now - ended).TotalSeconds < limits.MinIntervalSeconds) { await Reject(command, "min_interval", ct); return; }
        if (r.RunSecondsToday + seconds > limits.DailyCapSeconds) { await Reject(command, "daily_cap", ct); return; }

        switch (r.Def.Kind)
        {
            case "pump":
                if (r.FruitlessBlocked) { await Reject(command, "fruitless_runs", ct); return; }
                if (_physics.TankEmpty) { await Reject(command, "tank_empty", ct); return; }
                if (_physics.SoilMoisture >= _options.OverflowSoilMoisture) { await Reject(command, "overflow", ct); return; }
                break;
            case "heater":
                // A heater without a hardware thermal cutoff, or one already too hot, does not run.
                if (!r.Def.ThermalCutoff || r.Fault || _physics.Temperature >= _options.ThermalCutoffCelsius - 5) { await Reject(command, "exceeds_hard_limit", ct); return; }
                break;
        }
        if (r.Fault) { await Reject(command, "exceeds_hard_limit", ct); return; }

        r.Running = true;
        r.RunStartedAt = now;
        r.RunEndsAt = now.AddSeconds(seconds);
        r.RunCommand = command.CommandId;
        r.SoilAtStart = _physics.SoilMoisture;
        _log($"{r.Def.Id}: running for {seconds} s");
        await RememberAndAckAsync(command.CommandId, "accepted", null, ct);
    }

    private Task Reject(Command command, string reason, CancellationToken ct)
    {
        _log($"command {command.CommandId}: rejected ({reason})");
        return RememberAndAckAsync(command.CommandId, "rejected", reason, ct);
    }

    // ---- messages -------------------------------------------------------------------------------

    private async Task RememberAndAckAsync(Guid commandId, string status, string? reason, CancellationToken ct)
    {
        Remember(commandId, status, reason);
        await SendAckAsync(commandId, status, reason, ct);
    }

    // The answer a command got so far, so a repeat gets the same one. A run that is accepted is answered again later with its end.
    private void Remember(Guid commandId, string status, string? reason)
    {
        if (!_seen.ContainsKey(commandId))
        {
            _seenOrder.Enqueue(commandId);
            while (_seenOrder.Count > RememberedCommands) _seen.Remove(_seenOrder.Dequeue());
        }
        _seen[commandId] = new Seen(status, reason);
    }

    private async Task AckAsync(Guid commandId, string status, string? reason, CancellationToken ct)
    {
        Remember(commandId, status, reason);
        await SendAckAsync(commandId, status, reason, ct);
    }

    private Task SendAckAsync(Guid commandId, string status, string? reason, CancellationToken ct) =>
        _sink.PublishAsync(_topics.CommandAck, Json.Write(new Ack(commandId, status, reason, Stamp(_time.GetUtcNow()))), retain: false, qos: 1, ct);

    private async Task PublishStateIfChangedAsync(DateTimeOffset now, CancellationToken ct)
    {
        var actuators = _runtimes.Select(r => ActuatorStateOf(r, now)).ToList();
        var key = Json.Write(new State("", KillSwitch, actuators));
        if (key == _lastStateKey) return;
        _lastStateKey = key;
        await _sink.PublishAsync(_topics.State, Json.Write(new State(Stamp(now), KillSwitch, actuators)), retain: true, qos: 1, ct);
    }

    private ActuatorState ActuatorStateOf(Runtime r, DateTimeOffset now)
    {
        var minInterval = r.LastRunEnd is { } ended && (now - ended).TotalSeconds < r.Def.HardLimits.MinIntervalSeconds;
        var (state, reason) = true switch
        {
            _ when KillSwitch => ("blocked", "kill_switch"),
            _ when r.Running => ("running", (string?)null),
            _ when r.Fault => ("fault", null),
            _ when r.Def.Kind == "pump" && r.FruitlessBlocked => ("blocked", "fruitless_runs"),
            _ when r.Def.Kind == "pump" && _physics.TankEmpty => ("blocked", "tank_empty"),
            _ when r.Def.Kind == "pump" && _physics.SoilMoisture >= _options.OverflowSoilMoisture => ("blocked", "overflow"),
            _ when minInterval => ("blocked", "min_interval"),
            _ when r.RunSecondsToday >= r.Def.HardLimits.DailyCapSeconds => ("blocked", "daily_cap"),
            _ => ("idle", null),
        };
        return new ActuatorState(r.Def.Id, state, reason, (int)r.RunSecondsToday);
    }

    private async Task PublishTelemetryAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var channel in _kit.Channels)
        {
            if (_physics.Read(channel.Metric) is not { } value) continue;
            _sequence[channel.Id] = _sequence.GetValueOrDefault(channel.Id) + 1;
            await _sink.PublishAsync(_topics.Telemetry(channel.Id), Json.Write(new Reading(Stamp(now), value, _sequence[channel.Id])), retain: false, qos: 0, ct);
        }
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}
