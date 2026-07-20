using System.CommandLine;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
    private static Command BuildWorkflowConfigSet(MohistCliApi api)
    {
        var cmd = new Command("set", "Composite config writes (template / variables)");
        var numberArg = NumberArg();
        var templateOpt = new Option<string?>("--template")
        {
            Description = "Inline YAML template body, or '@<file>' to read UTF-8 from a file (PUT /workflow-profile/template)",
        };
        var varOpt = new Option<string[]?>("--var")
        {
            Description = "Set a top-level variable as 'k=v'. Repeatable; merged into one PATCH /workflow-profile/variables.",
            AllowMultipleArgumentsPerToken = true,
        };
        var stageVarOpt = new Option<string[]?>("--stage-var")
        {
            Description = "Set a stage-scoped variable as '<stage>.k=v'. Repeatable; merged into the same PATCH.",
            AllowMultipleArgumentsPerToken = true,
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(templateOpt);
        cmd.Options.Add(varOpt);
        cmd.Options.Add(stageVarOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var template = ctx.GetValue(templateOpt);
            var vars = ctx.GetValue(varOpt);
            var stageVars = ctx.GetValue(stageVarOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var templateProvided = ctx.GetResult(templateOpt) is not null;
            var varProvided = ctx.GetResult(varOpt) is not null;
            var stageVarProvided = ctx.GetResult(stageVarOpt) is not null;
            return SetAsync();

            async Task<int> SetAsync()
            {
                var hasAnyChange = templateProvided || varProvided || stageVarProvided;
                if (!hasAnyChange)
                {
                    api.Error.WriteLine("nothing to change — pass at least one of --template, --var, or --stage-var");
                    return 1;
                }

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;

                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var issuePath = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/workflow-profile");

                var varsPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
                var stagesPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
                string? templateText = null;

                if (varProvided)
                {
                    foreach (var entry in vars!)
                    {
                        var eq = entry.IndexOf('=');
                        if (eq <= 0)
                        {
                            api.Error.WriteLine($"--var '{entry}' must be in 'k=v' form");
                            return 1;
                        }
                        var key = entry[..eq];
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            api.Error.WriteLine($"--var '{entry}' has an empty key");
                            return 1;
                        }
                        varsPayload[key] = entry[(eq + 1)..];
                    }
                }

                if (stageVarProvided)
                {
                    foreach (var entry in stageVars!)
                    {
                        var dot = entry.IndexOf('.');
                        if (dot <= 0)
                        {
                            api.Error.WriteLine($"--stage-var '{entry}' must be in '<stage>.k=v' form");
                            return 1;
                        }
                        var stage = entry[..dot];
                        if (string.IsNullOrWhiteSpace(stage))
                        {
                            api.Error.WriteLine($"--stage-var '{entry}' has an empty stage");
                            return 1;
                        }
                        var remainder = entry[(dot + 1)..];
                        var eq = remainder.IndexOf('=');
                        if (eq <= 0)
                        {
                            api.Error.WriteLine($"--stage-var '{entry}' must be in '<stage>.k=v' form");
                            return 1;
                        }
                        var key = remainder[..eq];
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            api.Error.WriteLine($"--stage-var '{entry}' has an empty key");
                            return 1;
                        }
                        if (!stagesPayload.TryGetValue(stage, out var existing))
                        {
                            existing = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["vars"] = new Dictionary<string, object?>(StringComparer.Ordinal),
                            };
                            stagesPayload[stage] = existing;
                        }
                        var stageObj = (Dictionary<string, object?>)existing!;
                        var stageVars = (Dictionary<string, object?>)stageObj["vars"]!;
                        stageVars[key] = remainder[(eq + 1)..];
                    }
                }

                if (templateProvided)
                {
                    var expanded = await api.ExpandAtFileAsync(template, "--template");
                    if (expanded is MohistCliApi.ExpandAtFileResult.Failure)
                        return 1;
                    templateText = ((MohistCliApi.ExpandAtFileResult.Success)expanded).Value;
                }

                if (varProvided || stageVarProvided)
                {
                    var patchBody = new Dictionary<string, object?>(StringComparer.Ordinal);
                    if (varProvided)
                        patchBody["vars"] = varsPayload;
                    if (stageVarProvided)
                        patchBody["stages"] = stagesPayload;

                    var patchExit = await api.PrintPatchWithOutputAsync(
                        issuePath + "/variables",
                        patchBody,
                        mode,
                        nameof(MohistCliApi.TableShape.WorkflowVariables));
                    if (patchExit != 0)
                        return patchExit;
                }

                if (templateProvided)
                {
                    var putExit = await api.PrintPutWithOutputAsync(
                        issuePath + "/template",
                        new { yaml = templateText },
                        mode,
                        nameof(MohistCliApi.TableShape.WorkflowProfile));
                    if (putExit != 0)
                        return putExit;
                }

                return 0;
            }
        });
        return cmd;
    }
}
