using System.Text.Json;

namespace Greenhaus.MockServer;

public class Channel
{
    public required string Id { get; set; }
    public Metric Metric { get; set; }
    public Guid ZoneId { get; set; }
    public string Unit { get; set; } = "";
    public double Base { get; set; }
    public Calibration? Calibration { get; set; }
}

public class PotState
{
    public required Pot Pot { get; set; }
    public List<Zone> Zones { get; set; } = [];
    public List<Plant> Plants { get; set; } = [];
    public Requirements Requirements { get; set; } = new();
    public List<Actuator> Actuators { get; set; } = [];
    public List<AuditEntry> Audit { get; set; } = [];
    public List<CareTask> Tasks { get; set; } = [];
    public List<LinkedDevice> Devices { get; set; } = [];
    public List<Channel> Channels { get; set; } = [];
}

public class ClaimableDevice
{
    public required string Id { get; set; }
    public required string Model { get; set; }
    public required string Token { get; set; }
    public bool Shipped { get; set; }
    public bool Expired { get; set; }
    public bool Claimed { get; set; }
    public List<ClaimChannel> Channels { get; set; } = [];
    public List<ClaimActuator> Actuators { get; set; } = [];
}

/// <summary>
/// In-memory state behind the mock. It applies the same rules the real service will (tier limits, heater
/// and calibration guards, per-zone status without averaging) so clients can be built against it.
/// </summary>
public class MockStore
{
    private static readonly Metric[] AllMetrics = Enum.GetValues<Metric>();

