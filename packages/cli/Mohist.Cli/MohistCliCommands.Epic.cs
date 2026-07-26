using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static class EpicCommands
{
    private static readonly ResourceDescriptor EpicDescriptor = new(
        ResourceCardinality.Single,
        ["number", "title", "description", "status", "state", "priority", "createdAt", "updatedAt"]);

    private static JsonSelection Selection(ParseResult ctx, Option<string?> json) =>
        JsonSelection.Parse(EpicDescriptor, ctx.GetResult(json) is not null, ctx.GetValue(json));

    public static Command Build(MohistCliApi api)
    {
        var epic = new Command("epic", "Epic management");

        epic.Subcommands.Add(BuildList(api));
        epic.Subcommands.Add(BuildCreate(api));
        epic.Subcommands.Add(BuildView(api));
        epic.Subcommands.Add(BuildEdit(api));
        epic.Subcommands.Add(BuildLink(api));
        epic.Subcommands.Add(BuildUnlink(api));
        epic.Subcommands.Add(BuildStart(api));
        epic.Subcommands.Add(BuildPause(api));
        epic.Subcommands.Add(BuildResume(api));
        epic.Subcommands.Add(BuildDone(api));
        epic.Subcommands.Add(BuildClose(api));
        epic.Subcommands.Add(BuildReopen(api));

        return epic;
    }

    private static Argument<int> NumberArg() =>
        new("number") { Description = "Epic number" };

    private static string ProjectEpicsPath(string? projectId, string path = "")
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(MohistCliCommands.NoActiveProjectMessage);
        return $"/api/projects/{MohistCliCommands.Escape(projectId)}/epics{(path.StartsWith('/') ? path : "/" + path)}";
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List epics");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintWithOutputAsync(
                    ProjectEpicsPath(resolvedProjectId, "/"),
                    mode,
                    nameof(MohistCliApi.TableShape.EpicList));
            }
        });
        return cmd;
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create a new epic");
        var titleArg = new Argument<string>("title") { Description = "Epic title" };
        var descriptionOpt = new Option<string?>("--description", "-d") { Description = "Epic description" };
        var priorityOpt = new Option<string?>("--priority", "-p") { Description = "Epic priority (p0|p1|p2|p3)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(titleArg);
        cmd.Options.Add(descriptionOpt);
        cmd.Options.Add(priorityOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var title = ctx.GetValue(titleArg);
            var description = ctx.GetValue(descriptionOpt);
            var priority = ctx.GetValue(priorityOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var selection = Selection(ctx, jsonOpt);
            return CreateAsync();

            async Task<int> CreateAsync()
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    api.Error.WriteLine("Title is required");
                    return 1;
                }
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(EpicDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    ProjectEpicsPath(resolvedProjectId, "/"),
                    new { title, description, priority },
                    EpicDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.EpicShow));
            }
        });
        return cmd;
    }

    private static Command BuildView(MohistCliApi api)
    {
        var cmd = new Command("view", "Show epic details");
        var numberArg = NumberArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ShowAsync();

            async Task<int> ShowAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                return await api.PrintWithOutputAsync(
                    ProjectEpicsPath(resolvedProjectId, $"/{number}"),
                    mode,
                    nameof(MohistCliApi.TableShape.EpicShow));
            }
        });
        return cmd;
    }

    private static Command BuildEdit(MohistCliApi api)
    {
        var cmd = new Command("edit", "Update an epic");
        var numberArg = NumberArg();
        var titleOpt = new Option<string?>("--title") { Description = "New title" };
        var descriptionOpt = new Option<string?>("--description", "-d") { Description = "New description" };
        var priorityOpt = new Option<string?>("--priority", "-p") { Description = "New priority (p0|p1|p2|p3)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(titleOpt);
        cmd.Options.Add(descriptionOpt);
        cmd.Options.Add(priorityOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var title = ctx.GetValue(titleOpt);
            var description = ctx.GetValue(descriptionOpt);
            var priority = ctx.GetValue(priorityOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var selection = Selection(ctx, jsonOpt);
            return UpdateAsync();

            async Task<int> UpdateAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(EpicDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;
                var body = new JsonObject();
                if (title is not null)
                    body["title"] = title;
                if (description is not null)
                    body["description"] = description;
                if (priority is not null)
                    body["priority"] = priority;
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Patch,
                    ProjectEpicsPath(resolvedProjectId, $"/{number}"),
                    body,
                    EpicDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.EpicShow));
            }
        });
        return cmd;
    }

    private static Command BuildLink(MohistCliApi api)
    {
        var cmd = new Command("link", "Link an issue to an epic");
        var epicArg = new Argument<int>("epic") { Description = "Epic number" };
        var issueArg = new Argument<int>("issue") { Description = "Issue number" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(epicArg);
        cmd.Arguments.Add(issueArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var epic = ctx.GetValue(epicArg);
            var issue = ctx.GetValue(issueArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var selection = Selection(ctx, jsonOpt);
            return LinkAsync();

            async Task<int> LinkAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(EpicDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    ProjectEpicsPath(resolvedProjectId, $"/{epic}/issues"),
                    new { issueNumber = issue },
                    EpicDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.EpicLink));
            }
        });
        return cmd;
    }

    private static Command BuildUnlink(MohistCliApi api)
    {
        var cmd = new Command("unlink", "Unlink an issue from an epic");
        var epicArg = new Argument<int>("epic") { Description = "Epic number" };
        var issueArg = new Argument<int>("issue") { Description = "Issue number" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(epicArg);
        cmd.Arguments.Add(issueArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var epic = ctx.GetValue(epicArg);
            var issue = ctx.GetValue(issueArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var selection = Selection(ctx, jsonOpt);
            return UnlinkAsync();

            async Task<int> UnlinkAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(EpicDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Delete,
                    ProjectEpicsPath(resolvedProjectId, $"/{epic}/issues/{issue}"),
                    null,
                    EpicDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.EpicUnlink));
            }
        });
        return cmd;
    }

    private static Command BuildDone(MohistCliApi api)
    {
        var cmd = new Command("done", "Mark an epic done");
        var numberArg = NumberArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var selection = Selection(ctx, jsonOpt);
            return DoneAsync();

            async Task<int> DoneAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(EpicDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    ProjectEpicsPath(resolvedProjectId, $"/{number}/done"),
                    new { },
                    EpicDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.EpicShow));
            }
        });
        return cmd;
    }

    private static Command BuildStart(MohistCliApi api)
    {
        return BuildLifecyclePost(api, "start", "Start autonomous progression on an epic", "start");
    }

    private static Command BuildPause(MohistCliApi api)
    {
        return BuildLifecyclePost(api, "pause", "Pause autonomous progression on an epic", "pause");
    }

    private static Command BuildResume(MohistCliApi api)
    {
        return BuildLifecyclePost(api, "resume", "Resume autonomous progression on an epic", "resume");
    }

    private static Command BuildLifecyclePost(MohistCliApi api, string name, string description, string action)
    {
        var cmd = new Command(name, description);
        var numberArg = NumberArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var selection = Selection(ctx, jsonOpt);
            return PostAsync();

            async Task<int> PostAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(EpicDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    ProjectEpicsPath(resolvedProjectId, $"/{number}/{action}"),
                    new { },
                    EpicDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.EpicShow));
            }
        });
        return cmd;
    }

    private static Command BuildClose(MohistCliApi api)
    {
        var cmd = new Command("close", "Close an epic");
        var numberArg = NumberArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var selection = Selection(ctx, jsonOpt);
            return CloseAsync();

            async Task<int> CloseAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(EpicDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    ProjectEpicsPath(resolvedProjectId, $"/{number}/close"),
                    new { },
                    EpicDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.EpicShow));
            }
        });
        return cmd;
    }

    private static Command BuildReopen(MohistCliApi api)
    {
        return BuildLifecyclePost(api, "reopen", "Reopen an epic", "reopen");
    }
}
