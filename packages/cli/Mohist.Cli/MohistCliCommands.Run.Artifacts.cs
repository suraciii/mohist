using System.CommandLine;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class RunCommands
{
    internal static readonly ResourceDescriptor ArtifactListDescriptor = new(ResourceCardinality.Collection,
        ["artifactId", "path", "kind", "contentType", "size", "actionAttemptId", "recordedAt"]);

    internal static Command BuildArtifact(MohistCliApi api)
    {
        var group = new Command("artifact", "Read recorded workflow run artifacts");
        group.Subcommands.Add(BuildArtifactList(api));
        group.Subcommands.Add(BuildArtifactGet(api));
        return group;
    }

    private static Command BuildArtifactList(MohistCliApi api)
    {
        var cmd = new Command("list", "List the latest recorded artifacts for a workflow run");
        var runIdArg = RunIdArg(); var issueOpt = IssueOption(); var projectOpt = ProjectOptions();
        var jsonOpt = MohistCliCommands.JsonSelectionOption(ArtifactListDescriptor);
        cmd.Arguments.Add(runIdArg); cmd.Options.Add(issueOpt); cmd.Options.Add(projectOpt); cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx => ExecuteAsync(ctx.GetValue(runIdArg), ctx.GetValue(issueOpt), ctx.GetValue(projectOpt), ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt)));
        return cmd;
        async Task<int> ExecuteAsync(string? runId, string? issue, string? project, bool jsonProvided, string? json)
        {
            var selection = JsonSelection.Parse(ArtifactListDescriptor, jsonProvided, json);
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid) return api.WriteJsonSelectionResult(ArtifactListDescriptor, selection);
            var target = await ResolveArtifactTargetAsync(api, runId, issue, project).ConfigureAwait(false);
            if (target.Exit != 0) return target.Exit;
            var (exit, data) = await api.GetDataOrPrintErrorAsync(target.ArtifactsPath!).ConfigureAwait(false);
            if (exit != 0) return exit;
            if (selection.Kind == JsonSelectionKind.Selected)
                return await new CliResultWriter(api.Invocation).WriteSuccessAsync(selection.Project(data, ArtifactListDescriptor.Cardinality)).ConfigureAwait(false);
            return RenderArtifactTable(api.Output, data);
        }
    }

    private static int RenderArtifactTable(TextWriter output, JsonNode? data)
    {
        var rows = data as JsonArray;
        if (rows is null || rows.Count == 0) { output.WriteLine("No recorded artifacts"); return 0; }
        var headers = new[] { "artifact id", "path", "kind", "size", "task", "recorded" };
        var widths = new[] { 24, 42, 10, 10, 18, 24 };
        var cells = rows.OfType<JsonObject>().Select(item => new[]
        {
            Fit(item["artifactId"]?.GetValue<string>() ?? "", widths[0]),
            Fit(item["path"]?.GetValue<string>() ?? "", widths[1]),
            Fit(item["kind"]?.GetValue<string>() ?? "", widths[2]),
            Fit(item["size"]?.ToString() ?? "", widths[3]),
            Fit(item["actionAttemptId"]?.GetValue<string>() ?? "", widths[4]),
            Fit(item["recordedAt"]?.GetValue<string>() ?? "", widths[5]),
        }).ToList();
        AuthCommands.WriteTable(output, headers, widths, cells);
        return 0;
    }

    private static string Fit(string value, int width) => value.Length <= width ? value : value[..Math.Max(0, width - 1)] + "…";

    private static Command BuildArtifactGet(MohistCliApi api)
    {
        var cmd = new Command("get", "Print one recorded file artifact by stable artifact id");
        var runIdArg = RunIdArg(); var artifactIdArg = new Argument<string>("artifact-id");
        var issueOpt = IssueOption(); var projectOpt = ProjectOptions();
        cmd.Arguments.Add(runIdArg); cmd.Arguments.Add(artifactIdArg); cmd.Options.Add(issueOpt); cmd.Options.Add(projectOpt);
        cmd.SetAction(ctx => ExecuteAsync(ctx.GetValue(runIdArg), ctx.GetValue(issueOpt), ctx.GetValue(projectOpt), ctx.GetValue(artifactIdArg)!));
        return cmd;
        async Task<int> ExecuteAsync(string? runId, string? issue, string? project, string artifactId)
        {
            var target = await ResolveArtifactTargetAsync(api, runId, issue, project).ConfigureAwait(false);
            if (target.Exit != 0) return target.Exit;
            return await api.StreamGetAsync($"{target.ArtifactsPath}/{MohistCliCommands.Escape(artifactId)}/content").ConfigureAwait(false);
        }
    }

    private static async Task<(int Exit, string? ArtifactsPath)> ResolveArtifactTargetAsync(MohistCliApi api, string? runId, string? issue, string? project)
    {
        var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(api, runId, issue, project).ConfigureAwait(false);
        if (resolveExit != 0) return (resolveExit, null);
        var (runExit, run) = await api.GetDataOrPrintErrorAsync(WorkflowRunPath(resolvedRunId!, "")).ConfigureAwait(false);
        if (runExit != 0) return (runExit, null);
        var issueRef = run?["issueRef"];
        var projectId = issueRef?["projectId"]?.GetValue<string>();
        var issueNumber = issueRef?["number"]?.GetValue<int>();
        if (string.IsNullOrWhiteSpace(projectId) || issueNumber is null) { await api.Error.WriteLineAsync("Workflow run has no associated issue."); return (1, null); }
        var issuePath = $"/api/projects/{MohistCliCommands.Escape(projectId)}/issues/{issueNumber}";
        var (issueExit, issueData) = await api.GetDataOrPrintErrorAsync(issuePath).ConfigureAwait(false);
        if (issueExit != 0) return (issueExit, null);
        if (!string.Equals(issueData?["workflowRunId"]?.GetValue<string>(), resolvedRunId, StringComparison.Ordinal)) { await api.Error.WriteLineAsync("Issue is no longer bound to the requested workflow run."); return (1, null); }
        return (0, $"{issuePath}/workflow/artifacts");
    }
}
