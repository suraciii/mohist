using System.CommandLine;

namespace Mohist.Cli;

internal static class ProjectWorkflowCommands
{
    public static Command Build(MohistCliApi api)
    {
        var workflow = new Command("workflow", "Project workflow management");

        workflow.Subcommands.Add(BuildTemplate(api));
        workflow.Subcommands.Add(BuildConfig(api));

        return workflow;
    }

    private static Command BuildConfig(MohistCliApi api)
    {
        var config = new Command("config", "Manage project workflow configuration");
        config.Subcommands.Add(BuildConfigGet(api));
        config.Subcommands.Add(BuildConfigSet(api));
        config.Subcommands.Add(BuildConfigClear(api));
        config.Subcommands.Add(BuildConfigPreview(api));
        return config;
    }

    private static string ProjectWorkflowProfilePath(string projectId, string suffix = "") =>
        $"/api/projects/{MohistCliCommands.Escape(projectId)}/workflow-profile{suffix}";

    private static async Task<int> PrintProjectWorkflowProfileAsync(MohistCliApi api, string profilePath, string promptsPath, string mode)
    {
        var (exitCode, dataNode) = await api.GetDataOrPrintErrorAsync(profilePath);
        if (exitCode != 0)
            return exitCode;
        if (dataNode is null)
            return 1;

        if (string.Equals(mode, "json", StringComparison.Ordinal))
        {
            api.Output.WriteLine(dataNode.ToJsonString(MohistCliApi.JsonOutputOptions));
            return 0;
        }

        var (promptsExitCode, promptsData) = await api.GetDataOrPrintErrorAsync(promptsPath);
        if (promptsExitCode != 0)
            return promptsExitCode;
        if (promptsData is null)
            return 1;
        dataNode["prompts"] = promptsData.DeepClone();

        return await api.RenderTableAsync(dataNode, MohistCliApi.TableShape.ProjectWorkflowProfile);
    }

