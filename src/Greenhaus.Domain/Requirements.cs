namespace Greenhaus.Domain;

public sealed record RequirementDraft(Metric Metric, bool Enabled, double? Min, double? Max, double? Margin);

public sealed record RequirementConflict(Metric Metric, IReadOnlyList<Guid> Plants);

public sealed record RequirementsSuggestion(IReadOnlyList<RequirementDraft> Requirements, IReadOnlyList<RequirementConflict> Conflicts);

public sealed record PlantPreference(Guid PlantId, IReadOnlyDictionary<Metric, Range> Preferred);

public static class RequirementsSuggester
{
    /// <summary>
    /// The suggestion for a pot is the intersection of the plants' ranges. Where the ranges do not overlap the first
    /// plant's range is kept and the metric is reported as a conflict, so the user decides. pH is optional because it
    /// needs a probe, so it is suggested but not switched on.
    /// </summary>
    public static RequirementsSuggestion Suggest(IReadOnlyList<PlantPreference> plants)
    {
        var drafts = new List<RequirementDraft>();
        var conflicts = new List<RequirementConflict>();
        foreach (var metric in Enum.GetValues<Metric>())
        {
            var withRange = plants.Where(p => p.Preferred.ContainsKey(metric)).ToList();
            if (withRange.Count == 0)
            {
                drafts.Add(new RequirementDraft(metric, false, null, null, null));
                continue;
            }

            var min = withRange.Max(p => p.Preferred[metric].Min);
            var max = withRange.Min(p => p.Preferred[metric].Max);
            if (min > max)
            {
                conflicts.Add(new RequirementConflict(metric, [.. withRange.Select(p => p.PlantId)]));
                (min, max) = (withRange[0].Preferred[metric].Min, withRange[0].Preferred[metric].Max);
            }

            drafts.Add(new RequirementDraft(metric, metric != Metric.Ph, min, max, Math.Round((max - min) * 0.05, 1)));
        }

        return new RequirementsSuggestion(drafts, conflicts);
    }

    public static bool IsValid(IEnumerable<RequirementDraft> requirements, out string? problem)
    {
        var list = requirements.ToList();
        if (list.GroupBy(r => r.Metric).Any(g => g.Count() > 1)) { problem = "Each metric may appear once"; return false; }
        if (list.Any(r => r.Min is { } min && r.Max is { } max && min > max)) { problem = "A minimum cannot be above its maximum"; return false; }
        problem = null;
        return true;
    }
}

public static class ReadingEvaluator
{
    /// <summary>Compares one reading with the pot's target for its metric. A reading is never averaged with another.</summary>
    public static (ReadingState State, string? Direction) Evaluate(double value, RequirementDraft? target)
    {
        if (target is not { Enabled: true }) return (ReadingState.Ok, null);
        if (target.Min is { } min && value < min) return (ReadingState.Attention, "below");
        if (target.Max is { } max && value > max) return (ReadingState.Attention, "above");
        return (ReadingState.Ok, null);
    }

    /// <summary>A zone is as bad as its worst reading, and a pot as bad as its worst zone.</summary>
    public static ReadingState Worst(IEnumerable<ReadingState> states)
    {
        var list = states.ToList();
        if (list.Count == 0 || list.All(s => s == ReadingState.Unknown)) return ReadingState.Unknown;
        if (list.Contains(ReadingState.Attention)) return ReadingState.Attention;
        if (list.Contains(ReadingState.Offline)) return ReadingState.Offline;
        return ReadingState.Ok;
    }
}