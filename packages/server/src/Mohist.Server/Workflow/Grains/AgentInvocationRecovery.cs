using System.Text.Json;
using System.Text.RegularExpressions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Server-side recovery decision for a delegated Agent invocation
/// (issue 559, design D7). The inline executor decides recovery on the
/// runner (<c>tryRecovery</c> in <c>runtime/recovery.ts</c>) because the
/// runner holds the failure result; a handoff task's failure arrives as
/// an AgentJob terminal on the Server, so the finalizer applies the same
/// decision here: <c>when</c>-matching against the failure context
/// (<c>{output, error}</c>) under the remaining recovery budget, and on
/// a match the handler's tasks plus an optional self-retry are scheduled
/// as continuation attempts through the same
/// <see cref="RuntimeTaskFollowUps"/> projection the inline report
/// path uses. This is a faithful port of the runner's matcher and
/// renderer for the failure namespace (whole-string references preserve
/// the resolved JSON type; embedded references substitute strings;
/// unresolvable references fail the task with the runner's
/// <c>recovery-reference-unresolved</c> code), restricted to the
/// whole-string <c>${{ prompts.* }}</c> references recovery tasks may
/// embed.
/// </summary>
internal static class AgentInvocationRecovery
{
    private const int MaxMessageLength = 4000;
    private static readonly Regex FailureReferencePattern = new(@"\$\{\{\s*(failure(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}", RegexOptions.Compiled);
    private static readonly Regex FailureWholeStringPattern = new(@"^\s*\$\{\{\s*(failure(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}\s*$", RegexOptions.Compiled);
    private static readonly Regex PromptReferencePattern = new(@"^\s*\$\{\{\s*prompts\.([A-Za-z_][A-Za-z0-9_-]*)\s*\}\}\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Port of the runner's <c>tryRecovery</c> for a failed terminal.
    /// Returns the continuation tasks (handler tasks, then the self-retry
    /// last) when a handler matched under the remaining budget, or null
    /// when recovery does not apply. An unresolvable failure reference
    /// surfaces as <paramref name="failureMessage"/> with the runner's
    /// diagnostic so the caller fails the task instead.
    /// </summary>
    public static IReadOnlyList<RuntimeTaskInput>? TryRecover(
        TaskRun task,
        TaskReport failureReport,
        IReadOnlyDictionary<string, string> prompts,
        out string? failureMessage)
    {
        failureMessage = null;
        var recovery = task.Recovery;
        if (recovery is null)
            return null;

        var remaining = task.RecoveryRemaining.HasValue
            ? Math.Clamp(task.RecoveryRemaining.Value, 0, Math.Max(0, recovery.Budget))
            : Math.Max(0, recovery.Budget);
        if (remaining <= 0)
            return null;

        var context = FailureContext(failureReport);
        var error = failureReport.Error;
        var handler = recovery.Handlers.FirstOrDefault(h =>
                !string.IsNullOrWhiteSpace(h.When) && MatchesWhen(h.When!, context))
            ?? (error is not null
                ? recovery.Handlers.FirstOrDefault(h => string.IsNullOrWhiteSpace(h.When))
                : null);
        if (handler is null)
            return null;

        var addTasks = new List<RuntimeTaskInput>();
        foreach (var handlerTask in handler.Tasks)
        {
            RuntimeTaskInput rendered;
            try
            {
                rendered = RenderHandlerTask(handlerTask, context, prompts);
            }
            catch (RecoveryReferenceUnresolvedException ex)
            {
                failureMessage = ex.Message[..Math.Min(ex.Message.Length, MaxMessageLength)];
                return null;
            }

            addTasks.Add(rendered);
        }

        if (handler.RetrySelf)
        {
            var retryId = task.WorkId is not null && task.WorkId.Contains('.')
                ? task.WorkId[..task.WorkId.LastIndexOf('.')]
                : task.WorkId;
            addTasks.Add(new RuntimeTaskInput(
                retryId ?? task.DefinitionId,
                task.Title,
                task.Uses,
                CloneElement(task.WithInput),
                Expect: CloneElement(task.ExpectInput),
                Artifacts: task.Artifacts,
                SetVars: task.SetVars,
                Recovery: task.Recovery,
                RecoveryRemaining: remaining - 1));
        }

        return addTasks.Count > 0 ? addTasks : null;
    }

    /// <summary>Port of the runner's <c>matchesWhen</c>: <c>path=expected</c>.</summary>
    public static bool MatchesWhen(string when, JsonElement context)
    {
        var eq = when.IndexOf('=');
        if (eq == -1) return false;
        var path = when[..eq].Trim();
        var expected = when[(eq + 1)..].Trim();
        return String(GetPath(context, path)) == expected;
    }

    private static string String(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return string.Empty;
        return value.Value.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString() ?? string.Empty,
            _ => value.Value.GetRawText(),
        };
    }

