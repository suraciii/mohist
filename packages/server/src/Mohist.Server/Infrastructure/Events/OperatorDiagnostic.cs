using System.Text;
using System.Text.RegularExpressions;

namespace Mohist.Server.Infrastructure.Events;

public static partial class OperatorDiagnostic
{
    private const int MaximumLength = 1024;
    private const int MaximumInputLength = 4096;

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
        if (firstLine.Length > MaximumInputLength)
            firstLine = firstLine[..MaximumInputLength];

        var sanitized = RemoveControls(AnsiEscapePattern().Replace(firstLine.ToString(), string.Empty));
        sanitized = StackFramePattern().Replace(sanitized, "[stack]");
        sanitized = UncPathPattern().Replace(sanitized, "[path]");
        sanitized = PathPattern().Replace(sanitized, "[path]");
        sanitized = WhitespacePattern().Replace(sanitized, " ").Trim();
        var summary = sanitized.Length <= MaximumLength
            ? sanitized
            : sanitized[..MaximumLength].TrimEnd();
        return summary.Length == 0 ? null : summary;
    }

    private static string RemoveControls(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            result.Append(char.IsControl(character) ? ' ' : character);
        }
        return result.ToString();
    }

    [GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapePattern();

    [GeneratedRegex(@"(?<!\S)at\s+[\p{L}\p{N}_.$+`<>\[\],]+\([^)]*\)(?:\s+in\s+.*)?", RegexOptions.CultureInvariant)]
    private static partial Regex StackFramePattern();

    [GeneratedRegex(@"\\\\[^\\\s'""<>()\[\]{}]+\\[^\s'""<>()\[\]{}]+", RegexOptions.CultureInvariant)]
    private static partial Regex UncPathPattern();

    [GeneratedRegex(@"(?:file://)?(?:[a-zA-Z]:[\\/]|/)[^\s'""<>()\[\]{}]+", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
