using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mohist.Workflow.Definition;

internal enum TemplatePositionContext
{
    Ordinary,
    RecoveryHandler,
    ApprovalFeedback,
}

internal sealed class TaskPositionMap
{
    private readonly Dictionary<string, int> _idToPosition = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _positionToPath = new();
    private int _nextPosition;

    public IReadOnlyDictionary<string, int> IdToPosition => _idToPosition;

    public bool TryGetPosition(string id, out int position) =>
        _idToPosition.TryGetValue(id, out position);

    public string GetPath(int position) =>
        _positionToPath.TryGetValue(position, out var path) ? path : string.Empty;

    public void Register(string id, string path)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (_idToPosition.ContainsKey(id)) return;
        _idToPosition[id] = _nextPosition;
        _positionToPath[_nextPosition] = path;
        _nextPosition++;
    }
}

internal static class WorkflowDefinitionRules
{
    private static readonly Regex TemplateTokenRegex = new(
        @"\$\{\{\s*(?<path>[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}",
        RegexOptions.Compiled);

    public static void Apply(
        WorkflowDefinition definition,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths = null)
    {
        if (definition.Stages is null || definition.Stages.Count == 0)
        {
            AddError(errors, emittedPaths, "stages", "stages must be non-empty");
            return;
        }

        var positionMap = BuildPositionMap(definition);

        var stageIds = new HashSet<string>(StringComparer.Ordinal);
        for (var stageIndex = 0; stageIndex < definition.Stages.Count; stageIndex++)
        {
            var stage = definition.Stages[stageIndex];
            ValidateStage(stage, stageIndex, stageIds, positionMap, errors, emittedPaths);
        }

        if (definition.Approval?.Feedback is { Tasks: { Count: > 0 } tasks })
        {
            for (var taskIndex = 0; taskIndex < tasks.Count; taskIndex++)
            {
                ValidateApprovalFeedbackTask(
                    tasks[taskIndex],
                    $"approval.feedback.tasks[{taskIndex}]",
                    positionMap,
                    errors,
                    emittedPaths);
            }
        }
    }

    private static TaskPositionMap BuildPositionMap(WorkflowDefinition definition)
    {
        var map = new TaskPositionMap();

        if (definition.Stages is null) return map;

        for (var stageIndex = 0; stageIndex < definition.Stages.Count; stageIndex++)
        {
            var stage = definition.Stages[stageIndex];
            if (stage.Tasks is null) continue;

            for (var taskIndex = 0; taskIndex < stage.Tasks.Count; taskIndex++)
            {
                var task = stage.Tasks[taskIndex];
                var taskPath = $"stages[{stageIndex}].tasks[{taskIndex}]";
                map.Register(task.Id, taskPath);

                if (task.Recovery is { Handlers: { } handlers })
                {
                    for (var handlerIndex = 0; handlerIndex < handlers.Count; handlerIndex++)
                    {
                        var handler = handlers[handlerIndex];
                        if (handler.Tasks is null) continue;

                        for (var innerTaskIndex = 0; innerTaskIndex < handler.Tasks.Count; innerTaskIndex++)
                        {
                            var innerTask = handler.Tasks[innerTaskIndex];
                            var innerPath = $"{taskPath}.recovery.handlers[{handlerIndex}].tasks[{innerTaskIndex}]";
                            map.Register(innerTask.Id, innerPath);
                        }
                    }
                }
            }
        }

        if (definition.Approval?.Feedback is { Tasks: { Count: > 0 } feedbackTasks })
        {
            for (var taskIndex = 0; taskIndex < feedbackTasks.Count; taskIndex++)
            {
                var task = feedbackTasks[taskIndex];
                var path = $"approval.feedback.tasks[{taskIndex}]";
                map.Register(task.Id, path);
            }
        }

        return map;
    }

