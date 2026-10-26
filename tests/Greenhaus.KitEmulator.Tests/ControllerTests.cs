using System.Text.Json.Nodes;
using Greenhaus.KitEmulator;

namespace Greenhaus.KitEmulator.Tests;

public class ControllerTests
{
    private static Guid Id() => Guid.NewGuid();

    [Fact]
    public async Task On_connect_it_publishes_its_manifest_status_and_state_as_the_contract_says()
    {
        var rig = new Rig();
        await rig.Controller.StartAsync();

        var manifest = Assert.Single(rig.Sink.On(rig.Topics.Manifest));
        Assert.True(manifest.Retain);
        Repo.Verify("manifest", manifest.Payload);
        Assert.Equal("GH-POT-KIT-01".Length > 0 ? "Greenhaus Pot Controller P1" : "", manifest.Json["model"]!.GetValue<string>());
        Assert.Equal(["light", "soil_moisture", "tank_level", "temperature"], manifest.Json["channels"]!.AsArray().Select(c => c!["metric"]!.GetValue<string>()).Order());
        Assert.Equal(60, manifest.Json["actuators"]![0]!["hardLimits"]!["maxRunSeconds"]!.GetValue<int>());

        var status = Assert.Single(rig.Sink.On(rig.Topics.Status));
        Assert.True(status.Retain);
        Repo.Verify("status", status.Payload);
        Assert.Equal("online", status.Json["status"]!.GetValue<string>());

        var state = Assert.Single(rig.Sink.On(rig.Topics.State));
        Repo.Verify("state", state.Payload);
        Assert.Equal("idle", rig.ActuatorState("pump"));
    }

    [Fact]
    public async Task It_reports_every_channel_at_the_telemetry_interval_with_a_growing_sequence()
    {
        var rig = new Rig(controller: o => o.TelemetryInterval = TimeSpan.FromSeconds(5));
        await rig.Controller.StartAsync();
        await rig.Controller.TickAsync();
        await rig.AdvanceAsync(10, stepSeconds: 5);

        var soil = rig.Sink.On(rig.Topics.Telemetry("soil"));
        Assert.Equal(3, soil.Count);
        Assert.All(soil, m => Repo.Verify("reading", m.Payload));
        Assert.Equal([1, 2, 3], soil.Select(m => m.Json["seq"]!.GetValue<int>()));
        foreach (var channel in new[] { "temp", "light", "tank" }) Assert.Equal(3, rig.Sink.On(rig.Topics.Telemetry(channel)).Count);
        Assert.Equal(100.0, rig.Sink.On(rig.Topics.Telemetry("tank")).Last().Json["value"]!.GetValue<double>());
    }

