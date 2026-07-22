using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Mohist.Workflow.Definition;

public static class WorkflowDefinitionParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static WorkflowDefinitionParseResult Parse(string yaml)
    {
        if (yaml is null)
        {
            return new WorkflowDefinitionParseResult(
                null,
                new[] { new ValidationError("", "yaml input is required") });
        }

        YamlStream stream;
        try
        {
            using var reader = new StringReader(yaml);
            stream = new YamlStream();
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            return new WorkflowDefinitionParseResult(
                null,
                new[] { new ValidationError("", $"yaml syntax error: {ex.Message}") });
        }

        if (stream.Documents.Count == 0)
        {
            return new WorkflowDefinitionParseResult(
                null,
                new[] { new ValidationError("", "yaml document is empty") });
        }

        if (stream.Documents.Count > 1)
        {
            return new WorkflowDefinitionParseResult(
                null,
                new[] { new ValidationError("", "yaml must contain exactly one document") });
        }

        var root = stream.Documents[0].RootNode;
        if (root is null)
        {
            return new WorkflowDefinitionParseResult(
                null,
                new[] { new ValidationError("", "yaml document root is null") });
        }

        var errors = new List<ValidationError>();
        var emittedPaths = new HashSet<string>(StringComparer.Ordinal);
        var definition = YamlBuilder.Build(root, errors, emittedPaths);
        if (definition is not null)
        {
            WorkflowDefinitionRules.Apply(definition, errors, emittedPaths);
        }

        return new WorkflowDefinitionParseResult(
            definition,
            WorkflowDefinitionValidator.Sort(errors));
    }

    private static class YamlBuilder
    {
        private static readonly string[] TopLevelKeys = { "approval", "stages" };
        private static readonly string[] StageKeys =
        {
            "stage",
            "tasks",
            "checks",
            "requiresApproval",
            "lockBehavior",
            "resources",
        };
        private static readonly string[] TaskKeys =
        {
            "id",
            "title",
            "uses",
            "with",
            "expect",
            "artifacts",
            "setVars",
            "recovery",
        };
        private static readonly string[] CheckKeys = { "id", "title", "uses", "with" };
        private static readonly string[] ApprovalKeys = { "feedback" };
        private static readonly string[] ApprovalFeedbackKeys = { "tasks" };
        private static readonly string[] RecoveryKeys = { "budget", "handlers" };
        private static readonly string[] HandlerKeys = { "when", "tasks", "retrySelf" };
        private static readonly string[] ExpectKeys = { "files", "markers" };
        private static readonly string[] MarkerKeys = { "path", "oneOf", "failIf", "contains" };
        private static readonly string[] ArtifactKeys = { "files" };

        public static WorkflowDefinition? Build(YamlNode root, List<ValidationError> errors, HashSet<string> emittedPaths)
        {
            if (root is not YamlMappingNode rootMap)
            {
                AddError(errors, emittedPaths, "", "definition root must be an object");
                return null;
            }

            RejectUnknownKeys(rootMap, "", TopLevelKeys, errors, emittedPaths);

            var stages = BuildStages(GetValue(rootMap, "stages"), errors, emittedPaths);
            var approval = BuildApproval(GetValue(rootMap, "approval"), errors, emittedPaths);

            return new WorkflowDefinition(stages, approval);
        }

        private static ApprovalConfig? BuildApproval(YamlNode? node, List<ValidationError> errors, HashSet<string> emittedPaths)
        {
            if (node is null) return null;

            if (node is not YamlMappingNode map)
            {
                AddError(errors, emittedPaths, "approval", "approval must be an object");
                return null;
            }

            RejectUnknownKeys(map, "approval", ApprovalKeys, errors, emittedPaths);

            var feedbackNode = GetValue(map, "feedback");
            var feedback = BuildApprovalFeedback(feedbackNode, errors, emittedPaths);
            return new ApprovalConfig(feedback);
        }

        private static ApprovalFeedbackConfig? BuildApprovalFeedback(
            YamlNode? node,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            if (node is null) return null;

            if (node is not YamlMappingNode map)
            {
                AddError(errors, emittedPaths, "approval.feedback", "approval.feedback must be an object");
                return null;
            }

            RejectUnknownKeys(map, "approval.feedback", ApprovalFeedbackKeys, errors, emittedPaths);

            var tasksNode = GetValue(map, "tasks");
            if (tasksNode is null) return null;

            var tasks = BuildTaskList(tasksNode, "approval.feedback.tasks", errors, emittedPaths);
            return new ApprovalFeedbackConfig(tasks);
        }

        private static IReadOnlyList<StageDefinition> BuildStages(
            YamlNode? node,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            if (node is null)
            {
                AddError(errors, emittedPaths, "stages", "stages is required");
                return Array.Empty<StageDefinition>();
            }

            if (node is not YamlSequenceNode sequence)
            {
                AddError(errors, emittedPaths, "stages", "stages must be a list");
                return Array.Empty<StageDefinition>();
            }

            var stages = new List<StageDefinition>(sequence.Children.Count);
            for (var i = 0; i < sequence.Children.Count; i++)
            {
                var child = sequence.Children[i];
                var path = $"stages[{i}]";
                var stage = BuildStage(child, path, errors, emittedPaths);
                if (stage is not null) stages.Add(stage);
            }
            return stages;
        }

        private static StageDefinition? BuildStage(YamlNode node, string path, List<ValidationError> errors, HashSet<string> emittedPaths)
        {
            if (node is not YamlMappingNode map)
            {
                AddError(errors, emittedPaths, path, "stage must be an object");
                return null;
            }

            RejectUnknownKeys(map, path, StageKeys, errors, emittedPaths);

            var stageId = RequireString(map, "stage", $"{path}.stage", errors, emittedPaths) ?? string.Empty;
            var requiresApproval = ReadBool(map, "requiresApproval", $"{path}.requiresApproval", errors, emittedPaths);
            var lockBehavior = ReadOptionalString(map, "lockBehavior", $"{path}.lockBehavior", errors, emittedPaths);
            var resources = ReadStringList(map, "resources", $"{path}.resources", errors, emittedPaths);
            var tasksNode = GetValue(map, "tasks");
            var checksNode = GetValue(map, "checks");

            var tasks = tasksNode is null
                ? Array.Empty<TaskDefinition>()
                : BuildTaskList(tasksNode, $"{path}.tasks", errors, emittedPaths);
            var checks = checksNode is null
                ? Array.Empty<CheckDefinition>()
                : BuildCheckList(checksNode, $"{path}.checks", errors, emittedPaths);

            return new StageDefinition(
                stageId,
                tasks,
                checks,
                requiresApproval,
                lockBehavior,
                resources);
        }

        private static IReadOnlyList<TaskDefinition> BuildTaskList(
            YamlNode node,
            string path,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            if (node is not YamlSequenceNode sequence)
            {
                AddError(errors, emittedPaths, path, $"{path} must be a list");
                return Array.Empty<TaskDefinition>();
            }

            var tasks = new List<TaskDefinition>(sequence.Children.Count);
            for (var i = 0; i < sequence.Children.Count; i++)
            {
                var child = sequence.Children[i];
                var taskPath = $"{path}[{i}]";
                var task = BuildTask(child, taskPath, errors, emittedPaths);
                if (task is not null) tasks.Add(task);
            }
            return tasks;
        }

        private static TaskDefinition? BuildTask(YamlNode node, string path, List<ValidationError> errors, HashSet<string> emittedPaths)
        {
            if (node is not YamlMappingNode map)
            {
                AddError(errors, emittedPaths, path, $"{path} must be an object");
                return null;
            }

            RejectUnknownKeys(map, path, TaskKeys, errors, emittedPaths);

            var id = RequireString(map, "id", $"{path}.id", errors, emittedPaths) ?? string.Empty;
            var title = ReadOptionalString(map, "title", $"{path}.title", errors, emittedPaths);
            var uses = RequireString(map, "uses", $"{path}.uses", errors, emittedPaths) ?? string.Empty;
            var withNode = GetValue(map, "with");
            var expectNode = GetValue(map, "expect");
            var artifactsNode = GetValue(map, "artifacts");
            var setVarsNode = GetValue(map, "setVars");
            var recoveryNode = GetValue(map, "recovery");

            var with = ReadObjectMap(withNode, $"{path}.with", errors, emittedPaths);
            var expect = ReadObjectMap(expectNode, $"{path}.expect", errors, emittedPaths);
            var artifacts = BuildArtifacts(artifactsNode, path, errors, emittedPaths);
            var setVars = ReadSetVars(setVarsNode, $"{path}.setVars", errors, emittedPaths);
            var recovery = BuildRecovery(recoveryNode, path, errors, emittedPaths);

            return new TaskDefinition(
                id,
                title,
                uses,
                with,
                expect,
                artifacts,
                setVars,
                recovery);
        }

        private static IReadOnlyList<CheckDefinition> BuildCheckList(
            YamlNode node,
            string path,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            if (node is not YamlSequenceNode sequence)
            {
                AddError(errors, emittedPaths, path, $"{path} must be a list");
                return Array.Empty<CheckDefinition>();
            }

            var checks = new List<CheckDefinition>(sequence.Children.Count);
            for (var i = 0; i < sequence.Children.Count; i++)
            {
                var child = sequence.Children[i];
                var checkPath = $"{path}[{i}]";
                var check = BuildCheck(child, checkPath, errors, emittedPaths);
                if (check is not null) checks.Add(check);
            }
            return checks;
        }

        private static CheckDefinition? BuildCheck(YamlNode node, string path, List<ValidationError> errors, HashSet<string> emittedPaths)
        {
            if (node is not YamlMappingNode map)
            {
                AddError(errors, emittedPaths, path, $"{path} must be an object");
                return null;
            }

            RejectUnknownKeys(map, path, CheckKeys, errors, emittedPaths);

            var id = RequireString(map, "id", $"{path}.id", errors, emittedPaths) ?? string.Empty;
            var title = ReadOptionalString(map, "title", $"{path}.title", errors, emittedPaths);
            var uses = RequireString(map, "uses", $"{path}.uses", errors, emittedPaths) ?? string.Empty;
            var withNode = GetValue(map, "with");
            var with = ReadObjectMap(withNode, $"{path}.with", errors, emittedPaths);
            return new CheckDefinition(id, title, uses, with);
        }

        private static TaskArtifactCapture? BuildArtifacts(
            YamlNode? node,
            string taskPath,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            if (node is null) return null;

            if (node is not YamlMappingNode map)
            {
                AddError(errors, emittedPaths, $"{taskPath}.artifacts", $"{taskPath}.artifacts must be an object");
                return null;
            }

            RejectUnknownKeys(map, $"{taskPath}.artifacts", ArtifactKeys, errors, emittedPaths);

            var filesNode = GetValue(map, "files");
            if (filesNode is null) return null;

            if (filesNode is not YamlSequenceNode sequence)
            {
                AddError(errors, emittedPaths, $"{taskPath}.artifacts.files", $"{taskPath}.artifacts.files must be a list");
                return null;
            }

            var files = new List<TaskArtifactDeclaration>(sequence.Children.Count);
            for (var i = 0; i < sequence.Children.Count; i++)
            {
                var child = sequence.Children[i];
                var entryPath = $"{taskPath}.artifacts.files[{i}]";

                if (child is YamlScalarNode scalar)
                {
                    var text = scalar.Value;
                    if (!IsStringScalar(scalar))
                    {
                        AddError(errors, emittedPaths, entryPath, "artifacts.files[] entry must be a string");
                    }
                    else if (string.IsNullOrWhiteSpace(text))
                    {
                        AddError(errors, emittedPaths, entryPath, "artifacts.files[] entry must be a non-empty string");
                    }
                    else
                    {
                        files.Add(new TaskArtifactDeclaration(text));
                    }
                }
                else if (child is YamlMappingNode entryMap)
                {
                    var pathValue = ReadOptionalString(entryMap, "path", $"{entryPath}.path", errors, emittedPaths);
                    if (string.IsNullOrWhiteSpace(pathValue))
                    {
                        AddError(errors, emittedPaths, $"{entryPath}.path", "artifacts.files[].path must be non-empty");
                    }
                    else
                    {
                        files.Add(new TaskArtifactDeclaration(pathValue!));
                    }
                }
                else
                {
                    AddError(errors, emittedPaths, entryPath, "artifacts.files[] entry must be a string or an object");
                }
            }

            return files.Count == 0 ? null : new TaskArtifactCapture(files);
        }

        private static RecoveryDefinition? BuildRecovery(
            YamlNode? node,
            string taskPath,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            if (node is null) return null;

            if (node is not YamlMappingNode map)
            {
                AddError(errors, emittedPaths, $"{taskPath}.recovery", $"{taskPath}.recovery must be an object");
                return null;
            }

            RejectUnknownKeys(map, $"{taskPath}.recovery", RecoveryKeys, errors, emittedPaths);

            var budget = ReadNonNegativeInt(map, "budget", $"{taskPath}.recovery.budget", errors, emittedPaths);
            var handlersNode = GetValue(map, "handlers");
            if (handlersNode is null)
            {
                AddError(errors, emittedPaths,
                    $"{taskPath}.recovery.handlers",
                    "recovery.handlers must be a non-empty list");
                return new RecoveryDefinition(budget, Array.Empty<RecoveryHandlerDefinition>());
            }

            if (handlersNode is not YamlSequenceNode sequence)
            {
                AddError(errors, emittedPaths,
                    $"{taskPath}.recovery.handlers",
                    "recovery.handlers must be a non-empty list");
                return new RecoveryDefinition(budget, Array.Empty<RecoveryHandlerDefinition>());
            }

            var handlers = new List<RecoveryHandlerDefinition>(sequence.Children.Count);
            for (var i = 0; i < sequence.Children.Count; i++)
            {
                var child = sequence.Children[i];
                var handlerPath = $"{taskPath}.recovery.handlers[{i}]";
                var handler = BuildHandler(child, handlerPath, errors, emittedPaths);
                if (handler is not null) handlers.Add(handler);
            }

            return new RecoveryDefinition(budget, handlers);
        }

        private static RecoveryHandlerDefinition? BuildHandler(
            YamlNode node,
            string path,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            if (node is not YamlMappingNode map)
            {
                AddError(errors, emittedPaths, path, $"{path} must be an object");
                return null;
            }

            RejectUnknownKeys(map, path, HandlerKeys, errors, emittedPaths);

            var when = ReadOptionalString(map, "when", $"{path}.when", errors, emittedPaths);
            var retrySelf = ReadBool(map, "retrySelf", $"{path}.retrySelf", errors, emittedPaths);
            var tasksNode = GetValue(map, "tasks");
            var tasks = tasksNode is null
                ? Array.Empty<TaskDefinition>()
                : BuildTaskList(tasksNode, $"{path}.tasks", errors, emittedPaths);

            return new RecoveryHandlerDefinition(when, tasks, retrySelf);
        }

        private static Dictionary<string, string>? ReadSetVars(
            YamlNode? node,
            string path,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            if (node is null) return null;

            if (node is not YamlMappingNode map)
            {
                AddError(errors, emittedPaths, path, $"{path} must be an object");
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in map.Children)
            {
                var key = ScalarKey(entry.Key);
                var valueNode = entry.Value;
                if (valueNode is YamlScalarNode valueScalar && IsStringScalar(valueScalar))
                {
                    result[key] = valueScalar.Value ?? string.Empty;
                }
                else
                {
                    AddError(errors, emittedPaths,
                        $"{path}.{key}",
                        $"setVars value for '{key}' must be a string");
                }
            }
            return result.Count == 0 ? null : result;
        }

        private static Dictionary<string, JsonElement?>? ReadObjectMap(
            YamlNode? node,
            string path,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            if (node is null) return null;

            if (node is not YamlMappingNode map)
            {
                AddError(errors, emittedPaths, path, $"{path} must be an object");
                return null;
            }

            var result = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            foreach (var entry in map.Children)
            {
                var key = ScalarKey(entry.Key);
                var element = YamlToJsonElement(entry.Value, $"{path}.{key}", errors, emittedPaths);
                result[key] = element;
            }
            return result;
        }

        private static JsonElement? YamlToJsonElement(
            YamlNode? node,
            string path,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            if (node is null) return null;

            switch (node)
            {
                case YamlScalarNode scalar:
                    return YamlScalarToJsonElement(scalar, path);
                case YamlSequenceNode sequence:
                {
                    var array = new List<JsonElement?>();
                    for (var i = 0; i < sequence.Children.Count; i++)
                    {
                        array.Add(YamlToJsonElement(sequence.Children[i], $"{path}[{i}]", errors, emittedPaths));
                    }
                    return WrapArray(array);
                }
                case YamlMappingNode mapping:
                {
                    var obj = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
                    foreach (var entry in mapping.Children)
                    {
                        var key = ScalarKey(entry.Key);
                        obj[key] = YamlToJsonElement(entry.Value, $"{path}.{key}", errors, emittedPaths);
                    }
                    return WrapObject(obj);
                }
                default:
                    AddError(errors, emittedPaths, path, $"{path} has unsupported yaml node kind");
                    return null;
            }
        }

        private static JsonElement YamlScalarToJsonElement(YamlScalarNode scalar, string path)
        {
            _ = path;
            var style = scalar.Style;
            var raw = scalar.Value;
            if (raw is null)
            {
                return JsonDocument.Parse("null").RootElement.Clone();
            }

            if (style is ScalarStyle.Plain && raw.Equals("null", StringComparison.Ordinal))
            {
                return JsonDocument.Parse("null").RootElement.Clone();
            }

            if (style is ScalarStyle.Plain
                && (raw.Equals("true", StringComparison.Ordinal) || raw.Equals("false", StringComparison.Ordinal)))
            {
                return JsonDocument.Parse(raw).RootElement.Clone();
            }

            if (style is ScalarStyle.Plain
                && long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                return JsonDocument.Parse(raw).RootElement.Clone();
            }

            if (style is ScalarStyle.Plain
                && double.TryParse(
                    raw,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _))
            {
                return JsonDocument.Parse(raw).RootElement.Clone();
            }

            var escaped = JsonEncodedText.Encode(raw);
            return JsonDocument.Parse($"\"{escaped}\"").RootElement.Clone();
        }

        private static JsonElement WrapObject(Dictionary<string, JsonElement?> entries)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var (key, value) in entries)
                {
                    if (value.HasValue)
                    {
                        writer.WritePropertyName(key);
                        value.Value.WriteTo(writer);
                    }
                    else
                    {
                        writer.WriteNull(key);
                    }
                }
                writer.WriteEndObject();
            }
            stream.Position = 0;
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.Clone();
        }

        private static JsonElement WrapArray(List<JsonElement?> entries)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                foreach (var entry in entries)
                {
                    if (entry.HasValue)
                    {
                        entry.Value.WriteTo(writer);
                    }
                    else
                    {
                        writer.WriteNullValue();
                    }
                }
                writer.WriteEndArray();
            }
            stream.Position = 0;
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.Clone();
        }

        private static void RejectUnknownKeys(
            YamlMappingNode map,
            string path,
            string[] allowed,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            foreach (var entry in map.Children)
            {
                var key = ScalarKey(entry.Key);
                if (Array.IndexOf(allowed, key) < 0)
                {
                    var keyPath = path.Length == 0 ? key : $"{path}.{key}";
                    AddError(errors, emittedPaths, keyPath, $"unknown field '{key}'");
                }
            }
        }

        private static YamlNode? GetValue(YamlMappingNode map, string key)
        {
            foreach (var entry in map.Children)
            {
                if (string.Equals(ScalarKey(entry.Key), key, StringComparison.Ordinal))
                    return entry.Value;
            }
            return null;
        }

        private static string ScalarKey(YamlNode node)
        {
            if (node is YamlScalarNode scalar) return scalar.Value ?? string.Empty;
            return node.ToString() ?? string.Empty;
        }

        private static string? RequireString(
            YamlMappingNode map,
            string key,
            string path,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            var node = GetValue(map, key);
            if (node is null)
            {
                return null;
            }

            if (node is not YamlScalarNode scalar)
            {
                AddError(errors, emittedPaths, path, $"'{key}' must be a string");
                return null;
            }

            if (!IsStringScalar(scalar))
            {
                AddError(errors, emittedPaths, path, $"'{key}' must be a string");
                return null;
            }

            var value = scalar.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value;
        }

        private static string? ReadOptionalString(
            YamlMappingNode map,
            string key,
            string path,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            var node = GetValue(map, key);
            if (node is null) return null;
            if (node is not YamlScalarNode scalar)
            {
                AddError(errors, emittedPaths, path, $"'{key}' must be a string");
                return null;
            }

            if (!IsStringScalar(scalar))
            {
                AddError(errors, emittedPaths, path, $"'{key}' must be a string");
                return null;
            }

            return scalar.Value ?? string.Empty;
        }

        private static bool ReadBool(
            YamlMappingNode map,
            string key,
            string path,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            var node = GetValue(map, key);
            if (node is null) return false;
            if (node is not YamlScalarNode scalar)
            {
                AddError(errors, emittedPaths, path, $"'{key}' must be a boolean");
                return false;
            }

            if (scalar.Style != ScalarStyle.Plain)
            {
                AddError(errors, emittedPaths, path, $"'{key}' must be a boolean");
                return false;
            }

            var raw = scalar.Value;
            if (string.Equals(raw, "true", StringComparison.Ordinal))
                return true;
            if (string.Equals(raw, "false", StringComparison.Ordinal))
                return false;

            AddError(errors, emittedPaths,
                path,
                $"'{key}' must be a boolean (got '{raw}')");
            return false;
        }

        private static int ReadNonNegativeInt(
            YamlMappingNode map,
            string key,
            string path,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            var node = GetValue(map, key);
            if (node is null) return 0;
            if (node is not YamlScalarNode scalar)
            {
                AddError(errors, emittedPaths, path, $"'{key}' must be a non-negative integer");
                return 0;
            }

            if (scalar.Style is not ScalarStyle.Plain)
            {
                AddError(errors, emittedPaths, path, $"'{key}' must be a non-negative integer");
                return 0;
            }

            var raw = scalar.Value;
            if (!int.TryParse(
                raw,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
            {
                AddError(errors, emittedPaths,
                    path,
                    $"'{key}' must be a non-negative integer (got '{raw}')");
                return 0;
            }

            return value;
        }

        private static IReadOnlyList<string>? ReadStringList(
            YamlMappingNode map,
            string key,
            string path,
            List<ValidationError> errors,
            HashSet<string> emittedPaths)
        {
            var node = GetValue(map, key);
            if (node is null) return null;

            if (node is not YamlSequenceNode sequence)
            {
                AddError(errors, emittedPaths, path, $"'{key}' must be a list of strings");
                return null;
            }

            var values = new List<string>(sequence.Children.Count);
            for (var i = 0; i < sequence.Children.Count; i++)
            {
                var child = sequence.Children[i];
                if (child is not YamlScalarNode scalar)
                {
                    AddError(errors, emittedPaths,
                        $"{path}[{i}]",
                        $"'{key}' entries must be strings");
                    continue;
                }

                if (!IsStringScalar(scalar))
                {
                    AddError(errors, emittedPaths,
                        $"{path}[{i}]",
                        $"'{key}' entries must be strings");
                    continue;
                }

                var text = scalar.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    AddError(errors, emittedPaths,
                        $"{path}[{i}]",
                        $"'{key}' entries must be non-empty strings");
                    continue;
                }
                values.Add(text);
            }

            return values.Count == 0 ? null : values;
        }

        private static bool IsStringScalar(YamlScalarNode scalar)
        {
            var value = YamlScalarToJsonElement(scalar, "");
            return value.ValueKind == JsonValueKind.String;
        }

        private static void AddError(
            List<ValidationError> errors,
            HashSet<string> emittedPaths,
            string path,
            string message)
        {
            if (!emittedPaths.Add(path))
            {
                return;
            }
            errors.Add(new ValidationError(path, message));
        }
    }
}
