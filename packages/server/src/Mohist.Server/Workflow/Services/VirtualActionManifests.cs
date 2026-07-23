using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Server-owned virtual Workflow Action manifests exposed to profile
/// validation. These Actions are not Runner-executables: the Runner
/// receives only <c>mohist/opencode</c> or <c>mohist/pi</c> after the
/// server resolves them at dispatch time. The catalog entries exist only
/// so profile save and <c>mo workflow validate</c> can reason about
/// author intent at the boundary, without querying the current Agent
/// state or any mutable project data.
/// </summary>
internal static class VirtualActionManifests
{
    public const string MohistAgentUses = "mohist/agent";

    public static readonly ActionCatalogEntry MohistAgent = new(
        Name: MohistAgentUses,
        Inputs: new[]
        {
            new ActionCatalogInput(
                Name: "name",
                Types: ["string"],
                Required: true,
                Description: "Mohist Agent name or id. Resolves by the same name-or-id rules used by the Agent command surface: an 'agent_*' reference is an id lookup only; every other reference is looked up by name first, then by id only when no matching name exists."),
            new ActionCatalogInput(
                Name: "prompt",
                Types: ["string"],
                Required: true,
                Description: "Task prompt; supports workflow template expressions and is rendered by the Runner."),
            new ActionCatalogInput(
                Name: "session",
                Types: ["string"],
                Required: false,
                Description: "Logical Workflow session name within the owning WorkflowRun; falls back to the Work ID when absent."),
            new ActionCatalogInput(
                Name: "timeout",
                Types: ["number"],
                Required: false,
                Description: "Per-turn deadline in milliseconds; defaults to the selected runtime Action's existing one-hour default when absent."),
        },
        Outputs: [],
        Errors: new[]
        {
            new ActionCatalogError(
                Code: "agent_not_found",
                Description: "The referenced Agent does not exist or is archived at dispatch time."),
        },
        Description: "Reference a project Mohist Agent definition; the server composes the Agent instructions with the task prompt at dispatch time. Tasks only — checks are unsupported.");

    public static ActionCatalogEntry EnsureMohistAgent(ActionCatalogEntry entry) =>
        string.Equals(entry.Name, MohistAgentUses, StringComparison.Ordinal)
            ? MohistAgent
            : entry;
}