namespace Greenhaus.KitEmulator;

/// <summary>Which actuators of the controller are running right now.</summary>
public readonly record struct ActuatorsOn(bool Pump = false, bool Heater = false, bool Lamp = false, bool Fan = false, bool Humidifier = false);

public sealed class PhysicsOptions
{
    /// <summary>
    /// How much faster than real time the surroundings change (drying, cooling, the day cycle). What an actuator does is tied to the
    /// seconds it really runs, so a 10 second pump run waters the same at any speed, which is what lets a demo show
    /// dry soil, a pump run and a recovery within a minute.
    /// </summary>
    public double Speed { get; set; } = 1;

    public double RoomTemperature { get; set; } = 20;
    public double RoomHumidity { get; set; } = 45;

    /// <summary>Soil moisture lost per hour at 20 degrees, in percentage points. Warmer soil dries faster.</summary>
    public double SoilDryPerHour { get; set; } = 3;
    public double PumpGainPerSecond { get; set; } = 0.8;
    public double TankPerPumpSecond { get; set; } = 0.4;
    public double HeaterDegreesPerSecond { get; set; } = 0.1;
    public double FanDegreesPerSecond { get; set; } = 0.04;
    public double HumidifierPerSecond { get; set; } = 0.3;
    public double RoomRelaxationSeconds { get; set; } = 1800;
    public double HumidityRelaxationSeconds { get; set; } = 3600;

    public double DaylightPeakLux { get; set; } = 25000;
    public double LampLux { get; set; } = 12000;
    /// <summary>The hour of the simulated day (0 to 24) at the start.</summary>
    public double StartHour { get; set; } = 9;

    /// <summary>Scales the noise of the sensors. 0 gives exact readings.</summary>
    public double Noise { get; set; } = 1;

    public double InitialSoilMoisture { get; set; } = 45;
    public double InitialTemperature { get; set; } = 20;
    public double InitialAirHumidity { get; set; } = 45;
    public double InitialTankLevel { get; set; } = 100;
}

/// <summary>
/// A small model of a pot with a plant: enough for the charts to show a real closed loop. It is pure and deterministic
/// (the same options, seed and steps give the same numbers), and it only knows seconds, so tests drive it without a clock.
/// </summary>
public sealed class PlantPhysics(PhysicsOptions options, int seed = 1)
{
    private readonly Random _random = new(seed);

    public PhysicsOptions Options { get; } = options;
    public double SoilMoisture { get; set; } = options.InitialSoilMoisture;
    public double Temperature { get; set; } = options.InitialTemperature;
    public double AirHumidity { get; set; } = options.InitialAirHumidity;
    public double TankLevel { get; set; } = options.InitialTankLevel;

    /// <summary>Seconds of the simulated day that have passed.</summary>
    public double SimulatedSeconds { get; private set; }

    public bool LampOn { get; private set; }
    public bool FanOn { get; private set; }

    public bool TankEmpty => TankLevel <= 0.0001;

    public double Hour => (Options.StartHour + SimulatedSeconds / 3600) % 24;

    /// <summary>Moves the pot forward by the seconds that really passed, with the actuators in the given state.</summary>
    public void Step(double realSeconds, ActuatorsOn on)
    {
        if (realSeconds <= 0) return;
        var simulated = realSeconds * Options.Speed;
        SimulatedSeconds += simulated;
        LampOn = on.Lamp;
        FanOn = on.Fan;

        // The surroundings: the soil dries (faster when warm), the room pulls the temperature and the humidity towards its own.
        var warmth = Math.Max(0.2, 1 + 0.04 * (Temperature - 20));
        SoilMoisture -= Options.SoilDryPerHour / 3600 * simulated * warmth;
        Temperature += (Options.RoomTemperature - Temperature) * (1 - Math.Exp(-simulated / Options.RoomRelaxationSeconds));
        AirHumidity += (Options.RoomHumidity - AirHumidity) * (1 - Math.Exp(-simulated / Options.HumidityRelaxationSeconds));

        // The actuators: tied to the seconds they really run.
        if (on.Pump && !TankEmpty)
        {
            var seconds = Math.Min(realSeconds, TankLevel / Options.TankPerPumpSecond);
            SoilMoisture += Options.PumpGainPerSecond * seconds;
            TankLevel -= Options.TankPerPumpSecond * seconds;
        }
        if (on.Heater) Temperature += Options.HeaterDegreesPerSecond * realSeconds;
        if (on.Fan) Temperature -= Math.Min(Options.FanDegreesPerSecond * realSeconds, Math.Max(0, Temperature - (Options.RoomTemperature - 2)));
        if (on.Humidifier) AirHumidity += Options.HumidifierPerSecond * realSeconds;
        if (on.Fan) AirHumidity -= Math.Min(0.01 * realSeconds, Math.Max(0, AirHumidity - Options.RoomHumidity));

        SoilMoisture = Math.Clamp(SoilMoisture, 0, 100);
        TankLevel = Math.Clamp(TankLevel, 0, 100);
        AirHumidity = Math.Clamp(AirHumidity, 0, 100);
    }

    /// <summary>Daylight in lux, a sine over the day between 6 and 18 hours.</summary>
    public double Daylight()
    {
        var hour = Hour;
        return hour is < 6 or > 18 ? 0 : Math.Sin(Math.PI * (hour - 6) / 12) * Options.DaylightPeakLux;
    }

    public double Light => Daylight() + (LampOn ? Options.LampLux : 0);

    public double Airflow => FanOn ? 1.6 : 0.2;

    /// <summary>What the sensor of a metric would report: the true value plus a little seeded noise, rounded as a cheap sensor would.</summary>
    public double? Read(string metric) => metric switch
    {
        "soil_moisture" => Round(Math.Clamp(SoilMoisture + Noise(0.2), 0, 100), 1),
        "temperature" => Round(Temperature + Noise(0.1), 1),
        "air_humidity" => Round(Math.Clamp(AirHumidity + Noise(0.3), 0, 100), 1),
        "light" => Round(Math.Max(0, Light * (1 + Noise(0.01))), 0),
        "airflow" => Round(Math.Max(0, Airflow + Noise(0.02)), 2),
        "tank_level" => Round(TankLevel, 1),
        _ => null,
    };

    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);

    // Roughly bell-shaped: the sum of three uniform numbers, centred and scaled to the amplitude.
    private double Noise(double amplitude) =>
        Options.Noise == 0 ? 0 : amplitude * Options.Noise * ((_random.NextDouble() + _random.NextDouble() + _random.NextDouble()) / 1.5 - 1);
}
