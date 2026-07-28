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

    public static async Task<Result> ResolveAsync(
        string? raw,
        IFileSystem fileSystem,
        TextWriter error,
        string optionName)
    {
        return await ResolveAsync(raw, null, fileSystem, TextReader.Null, error, optionName, null).ConfigureAwait(false);
    }

    public static async Task<Result> ResolveAsync(
        string? inline,
        string? file,
        IFileSystem fileSystem,
        TextReader standardInput,
        TextWriter error,
        string inlineOptionName,
        string? fileOptionName)
    {
        var hasInline = inline is not null;
        var hasFile = file is not null;
        if (hasInline && hasFile)
        {
            var message = $"{inlineOptionName} and {fileOptionName} are mutually exclusive";
            await error.WriteLineAsync(message).ConfigureAwait(false);
            return new Result.Failure(message);
        }

        if (!hasInline && !hasFile)
        {
            await error.WriteLineAsync(
                $"{inlineOptionName} or {fileOptionName ?? "a JSON value"} is required").ConfigureAwait(false);
            return new Result.Failure($"{inlineOptionName} or {fileOptionName ?? "a JSON value"} is required");
        }

        var text = inline;
        if (hasFile)
        {
            if (file == "-")
            {
                text = await standardInput.ReadToEndAsync().ConfigureAwait(false);
            }
            else
            {
                try
                {
                    text = await fileSystem.ReadAllTextAsync(file!).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var message = $"{fileOptionName}: could not read file '{file}' ({ex.Message})";
                    await error.WriteLineAsync(message).ConfigureAwait(false);
                    return new Result.Failure(message);
                }
            }
        }

        try
        {
            var value = JsonSerializer.Deserialize<object?>(text ?? string.Empty);
            return new Result.Success(value);
        }
        catch (JsonException ex)
        {
            await error.WriteLineAsync(
                $"{inlineOptionName}: invalid JSON ({ex.Message})").ConfigureAwait(false);
            return new Result.Failure($"{inlineOptionName}: invalid JSON");
        }
    }
}
