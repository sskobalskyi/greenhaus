namespace Greenhaus.MockServer.Pulse;

public partial class PulseStore
{
    public static readonly TimeSpan CommandLatency = TimeSpan.FromSeconds(1);

    // ---- rules and calibration -------------------------------------------------------------------------------

    public List<string> SetRules(DeviceState state, List<Rule> rules)
    {
        var problems = new List<string>();
        var channelIds = (state.Device.Capabilities.Channels ?? []).Select(c => c.Id).ToHashSet();
        foreach (var rule in rules)
        {
            if (rule.ChannelId is null || !channelIds.Contains(rule.ChannelId)) problems.Add($"channel '{rule.ChannelId}' does not exist on this device");
            switch (rule.Kind)
            {
                case "threshold":
                    if (rule.Min is null && rule.Max is null) problems.Add($"a threshold rule for '{rule.ChannelId}' needs min or max");
                    else if (rule.Min is { } min && rule.Max is { } max && min > max) problems.Add($"rule for '{rule.ChannelId}': min is above max");
                    break;
                case "no_data":
                    if (rule.TimeoutSeconds is null or < 10) problems.Add($"a no_data rule for '{rule.ChannelId}' needs timeoutSeconds of at least 10");
                    break;
                default:
                    problems.Add($"rule for '{rule.ChannelId}' has an unknown kind '{rule.Kind}'");
                    break;
            }
            if (rule.Margin is < 0) problems.Add($"rule for '{rule.ChannelId}' has a negative margin");
        }
        if (problems.Count > 0) return problems;
        lock (_gate) state.Rules = rules;
        return problems;
    }

    public (PChannel? Channel, string? Problem) Calibrate(DeviceState state, string channelId, Calibration body)
    {
        var channel = (state.Device.Capabilities.Channels ?? []).FirstOrDefault(c => c.Id == channelId);
        if (channel is null) return (null, "unknown_channel");
        if (body.DryRaw is not { } dry || body.WetRaw is not { } wet || !double.IsFinite(dry) || !double.IsFinite(wet) || dry == wet) return (null, "invalid");
        lock (_gate) channel.Calibration = new Calibration { DryRaw = dry, WetRaw = wet, CalibratedAt = clock.UtcNow };
        return (channel, null);
    }

    // ---- commands ---------------------------------------------------------------------------------------------

    private static string Fingerprint(CommandRequest b) => $"{b.Type}|{b.ActuatorId}|{b.DurationSeconds}|{b.Enabled}|{b.ExpiresAt:O}";

    public (Command? Command, string? Code, string? Detail, int Status) SendCommand(DeviceState state, CommandRequest body)
    {
        var problems = new List<string>();
        if (body.CommandId is null) problems.Add("commandId is required");
        if (body.Type is null || !CommandTypes.Contains(body.Type)) problems.Add($"type must be one of: {string.Join(", ", CommandTypes)}");
        if (body.ExpiresAt is null) problems.Add("expiresAt is required");
        var actuators = state.Device.Capabilities.Actuators ?? [];
        if (body.Type is "run_actuator" or "stop_actuator" && actuators.All(a => a.Id != body.ActuatorId)) problems.Add($"actuator '{body.ActuatorId}' does not exist on this device");
        if (body.Type == "run_actuator" && body.DurationSeconds is null or < 1) problems.Add("run_actuator needs durationSeconds of at least 1");
        if (body.Type == "set_kill_switch" && body.Enabled is null) problems.Add("set_kill_switch needs enabled");
        if (problems.Count > 0) return (null, "validation_failed", string.Join("; ", problems), 400);

        lock (_gate)
        {
            var now = clock.UtcNow;
            var id = body.CommandId!.Value;
            if (state.CommandFingerprints.TryGetValue(id, out var known))
            {
                return known == Fingerprint(body)
                    ? (state.Commands.First(c => c.CommandId == id), null, null, 202)
                    : (null, "command_id_reused", "This commandId was used for a different command", 409);
            }
            if (body.ExpiresAt <= now) return (null, "command_expired", "The command is already past its expiry", 400);
            if (state.Device.State is not ("claimed" or "active")) return (null, "invalid_state", $"A device in state '{state.Device.State}' does not take commands", 409);

            Refresh(state);
            var command = new Command
            {
                CommandId = id, DeviceId = state.Device.Id, Type = body.Type!, ActuatorId = body.ActuatorId, CreatedAt = now,
                // A device that reports takes the command at once. An offline one gets it when it is back, if not expired.
                Status = state.Device.Connection == "online" ? "sent" : "queued",
            };
            state.Commands.Add(command);
            state.CommandFingerprints[id] = Fingerprint(body);
            state.CommandExpiry[id] = body.ExpiresAt!.Value;
            state.CommandSeconds[id] = body.DurationSeconds ?? 0;
            if (body.Type == "set_kill_switch") state.CommandSeconds[id] = body.Enabled == true ? -1 : -2;
            return (command, null, null, 202);
        }
    }

    public IReadOnlyList<Command> Commands(DeviceState state, int limit)
    {
        lock (_gate) return [.. state.Commands.OrderByDescending(c => c.CreatedAt).Take(limit)];
    }

    public Command? FindCommand(DeviceState state, Guid id)
    {
        lock (_gate) return state.Commands.FirstOrDefault(c => c.CommandId == id);
    }

