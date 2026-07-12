using System.Text;

namespace Mohist.Server.Infrastructure.Events;

public static class OperatorDiagnostic
{
    private const int MaximumLength = 1024;

    public static string? Summarize(Exception? exception) =>
        exception is null
            ? null
            : Summarize($"{exception.GetType().Name}: {exception.Message}");

    public static string? Summarize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var lineEnd = value.AsSpan().IndexOfAny('\r', '\n');
        var firstLine = lineEnd >= 0 ? value.AsSpan(0, lineEnd) : value.AsSpan();
        var result = new StringBuilder(Math.Min(firstLine.Length, MaximumLength));

        foreach (var token in firstLine.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.Length > 0)
                result.Append(' ');
            result.Append(LooksLikePath(token) ? "[path]" : RemoveControls(token));
            if (result.Length >= MaximumLength)
                break;
        }

        if (result.Length > MaximumLength)
            result.Length = MaximumLength;
        var summary = result.ToString().Trim();
        return summary.Length == 0 ? null : summary;
    }

    private static bool LooksLikePath(string token)
    {
        var candidate = token.TrimStart('(', '[', '{', '\'', '"');
        return candidate.StartsWith('/', StringComparison.Ordinal)
            || (candidate.Length >= 3
                && char.IsAsciiLetter(candidate[0])
                && candidate[1] == ':'
                && candidate[2] is '/' or '\\');
    }

    private static string RemoveControls(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsControl(character))
                result.Append(character);
        }
        return result.ToString();
    }
}
