namespace Mohist.Cli;

internal static class BodyInputResolver
{
    public abstract record Result
    {
        private Result() { }

        public sealed record Success(string Body) : Result;

        public sealed record Failure(string Message) : Result;
    }

    public static async Task<Result> ResolveAsync(
        string? inlineBody,
        string? bodyFile,
        bool bodyStdin,
        IFileSystem fileSystem,
        TextReader standardInput,
        TextWriter error)
    {
        var hasInline = !string.IsNullOrWhiteSpace(inlineBody);
        var hasFile = !string.IsNullOrWhiteSpace(bodyFile);
        var hasStdin = bodyStdin;

        var providedCount = (hasInline ? 1 : 0) + (hasFile ? 1 : 0) + (hasStdin ? 1 : 0);
        if (providedCount == 0)
        {
            await error.WriteLineAsync("issue body is required (use --body, --body-file, or --body-stdin)").ConfigureAwait(false);
            return new Result.Failure("issue body is required");
        }

        if (providedCount > 1)
        {
            var provided = new List<string>();
            if (hasInline) provided.Add("--body");
            if (hasFile) provided.Add("--body-file");
            if (hasStdin) provided.Add("--body-stdin");
            await error.WriteLineAsync(
                $"the following options are mutually exclusive: {string.Join(", ", provided)}; pass only one")
                .ConfigureAwait(false);
            return new Result.Failure($"mutually exclusive body sources: {string.Join(", ", provided)}");
        }

        if (hasInline)
            return new Result.Success(inlineBody!);

        if (hasStdin)
        {
            var text = await standardInput.ReadToEndAsync().ConfigureAwait(false);
            return new Result.Success(text ?? string.Empty);
        }

        try
        {
            var text = await fileSystem.ReadAllTextAsync(bodyFile!).ConfigureAwait(false);
            return new Result.Success(text ?? string.Empty);
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"could not read body file: {bodyFile} ({ex.Message})").ConfigureAwait(false);
            return new Result.Failure($"could not read body file: {bodyFile}");
        }
    }
}
