using System.Text.Json;

namespace Greenhaus.KitEmulator;

public sealed record ChannelDef(string Id, string Metric, string? Unit);

public sealed record HardLimits(int MaxRunSeconds, int MinIntervalSeconds, int DailyCapSeconds);

public sealed record ActuatorDef(string Id, string Kind, bool ThermalCutoff, HardLimits HardLimits);

/// <summary>What a kit contains, as data/kit-capabilities.json describes it: the same shape the Pulse device API registers.</summary>
public sealed record KitDescription(string Sku, string Model, IReadOnlyList<ChannelDef> Channels, IReadOnlyList<ActuatorDef> Actuators)
{
    public ChannelDef? ChannelFor(string metric) => Channels.FirstOrDefault(c => c.Metric == metric);

    public ActuatorDef? ActuatorOfKind(string kind) => Actuators.FirstOrDefault(a => a.Kind == kind);
}

public static class Kits
{
    public static IReadOnlyDictionary<string, KitDescription> Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var kits = new Dictionary<string, KitDescription>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var kit = property.Value;
            var channels = kit.GetProperty("channels").EnumerateArray()
                .Select(c => new ChannelDef(c.GetProperty("id").GetString()!, c.GetProperty("metric").GetString()!, c.TryGetProperty("unit", out var u) ? u.GetString() : null)).ToList();
            var actuators = kit.GetProperty("actuators").EnumerateArray().Select(a =>
            {
                var limits = a.GetProperty("hardLimits");
                return new ActuatorDef(a.GetProperty("id").GetString()!, a.GetProperty("kind").GetString()!,
                    a.TryGetProperty("thermalCutoff", out var cutoff) && cutoff.GetBoolean(),
                    new HardLimits(limits.GetProperty("maxRunSeconds").GetInt32(), limits.GetProperty("minIntervalSeconds").GetInt32(), limits.GetProperty("dailyCapSeconds").GetInt32()));
            }).ToList();
            kits[property.Name] = new KitDescription(property.Name, kit.GetProperty("model").GetString()!, channels, actuators);
        }
        return kits;
    }
}