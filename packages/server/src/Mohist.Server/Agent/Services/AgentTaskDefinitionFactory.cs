using System.Text;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

/// <summary>
/// The complete Agent definition derived by a task-first request. The
/// execution configuration is materialized so later Project-default changes
/// cannot change what this Agent means in lists or on its first launch.
/// </summary>
public sealed record AgentTaskDefinition(
    string Name,
    string Description,
    string Instructions,
    JsonElement AgentConfig);

public sealed class AgentTaskDefinitionExecutionConfigException : InvalidOperationException
{
    public AgentTaskDefinitionExecutionConfigException()
        : base("Execution configuration is unresolved. Supply runtime/model/variant hints or configure the Project default.")
    {
    }
}

/// <summary>
/// Derives the small, deterministic definition required by the task-first
/// route. Name lookup is intentionally kept at this boundary so the pure
/// derivation helpers can be unit-tested without an HTTP or grain fixture.
/// </summary>
public sealed class AgentTaskDefinitionFactory : IScopedService
{
    public const int NameLengthCap = 60;

    private readonly AgentQuerier _agents;
    private readonly ProjectDefaultExecutionConfigReader _defaults;

    public AgentTaskDefinitionFactory(
        AgentQuerier agents,
        ProjectDefaultExecutionConfigReader defaults)
    {
        _agents = agents;
        _defaults = defaults;
    }

    public async Task<AgentTaskDefinition> CreateAsync(
        string projectId,
        string? prompt,
        bool hasAcceptedAttachment,
        string? nameHint,
        ExecutionConfigHint? callerHint,
        string identity,
        CancellationToken ct = default,
        string? occupiedNameToIgnore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        var occupiedNames = nameHint is null
            ? (await _agents.ListAsync(projectId, all: true, ct: ct))
                .Select(agent => agent.Name)
                .Where(name => !string.Equals(name, occupiedNameToIgnore, StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];
        var projectDefault = await _defaults.GetAsync(projectId, ct);
        return Build(
            prompt,
            hasAcceptedAttachment,
            nameHint,
            callerHint,
            projectDefault,
            identity,
            occupiedNames);
    }

    /// <summary>
    /// Pure definition derivation used by the route and unit tests. The
    /// supplied names must include active and archived Agents; reserved
    /// built-in names are added by this method.
    /// </summary>
    public static AgentTaskDefinition Build(
        string? prompt,
        bool hasAcceptedAttachment,
        string? nameHint,
        ExecutionConfigHint? callerHint,
        ExecutionConfigHint? projectDefault,
        string identity,
        IEnumerable<string>? occupiedNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        var normalizedPrompt = prompt?.Trim() ?? string.Empty;
        var resolved = ExecutionConfigResolver.Resolve(callerHint, null, projectDefault);
        ValidateResolvedConfiguration(resolved);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var occupied in occupiedNames ?? [])
        {
            if (!string.IsNullOrWhiteSpace(occupied))
                names.Add(occupied.Trim());
        }
        foreach (var builtIn in BuiltInAgentCatalog.Definitions)
            names.Add(builtIn.Name);

        var suppliedName = string.IsNullOrWhiteSpace(nameHint) ? null : nameHint.Trim();
        var name = suppliedName ?? DeriveConflictFreeName(
            normalizedPrompt,
            hasAcceptedAttachment,
            identity,
            names);
        var taskDescription = string.IsNullOrWhiteSpace(normalizedPrompt)
            ? "Created from attachments"
            : $"Created from task: {FirstLine(normalizedPrompt)}";
        var instructions = BuildInstructions(normalizedPrompt, hasAcceptedAttachment);

        var configValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runtime"] = resolved.Runtime,
            ["model"] = resolved.Model!,
        };
        if (!string.IsNullOrWhiteSpace(resolved.Variant))
            configValues["variant"] = resolved.Variant!;

        var config = JsonSerializer.SerializeToElement(configValues, JSON.Options);
        var schemaError = AgentConfigSchema.Validate(config);
        if (schemaError is not null)
            throw new InvalidOperationException($"Derived agentConfig is invalid: {schemaError}");

        return new AgentTaskDefinition(name, taskDescription, instructions, config);
    }

    public static string DeriveConflictFreeName(
        string? prompt,
        bool hasAcceptedAttachment,
        string identity,
        IEnumerable<string>? occupiedNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        var baseName = DeriveBaseName(prompt, hasAcceptedAttachment, identity);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var occupied in occupiedNames ?? [])
        {
            if (!string.IsNullOrWhiteSpace(occupied))
                names.Add(occupied.Trim());
        }
        foreach (var builtIn in BuiltInAgentCatalog.Definitions)
            names.Add(builtIn.Name);

        if (!names.Contains(baseName))
            return baseName;

        for (var ordinal = 2; ordinal < 10_000; ordinal++)
        {
            var suffix = $" {ordinal}";
            var candidate = TruncateRunes(baseName, NameLengthCap - suffix.Length) + suffix;
            if (!names.Contains(candidate))
                return candidate;
        }

        throw new InvalidOperationException("Unable to derive a conflict-free Agent name.");
    }

    public static string DeriveBaseName(string? prompt, bool hasAcceptedAttachment, string identity)
    {
        var normalizedPrompt = prompt?.Trim() ?? string.Empty;
        var sentence = FirstSentence(normalizedPrompt);
        if (HasLetterOrDigit(sentence))
            return TruncateRunes(CollapseWhitespace(sentence), NameLengthCap);

        if (hasAcceptedAttachment)
            return $"Task {AgentLaunchCoordinatorCodec.StableToken(identity)[..8]}";

        return "Task";
    }

    private static void ValidateResolvedConfiguration(ResolvedExecutionConfig resolved)
    {
        if (!AgentConfigSchema.AllowedRuntimes.Contains(resolved.Runtime)
            || string.IsNullOrWhiteSpace(resolved.Model)
            || !AgentConfigSchema.HasProviderModelForm(resolved.Model))
        {
            throw new AgentTaskDefinitionExecutionConfigException();
        }
    }

    private static string BuildInstructions(string prompt, bool hasAcceptedAttachment)
    {
        var task = string.IsNullOrWhiteSpace(prompt)
            ? "Complete the task described by the attached files."
            : prompt;
        if (!hasAcceptedAttachment && string.IsNullOrWhiteSpace(prompt))
            task = "Complete the task described by the request.";

        return "You are an Agent created to complete the task below. "
            + "Work directly on the task, inspect relevant context, and report the result clearly.\n\n"
            + "Task:\n"
            + task;
    }

    private static string FirstSentence(string prompt)
    {
        if (prompt.Length == 0)
            return string.Empty;

        var end = prompt.IndexOfAny(['.', '!', '?', '\n', '\r']);
        return end < 0 ? prompt : prompt[..end];
    }

    private static string FirstLine(string prompt)
    {
        var line = prompt.Split(['\r', '\n'], 2, StringSplitOptions.None)[0];
        return CollapseWhitespace(line);
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
                builder.Append(' ');
            builder.Append(rune);
            pendingSpace = false;
        }
        return builder.ToString().Trim();
    }

    private static bool HasLetterOrDigit(string value) =>
        value.EnumerateRunes().Any(rune => Rune.IsLetter(rune) || Rune.IsDigit(rune));

    private static string TruncateRunes(string value, int maxRunes)
    {
        if (maxRunes <= 0)
            return string.Empty;

        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count++ >= maxRunes)
                break;
            builder.Append(rune);
        }
        return builder.ToString().TrimEnd();
    }
}
