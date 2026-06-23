using System.Text.Json;

namespace Mohist.Cli;

internal static class JsonInputResolver
{
    public abstract record Result
    {
        private Result() { }

        public sealed record Success(object? Value) : Result;

        public sealed record Failure(string Message) : Result;
    }

    /// <summary>
    /// Resolves a JSON-or-<c>@file</c> reference into a deserialized object.
    /// If <paramref name="raw"/> starts with <c>@</c>, the suffix is treated
    /// as a UTF-8 file path and the file content is parsed as JSON. Otherwise
    /// the value itself is parsed as JSON. Returns <see cref="Result.Failure"/>
    /// with a user-facing message on bad input; the caller writes it to the
    /// error stream and exits with code 1.
    /// </summary>
    public static async Task<Result> ResolveAsync(
        string? raw,
        IFileSystem fileSystem,
        TextWriter error,
        string optionName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            await error.WriteLineAsync(
                $"{optionName} requires a JSON object or @<file> reference").ConfigureAwait(false);
            return new Result.Failure($"{optionName} requires a JSON object or @<file> reference");
        }

        var text = raw!;
        if (text.StartsWith('@'))
        {
            var path = text[1..];
            if (string.IsNullOrWhiteSpace(path))
            {
                await error.WriteLineAsync(
                    $"{optionName}: '@' must be followed by a file path").ConfigureAwait(false);
                return new Result.Failure($"{optionName}: missing file path after '@'");
            }
            try
            {
                text = await fileSystem.ReadAllTextAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await error.WriteLineAsync(
                    $"{optionName}: could not read file '{path}' ({ex.Message})").ConfigureAwait(false);
                return new Result.Failure($"{optionName}: could not read file '{path}'");
            }
        }

        try
        {
            var value = JsonSerializer.Deserialize<object?>(text);
            return new Result.Success(value);
        }
        catch (JsonException ex)
        {
            await error.WriteLineAsync(
                $"{optionName}: invalid JSON ({ex.Message})").ConfigureAwait(false);
            return new Result.Failure($"{optionName}: invalid JSON");
        }
    }
}