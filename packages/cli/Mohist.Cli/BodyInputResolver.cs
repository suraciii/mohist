namespace Mohist.Cli;

internal static class BodyInputResolver
{
    public abstract record Result
    {
        private Result() { }

        public sealed record Success(string Body) : Result;

        public sealed record Failure(string Message) : Result;
    }

    /// <summary>
    /// Generic labels for a body-source flag set. Each surface (issue body,
    /// agent launch prompt, agent session followup text, ...) names its three
    /// flags so error messages can name the right option without duplicating
    /// the resolution loop.
    /// </summary>
    public sealed record SourceFlags(
        string InlineFlag,
        string FileFlag,
        string BodyKind);

    public static Task<Result> ResolveAsync(
        string? inlineBody,
        string? bodyFile,
        IFileSystem fileSystem,
        TextReader standardInput,
        TextWriter error) =>
        ResolveAsync(inlineBody, bodyFile,
            new SourceFlags("--body", "--body-file", "issue body"),
            fileSystem, standardInput, error);

    public static async Task<Result> ResolveAsync(
        string? inlineBody,
        string? bodyFile,
        SourceFlags flags,
        IFileSystem fileSystem,
        TextReader standardInput,
        TextWriter error)
    {
        var hasInline = inlineBody is not null;
        var hasFile = !string.IsNullOrWhiteSpace(bodyFile);
        var providedCount = (hasInline ? 1 : 0) + (hasFile ? 1 : 0);
        if (providedCount == 0)
        {
            var message = $"{flags.BodyKind} is required (use {flags.InlineFlag} or {flags.FileFlag})";
            await error.WriteLineAsync(message)
                .ConfigureAwait(false);
            return new Result.Failure(message);
        }

        if (providedCount > 1)
        {
            var provided = new List<string>();
            if (hasInline) provided.Add(flags.InlineFlag);
            if (hasFile) provided.Add(flags.FileFlag);
            var message = $"the following options are mutually exclusive: {string.Join(", ", provided)}; pass only one";
            await error.WriteLineAsync(message)
                .ConfigureAwait(false);
            return new Result.Failure(message);
        }

        string resolved;
        if (hasInline)
        {
            resolved = inlineBody!;
        }
        else if (bodyFile == "-")
        {
            var text = await standardInput.ReadToEndAsync().ConfigureAwait(false);
            resolved = text ?? string.Empty;
        }
        else
        {
            try
            {
                var text = await fileSystem.ReadAllTextAsync(bodyFile!).ConfigureAwait(false);
                resolved = text ?? string.Empty;
            }
            catch (Exception ex)
            {
                var message = $"could not read body file: {bodyFile} ({ex.Message})";
                await error.WriteLineAsync(message)
                    .ConfigureAwait(false);
                return new Result.Failure(message);
            }
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            var message = $"{flags.BodyKind} is required (resolved body is empty)";
            await error.WriteLineAsync(message)
                .ConfigureAwait(false);
            return new Result.Failure(message);
        }

        return new Result.Success(resolved);
    }
}