    private static Command BuildConfigGet(MohistCliApi api)
    {
        var cmd = new Command("get", "Show the project's full workflow profile (default template / variables / prompts)");
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
            return GetAsync();

            async Task<int> GetAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                var validation = MohistCliApi.ValidateOutputMode(output);
                if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
                {
                    api.Error.WriteLine(invalid.Message);
                    return 1;
                }
                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;

                var profilePath = ProjectWorkflowProfilePath(resolvedProjectId);
                var promptsPath = ProjectWorkflowProfilePath(resolvedProjectId, "/prompts");

                return await PrintProjectWorkflowProfileAsync(api, profilePath, promptsPath, mode);
            }
        });
        return cmd;
    }

    private static Command BuildConfigSet(MohistCliApi api)
    {
        var cmd = new Command("set", "Composite config writes (default template / variables / prompts)");
        var defaultTemplateOpt = new Option<string?>("--default-template")
        {
            Description = "Set the default workflow template by ID (PUT /workflow-profile/default-template)",
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
        var varsFileOpt = new Option<string?>("--vars-file")
        {
            Description = "Full replace of all variables from a JSON file (PUT /workflow-profile/variables). Mutually exclusive with --var and --stage-var.",
        };
        var promptOpt = new Option<string[]?>("--prompt")
        {
            Description = "Set a prompt as 'key=body' or 'key=@<file>'. Repeatable; one PUT /workflow-profile/prompts/<key> per occurrence.",
            AllowMultipleArgumentsPerToken = true,
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(defaultTemplateOpt);
        cmd.Options.Add(varOpt);
        cmd.Options.Add(stageVarOpt);
        cmd.Options.Add(varsFileOpt);
        cmd.Options.Add(promptOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var defaultTemplate = ctx.GetValue(defaultTemplateOpt);
            var vars = ctx.GetValue(varOpt);
            var stageVars = ctx.GetValue(stageVarOpt);
            var varsFile = ctx.GetValue(varsFileOpt);
            var prompts = ctx.GetValue(promptOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var defaultTemplateProvided = ctx.GetResult(defaultTemplateOpt) is not null;
            var varProvided = ctx.GetResult(varOpt) is not null;
            var stageVarProvided = ctx.GetResult(stageVarOpt) is not null;
            var varsFileProvided = ctx.GetResult(varsFileOpt) is not null;
            var promptProvided = ctx.GetResult(promptOpt) is not null;
            return SetAsync();

            async Task<int> SetAsync()
            {
                var hasAnyChange = defaultTemplateProvided || varProvided || stageVarProvided || varsFileProvided || promptProvided;
                if (!hasAnyChange)
                {
                    api.Error.WriteLine("nothing to change — pass at least one of --default-template, --var, --stage-var, --vars-file, or --prompt");
                    return 1;
                }

                if (varsFileProvided && (varProvided || stageVarProvided))
                {
                    api.Error.WriteLine("--vars-file is mutually exclusive with --var and --stage-var");
                    return 1;
                }

                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;

                var validation = MohistCliApi.ValidateOutputMode(output);
                if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
                {
                    api.Error.WriteLine(invalid.Message);
                    return 1;
                }
                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;

                var profilePath = ProjectWorkflowProfilePath(resolvedProjectId);

                var varsPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
                var stagesPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
                var promptPayloads = new List<(string Key, string Body)>();

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
                        var stageVarsDict = (Dictionary<string, object?>)stageObj["vars"]!;
                        stageVarsDict[key] = remainder[(eq + 1)..];
                    }
                }

                if (promptProvided)
                {
                    foreach (var entry in prompts!)
                    {
                        var eq = entry.IndexOf('=');
                        if (eq <= 0)
                        {
                            api.Error.WriteLine($"--prompt '{entry}' must be in 'key=body' or 'key=@<file>' form");
                            return 1;
                        }
                        var key = entry[..eq];
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            api.Error.WriteLine($"--prompt '{entry}' has an empty key");
                            return 1;
                        }
                        if (key.Contains('/'))
                        {
                            api.Error.WriteLine($"--prompt key '{key}' must not contain '/'");
                            return 1;
                        }

                        var rawBody = entry[(eq + 1)..];
                        var expanded = await api.ExpandAtFileAsync(rawBody, "--prompt");
                        if (expanded is MohistCliApi.ExpandAtFileResult.Failure)
                            return 1;
                        promptPayloads.Add((key, ((MohistCliApi.ExpandAtFileResult.Success)expanded).Value));
                    }
                }

                // 1. If --vars-file is provided, do a full replace PUT
                if (varsFileProvided)
                {
                    string fileContent;
                    try
                    {
                        fileContent = await api.FileSystem.ReadAllTextAsync(varsFile!);
                    }
                    catch (Exception ex)
                    {
                        api.Error.WriteLine($"--vars-file: could not read file '{varsFile}' ({ex.Message})");
                        return 1;
                    }

                    System.Text.Json.Nodes.JsonNode? varsBody;
                    try
                    {
                        varsBody = System.Text.Json.Nodes.JsonNode.Parse(fileContent);
                    }
                    catch (Exception ex)
                    {
                        api.Error.WriteLine($"--vars-file: invalid JSON in '{varsFile}' ({ex.Message})");
                        return 1;
                    }

                    var putExit = await api.PrintPutWithOutputAsync(
                        profilePath + "/variables",
                        varsBody!,
                        mode,
                        nameof(MohistCliApi.TableShape.WorkflowVariables));
                    if (putExit != 0)
                        return putExit;
                }

                // 2. Variables PATCH (incremental merge)
                if (!varsFileProvided && (varProvided || stageVarProvided))
                {
                    var patchBody = new Dictionary<string, object?>(StringComparer.Ordinal);
                    if (varProvided)
                        patchBody["vars"] = varsPayload;
                    if (stageVarProvided)
                        patchBody["stages"] = stagesPayload;

                    var patchExit = await api.PrintPatchWithOutputAsync(
                        profilePath + "/variables",
                        patchBody,
                        mode,
                        nameof(MohistCliApi.TableShape.WorkflowVariables));
                    if (patchExit != 0)
                        return patchExit;
                }

                // 3. Default-template PUT
                if (defaultTemplateProvided)
                {
                    var putExit = await api.PrintPutWithOutputAsync(
                        profilePath + "/default-template",
                        new { templateId = defaultTemplate },
                        mode,
                        nameof(MohistCliApi.TableShape.ProjectWorkflowProfile));
                    if (putExit != 0)
                        return putExit;
                }

                // 4. Prompt PUTs
                if (promptProvided)
                {
                    foreach (var prompt in promptPayloads)
                    {
                        var promptExit = await api.PrintPutWithOutputAsync(
                            $"{profilePath}/prompts/{Uri.EscapeDataString(prompt.Key)}",
                            new { body = prompt.Body },
                            mode,
                            nameof(MohistCliApi.TableShape.WorkflowProfilePrompt));
                        if (promptExit != 0)
                            return promptExit;
                    }
                }

                return 0;
            }
        });
        return cmd;
    }

    private static Command BuildConfigClear(MohistCliApi api)
    {
        var cmd = new Command("clear", "Composite config removals (default template / variables / prompts)");
        var defaultTemplateOpt = new Option<bool>("--default-template")
        {
            Description = "Clear the default template (DELETE /workflow-profile/default-template)",
        };
        var varOpt = new Option<string[]?>("--var")
        {
            Description = "Remove a variable by key. Use '<stage>.k' for stage-scoped variables. Repeatable; merged into one PATCH /workflow-profile/variables with each key set to null.",
            AllowMultipleArgumentsPerToken = true,
        };
        var promptOpt = new Option<string[]?>("--prompt")
        {
            Description = "Remove a prompt by key. Repeatable; one DELETE /workflow-profile/prompts/<key> per occurrence.",
            AllowMultipleArgumentsPerToken = true,
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(defaultTemplateOpt);
        cmd.Options.Add(varOpt);
        cmd.Options.Add(promptOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var vars = ctx.GetValue(varOpt);
            var prompts = ctx.GetValue(promptOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var defaultTemplateResult = ctx.GetResult(defaultTemplateOpt);
            var defaultTemplateProvided = defaultTemplateResult is not null && !defaultTemplateResult.Implicit;
            var varProvided = ctx.GetResult(varOpt) is not null;
            var promptProvided = ctx.GetResult(promptOpt) is not null;
            return ClearAsync();

            async Task<int> ClearAsync()
            {
                var hasAnyClear = defaultTemplateProvided || varProvided || promptProvided;
                if (!hasAnyClear)
                {
                    api.Error.WriteLine("nothing to clear — pass at least one of --default-template, --var, or --prompt");
                    return 1;
                }

                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;

                var validation = MohistCliApi.ValidateOutputMode(output);
                if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
                {
                    api.Error.WriteLine(invalid.Message);
                    return 1;
                }
                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;

                var profilePath = ProjectWorkflowProfilePath(resolvedProjectId);

                var varsPatchBody = new Dictionary<string, object?>(StringComparer.Ordinal);
                var promptKeys = new List<string>();

                if (varProvided)
                {
                    var varsPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
                    var stagesPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var entry in vars!)
                    {
                        var key = entry;
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            api.Error.WriteLine($"--var '{entry}' has an empty key");
                            return 1;
                        }
                        var dot = key.IndexOf('.');
                        if (dot > 0 && dot < key.Length - 1)
                        {
                            var stage = key[..dot];
                            var stageKey = key[(dot + 1)..];
                            if (!stagesPayload.TryGetValue(stage, out var existing))
                            {
                                existing = new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["vars"] = new Dictionary<string, object?>(StringComparer.Ordinal),
                                };
                                stagesPayload[stage] = existing;
                            }
                            var stageObj = (Dictionary<string, object?>)existing!;
                            var stageVarsDict = (Dictionary<string, object?>)stageObj["vars"]!;
                            stageVarsDict[stageKey] = null;
                        }
                        else
                        {
                            varsPayload[key] = null;
                        }
                    }
                    if (varsPayload.Count > 0)
                        varsPatchBody["vars"] = varsPayload;
                    if (stagesPayload.Count > 0)
                        varsPatchBody["stages"] = stagesPayload;
                }

                if (promptProvided)
                {
                    foreach (var key in prompts!)
                    {
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            api.Error.WriteLine($"--prompt key '{key}' must not be empty");
                            return 1;
                        }
                        if (key.Contains('/'))
                        {
                            api.Error.WriteLine($"--prompt key '{key}' must not contain '/'");
                            return 1;
                        }
                        promptKeys.Add(key);
                    }
                }

                if (varProvided && varsPatchBody.Count > 0)
                {
                    var patchExit = await api.PrintPatchWithOutputAsync(
                        profilePath + "/variables",
                        varsPatchBody,
                        mode,
                        nameof(MohistCliApi.TableShape.WorkflowVariables));
                    if (patchExit != 0)
                        return patchExit;
                }

                if (defaultTemplateProvided)
                {
                    var deleteExit = await api.PrintDeleteWithOutputAsync(
                        profilePath + "/default-template",
                        mode,
                        nameof(MohistCliApi.TableShape.ProjectWorkflowProfile));
                    if (deleteExit != 0)
                        return deleteExit;
                }

                if (promptProvided)
                {
                    foreach (var key in promptKeys)
                    {
                        var promptExit = await api.PrintDeleteWithOutputAsync(
                            $"{profilePath}/prompts/{Uri.EscapeDataString(key)}",
                            mode,
                            nameof(MohistCliApi.TableShape.WorkflowProfilePrompt),
                            new System.Text.Json.Nodes.JsonObject
                            {
                                ["key"] = key,
                                ["deleted"] = true,
                            });
                        if (promptExit != 0)
                            return promptExit;
                    }
                }

                return 0;
            }
        });
        return cmd;
    }

    private static Command BuildConfigPreview(MohistCliApi api)
    {
        var cmd = new Command("preview", "Render a prompt under the project's current variables");
        var keyArg = new Argument<string>("key") { Description = "Prompt key (e.g. plan_prompt)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(keyArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var key = ctx.GetValue(keyArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return PreviewAsync();

            async Task<int> PreviewAsync()
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    api.Error.WriteLine("prompt key is required");
                    return 1;
                }
                if (key.Contains('/'))
                {
                    api.Error.WriteLine($"prompt key '{key}' must not contain '/'");
                    return 1;
                }
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                var validation = MohistCliApi.ValidateOutputMode(output);
                if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
                {
                    api.Error.WriteLine(invalid.Message);
                    return 1;
                }
                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
                return await api.PrintPostWithOutputAsync(
                    ProjectWorkflowProfilePath(resolvedProjectId, $"/prompts/{Uri.EscapeDataString(key!)}/preview"),
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.WorkflowProfilePreview));
            }
        });
        return cmd;
    }

    private static Command BuildTemplate(MohistCliApi api)
    {
        var template = new Command("template", "Manage project workflow templates");

        template.Subcommands.Add(BuildTemplateList(api));
        template.Subcommands.Add(BuildTemplateCreate(api));
        template.Subcommands.Add(BuildTemplateShow(api));
        template.Subcommands.Add(BuildTemplateUpdate(api));
        template.Subcommands.Add(BuildTemplateDelete(api));

        return template;
    }

    private static Command BuildTemplateList(MohistCliApi api)
    {
        var cmd = new Command("list", "List workflow templates");
        cmd.Aliases.Add("ls");
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
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return ListAsync();

            async Task<int> ListAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (string.IsNullOrWhiteSpace(resolvedProjectId))
                    return 1;
                return await api.PrintWithOutputAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/workflow-templates",
                    mode,
                    nameof(MohistCliApi.TableShape.ProjectTemplateList));
            }
        });
        return cmd;
    }

    private static Command BuildTemplateCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create a workflow template");
        var yamlOpt = new Option<string>("--yaml") { Description = "Template YAML body (inline or @file)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(yamlOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var yaml = ctx.GetValue(yamlOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return CreateAsync();

            async Task<int> CreateAsync()
            {
                var expanded = await api.ExpandAtFileAsync(yaml, "--yaml");
                if (expanded is MohistCliApi.ExpandAtFileResult.Failure)
                    return 1;
                var body = ((MohistCliApi.ExpandAtFileResult.Success)expanded).Value;
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (string.IsNullOrWhiteSpace(resolvedProjectId))
                    return 1;
                return await api.PrintPostWithOutputAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/workflow-templates",
                    new { yaml = body },
                    mode,
                    nameof(MohistCliApi.TableShape.ProjectTemplateShow));
            }
        });
        return cmd;
    }

    private static Command BuildTemplateShow(MohistCliApi api)
    {
        var cmd = new Command("show", "Show a workflow template");
        var templateIdArg = new Argument<string>("template-id") { Description = "Template ID" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(templateIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var templateId = ctx.GetValue(templateIdArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return ShowAsync();

            async Task<int> ShowAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (string.IsNullOrWhiteSpace(resolvedProjectId))
                    return 1;
                return await api.PrintWithOutputAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/workflow-templates/{MohistCliCommands.Escape(templateId!)}",
                    mode,
                    nameof(MohistCliApi.TableShape.ProjectTemplateShow));
            }
        });
        return cmd;
    }

    private static Command BuildTemplateUpdate(MohistCliApi api)
    {
        var cmd = new Command("update", "Update a workflow template");
        var templateIdArg = new Argument<string>("template-id") { Description = "Template ID" };
        var yamlOpt = new Option<string>("--yaml") { Description = "Template YAML body (inline or @file)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(templateIdArg);
        cmd.Options.Add(yamlOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var templateId = ctx.GetValue(templateIdArg);
            var yaml = ctx.GetValue(yamlOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return UpdateAsync();

            async Task<int> UpdateAsync()
            {
                var expanded = await api.ExpandAtFileAsync(yaml, "--yaml");
                if (expanded is MohistCliApi.ExpandAtFileResult.Failure)
                    return 1;
                var body = ((MohistCliApi.ExpandAtFileResult.Success)expanded).Value;
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (string.IsNullOrWhiteSpace(resolvedProjectId))
                    return 1;
                return await api.PrintPutWithOutputAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/workflow-templates/{MohistCliCommands.Escape(templateId!)}",
                    new { yaml = body },
                    mode,
                    nameof(MohistCliApi.TableShape.ProjectTemplateShow));
            }
        });
        return cmd;
    }

    private static Command BuildTemplateDelete(MohistCliApi api)
    {
        var cmd = new Command("delete", "Delete a workflow template");
        cmd.Aliases.Add("remove");
        cmd.Aliases.Add("rm");
        var templateIdArg = new Argument<string>("template-id") { Description = "Template ID" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(templateIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var templateId = ctx.GetValue(templateIdArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return DeleteAsync();

            async Task<int> DeleteAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (string.IsNullOrWhiteSpace(resolvedProjectId))
                    return 1;
                return await api.PrintDeleteWithOutputAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/workflow-templates/{MohistCliCommands.Escape(templateId!)}",
                    mode,
                    nameof(MohistCliApi.TableShape.ProjectTemplateShow));
            }
        });
        return cmd;
    }
}
