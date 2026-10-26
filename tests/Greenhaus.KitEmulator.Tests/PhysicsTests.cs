using Greenhaus.KitEmulator;

namespace Greenhaus.KitEmulator.Tests;

public class PhysicsTests
{
    private static PlantPhysics Pot(Action<PhysicsOptions>? configure = null)
    {
        var options = new PhysicsOptions { Noise = 0 };
        configure?.Invoke(options);
        return new PlantPhysics(options);
    }

    [Fact]
    public void Soil_dries_over_time_and_faster_when_it_is_warm()
    {
        var cool = Pot(o => { o.InitialTemperature = 20; o.RoomTemperature = 20; });
        var warm = Pot(o => { o.InitialTemperature = 30; o.RoomTemperature = 30; });
        cool.Step(3600, default);
        warm.Step(3600, default);

        Assert.InRange(45 - cool.SoilMoisture, 2.9, 3.1);
        Assert.True(45 - warm.SoilMoisture > 45 - cool.SoilMoisture);
        Assert.True(cool.SoilMoisture >= 0);
    }

    [Fact]
    public void Soil_never_goes_below_zero_or_above_one_hundred()
    {
        var dry = Pot(o => o.InitialSoilMoisture = 1);
        dry.Step(10 * 3600, default);
        Assert.Equal(0, dry.SoilMoisture);

        var wet = Pot(o => o.InitialSoilMoisture = 99);
        wet.Step(30, new ActuatorsOn(Pump: true));
        Assert.Equal(100, wet.SoilMoisture);
    }

    [Fact]
    public void A_running_pump_wets_the_soil_in_proportion_to_the_seconds_it_runs_and_empties_the_tank()
    {
        var pot = Pot(o => o.InitialSoilMoisture = 30);
        pot.Step(10, new ActuatorsOn(Pump: true));

        // 10 seconds of pump: +8 points, minus 10 seconds of drying (a hair), and 4 percent of the tank.
        Assert.InRange(pot.SoilMoisture, 37.9, 38.1);
        Assert.InRange(pot.TankLevel, 95.9, 96.1);
    }

    [Fact]
    public void What_an_actuator_does_is_tied_to_the_seconds_it_runs_not_to_the_speed_of_the_demo()
    {
        var normal = Pot(o => { o.InitialSoilMoisture = 30; o.Speed = 1; });
        var fast = Pot(o => { o.InitialSoilMoisture = 30; o.Speed = 60; });
        normal.Step(10, new ActuatorsOn(Pump: true));
        fast.Step(10, new ActuatorsOn(Pump: true));

        // The pump gain is the same, only the drying during those 10 seconds is 60 times faster.
        Assert.Equal(normal.TankLevel, fast.TankLevel, 6);
        Assert.InRange(fast.SoilMoisture, 37.5, 37.9);
    }

    [Fact]
    public void At_a_demo_speed_the_surroundings_change_a_minute_at_a_time()
    {
        var pot = Pot(o => { o.Speed = 60; o.InitialSoilMoisture = 50; });
        pot.Step(60, default);

        // One real minute is one simulated hour: about three points lost.
        Assert.InRange(50 - pot.SoilMoisture, 2.9, 3.1);
    }

    [Fact]
    public void A_pump_with_an_empty_tank_adds_nothing()
    {
        var pot = Pot(o => { o.InitialSoilMoisture = 30; o.InitialTankLevel = 0; });
        pot.Step(10, new ActuatorsOn(Pump: true));

        Assert.True(pot.TankEmpty);
        Assert.True(pot.SoilMoisture < 30);
    }

    [Fact]
    public void A_tank_that_runs_dry_during_a_step_only_gives_the_water_it_had()
    {
        var pot = Pot(o => { o.InitialSoilMoisture = 30; o.InitialTankLevel = 2; });
        pot.Step(60, new ActuatorsOn(Pump: true));

        // 2 percent of the tank is 5 seconds of pump: 4 points of moisture, not 48.
        Assert.InRange(pot.SoilMoisture, 33.9, 34.1);
        Assert.True(pot.TankEmpty);
    }

    [Fact]
    public void Temperature_drifts_to_the_room_and_the_heater_and_the_fan_move_it()
    {
        var room = Pot(o => { o.InitialTemperature = 30; o.RoomTemperature = 20; });
        room.Step(4 * 3600, default);
        Assert.InRange(room.Temperature, 20, 20.5);

        var heated = Pot();
        heated.Step(10, new ActuatorsOn(Heater: true));
        Assert.InRange(heated.Temperature, 20.9, 21.1);

        var cooled = Pot(o => { o.InitialTemperature = 24; });
        cooled.Step(10, new ActuatorsOn(Fan: true));
        Assert.True(cooled.Temperature < 24);
        Assert.True(cooled.Temperature > 22);
    }

    [Fact]
    public void Light_follows_the_day_and_the_lamp_adds_to_it()
    {
        var noon = Pot(o => o.StartHour = 12);
        Assert.InRange(noon.Light, 24900, 25000);

        var night = Pot(o => o.StartHour = 2);
        Assert.Equal(0, night.Light);
        night.Step(1, new ActuatorsOn(Lamp: true));
        Assert.Equal(12000, night.Light);
        night.Step(1, default);
        Assert.Equal(0, night.Light);
    }

    [Fact]
    public void The_fan_moves_the_air_and_the_humidifier_wets_it()
    {
        var pot = Pot(o => o.InitialAirHumidity = 30);
        Assert.Equal(0.2, pot.Airflow);
        pot.Step(10, new ActuatorsOn(Fan: true, Humidifier: true));

        Assert.Equal(1.6, pot.Airflow);
        Assert.True(pot.AirHumidity > 30);
    }

    [Fact]
    public void A_sensor_reads_the_true_value_plus_seeded_noise()
    {
        var exact = Pot();
        Assert.Equal(45.0, exact.Read("soil_moisture"));
        Assert.Null(exact.Read("ph"));

        PlantPhysics Noisy() => new(new PhysicsOptions { Noise = 1 }, seed: 7);
        var first = Noisy();
        var second = Noisy();
        var readings = Enumerable.Range(0, 20).Select(_ => first.Read("soil_moisture")!.Value).ToList();

        Assert.Equal(readings, Enumerable.Range(0, 20).Select(_ => second.Read("soil_moisture")!.Value).ToList());
        Assert.All(readings, r => Assert.InRange(r, 44.7, 45.3));
        Assert.True(readings.Distinct().Count() > 1);
    }
}