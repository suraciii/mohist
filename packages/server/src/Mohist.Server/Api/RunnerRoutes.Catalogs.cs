using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    private static Dictionary<string, RuntimeCatalogEntry>? NormalizeRuntimeCatalogs(Dictionary<string, RuntimeCatalogEntry>? catalogs)
    {
        if (catalogs is null || catalogs.Count == 0)
            return null;

        var normalized = new Dictionary<string, RuntimeCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalogs)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null)
                continue;

            var models = (entry.Value.Models ?? [])
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Where(model => model.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            normalized[entry.Key.Trim()] = new RuntimeCatalogEntry(
                models,
                NormalizeCoderModelVariants(entry.Value.Variants),
                entry.Value.SupportsReasoningEffort,
                entry.Value.Complete,
                NormalizeIdentity(entry.Value.CapabilityRevision),
                NormalizeCoderModelVariants(entry.Value.ReasoningEfforts));
        }

        return normalized.Count == 0 ? null : normalized;
    }
}