    [Fact]
    public async Task A_run_is_accepted_then_completed_and_the_soil_gets_wetter()
    {
        var rig = new Rig(physics: o => o.InitialSoilMoisture = 30);
        await rig.Controller.StartAsync();
        var id = Id();
        await rig.SendAsync(rig.Run("pump", 10, id));

        Assert.Equal([("accepted", (string?)null)], rig.Acks(id));
        Assert.Equal("running", rig.ActuatorState("pump"));

        await rig.AdvanceAsync(10, stepSeconds: 1);

        Assert.Equal([("accepted", null), ("completed", null)], rig.Acks(id));
        Assert.InRange(rig.Physics.SoilMoisture, 37.5, 38.5);
        Assert.All(rig.Sink.On(rig.Topics.CommandAck), m => Repo.Verify("ack", m.Payload));
        Assert.All(rig.Sink.On(rig.Topics.State), m => Repo.Verify("state", m.Payload));
        // Right after a run the pump waits out its minimum interval.
        Assert.Equal("blocked", rig.ActuatorState("pump"));
        Assert.Equal("min_interval", rig.BlockedReason("pump"));
        Assert.Equal(10, rig.LastState()["actuators"]![0]!["runSecondsToday"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_command_beyond_the_hard_limit_is_refused_by_the_controller_itself()
    {
        var rig = new Rig(physics: o => o.InitialSoilMoisture = 30);
        await rig.Controller.StartAsync();
        var id = Id();
        await rig.SendAsync(rig.Run("pump", 61, id));

        Assert.Equal([("rejected", "exceeds_hard_limit")], rig.Acks(id));
        Assert.Equal("idle", rig.ActuatorState("pump"));
        await rig.AdvanceAsync(5);
        Assert.True(rig.Physics.SoilMoisture < 30);
    }

    [Fact]
    public async Task The_minimum_interval_and_the_daily_cap_are_kept()
    {
        // A gentle pump with a big tank, so neither the soil nor the tank refuses a run before the daily cap does.
        var rig = new Rig(physics: o => { o.PumpGainPerSecond = 0.05; o.TankPerPumpSecond = 0.01; });
        await rig.Controller.StartAsync();
        await rig.SendAsync(rig.Run("pump", 30));
        await rig.AdvanceAsync(30, stepSeconds: 5);

        var tooSoon = Id();
        await rig.SendAsync(rig.Run("pump", 10, tooSoon));
        Assert.Equal([("rejected", "min_interval")], rig.Acks(tooSoon));

        // The daily cap of the pump is 300 seconds: 30 already run, and every further run waits out 600 seconds.
        for (var run = 0; run < 4; run++)
        {
            await rig.AdvanceAsync(600, stepSeconds: 100);
            await rig.SendAsync(rig.Run("pump", 60));
            await rig.AdvanceAsync(60, stepSeconds: 10);
        }
        Assert.Equal(270, rig.LastState()["actuators"]![0]!["runSecondsToday"]!.GetValue<int>());

        await rig.AdvanceAsync(600, stepSeconds: 100);
        var overCap = Id();
        await rig.SendAsync(rig.Run("pump", 40, overCap));
        Assert.Equal([("rejected", "daily_cap")], rig.Acks(overCap));
    }

    [Fact]
    public async Task A_command_past_its_expiry_never_runs()
    {
        var rig = new Rig(physics: o => o.InitialSoilMoisture = 30);
        await rig.Controller.StartAsync();
        var id = Id();
        var command = rig.Run("pump", 10, id, validFor: TimeSpan.FromSeconds(30));
        rig.Time.Advance(TimeSpan.FromSeconds(31));
        await rig.SendAsync(command);

        Assert.Equal([("expired", "expired")], rig.Acks(id));
        Assert.Equal("idle", rig.ActuatorState("pump"));
        await rig.AdvanceAsync(10);
        Assert.Equal(100.0, rig.Physics.TankLevel);
    }

    [Fact]
    public async Task The_same_command_twice_runs_once_and_is_answered_again()
    {
        var rig = new Rig(physics: o => o.InitialSoilMoisture = 30);
        await rig.Controller.StartAsync();
        var id = Id();
        var command = rig.Run("pump", 10, id);
        await rig.SendAsync(command);
        await rig.AdvanceAsync(5);
        await rig.SendAsync(command);
        await rig.AdvanceAsync(10);
        await rig.SendAsync(command);

        // Accepted, the repeat while it runs, completed, and the repeat after the end: never a second run.
        Assert.Equal([("accepted", null), ("accepted", null), ("completed", null), ("completed", null)], rig.Acks(id));
        Assert.Equal(10, rig.LastState()["actuators"]![0]!["runSecondsToday"]!.GetValue<int>());
        Assert.InRange(rig.Physics.TankLevel, 95.9, 96.1);
    }

    [Fact]
    public async Task The_kill_switch_stops_a_running_actuator_and_refuses_runs_until_it_is_off()
    {
        var rig = new Rig(physics: o => o.InitialSoilMoisture = 30);
        await rig.Controller.StartAsync();
        var run = Id();
        await rig.SendAsync(rig.Run("pump", 30, run));
        await rig.AdvanceAsync(5);

        var kill = Id();
        await rig.SendAsync(rig.KillSwitch(true, kill));
        Assert.Equal([("accepted", (string?)null), ("failed", "kill_switch")], rig.Acks(run));
        Assert.Equal([("completed", (string?)null)], rig.Acks(kill));
        Assert.True(rig.LastState()["killSwitch"]!.GetValue<bool>());
        Assert.Equal("blocked", rig.ActuatorState("pump"));
        Assert.Equal("kill_switch", rig.BlockedReason("pump"));

        var refused = Id();
        await rig.SendAsync(rig.Run("pump", 5, refused));
        Assert.Equal([("rejected", "kill_switch")], rig.Acks(refused));

        await rig.SendAsync(rig.KillSwitch(false));
        Assert.False(rig.LastState()["killSwitch"]!.GetValue<bool>());
        await rig.AdvanceAsync(600, stepSeconds: 100);
        var again = Id();
        await rig.SendAsync(rig.Run("pump", 5, again));
        Assert.Equal("accepted", rig.Acks(again)[0].Status);
    }

    [Fact]
    public async Task A_stop_command_ends_the_run_and_an_unknown_actuator_is_rejected()
    {
        var rig = new Rig();
        await rig.Controller.StartAsync();
        var run = Id();
        await rig.SendAsync(rig.Run("pump", 30, run));
        await rig.AdvanceAsync(3);
        await rig.SendAsync(rig.Stop("pump"));

        Assert.Equal([("accepted", (string?)null), ("completed", null)], rig.Acks(run));
        Assert.Equal(3, rig.LastState()["actuators"]![0]!["runSecondsToday"]!.GetValue<int>());

        var unknown = Id();
        await rig.SendAsync(rig.Run("sprinkler", 5, unknown));
        Assert.Equal([("rejected", "unknown_actuator")], rig.Acks(unknown));
    }

    [Fact]
    public async Task A_pump_stops_when_the_tank_runs_dry_and_stays_blocked_until_it_is_filled()
    {
        var rig = new Rig(physics: o => { o.InitialSoilMoisture = 30; o.InitialTankLevel = 2; });
        await rig.Controller.StartAsync();
        var id = Id();
        await rig.SendAsync(rig.Run("pump", 30, id));
        await rig.AdvanceAsync(10, stepSeconds: 1);

        Assert.Equal([("accepted", (string?)null), ("failed", "tank_empty")], rig.Acks(id));
        Assert.Equal("fault", rig.ActuatorState("pump"));

        rig.Physics.TankLevel = 50;
        await rig.AdvanceAsync(600, stepSeconds: 100);
        Assert.Equal("idle", rig.ActuatorState("pump"));
    }

    [Fact]
    public async Task A_pump_that_does_not_wet_the_soil_is_blocked_after_a_few_runs_and_the_kill_switch_clears_it()
    {
        // A pump that moves hardly any water, as one with a clogged pipe would.
        var rig = new Rig(physics: o => { o.InitialSoilMoisture = 30; o.PumpGainPerSecond = 0.01; });
        await rig.Controller.StartAsync();
        for (var run = 0; run < 3; run++)
        {
            await rig.SendAsync(rig.Run("pump", 10));
            await rig.AdvanceAsync(10, stepSeconds: 1);
            await rig.AdvanceAsync(600, stepSeconds: 100);
        }

        Assert.Equal("blocked", rig.ActuatorState("pump"));
        Assert.Equal("fruitless_runs", rig.BlockedReason("pump"));
        var refused = Id();
        await rig.SendAsync(rig.Run("pump", 10, refused));
        Assert.Equal([("rejected", "fruitless_runs")], rig.Acks(refused));

        await rig.SendAsync(rig.KillSwitch(true));
        await rig.SendAsync(rig.KillSwitch(false));
        Assert.Equal("idle", rig.ActuatorState("pump"));
    }

    [Fact]
    public async Task The_heater_trips_its_thermal_cutoff_on_its_own()
    {
        var rig = new Rig("GH-GREENHOUSE-01", physics: o => { o.InitialTemperature = 28; o.RoomTemperature = 28; });
        await rig.Controller.StartAsync();
        var id = Id();
        await rig.SendAsync(rig.Run("heater", 600, id));
        await rig.AdvanceAsync(120, stepSeconds: 1);

        // 0.1 degrees a second: 7 degrees need 70 seconds, then the cutoff stops it although the command asked for 600.
        Assert.Equal([("accepted", (string?)null), ("failed", null)], rig.Acks(id));
        Assert.InRange(rig.Physics.Temperature, 34, 35.5);
        Assert.InRange(rig.LastState()["actuators"]!.AsArray().First(a => a!["id"]!.GetValue<string>() == "heater")!["runSecondsToday"]!.GetValue<int>(), 60, 80);
        Assert.Equal("fault", rig.ActuatorState("heater"));

        // After its minimum interval it is still too hot to start again.
        await rig.AdvanceAsync(300, stepSeconds: 50);
        var tooHot = Id();
        await rig.SendAsync(rig.Run("heater", 10, tooHot));
        Assert.Equal([("rejected", "exceeds_hard_limit")], rig.Acks(tooHot));
    }

    [Fact]
    public async Task Every_actuator_of_the_big_kit_can_run_and_changes_its_part_of_the_pot()
    {
        var rig = new Rig("GH-GREENHOUSE-01", physics: o => { o.StartHour = 2; o.InitialAirHumidity = 30; });
        await rig.Controller.StartAsync();
        await rig.SendAsync(rig.Run("lamp", 20));
        await rig.SendAsync(rig.Run("fan", 20));
        await rig.AdvanceAsync(10, stepSeconds: 1);

        Assert.Equal(12000, rig.Physics.Light);
        Assert.Equal(1.6, rig.Physics.Airflow);
        Assert.Equal("running", rig.ActuatorState("lamp"));
        Assert.Equal("running", rig.ActuatorState("fan"));
        await rig.AdvanceAsync(15, stepSeconds: 1);
        Assert.Equal("idle", rig.ActuatorState("fan"));
    }

    [Fact]
    public async Task A_message_it_cannot_read_is_ignored_and_the_starter_module_has_no_actuators()
    {
        var rig = new Rig("GH-STARTER-01");
        await rig.Controller.StartAsync();
        await rig.Controller.HandleCommandAsync("{ not json");
        await rig.Controller.HandleCommandAsync("{}");

        Assert.Empty(rig.Sink.On(rig.Topics.CommandAck));
        Assert.Empty(rig.LastState()["actuators"]!.AsArray());
        var id = Id();
        await rig.SendAsync(rig.Run("pump", 5, id));
        Assert.Equal([("rejected", "unknown_actuator")], rig.Acks(id));
    }

    [Fact]
    public async Task State_is_only_published_when_it_changes()
    {
        var rig = new Rig();
        await rig.Controller.StartAsync();
        await rig.AdvanceAsync(30, stepSeconds: 5);
        Assert.Single(rig.Sink.On(rig.Topics.State));

        await rig.SendAsync(rig.Run("pump", 5));
        await rig.AdvanceAsync(5, stepSeconds: 1);
        // running, then blocked for the minimum interval
        Assert.Equal(3, rig.Sink.On(rig.Topics.State).Count);
        await rig.AdvanceAsync(600, stepSeconds: 100);
        Assert.Equal(4, rig.Sink.On(rig.Topics.State).Count);
        Assert.Equal("idle", rig.ActuatorState("pump"));
    }
}