    private static void ValidateStage(
        StageDefinition stage,
        int stageIndex,
        HashSet<string> stageIds,
        TaskPositionMap positionMap,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        var stagePath = $"stages[{stageIndex}]";

        if (string.IsNullOrWhiteSpace(stage.Stage))
        {
            AddError(errors, emittedPaths, $"{stagePath}.stage", "stage identifier is required");
        }
        else
        {
            if (!stageIds.Add(stage.Stage))
            {
                AddError(errors, emittedPaths,
                    $"{stagePath}.stage",
                    $"stage identifier '{stage.Stage}' is duplicated");
            }
        }

        ValidateLockBehavior(stage, stagePath, errors, emittedPaths);

        var taskIds = new HashSet<string>(StringComparer.Ordinal);
        for (var taskIndex = 0; taskIndex < stage.Tasks.Count; taskIndex++)
        {
            ValidateTask(
                stage.Tasks[taskIndex],
                $"{stagePath}.tasks[{taskIndex}]",
                taskIds,
                positionMap,
                TemplatePositionContext.Ordinary,
                errors,
                emittedPaths);
        }

        var checkIds = new HashSet<string>(StringComparer.Ordinal);
        for (var checkIndex = 0; checkIndex < stage.Checks.Count; checkIndex++)
        {
            ValidateCheck(
                stage.Checks[checkIndex],
                $"{stagePath}.checks[{checkIndex}]",
                checkIds,
                positionMap,
                errors,
                emittedPaths);
        }
    }

    private static void ValidateLockBehavior(
        StageDefinition stage,
        string stagePath,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        var hasLockBehavior = !string.IsNullOrWhiteSpace(stage.LockBehavior);
        var hasResources = stage.Resources is { Count: > 0 };

        if (hasLockBehavior && !string.Equals(stage.LockBehavior, "sequential", StringComparison.Ordinal))
        {
            AddError(errors, emittedPaths,
                $"{stagePath}.lockBehavior",
                "lockBehavior must be 'sequential'");
        }

        if (hasLockBehavior && !hasResources)
        {
            AddError(errors, emittedPaths,
                $"{stagePath}.lockBehavior",
                "lockBehavior requires non-empty resources");
        }

        if (!hasLockBehavior && hasResources)
        {
            AddError(errors, emittedPaths,
                $"{stagePath}.resources",
                "resources require lockBehavior");
        }
    }

    private static void ValidateTask(
        TaskDefinition task,
        string taskPath,
        HashSet<string> taskIds,
        TaskPositionMap positionMap,
        TemplatePositionContext context,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
        {
            AddError(errors, emittedPaths, $"{taskPath}.id", "task identifier is required");
        }
        else
        {
            if (!taskIds.Add(task.Id))
            {
                AddError(errors, emittedPaths,
                    $"{taskPath}.id",
                    $"task identifier '{task.Id}' is duplicated");
            }
        }

        if (string.IsNullOrWhiteSpace(task.Uses))
        {
            AddError(errors, emittedPaths, $"{taskPath}.uses", "uses is required");
        }

        ValidateSetVars(task.SetVars, $"{taskPath}.setVars", errors, emittedPaths);
        ValidateArtifacts(task.Artifacts, taskPath, errors, emittedPaths);
        ValidateExpect(task.Expect, taskPath, errors, emittedPaths);
        ValidateRecovery(task.Recovery, taskPath, positionMap, errors, emittedPaths);
        ValidateTaskTemplates(task, taskPath, context, positionMap, errors, emittedPaths);
    }

    private static void ValidateApprovalFeedbackTask(
        TaskDefinition task,
        string taskPath,
        TaskPositionMap positionMap,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
        {
            AddError(errors, emittedPaths, $"{taskPath}.id", "task identifier is required");
        }

        if (string.IsNullOrWhiteSpace(task.Uses))
        {
            AddError(errors, emittedPaths, $"{taskPath}.uses", "uses is required");
        }

        ValidateRecovery(task.Recovery, taskPath, positionMap, errors, emittedPaths);
        ValidateTaskTemplates(task, taskPath, TemplatePositionContext.ApprovalFeedback, positionMap, errors, emittedPaths);
    }

    private static void ValidateCheck(
        CheckDefinition check,
        string checkPath,
        HashSet<string> checkIds,
        TaskPositionMap positionMap,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        if (string.IsNullOrWhiteSpace(check.Id))
        {
            AddError(errors, emittedPaths, $"{checkPath}.id", "check identifier is required");
        }
        else
        {
            if (!checkIds.Add(check.Id))
            {
                AddError(errors, emittedPaths,
                    $"{checkPath}.id",
                    $"check identifier '{check.Id}' is duplicated");
            }
        }

        if (string.IsNullOrWhiteSpace(check.Uses))
        {
            AddError(errors, emittedPaths, $"{checkPath}.uses", "uses is required");
        }

        ValidateCheckTemplates(check, checkPath, positionMap, errors, emittedPaths);
    }

