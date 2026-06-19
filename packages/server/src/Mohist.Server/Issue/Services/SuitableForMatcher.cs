namespace Mohist.Server.Issue.Services;

public static class SuitableForMatcher
{
    public static bool Matches(IReadOnlyList<string> suitableFor, string? context)
    {
        if (suitableFor.Count == 0) return false;
        if (string.IsNullOrWhiteSpace(context)) return false;

        return suitableFor.Any(value =>
            string.Equals(value, context, StringComparison.OrdinalIgnoreCase));
    }
}
