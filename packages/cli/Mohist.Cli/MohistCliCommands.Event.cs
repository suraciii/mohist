using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class EventCommands
{
    private static readonly AsyncLocal<CancellationToken> TailCancellation = new();

    internal static CancellationToken TailCancellationOverride
    {
        get => TailCancellation.Value;
        set => TailCancellation.Value = value;
    }

    public static Command Build(MohistCliApi api, CliCredentialProvider credentials)
    {
        var eventCommand = new Command(
            "event",
            "Subscribe to realtime Event envelopes and inspect or recover failed deliveries.");
        eventCommand.Subcommands.Add(BuildTail(api));
        var deadLetter = new Command(
            "dead-letter",
            "Inspect current failed deliveries and retry them with explicit recovery side effects.");
        deadLetter.Subcommands.Add(BuildList(api, credentials));
        deadLetter.Subcommands.Add(BuildRedeliver(api, credentials));
        eventCommand.Subcommands.Add(deadLetter);
        return eventCommand;
    }

    private static Command BuildTail(MohistCliApi api)
    {
        var descriptor = new ResourceDescriptor(
            ResourceCardinality.Stream,
            ["type", "source", "id", "time", "specversion", "subject", "extensions", "data"]);
        var cmd = new Command(
            "tail",
            "Subscribe to realtime Event envelopes from subscription establishment; emit one NDJSON object per line. With --match, only matching events are emitted.");
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var matchOpt = new Option<string?>("--match") { Description = "Match expression (CEL subset) forwarded to the server; the server is the single compile authority" };
        var jsonOpt = MohistCliCommands.JsonSelectionOption(descriptor);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(matchOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(async ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var match = ctx.GetValue(matchOpt);
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(descriptor, ctx.GetResult(jsonOpt) is not null, json);
            if (selection.Kind == JsonSelectionKind.Discovery || selection.Kind == JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(descriptor, selection);

            var (resolved, resolveExit) = await api.ResolveProject(project).ConfigureAwait(false);
            if (resolveExit != 0)
                return resolveExit;

            var path = $"/api/projects/{Uri.EscapeDataString(resolved)}/events/tail";
            if (!string.IsNullOrWhiteSpace(match))
                path += $"?match={Uri.EscapeDataString(match!)}";

            return await RunTailAsync(api, path, selection, descriptor).ConfigureAwait(false);
        });
        return cmd;
    }

    internal static async Task<int> RunTailAsync(MohistCliApi api, string path, JsonSelection? selection = null, ResourceDescriptor? descriptor = null)
    {
        if (selection is { Kind: JsonSelectionKind.Selected } && descriptor is not null)
        {
            var token = TailCancellationOverride != default
                ? TailCancellationOverride
                : api.Invocation.CancellationToken;
            return await NdjsonStream.ReadSelectedAsync(api.Http, path, api.Output, api.Error, selection, token)
                .ConfigureAwait(false);
        }
        if (TailCancellationOverride != default)
            return await NdjsonStream.ReadAsync(api.Http, path, api.Output, api.Error, TailCancellationOverride)
                .ConfigureAwait(false);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += OnCancel;
        try
        {
            return await NdjsonStream.ReadAsync(api.Http, path, api.Output, api.Error, cts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            cts.Dispose();
        }

        void OnCancel(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static Command BuildList(MohistCliApi api, CliCredentialProvider credentials)
    {
        var cmd = new Command(
            "list",
            "List current unresolved event deliveries for operator recovery. Redeliver retries the recorded failing handler and may repeat delivery side effects.");
        var handlerOpt = new Option<string?>("--handler") { Description = "Filter by failing handler full name" };
        var limitOpt = new Option<int>("--limit")
        {
            Description = "Maximum rows to return (1-500)",
            DefaultValueFactory = _ => 100,
        };
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.DeadLetterList)));
        cmd.Options.Add(handlerOpt);
        cmd.Options.Add(limitOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(async ctx =>
        {
            var handler = ctx.GetValue(handlerOpt);
            var limit = ctx.GetValue(limitOpt);
            var output = ctx.GetValue(outputOpt);
            if (limit is < 1 or > 500)
            {
                api.Error.WriteLine("--limit must be between 1 and 500");
                return 1;
            }

            var (mode, exit) = api.ResolveOutputMode(output);
            if (exit != 0)
                return exit;

            var headers = await GetAuthorizationHeadersAsync(api, credentials).ConfigureAwait(false);
            if (headers is null)
                return 1;

            var query = $"?limit={limit}";
            if (!string.IsNullOrWhiteSpace(handler))
                query += $"&handler={MohistCliCommands.Escape(handler)}";
            return await api.PrintWithOutputAsync(
                $"/api/events/dead-letters{query}",
                mode,
                nameof(MohistCliApi.TableShape.DeadLetterList),
                headers).ConfigureAwait(false);
        });
        return cmd;
    }

    private static Command BuildRedeliver(MohistCliApi api, CliCredentialProvider credentials)
    {
        var cmd = new Command(
            "redeliver",
            "Retry the failing handler recorded by a dead-letter row; this recovery action may repeat delivery side effects.");
        var idArg = new Argument<long>("id") { Description = "Dead-letter id" };
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.DeadLetterRedelivery)));
        cmd.Arguments.Add(idArg);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(async ctx =>
        {
            var id = ctx.GetValue(idArg);
            var output = ctx.GetValue(outputOpt);
            if (id <= 0)
            {
                api.Error.WriteLine("id must be positive");
                return 1;
            }

            var (mode, exit) = api.ResolveOutputMode(output);
            if (exit != 0)
                return exit;

            var headers = await GetAuthorizationHeadersAsync(api, credentials).ConfigureAwait(false);
            if (headers is null)
                return 1;

            return await api.PrintPostWithOutputAsync(
                $"/api/events/dead-letters/{id}/redeliver",
                new { },
                mode,
                nameof(MohistCliApi.TableShape.DeadLetterRedelivery),
                headers: headers).ConfigureAwait(false);
        });
        return cmd;
    }

    private static async Task<IReadOnlyDictionary<string, string>?> GetAuthorizationHeadersAsync(
        MohistCliApi api,
        CliCredentialProvider credentials)
    {
        var baseAddress = api.Http.BaseAddress;
        if (baseAddress is null || !baseAddress.IsLoopback)
        {
            api.Error.WriteLine(
                $"Dead-letter operations require a loopback Mohist server URL; refusing to send the operator credential to '{baseAddress}'.");
            return null;
        }

        try
        {
            var token = await credentials.GetAsync().ConfigureAwait(false);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["authorization"] = $"Bearer {token}",
            };
        }
        catch (InvalidOperationException ex)
        {
            api.Error.WriteLine(ex.Message);
            return null;
        }
    }
}