    private static void ValidateSetVars(
        Dictionary<string, string>? setVars,
        string setVarsPath,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        if (setVars is null) return;

        foreach (var (key, value) in setVars)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                AddError(errors, emittedPaths, setVarsPath, "setVars key must be non-empty");
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                AddError(errors, emittedPaths,
                    $"{setVarsPath}.{key}",
                    $"setVars entry '{key}' requires a non-empty value");
                continue;
            }

            if (!value.StartsWith("output.", StringComparison.Ordinal))
            {
                AddError(errors, emittedPaths,
                    $"{setVarsPath}.{key}",
                    $"setVars value must be an output.* path (got '{value}')");
            }
        }
    }

    private static void ValidateArtifacts(
        TaskArtifactCapture? artifacts,
        string taskPath,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        if (artifacts is null || artifacts.IsEmpty) return;

        for (var fileIndex = 0; fileIndex < artifacts.Files.Count; fileIndex++)
        {
            var file = artifacts.Files[fileIndex];
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                AddError(errors, emittedPaths,
                    $"{taskPath}.artifacts.files[{fileIndex}].path",
                    "artifacts.files[].path must be non-empty");
            }
        }
    }

    private static void ValidateExpect(
        Dictionary<string, System.Text.Json.JsonElement?>? expect,
        string taskPath,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        if (expect is null) return;

        if (expect.TryGetValue("files", out var filesValue) && filesValue is not null)
        {
            if (filesValue.Value.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                AddError(errors, emittedPaths, $"{taskPath}.expect.files", "expect.files must be a list");
            }
            else
            {
                for (var fileIndex = 0; fileIndex < filesValue.Value.GetArrayLength(); fileIndex++)
                {
                    var entry = filesValue.Value[fileIndex];
                    if (entry.ValueKind != System.Text.Json.JsonValueKind.Object)
                    {
                        AddError(errors, emittedPaths,
                            $"{taskPath}.expect.files[{fileIndex}]",
                            "expect.files[] entries must be objects");
                        continue;
                    }

                    if (!entry.TryGetProperty("path", out var pathProp)
                        || pathProp.ValueKind != System.Text.Json.JsonValueKind.String
                        || string.IsNullOrWhiteSpace(pathProp.GetString()))
                    {
                        AddError(errors, emittedPaths,
                            $"{taskPath}.expect.files[{fileIndex}].path",
                            "expect.files[].path must be non-empty");
                    }
                }
            }
        }

        if (!expect.TryGetValue("markers", out var markersValue) || markersValue is null)
            return;

        if (markersValue.Value.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            AddError(errors, emittedPaths, $"{taskPath}.expect.markers", "expect.markers must be a list");
            return;
        }

        for (var markerIndex = 0; markerIndex < markersValue.Value.GetArrayLength(); markerIndex++)
        {
            var marker = markersValue.Value[markerIndex];
            if (marker.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                AddError(errors, emittedPaths,
                    $"{taskPath}.expect.markers[{markerIndex}]",
                    "expect.markers[] entries must be objects");
                continue;
            }

            HashSet<string>? oneOfValues = null;
            if (!marker.TryGetProperty("oneOf", out var oneOf)
                || oneOf.ValueKind != System.Text.Json.JsonValueKind.Array
                || oneOf.GetArrayLength() == 0)
            {
                AddError(errors, emittedPaths,
                    $"{taskPath}.expect.markers[{markerIndex}].oneOf",
                    marker.TryGetProperty("contains", out _)
                        ? "verdict marker must use a non-empty oneOf list"
                        : "expect.markers[].oneOf must be a non-empty list");
            }
            else
            {
                oneOfValues = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < oneOf.GetArrayLength(); i++)
                {
                    var value = oneOf[i];
                    if (value.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                    var text = value.GetString();
                    if (text is null) continue;
                    oneOfValues.Add(text);
                }
            }

            if (marker.TryGetProperty("failIf", out var failIf)
                && failIf.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrWhiteSpace(failIf.GetString()))
            {
                var failIfText = failIf.GetString()!;
                if (oneOfValues is null || !oneOfValues.Contains(failIfText))
                {
                    AddError(errors, emittedPaths,
                        $"{taskPath}.expect.markers[{markerIndex}].failIf",
                        $"expect.markers[].failIf must be a member of oneOf (got '{failIfText}')");
                }
            }
        }
    }

    private static void ValidateRecovery(
        RecoveryDefinition? recovery,
        string taskPath,
        TaskPositionMap positionMap,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        if (recovery is null) return;

        if (recovery.Budget < 0)
        {
            AddError(errors, emittedPaths,
                $"{taskPath}.recovery.budget",
                "recovery.budget must be a non-negative integer");
        }

        if (recovery.Handlers is null || recovery.Handlers.Count == 0)
        {
            AddError(errors, emittedPaths,
                $"{taskPath}.recovery.handlers",
                "recovery.handlers must be non-empty");
            return;
        }

        var hasDefault = false;
        var defaultAt = -1;
        for (var handlerIndex = 0; handlerIndex < recovery.Handlers.Count; handlerIndex++)
        {
            var handler = recovery.Handlers[handlerIndex];
            var handlerPath = $"{taskPath}.recovery.handlers[{handlerIndex}]";

            ValidateWhen(handler, handlerPath, errors, emittedPaths);

            var hasWhen = handler.When is not null;
            if (!hasWhen)
            {
                if (hasDefault)
                {
                    AddError(errors, emittedPaths,
                        handlerPath,
                        "recovery allows at most one default handler (one without 'when')");
                }
                hasDefault = true;
                defaultAt = handlerIndex;
                if (handlerIndex != recovery.Handlers.Count - 1)
                {
                    AddError(errors, emittedPaths,
                        handlerPath,
                        "recovery default handler (without 'when') must be last");
                }
            }

            var hasTasks = handler.Tasks is { Count: > 0 };
            if (!hasTasks && !handler.RetrySelf)
            {
                AddError(errors, emittedPaths,
                    handlerPath,
                    "recovery handler must declare tasks or retrySelf");
            }

            if (hasTasks)
            {
                var innerTaskIds = new HashSet<string>(StringComparer.Ordinal);
                for (var taskIndex = 0; taskIndex < handler.Tasks!.Count; taskIndex++)
                {
                    ValidateRecoveryTask(
                        handler.Tasks[taskIndex],
                        $"{handlerPath}.tasks[{taskIndex}]",
                        innerTaskIds,
                        positionMap,
                        errors,
                        emittedPaths);
                }
            }
        }

        _ = defaultAt;
    }

    private static void ValidateWhen(
        RecoveryHandlerDefinition handler,
        string handlerPath,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        if (handler.When is null) return;

        var when = handler.When;
        var equals = when.IndexOf('=');
        if (equals <= 0 || equals == when.Length - 1)
        {
            AddError(errors, emittedPaths,
                $"{handlerPath}.when",
                "recovery 'when' must be of the form field=value with both sides non-empty");
            return;
        }

        var field = when[..equals];
        var value = when[(equals + 1)..];
        if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(value))
        {
            AddError(errors, emittedPaths,
                $"{handlerPath}.when",
                "recovery 'when' must be of the form field=value with both sides non-empty");
        }
    }

    private static void ValidateRecoveryTask(
        TaskDefinition task,
        string taskPath,
        HashSet<string> taskIds,
        TaskPositionMap positionMap,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
        {
            AddError(errors, emittedPaths, $"{taskPath}.id", "task identifier is required");
        }
        else
        {
            if (!taskIds.Add(task.Id))
            {
                AddError(errors, emittedPaths,
                    $"{taskPath}.id",
                    $"task identifier '{task.Id}' is duplicated");
            }
        }

        if (string.IsNullOrWhiteSpace(task.Uses))
        {
            AddError(errors, emittedPaths, $"{taskPath}.uses", "uses is required");
        }

        ValidateTaskTemplates(task, taskPath, TemplatePositionContext.RecoveryHandler, positionMap, errors, emittedPaths);
    }

    private static void ValidateTaskTemplates(
        TaskDefinition task,
        string taskPath,
        TemplatePositionContext context,
        TaskPositionMap positionMap,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        var referencingPosition = positionMap.TryGetPosition(task.Id, out var pos)
            ? pos
            : -1;
        ValidateDictionaryForTemplates(
            task.With,
            $"{taskPath}.with",
            context,
            referencingPosition,
            positionMap,
            errors,
            emittedPaths);
        ValidateDictionaryForTemplates(
            task.Expect,
            $"{taskPath}.expect",
            context,
            referencingPosition,
            positionMap,
            errors,
            emittedPaths);
    }

    private static void ValidateCheckTemplates(
        CheckDefinition check,
        string checkPath,
        TaskPositionMap positionMap,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        ValidateDictionaryForTemplates(
            check.With,
            $"{checkPath}.with",
            TemplatePositionContext.Ordinary,
            referencingPosition: -1,
            positionMap,
            errors,
            emittedPaths);
    }

    private static void ValidateDictionaryForTemplates(
        Dictionary<string, JsonElement?>? values,
        string path,
        TemplatePositionContext context,
        int referencingPosition,
        TaskPositionMap positionMap,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        if (values is null) return;

        foreach (var (key, value) in values)
        {
            var keyPath = $"{path}.{key}";
            if (value is null) continue;
            ValidateJsonElementForTemplates(
                value.Value,
                keyPath,
                context,
                referencingPosition,
                positionMap,
                errors,
                emittedPaths);
        }
    }

    private static void ValidateJsonElementForTemplates(
        JsonElement element,
        string path,
        TemplatePositionContext context,
        int referencingPosition,
        TaskPositionMap positionMap,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    ValidateTemplateExpressions(
                        text,
                        path,
                        context,
                        referencingPosition,
                        positionMap,
                        errors,
                        emittedPaths);
                }
                break;
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    ValidateJsonElementForTemplates(
                        prop.Value,
                        $"{path}.{prop.Name}",
                        context,
                        referencingPosition,
                        positionMap,
                        errors,
                        emittedPaths);
                }
                break;
            case JsonValueKind.Array:
                for (var i = 0; i < element.GetArrayLength(); i++)
                {
                    ValidateJsonElementForTemplates(
                        element[i],
                        $"{path}[{i}]",
                        context,
                        referencingPosition,
                        positionMap,
                        errors,
                        emittedPaths);
                }
                break;
        }
    }

    private static void ValidateTemplateExpressions(
        string text,
        string path,
        TemplatePositionContext context,
        int referencingPosition,
        TaskPositionMap positionMap,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        var matches = TemplateTokenRegex.Matches(text);
        if (matches.Count == 0) return;

        foreach (Match match in matches)
        {
            var expr = match.Groups["path"].Value;
            if (string.IsNullOrEmpty(expr)) continue;

            var root = expr.Split('.', 2)[0];
            if (!TemplateRoots.IsAllowed(root))
            {
                AddError(errors, emittedPaths, path,
                    $"template root '{root}' is not in the public table");
                continue;
            }

            if (string.Equals(root, TemplateRoots.Failure, StringComparison.Ordinal)
                && context != TemplatePositionContext.RecoveryHandler)
            {
                AddError(errors, emittedPaths, path,
                    "template 'failure.*' is allowed only inside recovery-handler tasks");
            }

            if (string.Equals(root, TemplateRoots.Work, StringComparison.Ordinal)
                && context != TemplatePositionContext.ApprovalFeedback
                && IsApprovalFeedbackPath(expr))
            {
                AddError(errors, emittedPaths, path,
                    "template 'work.approvalFeedback.*' is allowed only inside approval-feedback tasks");
            }

            if (string.Equals(root, TemplateRoots.Tasks, StringComparison.Ordinal))
            {
                ValidateTasksReference(expr, path, referencingPosition, positionMap, errors, emittedPaths);
            }
        }
    }

    private static bool IsApprovalFeedbackPath(string expr)
    {
        var parts = expr.Split('.');
        return parts.Length >= 2 && string.Equals(parts[1], "approvalFeedback", StringComparison.Ordinal);
    }

    private static void ValidateTasksReference(
        string expr,
        string path,
        int referencingPosition,
        TaskPositionMap positionMap,
        List<ValidationError> errors,
        HashSet<string>? emittedPaths)
    {
        var parts = expr.Split('.');
        if (parts.Length < 2 || string.IsNullOrEmpty(parts[1])) return;

        var referencedId = parts[1];

        if (!positionMap.TryGetPosition(referencedId, out var referencedPosition))
        {
            AddError(errors, emittedPaths, path,
                $"template references undeclared task '{referencedId}'");
            return;
        }

        if (referencingPosition < 0) return;

        if (referencedPosition == referencingPosition)
        {
            AddError(errors, emittedPaths, path,
                $"template references self task '{referencedId}'");
            return;
        }

        if (referencedPosition > referencingPosition)
        {
            AddError(errors, emittedPaths, path,
                $"template references forward task '{referencedId}'");
        }
    }

    private static void AddError(
        List<ValidationError> errors,
        HashSet<string>? emittedPaths,
        string path,
        string message)
    {
        if (emittedPaths is not null && !emittedPaths.Add(path))
        {
            return;
        }
        errors.Add(new ValidationError(path, message));
    }
}