    private static JsonElement FailureContext(TaskReport report)
    {
        var members = new List<KeyValuePair<string, object?>>();
        if (report.Output is { } output)
            members.Add(new("output", output));
        if (report.Error is { } error)
            members.Add(new("error", JSON.SerializeToElement(new { code = error.Code, message = error.Message })));
        return JSON.SerializeToElement(ObjectFromMembers(members));
    }

    private static Dictionary<string, JsonElement?> ObjectFromMembers(IEnumerable<KeyValuePair<string, object?>> members)
    {
        var result = new Dictionary<string, JsonElement?>();
        foreach (var (key, value) in members)
            result[key] = value is JsonElement element ? element : JSON.SerializeToElement(value);
        return result;
    }

    private static RuntimeTaskInput RenderHandlerTask(
        TaskDefinition task,
        JsonElement failureContext,
        IReadOnlyDictionary<string, string> prompts)
    {
        var with = RenderFieldMap(task.With, failureContext, prompts);
        var expect = RenderFieldMap(task.Expect, failureContext, prompts);
        return new RuntimeTaskInput(
            task.Id,
            task.Title ?? task.Id,
            task.Uses,
            with is null ? null : JSON.SerializeToElement(with),
            Expect: expect is null ? null : JSON.SerializeToElement(expect),
            Artifacts: task.Artifacts,
            SetVars: task.SetVars,
            Recovery: task.Recovery,
            RecoveryRemaining: task.Recovery is not null ? Math.Max(0, task.Recovery.Budget) : null);
    }

    private static Dictionary<string, JsonElement?>? RenderFieldMap(
        Dictionary<string, JsonElement?>? input,
        JsonElement failureContext,
        IReadOnlyDictionary<string, string> prompts)
    {
        if (input is null) return null;
        var result = new Dictionary<string, JsonElement?>(input.Count);
        foreach (var (key, value) in input)
            result[key] = value is { } element ? RenderFieldValue(element, failureContext, prompts) : null;
        return result;
    }

    private static JsonElement RenderFieldValue(
        JsonElement value,
        JsonElement failureContext,
        IReadOnlyDictionary<string, string> prompts)
    {
        if (value.ValueKind == JsonValueKind.String)
            return RenderFieldString(value.GetString()!, failureContext, prompts);
        if (value.ValueKind == JsonValueKind.Array)
            return JSON.SerializeToElement(value.EnumerateArray()
                .Select(item => RenderFieldValue(item, failureContext, prompts))
                .ToArray());
        if (value.ValueKind == JsonValueKind.Object)
        {
            var rendered = new Dictionary<string, JsonElement?>();
            foreach (var property in value.EnumerateObject())
                rendered[property.Name] = RenderFieldValue(property.Value.Clone(), failureContext, prompts);
            return JSON.SerializeToElement(rendered);
        }

        return value;
    }

