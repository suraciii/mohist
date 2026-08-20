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

    public static Command Build(MohistCliApi api)
    {
        var eventCommand = new Command(
            "event",
            "Subscribe to realtime Event envelopes and inspect or recover failed deliveries.");
        eventCommand.Subcommands.Add(BuildTail(api));
        var deadLetter = new Command(
            "dead-letter",
            "Inspect current failed deliveries and retry them with explicit recovery side effects.");
        deadLetter.Subcommands.Add(BuildList(api));
        deadLetter.Subcommands.Add(BuildRedeliver(api));
        eventCommand.Subcommands.Add(deadLetter);
        return eventCommand;
    }

    private static Command BuildTail(MohistCliApi api)
    {
        var descriptor = new ResourceDescriptor(
            ResourceCardinality.Stream,
            ["type", "source", "id", "time", "specversion", "subject", "datacontenttype", "data",
                "projectid", "issue", "epic", "workflowrunid", "agentid", "sessionid", "runnerid",
                "workspace", "workspaceoriginkind", "stage", "parent", "githubrepo", "githubissue"]);
        var cmd = new Command(
            "tail",
            "Subscribe to realtime Event envelopes from subscription establishment; emit one NDJSON object per line. With --match, only matching events are emitted.");
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var matchOpt = new Option<string?>("--match") { Description = "Match expression (CEL subset) forwarded to the server; the server is the single compile authority" };
        var eventOpt = new Option<string[]?>("--event")
        {
            Description = "Domain event type to subscribe to (repeatable)",
            AllowMultipleArgumentsPerToken = true,
        };
        var jsonOpt = MohistCliCommands.JsonSelectionOption(descriptor);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(matchOpt);
        cmd.Options.Add(eventOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(async ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var match = ctx.GetValue(matchOpt);
            var eventValues = ctx.GetValue(eventOpt);
            var json = ctx.GetValue(jsonOpt);
            var selection = JsonSelection.Parse(descriptor, ctx.GetResult(jsonOpt) is not null, json);
            if (selection.Kind == JsonSelectionKind.Discovery || selection.Kind == JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(descriptor, selection);

            string[]? eventTypes = null;
            if (eventValues is { Length: > 0 })
            {
                if (eventValues.Any(string.IsNullOrWhiteSpace))
                    return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--event values must not be empty.");
                eventTypes = eventValues
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }

            var (resolved, resolveExit) = await api.ResolveProject(project).ConfigureAwait(false);
            if (resolveExit != 0)
                return resolveExit;

            return await RunTailAsync(api, resolved, match, eventTypes, selection).ConfigureAwait(false);
        });
        return cmd;
    }

    internal static async Task<int> RunTailAsync(
        MohistCliApi api,
        string projectRef,
        string? match,
        string[]? eventTypes,
        JsonSelection? selection = null)
    {
        if (TailCancellationOverride != default)
            return await api.EventStream!.RunAsync(projectRef, match, eventTypes, selection, TailCancellationOverride)
                .ConfigureAwait(false);

        if (api.Invocation.CancellationToken != default)
            return await api.EventStream!.RunAsync(projectRef, match, eventTypes, selection, api.Invocation.CancellationToken)
                .ConfigureAwait(false);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += OnCancel;
        try
        {
            return await api.EventStream!.RunAsync(projectRef, match, eventTypes, selection, cts.Token)
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

    private static Command BuildList(MohistCliApi api)
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

            var query = $"?limit={limit}";
            if (!string.IsNullOrWhiteSpace(handler))
                query += $"&handler={MohistCliCommands.Escape(handler)}";
            return await api.PrintWithOutputAsync(
                $"/api/events/dead-letters{query}",
                mode,
                nameof(MohistCliApi.TableShape.DeadLetterList)).ConfigureAwait(false);
        });
        return cmd;
    }

    private static Command BuildRedeliver(MohistCliApi api)
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

            return await api.PrintPostWithOutputAsync(
                $"/api/events/dead-letters/{id}/redeliver",
                new { },
                mode,
                nameof(MohistCliApi.TableShape.DeadLetterRedelivery)).ConfigureAwait(false);
        });
        return cmd;
    }


}
