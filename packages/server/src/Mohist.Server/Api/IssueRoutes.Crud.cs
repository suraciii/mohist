using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Grains.Coordinator;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using IssueDomain = Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueCrud(this RouteGroupBuilder group)
    {
        group.MapGet("/parent-candidates", async (HttpContext ctx, IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            var candidates = await issuesQuery.ListParentCandidatesAsync(project.Id, ctx.RequestAborted);
            return ApiResults.Ok(candidates);
        });

        group.MapGet("/", async (
            HttpContext ctx,
            string projectRef,
            string? stage,
            string[]? label,
            string? priority,
            bool? archived,
            bool? all,
            string? repository,
            int? parent,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            if (TryValidateLabelFilters(label, out var labelError) is false)
                return ApiResults.BadRequest(labelError!, "invalid_label");

            var list = await issuesQuery.ListWithLabelFiltersAsync(project.Id, project, stage, label, priority, archived, all, repository, parent);
            return ApiResults.Ok(list);
        });

        group.MapPost("/", async (
            HttpContext ctx,
            string projectRef,
            CreateIssueRequest req,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            IssueRepositoryResolver repositoryResolver,
            IWorkflowProfileProvider profileProvider,
            IssueWorkflowProfileManager issueProfileManager) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return ApiResults.BadRequest("title is required");

            var project = GetRequiredProject(ctx);

            if (TryValidateLabels(req.Labels, out var labelError) is false)
                return ApiResults.BadRequest(labelError!, "invalid_label");

            if (TryValidateModelMetadata(req, out var modelError) is false)
                return ApiResults.BadRequest(modelError!, "invalid_model_metadata");

            if (TryValidateAgentConfigForbiddenKeys(req.Raw, "agentConfig", out var agentConfigError) is false)
                return ApiResults.BadRequest(agentConfigError!, "invalid_agent_config");

            var requestedWorkflowProfileId = req.WorkflowProfileId;
            if (!string.IsNullOrWhiteSpace(requestedWorkflowProfileId)
                && !await profileProvider.ContainsAsync(project.Id, requestedWorkflowProfileId))
                return ApiResults.BadRequest($"Unknown workflow profile '{req.WorkflowProfileId}'", "unknown_workflow_profile");

            var resolution = repositoryResolver.Resolve(project, req.RepositoryName);
            if (resolution.HasProblem)
                return ApiResults.BadRequest(resolution.Problem!.Message, IssueRepositoryResolutionHelpers.RepositoryProblemCodeToApiCode(resolution.Problem.Code));

            var counter = grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(project.Id));
            var number = await counter.NextAsync();
            var commandId = $"create:{project.Id}:{number}";

            // issue-417 T-005: route the create through the
            // Project-scoped coordinator so it serializes against the
            // matching Project repository removal. The coordinator
            // resolves the canonical name, captures the issue's
            // pre-existing revision (0 for a fresh slot), fences the
            // command, and invokes the idempotent Issue participant.
            var coordinator = grains.GetGrain<IIssueRepositoryCoordinatorGrain>(project.Id);
            IssueRepositoryBindingResult coordinatorResult;
            try
            {
                coordinatorResult = await coordinator.CreateIssueAsync(
                    new RepositoryCommandPayload.Create(
                        ProjectId: project.Id,
                        IssueNumber: number,
                        RepositoryName: resolution.Repository!.Name,
                        Title: req.Title,
                        Body: req.Body,
                        Labels: req.Labels,
                        Priority: req.Priority,
                        Risk: req.Risk,
                        IsDraft: req.IsDraft ?? true,
                        AttachmentIds: req.AttachmentIds,
                        WorkflowProfileId: requestedWorkflowProfileId,
                        PrerequisiteNumbers: req.PrerequisiteNumbers,
                        ParentIssueNumber: req.ParentIssueNumber),
                    commandId: commandId,
                    expectedRevision: null);
            }
            catch (AttachmentLimitException ex)
            {
                return ApiResults.Fail(ex.Message, 413, "attachment_count_limit_exceeded");
            }
            catch (AttachmentValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_attachment");
            }
            catch (IssueDomain.UnknownWorkflowProfileException ex)
            {
                return ApiResults.BadRequest(ex.Message, "unknown_workflow_profile");
            }
            catch (IssueDomain.PrerequisiteValidationException ex)
            {
                var code = ex.Reason switch
                {
                    "self_reference" => "circular_prerequisite",
                    "not_found" => "prerequisite_not_found",
                    _ => $"prerequisite_{ex.Reason}",
                };
                return ApiResults.BadRequest(ex.Message, code);
            }
            catch (IssueDomain.IssueParentNotFoundException ex)
            {
                return ApiResults.BadRequest(ex.Message, "parent_not_found");
            }
            catch (IssueDomain.IssueParentIneligibleException ex)
            {
                return ApiResults.Conflict(ex.Message, "parent_ineligible");
            }
            catch (IssueDomain.IssueParentIsChildException ex)
            {
                return ApiResults.Conflict(ex.Message, "parent_is_sub_issue");
            }
            catch (IssueDomain.IssueEpicMemberCannotBecomeChildException ex)
            {
                return ApiResults.Conflict(ex.Message, "issue_belongs_to_epic");
            }

            // Coordinator-level results are mapped onto the same HTTP
            // envelopes the direct IssueGrain.CreateAsync throws so
            // the route surface stays uniform for callers.
            if (coordinatorResult.Code == IssueRepositoryBindingResultCode.RepositoryUnknown)
                return ApiResults.BadRequest(coordinatorResult.Message ?? $"Repository '{resolution.Repository!.Name}' is not declared", "repository_not_found");
            if (coordinatorResult.Code == IssueRepositoryBindingResultCode.RepositoryStaleRevision)
                return ApiResults.Conflict(coordinatorResult.Message ?? "Repository revision is stale", "repository_stale_revision");

            try
            {
                await ApplyCreateModelMetadataAsync(issueProfileManager, project.Id, number, req);
            }
            catch (AttachmentLimitException ex)
            {
                return ApiResults.Fail(ex.Message, 413, "attachment_count_limit_exceeded");
            }
            catch (AttachmentValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_attachment");
            }

            var issue = await issuesQuery.GetAsync(project.Id, number, project);
            return Results.Json(new { success = true, data = issue }, statusCode: 201);
        });

        group.MapGet("/{number:int}", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            var info = await issuesQuery.GetAsync(project.Id, number, project);
            return info is not null ? ApiResults.Ok(info) : ApiResults.NotFound($"Issue #{number} not found");
        });

        group.MapPatch("/{number:int}", async (
            HttpContext ctx,
            string projectRef,
            int number,
            UpdateIssueRequest req,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            IssueRepositoryResolver repositoryResolver,
            IWorkflowProfileProvider profileProvider,
            IssueWorkflowProfileManager issueProfileManager) =>
        {
            var project = GetRequiredProject(ctx);

            if (req.Contains(nameof(UpdateIssueRequest.Labels))
                && TryValidateLabels(req.Labels, out var labelError) is false)
                return ApiResults.BadRequest(labelError!, "invalid_label");

            if (TryValidateModelMetadata(req, out var modelError) is false)
                return ApiResults.BadRequest(modelError!, "invalid_model_metadata");

            if (TryValidateModelMetadataRawTypes(req.Raw, out var rawTypeError) is false)
                return ApiResults.BadRequest(rawTypeError!, "invalid_model_metadata");

            if (TryValidateAgentConfigForbiddenKeys(req.Raw, "agentConfig", out var agentConfigError) is false)
                return ApiResults.BadRequest(agentConfigError!, "invalid_agent_config");

            // Workflow profile id: any present non-null value must refer to a
            // known registered profile. Null means "clear to inherit default"
            // and is part of the established three-state semantics.
            var workflowProfileIdForUpdate = req.WorkflowProfileId;
            if (req.Contains(nameof(UpdateIssueRequest.WorkflowProfileId))
                && !string.IsNullOrWhiteSpace(req.WorkflowProfileId))
            {
                var requestedWorkflowProfileId = req.WorkflowProfileId;
                if (!await profileProvider.ContainsAsync(project.Id, requestedWorkflowProfileId))
                {
                    return ApiResults.BadRequest($"Unknown workflow profile '{req.WorkflowProfileId}'", "unknown_workflow_profile");
                }

                workflowProfileIdForUpdate = requestedWorkflowProfileId;
            }

            var grain = await GetIssueGrainAsync(grains, issuesQuery, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");

            // issue-417 T-005: a repository-bearing PATCH must be routed
            // through the Project-scoped coordinator so the complete
            // aggregate PATCH is fenced as a single command — an ambiguous
            // result cannot commit the repository reassignment while
            // dropping sibling Issue fields.
            if (req.Contains(nameof(UpdateIssueRequest.RepositoryName)))
            {
                var repositoryUpdateName = req.RepositoryName;
                if (string.IsNullOrWhiteSpace(repositoryUpdateName))
                {
                    return ApiResults.BadRequest(
                        "Repository name must not be empty when present in the PATCH body",
                        "repository_not_found");
                }

                var resolution = repositoryResolver.Resolve(project, repositoryUpdateName);
                if (resolution.HasProblem)
                {
                    return ApiResults.BadRequest(
                        resolution.Problem!.Message,
                        IssueRepositoryResolutionHelpers.RepositoryProblemCodeToApiCode(resolution.Problem.Code));
                }
                var canonicalRepositoryName = resolution.Repository!.Name;

                var coordinator = grains.GetGrain<IIssueRepositoryCoordinatorGrain>(project.Id);
                IssueRepositoryBindingResult coordinatorResult;
                try
                {
                    coordinatorResult = await coordinator.ChangeRepositoryAsync(
                        new RepositoryCommandPayload.Change(
                            ProjectId: project.Id,
                            IssueNumber: number,
                            RepositoryName: canonicalRepositoryName,
                            Body: req.Body,
                            Labels: req.Labels,
                            Priority: req.Priority,
                            IsDraft: req.IsDraft,
                            AttachmentIds: req.AttachmentIds,
                            WorkflowProfileId: workflowProfileIdForUpdate,
                            PresentFields: req.Fields,
                            Title: req.Title,
                            ParentIssueNumber: req.ParentIssueNumber),
                        commandId: $"change:{project.Id}:{number}:{Guid.NewGuid():N}",
                        expectedRevision: null);
                }
                catch (IssueDomain.WorkflowProfileLockedException ex)
                {
                    return ApiResults.Conflict(ex.Message, "workflow_profile_locked");
                }
                catch (IssueDomain.IssueParentNotFoundException ex) { return ApiResults.BadRequest(ex.Message, "parent_not_found"); }
                catch (IssueDomain.IssueParentIneligibleException ex) { return ApiResults.Conflict(ex.Message, "parent_ineligible"); }
                catch (IssueDomain.IssueParentIsChildException ex) { return ApiResults.Conflict(ex.Message, "parent_is_sub_issue"); }
                catch (IssueDomain.IssueHasChildrenCannotBecomeChildException ex) { return ApiResults.Conflict(ex.Message, "target_has_children"); }
                catch (IssueDomain.IssueEpicMemberCannotBecomeChildException ex) { return ApiResults.Conflict(ex.Message, "issue_belongs_to_epic"); }
                catch (InvalidOperationException ex)
                {
                    return ApiResults.Conflict(ex.Message);
                }
                catch (AttachmentLimitException ex)
                {
                    return ApiResults.Fail(ex.Message, 413, "attachment_count_limit_exceeded");
                }
                catch (AttachmentValidationException ex)
                {
                    return ApiResults.BadRequest(ex.Message, "invalid_attachment");
                }

                switch (coordinatorResult.Code)
                {
                    case IssueRepositoryBindingResultCode.Applied:
                    case IssueRepositoryBindingResultCode.AlreadyApplied:
                        break;
                    case IssueRepositoryBindingResultCode.RepositoryUnknown:
                        return ApiResults.BadRequest(
                            coordinatorResult.Message ?? "Repository is not declared",
                            "repository_not_found");
                    case IssueRepositoryBindingResultCode.RepositoryLocked:
                        return ApiResults.Conflict(
                            coordinatorResult.Message ?? "Target repository is locked",
                            "repository_locked");
                    case IssueRepositoryBindingResultCode.RepositoryStaleRevision:
                        return ApiResults.Conflict(
                            coordinatorResult.Message ?? "Repository revision is stale",
                            "repository_stale_revision");
                    default:
                        return ApiResults.Conflict(
                            coordinatorResult.Message ?? "Repository change rejected");
                }

                await ApplyUpdateModelMetadataAsync(issueProfileManager, project.Id, number, req, req.Raw);

                var info = await issuesQuery.GetAsync(project.Id, number);
                return ApiResults.Ok(info);
            }

            // Non-repository PATCHes remain on the direct Issue path
            // (T-001 / T-003): the coordinator only fences the four
            // binding-sensitive commands.
            try
            {
                await grain.UpdateFullAsync(new UpdateIssueData(
                    Title: req.Title,
                    Body: req.Body,
                    Labels: req.Labels,
                    Priority: req.Priority,
                    IsDraft: req.IsDraft,
                    AttachmentIds: req.AttachmentIds,
                    PresentFields: req.Fields,
                    WorkflowProfileId: workflowProfileIdForUpdate,
                    ParentIssueNumber: req.ParentIssueNumber));
            }
            catch (IssueDomain.WorkflowProfileLockedException ex)
            {
                return ApiResults.Conflict(ex.Message, "workflow_profile_locked");
            }
            catch (IssueDomain.IssueParentNotFoundException ex) { return ApiResults.BadRequest(ex.Message, "parent_not_found"); }
            catch (IssueDomain.IssueParentIneligibleException ex) { return ApiResults.Conflict(ex.Message, "parent_ineligible"); }
            catch (IssueDomain.IssueParentIsChildException ex) { return ApiResults.Conflict(ex.Message, "parent_is_sub_issue"); }
            catch (IssueDomain.IssueHasChildrenCannotBecomeChildException ex) { return ApiResults.Conflict(ex.Message, "target_has_children"); }
            catch (IssueDomain.IssueEpicMemberCannotBecomeChildException ex) { return ApiResults.Conflict(ex.Message, "issue_belongs_to_epic"); }
            catch (IssueRepositoryUnknownException ex)
            {
                return ApiResults.BadRequest(ex.Message, "repository_not_found");
            }
            catch (IssueRepositoryLockedException ex)
            {
                return ApiResults.Conflict(ex.Message, "repository_locked");
            }
            catch (IssueRepositoryStaleRevisionException ex)
            {
                return ApiResults.Conflict(ex.Message, "repository_stale_revision");
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
            catch (AttachmentLimitException ex)
            {
                return ApiResults.Fail(ex.Message, 413, "attachment_count_limit_exceeded");
            }
            catch (AttachmentValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_attachment");
            }

            await ApplyUpdateModelMetadataAsync(issueProfileManager, project.Id, number, req, req.Raw);

            var patched = await issuesQuery.GetAsync(project.Id, number);
            return ApiResults.Ok(patched);
        });
    }

    internal static bool TryValidateLabels(Dictionary<string, string>? labels, out string? error)
    {
        if (labels is null) { error = null; return true; }
        foreach (var (key, value) in labels)
        {
            try
            {
                IssueDomain.Issue.ValidateLabelKey(key);
                IssueDomain.Issue.ValidateLabelValue(value);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
        }
        error = null;
        return true;
    }

    internal static bool TryValidateLabelFilters(IReadOnlyList<string>? labels, out string? error)
    {
        if (labels is null || labels.Count == 0) { error = null; return true; }

        foreach (var token in labels)
        {
            var idx = token.IndexOf('=');
            if (idx < 0)
            {
                error = $"Issue label filter '{token}' is invalid; expected key=value";
                return false;
            }

            try
            {
                IssueDomain.Issue.ValidateLabelKey(token[..idx]);
                IssueDomain.Issue.ValidateLabelValue(token[(idx + 1)..]);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
        }
        error = null;
        return true;
    }

    private static bool TryValidateModelMetadata(CreateIssueRequest req, out string? error)
    {
        error = IssueModelMetadata.Validate(req.Model, req.StageModels);
        return error is null;
    }

    private static bool TryValidateModelMetadata(UpdateIssueRequest req, out string? error)
    {
        error = IssueModelMetadata.Validate(req.Model, req.StageModels);
        return error is null;
    }

    private static bool TryValidateModelMetadataRawTypes(JsonElement rawPatch, out string? error)
    {
        if (TryValidateStringFieldType(rawPatch, "model", out error) is false)
            return false;
        if (TryValidateStringFieldType(rawPatch, "modelVariant", out error) is false)
            return false;
        if (TryValidateStringMapFieldType(rawPatch, "stageModels", out error) is false)
            return false;
        if (TryValidateStringMapFieldType(rawPatch, "stageModelVariants", out error) is false)
            return false;
        error = null;
        return true;
    }

    private static bool TryValidateStringFieldType(JsonElement raw, string name, out string? error)
    {
        error = null;
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty(name, out var el))
            return true;
        if (el.ValueKind is JsonValueKind.Null or JsonValueKind.String)
            return true;
        error = $"{name} must be a string or null";
        return false;
    }

    private static bool TryValidateStringMapFieldType(JsonElement raw, string name, out string? error)
    {
        error = null;
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty(name, out var el))
            return true;
        if (el.ValueKind == JsonValueKind.Null)
            return true;
        if (el.ValueKind != JsonValueKind.Object)
        {
            error = $"{name} must be an object or null";
            return false;
        }
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
                continue;
            error = $"{name}.{prop.Name} must be a string";
            return false;
        }
        return true;
    }

    private static bool TryValidateAgentConfigForbiddenKeys(JsonElement raw, string fieldName, out string? error)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty(fieldName, out var el))
        {
            error = null;
            return true;
        }
        if (el.ValueKind == JsonValueKind.Null)
        {
            error = null;
            return true;
        }
        error = IssueModelMetadata.ValidateAgentConfig(el);
        return error is null;
    }

    private static async Task ApplyCreateModelMetadataAsync(
        IssueWorkflowProfileManager profileManager,
        string projectId,
        int issueNumber,
        CreateIssueRequest req)
    {
        var patch = BuildCreatePatch(req);
        if (!patch.TouchesAnyField) return;

        var seed = IssueModelMetadata.ApplyModelMetadata(VariableBundle.Empty, patch);
        await profileManager.SetVariablesAsync(projectId, issueNumber, seed);
    }

    private static async Task ApplyUpdateModelMetadataAsync(
        IssueWorkflowProfileManager profileManager,
        string projectId,
        int issueNumber,
        UpdateIssueRequest req,
        JsonElement rawPatch)
    {
        var patch = BuildUpdatePatch(req, rawPatch);
        if (!patch.TouchesAnyField) return;

        var current = await profileManager.GetVariablesAsync(projectId, issueNumber);
        var patched = IssueModelMetadata.ApplyModelMetadata(current, patch);
        await profileManager.SetVariablesAsync(projectId, issueNumber, patched);
    }

    /// <summary>
    /// Build a <see cref="IssueModelMetadata.ModelMetadataPatch"/> for the
    /// create path. On create, every field with a non-null bound value is
    /// <see cref="IssueModelMetadata.FieldPatchKind.Set"/>; nothing is
    /// "clear" or "absent" because there's no prior state to clear.
    /// </summary>
    private static IssueModelMetadata.ModelMetadataPatch BuildCreatePatch(CreateIssueRequest req)
    {
        return new IssueModelMetadata.ModelMetadataPatch(
            Model: req.Model is null ? IssueModelMetadata.FieldPatch<string>.Absent : IssueModelMetadata.FieldPatch<string>.Set(req.Model),
            ModelVariant: req.ModelVariant is null ? IssueModelMetadata.FieldPatch<string>.Absent : IssueModelMetadata.FieldPatch<string>.Set(req.ModelVariant),
            StageModels: req.StageModels is null ? IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Absent : IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Set(req.StageModels),
            StageModelVariants: req.StageModelVariants is null ? IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Absent : IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Set(req.StageModelVariants));
    }

    /// <summary>
    /// Build a <see cref="IssueModelMetadata.ModelMetadataPatch"/> for the
    /// update path. We inspect the raw patch JSON to detect explicit
    /// presence (vs. absence) per field — System.Text.Json deserializes
    /// both "absent" and "explicit null" into <c>null</c> on a nullable
    /// string, but the spec says a present-but-null field means "clear".
    /// </summary>
    private static IssueModelMetadata.ModelMetadataPatch BuildUpdatePatch(UpdateIssueRequest req, JsonElement rawPatch)
    {
        return new IssueModelMetadata.ModelMetadataPatch(
            Model: ResolveStringField(rawPatch, "model", req.Model),
            ModelVariant: ResolveStringField(rawPatch, "modelVariant", req.ModelVariant),
            StageModels: ResolveMapField(rawPatch, "stageModels", req.StageModels),
            StageModelVariants: ResolveMapField(rawPatch, "stageModelVariants", req.StageModelVariants));
    }

    private static IssueModelMetadata.FieldPatch<string> ResolveStringField(JsonElement raw, string name, string? boundValue)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty(name, out var el))
            return IssueModelMetadata.FieldPatch<string>.Absent;

        return el.ValueKind switch
        {
            JsonValueKind.Null => IssueModelMetadata.FieldPatch<string>.Clear,
            JsonValueKind.String when string.IsNullOrWhiteSpace(el.GetString())
                => IssueModelMetadata.FieldPatch<string>.Clear,
            JsonValueKind.String => IssueModelMetadata.FieldPatch<string>.Set(el.GetString()!),
            _ => IssueModelMetadata.FieldPatch<string>.Absent,
        };
    }

    private static IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>> ResolveMapField(
        JsonElement raw,
        string name,
        IReadOnlyDictionary<string, string>? boundValue)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty(name, out var el))
            return IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Absent;

        if (el.ValueKind == JsonValueKind.Null)
            return IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Clear;

        if (el.ValueKind != JsonValueKind.Object)
            return IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Absent;

        // Preserve raw JSON order — the helper reads keys via TryGetValue and
        // does not depend on order, but tests expect the order the caller sent.
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { } s)
            {
                dict[prop.Name] = s;
            }
        }
        return IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Set(dict);
    }
}
