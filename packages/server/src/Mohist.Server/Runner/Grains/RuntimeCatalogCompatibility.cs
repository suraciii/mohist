namespace Mohist.Server.Runner.Grains;

internal static class RuntimeCatalogCompatibility
{
    public static bool AcceptsModel(string runtime, RuntimeCatalogEntry catalog, string? model) =>
        model is null
        || IsRuntimeValidated(runtime)
        || catalog.Models is not { Length: > 0 }
        || catalog.Models.Any(value => string.Equals(value, model, StringComparison.OrdinalIgnoreCase));

    public static bool AcceptsVariant(string runtime, RuntimeCatalogEntry catalog, string? model, string? variant)
    {
        if (variant is null || IsRuntimeValidated(runtime) || catalog.Variants is not { Count: > 0 })
            return true;
        if (model is null)
            return false;

        var values = catalog.Variants.FirstOrDefault(entry =>
            string.Equals(entry.Key, model, StringComparison.OrdinalIgnoreCase)).Value;
        return values?.Any(value => string.Equals(value, variant, StringComparison.Ordinal)) == true;
    }

    private static bool IsRuntimeValidated(string runtime) =>
        string.Equals(runtime, "opencode", StringComparison.OrdinalIgnoreCase);
}
