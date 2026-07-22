using System.CommandLine;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
    private static Command BuildAction(string name, string description, MohistCliApi api)
    {
        var cmd = new Command(name, $"{description} an issue");
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
            var selection = JsonSelection.Parse(IssueDescriptor, ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt));
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(IssueDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/{name}"),
                    new { },
                    IssueDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildReject(MohistCliApi api)
    {
        var cmd = new Command("reject", "Reject the workflow run with a message (request changes at an approval gate)");
        var numberArg = NumberArg();
        var messageOpt = new Option<string?>("--message", "-m")
        {
            Description = "Reject reason / change request message (required)",
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(messageOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var message = ctx.GetValue(messageOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var selection = JsonSelection.Parse(IssueDescriptor, ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt));
            return RejectAsync();

            async Task<int> RejectAsync()
            {
                if (string.IsNullOrWhiteSpace(message))
                {
                    api.Error.WriteLine("--message is required and must not be empty");
                    return 1;
                }
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(IssueDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/reject");
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    path,
                    new { message },
                    IssueDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildRerun(MohistCliApi api)
    {
        var cmd = new Command("rerun", "Rerun the issue workflow from the start (no flag) or from a specific stage (--from-stage; equivalent to 'mo issue rerun-from-stage --stage')");
        var numberArg = NumberArg();
        var fromStageOpt = new Option<string?>("--from-stage")
        {
            Description = "Rerun from the specified stage (equivalent to 'mo issue rerun-from-stage --stage')",
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(fromStageOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var fromStage = ctx.GetValue(fromStageOpt);
            var fromStageProvided = IsOptionProvided(ctx, fromStageOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return RerunAsync();

            async Task<int> RerunAsync()
            {
                if (fromStageProvided && string.IsNullOrWhiteSpace(fromStage))
                {
                    api.Error.WriteLine("--from-stage is required and must not be empty");
                    return 1;
                }
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var suffix = fromStageProvided ? "/rerun-from-stage" : "/rerun";
                object body = fromStageProvided ? new { stage = fromStage! } : new { };
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}{suffix}");
                return await api.PrintPostAsync(path, body);
            }
        });
        return cmd;
    }

    private static Command BuildRerunFromStage(MohistCliApi api)
    {
        var cmd = new Command("rerun-from-stage", "[transitional alias of 'mo issue rerun --from-stage'] Rerun the workflow from a specified stage (invalidates the target stage and all later stages, creating new attempts)");
        var numberArg = NumberArg();
        var stageOpt = new Option<string>("--stage")
        {
            Description = "Target stage to rerun from (e.g. plan, build, check, integrate)",
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(stageOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var stage = ctx.GetValue(stageOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return RerunFromStageAsync();

            async Task<int> RerunFromStageAsync()
            {
                if (string.IsNullOrWhiteSpace(stage))
                {
                    api.Error.WriteLine("--stage is required and must not be empty");
                    return 1;
                }
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/rerun-from-stage");
                return await api.PrintPostAsync(path, new { stage });
            }
        });
        return cmd;
    }

    private static Command BuildStop(MohistCliApi api)
    {
        var cmd = new Command(
            "stop",
            "Stop the workflow run permanently (terminal — cannot be resumed; use 'force-stop' if you want a pause you can resume)");
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
            var selection = JsonSelection.Parse(IssueDescriptor, ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt));
            return StopAsync();

            async Task<int> StopAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(IssueDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/stop");
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    path,
                    new { },
                    IssueDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildRebase(MohistCliApi api)
    {
        var cmd = new Command("rebase", "Rebase issue branch");
        var numberArg = NumberArg();
        var baseBranchOpt = new Option<string?>("--base-branch", "-b") { Description = "Base branch to rebase onto" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(baseBranchOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var baseBranch = ctx.GetValue(baseBranchOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return RebaseAsync();

            async Task<int> RebaseAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                return await api.PrintPostAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/rebase"),
                    new { baseBranch });
            }
        });
        return cmd;
    }

    private static Command BuildArchive(MohistCliApi api)
    {
        var cmd = new Command("archive", "Archive issues");
        var numberArg = new Argument<string?>("number")
        {
            Description = "Issue number (omit with --all-completed)",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => null,
        };
        var allCompletedOpt = new Option<bool>("--all-completed") { Description = "Archive all completed issues" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(allCompletedOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var allCompleted = ctx.GetValue(allCompletedOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var number = ctx.GetValue(numberArg);
            var json = ctx.GetValue(jsonOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            return ArchiveAsync();

            async Task<int> ArchiveAsync()
            {
                if (allCompleted && number is not null)
                {
                    api.Error.WriteLine("<number> and --all-completed are mutually exclusive");
                    return 1;
                }

                if (!allCompleted && string.IsNullOrWhiteSpace(number))
                {
                    api.Error.WriteLine("<number> is required unless --all-completed is used");
                    return 1;
                }

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;

                if (allCompleted)
                {
                    var selection = JsonSelection.Parse(IssueListDescriptor, jsonProvided, json);
                    if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                        return api.WriteJsonSelectionResult(IssueListDescriptor, selection);
                    return await api.PrintMutationResourceAsync(
                        HttpMethod.Post,
                        ProjectIssuesPath(resolvedProjectId, "/issues/archive-completed"),
                        new { },
                        IssueListDescriptor,
                        selection,
                        data => api.RenderTableAsync(data, MohistCliApi.TableShape.IssueArchiveCompleted));
                }

                var issueSelection = JsonSelection.Parse(IssueDescriptor, jsonProvided, json);
                if (issueSelection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(IssueDescriptor, issueSelection);
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{Uri.EscapeDataString(number!)}/archive"),
                    new { },
                    IssueDescriptor,
                    issueSelection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildGetSub(string name, MohistCliApi api)
    {
        var cmd = new Command(name, $"Show issue {name}");
        var numberArg = NumberArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return GetAsync();

            async Task<int> GetAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                return await api.PrintGetAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/{name}"));
            }
        });
        return cmd;
    }
}