    /// <summary>The simulated controller: it carries out a command, or refuses it for a reason the real one would give.</summary>
    private void Progress(DeviceState state, Command command, DateTimeOffset now)
    {
        if (command.Status is not ("queued" or "sent" or "accepted")) return;
        var expiry = state.CommandExpiry[command.CommandId];
        if (now >= expiry)
        {
            Finish(command, "expired", "expired", expiry);
            return;
        }
        if (state.Device.Connection != "online") return;
        if (command.Status == "queued") command.Status = "sent";
        if (now - command.CreatedAt < CommandLatency) return;

        var seconds = state.CommandSeconds[command.CommandId];
        switch (command.Type)
        {
            case "set_kill_switch":
                state.KillSwitch = seconds == -1;
                Finish(command, "completed", null, now);
                return;
            case "stop_actuator":
                Finish(command, "completed", null, now);
                return;
        }

        var limits = state.Device.Capabilities.Actuators!.First(a => a.Id == command.ActuatorId).HardLimits!;
        var finishedToday = state.Commands.Where(c => c.Type == "run_actuator" && c.ActuatorId == command.ActuatorId && c.Status == "completed" && c.FinishedAt > now - TimeSpan.FromDays(1)).ToList();
        string? reason = null;
        if (state.KillSwitch) reason = "kill_switch";
        else if (seconds > limits.MaxRunSeconds) reason = "exceeds_hard_limit";
        else if (finishedToday.Any(c => now - c.FinishedAt < TimeSpan.FromSeconds(limits.MinIntervalSeconds!.Value))) reason = "min_interval";
        else if (finishedToday.Sum(c => state.CommandSeconds[c.CommandId]) + seconds > limits.DailyCapSeconds) reason = "daily_cap";

        if (reason is null) Finish(command, "completed", null, now);
        else Finish(command, "rejected", reason, now);
    }

    private static void Finish(Command command, string status, string? reason, DateTimeOffset at)
    {
        command.Status = status;
        command.Reason = reason;
        command.FinishedAt = at;
    }

    // ---- series -----------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, TimeSpan> Windows = new()
    {
        ["raw"] = TimeSpan.FromHours(6),
        ["1m"] = TimeSpan.FromDays(7),
        ["1h"] = TimeSpan.FromDays(365),
    };

    public const int RawPointLimit = 10_000;
    private static readonly TimeSpan RawStep = TimeSpan.FromSeconds(2);

    public (List<Point>? Points, bool Truncated, string? Code, string? Detail, int Status) Series(DeviceState state, string channelId, string? from, string? to, string? resolution)
    {
        var channel = (state.Device.Capabilities.Channels ?? []).FirstOrDefault(c => c.Id == channelId);
        if (channel is null) return (null, false, "not_found", "Unknown channel", 404);

        resolution ??= "raw";
        if (!Windows.ContainsKey(resolution)) return (null, false, "invalid_resolution", "resolution must be raw, 1m or 1h", 400);
        if (!DateTimeOffset.TryParse(from, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var start))
            return (null, false, "invalid_range", "from is required and must be a date and time", 400);
        var end = clock.UtcNow;
        if (to is not null && !DateTimeOffset.TryParse(to, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out end))
            return (null, false, "invalid_range", "to must be a date and time", 400);
        if (start >= end) return (null, false, "invalid_range", "from must be before to", 400);
        if (end - start > Windows[resolution]) return (null, false, "window_too_large", $"The longest window for {resolution} is {Windows[resolution].TotalHours:0} hours", 400);

        var metric = channel.Metric!;
        var points = new List<Point>();
        var truncated = false;

        if (resolution == "raw")
        {
            var count = (int)((end - start) / RawStep) + 1;
            var first = 0;
            if (count > RawPointLimit)
            {
                // Only the latest points, and the header says so.
                first = count - RawPointLimit;
                truncated = true;
            }
            for (var i = first; i < count; i++)
            {
                var ts = start + RawStep * i;
                points.Add(new Point { Ts = ts, Value = Math.Round(Sample(metric, channelId, ts), 2) });
            }
            return (points, truncated, null, null, 200);
        }

        var bucket = resolution == "1m" ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(1);
        var bucketStart = new DateTimeOffset(start.Ticks - start.Ticks % bucket.Ticks, TimeSpan.Zero);
        for (var t = bucketStart; t < end; t += bucket)
        {
            var samples = Enumerable.Range(0, 12).Select(k => Sample(metric, channelId, t + bucket / 12 * k)).ToList();
            points.Add(new Point { Ts = t, Value = Math.Round(samples.Average(), 2), Min = Math.Round(samples.Min(), 2), Max = Math.Round(samples.Max(), 2) });
        }
        return (points, false, null, null, 200);
    }

    /// <summary>A smooth, plausible value for a metric at a moment, different for every channel.</summary>
    private static double Sample(string metric, string channelId, DateTimeOffset at)
    {
        var seconds = at.ToUnixTimeSeconds();
        var phase = channelId.Aggregate(0, (h, c) => h * 31 + c) % 100 / 10.0;
        var slow = Math.Sin(seconds / 1800.0 + phase);
        var fast = Math.Sin(seconds / 37.0 + phase) * 0.15;
        var day = Math.Max(0, Math.Sin((seconds % 86400) / 86400.0 * 2 * Math.PI - Math.PI / 2));
        return metric switch
        {
            "temperature" => 22 + 3 * slow + fast,
            "air_humidity" => 52 + 6 * slow + fast,
            "soil_moisture" => 55 + 8 * slow + fast,
            "ph" => 6.5 + 0.4 * slow + fast / 10,
            "light" => 2000 + 28000 * day + 200 * fast,
            "airflow" => 1 + 0.5 * slow,
            "tank_level" => 80 + 5 * slow,
            _ => 0,
        };
    }
}