    private static JsonElement RenderFieldString(
        string value,
        JsonElement failureContext,
        IReadOnlyDictionary<string, string> prompts)
    {
        var promptMatch = PromptReferencePattern.Match(value);
        if (promptMatch.Success)
        {
            var key = promptMatch.Groups[1].Value;
            if (!prompts.TryGetValue(key, out var body))
                throw new RecoveryReferenceUnresolvedException(DescribeUnresolved(value, failureContext));
            try
            {
                return ExpandFailureValue(JSON.SerializeToElement(body), failureContext);
            }
            catch (RecoveryReferenceUnresolvedException)
            {
                throw new RecoveryReferenceUnresolvedException(DescribeUnresolved(value, failureContext));
            }
        }

        return ExpandFailureString(value, failureContext);
    }

    private static JsonElement ExpandFailureValue(JsonElement value, JsonElement failureContext)
    {
        if (value.ValueKind == JsonValueKind.String)
            return ExpandFailureString(value.GetString()!, failureContext);
        if (value.ValueKind == JsonValueKind.Array)
            return JSON.SerializeToElement(value.EnumerateArray()
                .Select(item => ExpandFailureValue(item.Clone(), failureContext))
                .ToArray());
        if (value.ValueKind == JsonValueKind.Object)
        {
            var expanded = new Dictionary<string, JsonElement?>();
            foreach (var property in value.EnumerateObject())
                expanded[property.Name] = ExpandFailureValue(property.Value.Clone(), failureContext);
            return JSON.SerializeToElement(expanded);
        }

        return value;
    }

    private static JsonElement ExpandFailureString(string value, JsonElement failureContext)
    {
        var whole = FailureWholeStringPattern.Match(value);
        if (whole.Success)
        {
            var resolved = ResolveFailurePath(failureContext, whole.Groups[1].Value);
            if (resolved is null)
                throw new RecoveryReferenceUnresolvedException(DescribeUnresolved(value, failureContext));
            return resolved.Value;
        }

        var replaced = FailureReferencePattern.Replace(value, match =>
            FailureStringify(ResolveFailurePath(failureContext, match.Groups[1].Value)));
        return JSON.SerializeToElement(replaced);
    }

    private static JsonElement? ResolveFailurePath(JsonElement failureContext, string path)
    {
        var parts = path.Split('.');
        if (parts.Length == 0 || parts[0] != "failure") return null;
        if (parts.Length == 1) return failureContext;
        if (parts[1] is not ("output" or "error")) return null;
        if (!failureContext.TryGetProperty(parts[1], out var current)) return null;
        foreach (var part in parts.Skip(2))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
                return null;
        }

        return current.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? current.Clone() : current;
    }

    private static string FailureStringify(JsonElement? value)
    {
        if (value is null) return string.Empty;
        return value.Value.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.Value.GetRawText(),
            JsonValueKind.Null => string.Empty,
            _ => value.Value.GetRawText(),
        };
    }

    private static JsonElement? GetPath(JsonElement obj, string path)
    {
        var current = (JsonElement?)obj;
        foreach (var part in path.Split('.'))
        {
            if (current is not { ValueKind: JsonValueKind.Object }
                || !current.Value.TryGetProperty(part, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static JsonElement? CloneElement(Dictionary<string, JsonElement?>? value) =>
        value is null
            ? null
            : JSON.SerializeToElement(new Dictionary<string, JsonElement?>(value, StringComparer.Ordinal));

    private static string DescribeUnresolved(string reference, JsonElement failureContext) =>
        $"recovery-reference-unresolved: recovery task references unresolved failure expression '{reference}'. Available failure context: failure.output {Describe(failureContext, "output")}; failure.error {Describe(failureContext, "error")}.";

    private static string Describe(JsonElement failureContext, string key)
    {
        if (!failureContext.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
            return "is unavailable";
        if (value.ValueKind != JsonValueKind.Object) return $"is {value.ValueKind}";
        var fields = string.Join(", ", value.EnumerateObject().Select(p => p.Name).OrderBy(name => name, StringComparer.Ordinal));
        return fields.Length == 0 ? "has no fields" : $"fields [{fields}]";
    }

    private sealed class RecoveryReferenceUnresolvedException : Exception
    {
        public RecoveryReferenceUnresolvedException(string message) : base(message)
        {
        }
    }
}