    public Profile User { get; } = new()
    {
        Id = "demo-user",
        DisplayName = "Demo Gardener",
        Email = "demo@greenhaus.local",
        Locale = "en",
        Tier = Tier.Gardener,
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-40),
    };

    public List<Species> Species { get; }
    public Dictionary<Guid, PotState> Pots { get; } = [];
    public Dictionary<string, ClaimableDevice> Devices { get; } = [];
    public object Sync { get; } = new();

    private readonly Billing.BillingStore _billing;

    public MockStore(string speciesPath, Billing.BillingStore billing)
    {
        _billing = billing;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Species = JsonSerializer.Deserialize<SpeciesFile>(File.ReadAllText(speciesPath), options)!.Species;
        Seed();
        SeedNotifications();
    }

    /// <summary>Puts the mock back to its seeded state. Used by end-to-end tests, it is not part of the contract.</summary>
    /// <summary>Assistant questions answered this month, which count against the tier quota.</summary>
    public int AiQuestions { get; set; }

    public void Reset()
    {
        lock (Sync)
        {
            AiQuestions = 0;
            Pots.Clear();
            Devices.Clear();
            User.DisplayName = "Demo Gardener";
            User.Locale = "en";
            Seed();
            SeedNotifications();
        }
    }

    // ---- tiers -------------------------------------------------------------------------------------

    public static Limits LimitsFor(Tier tier)
    {
        static List<AutomationLevel> Levels(params Metric[] auto) =>
            [.. AllMetrics.Select(m => new AutomationLevel { Metric = m, MaxMode = auto.Contains(m) ? ControlMode.Auto : ControlMode.Notify })];

        return tier switch
        {
            Tier.Novice => new Limits
            {
                Pots = 2, HistoryDays = 7, AiQuestionsPerMonth = 10, AiDataAccess = "none", ZoneGridMax = 1,
                Automation = Levels(Metric.SoilMoisture, Metric.Light),
            },
            Tier.Gardener => new Limits
            {
                Pots = 6, HistoryDays = 30, AiQuestionsPerMonth = 100, AiDataAccess = "current", ZoneGridMax = 2,
                Automation = Levels(Metric.SoilMoisture, Metric.Light, Metric.Temperature, Metric.AirHumidity, Metric.Airflow),
            },
            _ => new Limits
            {
                Pots = null, HistoryDays = 365, AiQuestionsPerMonth = 500, AiDataAccess = "history", ZoneGridMax = 8,
                Automation = Levels(Metric.SoilMoisture, Metric.Light, Metric.Temperature, Metric.AirHumidity, Metric.Airflow),
            },
        };
    }

    public static Tier LowestTierAllowingAuto(Metric metric) =>
        new[] { Tier.Novice, Tier.Gardener, Tier.Druid }
            .First(t => LimitsFor(t).Automation.First(a => a.Metric == metric).MaxMode == ControlMode.Auto);

    /// <summary>The tier the demo user is on: what billing says, and Novice when nothing is paid or granted.</summary>
    public Tier DefaultTier() => _billing.EffectiveTier() ?? Tier.Novice;

    /// <summary>Entitlements built from the billing mock: the subscription and every grant that still counts.</summary>
    public Entitlements EntitlementsFromBilling()
    {
        var tier = DefaultTier();
        var sources = new List<EntitlementSource>();
        if (_billing.Current() is { Status: "active" or "past_due" } subscription)
        {
            sources.Add(new EntitlementSource
            {
                Type = "subscription", Tier = subscription.Tier, Status = subscription.Status, ValidUntil = subscription.CurrentPeriodEnd,
            });
        }
        foreach (var grant in _billing.Grants().Where(g => g.Status != "revoked"))
        {
            sources.Add(new EntitlementSource
            {
                Type = "purchase_grant", Tier = grant.Tier, Status = grant.Status, OrderId = grant.OrderId,
                ValidUntil = grant.ValidUntil, GraceEndsAt = grant.GraceEndsAt,
            });
        }
        return new Entitlements
        {
            Tier = tier, Limits = LimitsFor(tier), Usage = new Usage { Pots = Pots.Count, AiQuestionsThisMonth = AiQuestions }, Sources = sources,
        };
    }

    public Entitlements EntitlementsFor(Tier tier, string? scenario)
    {
        var sources = new List<EntitlementSource>();
        if (tier != Tier.Novice)
        {
            sources.Add(new EntitlementSource
            {
                Type = "subscription", Tier = tier, Status = "active", ValidUntil = DateTimeOffset.UtcNow.AddDays(21),
            });
        }
        if (scenario == "grant-at-risk")
        {
            sources.Add(new EntitlementSource
            {
                Type = "purchase_grant", Tier = Tier.Gardener, Status = "at_risk", OrderId = Guid.NewGuid(),
                ValidUntil = DateTimeOffset.UtcNow.AddDays(60), GraceEndsAt = DateTimeOffset.UtcNow.AddDays(5),
            });
        }
        return new Entitlements
        {
            Tier = tier,
            Limits = LimitsFor(tier),
            Usage = new Usage { Pots = Pots.Count, AiQuestionsThisMonth = AiQuestions },
            Sources = sources,
        };
    }

    // ---- requirements --------------------------------------------------------------------------------

    public Species? FindSpecies(string id) => Species.FirstOrDefault(s => s.Id == id);

    private static string Key(Metric m) => JsonNamingPolicy.SnakeCaseLower.ConvertName(m.ToString());

    /// <summary>The intersection of the plants' ranges. Metrics whose ranges do not overlap are conflicts.</summary>
    public RequirementsSuggestion Suggest(PotState pot)
    {
        var requirements = new Requirements();
        var conflicts = new List<RequirementConflict>();
        foreach (var metric in AllMetrics)
        {
            var withRange = pot.Plants
                .Select(p => (Plant: p, Range: FindSpecies(p.SpeciesId)?.Preferred.GetValueOrDefault(Key(metric))))
                .Where(x => x.Range is not null)
                .ToList();
            if (withRange.Count == 0)
            {
                requirements.Metrics.Add(new MetricRequirement { Metric = metric, Enabled = false });
                continue;
            }

            var min = withRange.Max(x => x.Range!.Min);
            var max = withRange.Min(x => x.Range!.Max);
            if (min > max)
            {
                conflicts.Add(new RequirementConflict { Metric = metric, Plants = [.. withRange.Select(x => x.Plant.Id)] });
                (min, max) = (withRange[0].Range!.Min, withRange[0].Range!.Max);
            }

            requirements.Metrics.Add(new MetricRequirement
            {
                Metric = metric,
                Enabled = metric != Metric.Ph,
                Min = min,
                Max = max,
                Margin = Math.Round((max - min) * 0.05, 1),
            });
        }

        return new RequirementsSuggestion { Requirements = requirements, Conflicts = conflicts };
    }

    // ---- status --------------------------------------------------------------------------------------

    private static double Wobble(Channel channel, DateTimeOffset now) =>
        Math.Sin(now.ToUnixTimeSeconds() / 30.0 + channel.Id.Length) * Math.Abs(channel.Base) * 0.01;

    /// <summary>Per-zone status. A pot is as bad as its worst zone and values are never averaged.</summary>
    public PotStatus StatusOf(PotState pot, DateTimeOffset now)
    {
        var zones = new List<ZoneStatus>();
        var alerts = new List<ActiveAlert>();
        foreach (var zone in pot.Zones)
        {
            var metrics = new List<MetricReading>();
            foreach (var channel in pot.Channels.Where(c => c.ZoneId == zone.Id))
            {
                var value = Math.Round(channel.Base + Wobble(channel, now), 1);
                var req = pot.Requirements.Metrics.FirstOrDefault(r => r.Metric == channel.Metric && r.Enabled);
                var state = ReadingState.Ok;
                string? direction = null;
                if (req?.Min is { } min && value < min) { state = ReadingState.Attention; direction = "below"; }
                else if (req?.Max is { } max && value > max) { state = ReadingState.Attention; direction = "above"; }

                metrics.Add(new MetricReading
                {
                    Metric = channel.Metric, ChannelId = channel.Id, Value = value, Unit = channel.Unit,
                    State = state, Direction = direction, UpdatedAt = now.AddSeconds(-5),
                });
                if (state == ReadingState.Attention)
                {
                    alerts.Add(new ActiveAlert
                    {
                        Id = $"{channel.Id}-{direction}", Metric = channel.Metric, ZoneId = zone.Id,
                        Since = now.AddMinutes(-12),
                        Message = $"{zone.Label ?? $"Zone {zone.Row * (pot.Pot.GridCols ?? 1) + zone.Col + 1}"}: {Key(channel.Metric)} is {direction} the target range",
                    });
                }
            }

            var zoneState = metrics.Count == 0 ? ReadingState.Unknown
                : metrics.Any(m => m.State == ReadingState.Attention) ? ReadingState.Attention : ReadingState.Ok;
            zones.Add(new ZoneStatus { ZoneId = zone.Id, State = zoneState, Metrics = metrics });
        }

        var worst = zones.FirstOrDefault(z => z.State == ReadingState.Attention);
        return new PotStatus
        {
            PotId = pot.Pot.Id,
            State = worst is not null ? ReadingState.Attention
                : zones.All(z => z.State == ReadingState.Unknown) ? ReadingState.Unknown : ReadingState.Ok,
            WorstZoneId = worst?.ZoneId,
            Zones = zones,
            Alerts = alerts,
        };
    }

    // ---- zones ---------------------------------------------------------------------------------------

    public static List<Zone> BuildZones(int rows, int cols, bool simple)
    {
        if (simple) return [new Zone { Id = Guid.NewGuid(), Row = 0, Col = 0 }];
        var zones = new List<Zone>();
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                zones.Add(new Zone { Id = Guid.NewGuid(), Row = r, Col = c });
            }
        }
        return zones;
    }

    public PotState CreatePot(string name, string? location, PotKind kind)
    {
        var pot = new PotState
        {
            Pot = new Pot
            {
                Id = Guid.NewGuid(), Name = name, Location = location, Kind = kind, ViewMode = ViewMode.Simple,
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };
        pot.Zones = BuildZones(1, 1, simple: true);
        pot.Requirements = Suggest(pot).Requirements;
        Pots[pot.Pot.Id] = pot;
        return pot;
    }

    // ---- seed ----------------------------------------------------------------------------------------

    private Channel AddChannel(PotState pot, string id, Metric metric, Zone zone, string unit, double baseValue, bool calibrated = true)
    {
        var channel = new Channel
        {
            Id = id, Metric = metric, ZoneId = zone.Id, Unit = unit, Base = baseValue,
            Calibration = calibrated ? new Calibration { DryRaw = 3100, WetRaw = 1300, CalibratedAt = DateTimeOffset.UtcNow.AddDays(-20) } : null,
        };
        pot.Channels.Add(channel);
        return channel;
    }

    private Plant AddPlant(PotState pot, string speciesId, string nickname, Zone? zone, int daysAgo)
    {
        var plant = new Plant
        {
            Id = Guid.NewGuid(), SpeciesId = speciesId, Nickname = nickname, ZoneId = zone?.Id,
            PlantedAt = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-daysAgo)),
        };
        pot.Plants.Add(plant);
        return plant;
    }

    private static SafetyLimits HardLimits(ActuatorKind kind) => kind switch
    {
        ActuatorKind.Pump => new SafetyLimits { MaxRunSeconds = 60, MinIntervalSeconds = 600, DailyCapSeconds = 600, MaxFruitlessRuns = 5 },
        ActuatorKind.Heater => new SafetyLimits { MaxRunSeconds = 1800, MinIntervalSeconds = 300, DailyCapSeconds = 14400 },
        _ => new SafetyLimits { MaxRunSeconds = 3600, MinIntervalSeconds = 0, DailyCapSeconds = 43200 },
    };

    private static Actuator NewActuator(string id, ActuatorKind kind, Metric metric, ControlMode mode, SafetyLimits limits) => new()
    {
        Id = id, Kind = kind, Metric = metric, Mode = mode, State = ActuatorState.Idle, Limits = limits, HardLimits = HardLimits(kind),
        LastRunAt = DateTimeOffset.UtcNow.AddHours(-5),
    };

    /// <summary>What happened to the demo user, newest last in the list: one unread alert, one unread offline, one read plan change.</summary>
    public List<Notification> Notifications { get; } = [];

    private void SeedNotifications()
    {
        Notifications.Clear();
        var herbs = Pots.Values.FirstOrDefault(p => p.Pot.Name == "Kitchen herbs");
        var now = DateTimeOffset.UtcNow;
        Notifications.Add(new Notification { Id = Guid.NewGuid(), Kind = "plan_changed", Params = new() { ["from"] = "gardener", ["to"] = "novice" }, CreatedAt = now.AddDays(-2), ReadAt = now.AddDays(-2).AddMinutes(5) });
        Notifications.Add(new Notification { Id = Guid.NewGuid(), Kind = "device_offline", PotId = herbs?.Pot.Id, Params = new() { ["deviceId"] = "ghx-demo-0001" }, CreatedAt = now.AddHours(-3) });
        Notifications.Add(new Notification { Id = Guid.NewGuid(), Kind = "alert_raised", PotId = herbs?.Pot.Id, Params = new() { ["metric"] = "soil_moisture", ["direction"] = "below", ["value"] = "31.5", ["zone"] = "the pot" }, CreatedAt = now.AddMinutes(-20) });
    }

    private void Seed()
    {
        // 1. Kitchen herbs: a simple pot with one pump and everything in range.
        var herbs = CreatePot("Kitchen herbs", "Kitchen window", PotKind.Pot);
        var herbsZone = herbs.Zones[0];
        AddPlant(herbs, "basil", "Basil", null, 21);
        AddPlant(herbs, "mint", "Mint", null, 35);
        herbs.Requirements = Suggest(herbs).Requirements;
        AddChannel(herbs, "temperature_z0", Metric.Temperature, herbsZone, "°C", 22.4);
        AddChannel(herbs, "air_humidity_z0", Metric.AirHumidity, herbsZone, "%", 52);
        AddChannel(herbs, "soil_moisture_z0", Metric.SoilMoisture, herbsZone, "%", 58);
        AddChannel(herbs, "light_z0", Metric.Light, herbsZone, "lux", 24000);
        herbs.Actuators.Add(NewActuator("pump-1", ActuatorKind.Pump, Metric.SoilMoisture, ControlMode.Auto,
            new SafetyLimits { MaxRunSeconds = 20, MinIntervalSeconds = 3600, DailyCapSeconds = 120, MaxFruitlessRuns = 3 }));
        herbs.Actuators.Add(NewActuator("lamp-1", ActuatorKind.Lamp, Metric.Light, ControlMode.Notify,
            new SafetyLimits { MaxRunSeconds = 3600, MinIntervalSeconds = 0, DailyCapSeconds = 43200 }));
        herbs.Devices.Add(new LinkedDevice
        {
            DeviceId = "ghx-pot-kitchen", Model = "Greenhaus Pot Controller P1", Status = "online",
            LastSeenAt = DateTimeOffset.UtcNow.AddSeconds(-4), Firmware = "0.3.1", ThermalCutoffDeclared = true,
        });
        herbs.Audit.Add(new AuditEntry
        {
            Id = Guid.NewGuid(), At = DateTimeOffset.UtcNow.AddHours(-5), Actor = "automation", ActuatorId = "pump-1",
            Action = "run", Reason = "Soil moisture was below the minimum", Outcome = "completed",
        });
        herbs.Tasks.Add(new CareTask
        {
            Id = Guid.NewGuid(), Kind = "fertilize", Title = "Fertilize the basil", DueAt = DateTimeOffset.UtcNow.AddDays(2),
            State = "open", Origin = "species_schedule",
        });
        herbs.Tasks.Add(new CareTask
        {
            Id = Guid.NewGuid(), Kind = "check_pump", Title = "Check the pump and the float switch", DueAt = DateTimeOffset.UtcNow.AddDays(9),
            State = "open", Origin = "safety_schedule",
        });

        // 2. Balcony bed: a 2x2 zone grid with a dry zone and an alkaline zone, an uncalibrated sensor and a heater with no cutoff.
        var bed = CreatePot("Balcony bed", "Balcony", PotKind.Bed);
        bed.Pot.ViewMode = ViewMode.Zones;
        bed.Pot.GridRows = 2;
        bed.Pot.GridCols = 2;
        bed.Zones = BuildZones(2, 2, simple: false);
        AddPlant(bed, "tomato", "Cherry tomato", bed.Zones[0], 50);
        AddPlant(bed, "lettuce", "Lettuce", bed.Zones[2], 18);
        bed.Requirements = Suggest(bed).Requirements;
        bed.Requirements.Metrics.First(m => m.Metric == Metric.Ph).Enabled = true;
        AddChannel(bed, "temperature_z1", Metric.Temperature, bed.Zones[0], "°C", 21.0);
        var soilBase = new[] { 62.0, 63.0, 41.0, 60.0 };
        var phBase = new[] { 6.4, 7.6, 6.3, 6.5 };
        for (var i = 0; i < 4; i++)
        {
            AddChannel(bed, $"soil_moisture_z{i + 1}", Metric.SoilMoisture, bed.Zones[i], "%", soilBase[i], calibrated: false);
            AddChannel(bed, $"ph_z{i + 1}", Metric.Ph, bed.Zones[i], "pH", phBase[i]);
        }
        bed.Actuators.Add(NewActuator("pump-1", ActuatorKind.Pump, Metric.SoilMoisture, ControlMode.Notify,
            new SafetyLimits { MaxRunSeconds = 30, MinIntervalSeconds = 1800, DailyCapSeconds = 240, MaxFruitlessRuns = 3 }));
        bed.Actuators.Add(NewActuator("heater-1", ActuatorKind.Heater, Metric.Temperature, ControlMode.Notify,
            new SafetyLimits { MaxRunSeconds = 900, MinIntervalSeconds = 600, DailyCapSeconds = 7200 }));
        bed.Devices.Add(new LinkedDevice
        {
            DeviceId = "ghx-bed-balcony", Model = "Greenhaus Bed Controller B4", Status = "online",
            LastSeenAt = DateTimeOffset.UtcNow.AddSeconds(-9), Firmware = "0.3.1", ThermalCutoffDeclared = false,
        });
        bed.Tasks.Add(new CareTask
        {
            Id = Guid.NewGuid(), Kind = "ph_check", Title = "Zone 2 is more alkaline: check the fertilizer", DueAt = DateTimeOffset.UtcNow.AddDays(1),
            State = "open", Origin = "ph_sensor",
        });

        // 3. Window sill: basil and a cactus disagree about moisture, which shows the compatibility warning.
        var sill = CreatePot("Window sill", "Living room", PotKind.Pot);
        AddPlant(sill, "basil", "Basil", null, 10);
        AddPlant(sill, "cactus", "Cactus", null, 200);
        sill.Requirements = Suggest(sill).Requirements;

        // Devices a customer could scan.
        Devices["ghx-demo-0001"] = new ClaimableDevice
        {
            Id = "ghx-demo-0001", Model = "Greenhaus Pot Controller P1", Token = "demo-token", Shipped = true,
            Channels = [new() { ChannelId = "soil_moisture_c1", Metric = Metric.SoilMoisture }, new() { ChannelId = "temperature_c1", Metric = Metric.Temperature }],
            Actuators = [new() { Id = "pump-1", Kind = ActuatorKind.Pump }],
        };
        Devices["ghx-demo-0002"] = new ClaimableDevice
        {
            Id = "ghx-demo-0002", Model = "Greenhaus Pot Controller P1", Token = "demo-token-2", Shipped = false,
            Channels = [new() { ChannelId = "soil_moisture_c1", Metric = Metric.SoilMoisture }],
        };
        Devices["ghx-demo-0003"] = new ClaimableDevice
        {
            Id = "ghx-demo-0003", Model = "Greenhaus Pot Controller P1", Token = "expired-token", Shipped = true, Expired = true,
            Channels = [new() { ChannelId = "soil_moisture_c1", Metric = Metric.SoilMoisture }],
        };
    }
}
