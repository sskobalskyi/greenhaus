using System.Text.Json;

namespace Greenhaus.Domain;

public sealed record Species(string Id, string CommonName, string ScientificName, string? Category, IReadOnlyDictionary<Metric, Range> Preferred, int? FertilizerIntervalDays);

/// <summary>The curated catalog of plants, read from data/species.json.</summary>
public sealed class SpeciesCatalog
{
    public IReadOnlyList<Species> All { get; }

    public SpeciesCatalog(IReadOnlyList<Species> all) => All = all;

    public Species? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    public IReadOnlyList<Species> Search(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? All
            : [.. All.Where(s => s.CommonName.Contains(query, StringComparison.OrdinalIgnoreCase) || s.ScientificName.Contains(query, StringComparison.OrdinalIgnoreCase))];

    public static SpeciesCatalog Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var species = document.RootElement.GetProperty("species").EnumerateArray().Select(element =>
        {
            var preferred = new Dictionary<Metric, Range>();
            foreach (var property in element.GetProperty("preferred").EnumerateObject())
            {
                if (MetricNames.TryParse(property.Name, out var metric))
                    preferred[metric] = new Range(property.Value.GetProperty("min").GetDouble(), property.Value.GetProperty("max").GetDouble());
            }
            return new Species(
                element.GetProperty("id").GetString()!,
                element.GetProperty("commonName").GetString()!,
                element.GetProperty("scientificName").GetString()!,
                element.TryGetProperty("category", out var category) ? category.GetString() : null,
                preferred,
                element.TryGetProperty("fertilizerIntervalDays", out var interval) && interval.ValueKind == JsonValueKind.Number ? interval.GetInt32() : null);
        }).ToList();
        return new SpeciesCatalog(species);
    }
}

/// <summary>The snake_case names metrics have on the wire.</summary>
public static class MetricNames
{
    public static string Key(Metric metric) => JsonNamingPolicy.SnakeCaseLower.ConvertName(metric.ToString());

    public static bool TryParse(string key, out Metric metric)
    {
        foreach (var candidate in Enum.GetValues<Metric>())
        {
            if (Key(candidate) == key) { metric = candidate; return true; }
        }
        metric = default;
        return false;
    }
}