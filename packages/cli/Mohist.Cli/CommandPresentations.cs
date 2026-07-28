using System.CommandLine;

namespace Mohist.Cli;

internal static class CommandPresentations
{
    public static void AttachTo(RootCommand root)
    {
        AttachRoot(root);
        AttachToArea(root, "project", CommandCapability.Work,
            summary: "Manage Projects and their state",
            boundary: "Each Project owns one or more Repositories and one active Workflow Profile; project-scoped commands resolve --project <name-or-id> before any directory-based fallback.",
            seeAlso: "mo workflow for Workflow Profile selection.");
        AttachToArea(root, "repo", CommandCapability.Work,
            summary: "Manage Repositories inside the active Project",
            boundary: "A Repository always belongs to exactly one Project; it does not address cross-project concerns.",
            seeAlso: "mo project for the owning Project; mo run for execution that targets a Repository.");
        AttachToArea(root, "issue", CommandCapability.Work,
            summary: "Track and drive Issue work",
            boundary: "Issues are Project-scoped. Issue lifecycle (start, done, close, reopen) lives here; Run control belongs to `mo run`.");
        AttachToArea(root, "epic", CommandCapability.Work,
            summary: "Group Issues under Epics",
            boundary: "Epics are Project-scoped and aggregate Issues; they never own execution state.");
        AttachToArea(root, "label", CommandCapability.Work,
            summary: "Define label vocabulary",
            boundary: "A label definition is Project-scoped and shared across Issues and Epics; Issue commands reference it.");
        AttachToArea(root, "workflow", CommandCapability.Automation,
            summary: "Manage Project-scoped Workflow Profiles",
            boundary: "Workflow Profiles define how Issue work is staged and approved. They do not own execution state; use `mo run` for that.",
            seeAlso: "mo run --help to control WorkflowRun execution.");
        AttachToArea(root, "run", CommandCapability.Automation,
            summary: "Control and read WorkflowRuns",
            boundary: "Each WorkflowRun belongs to one Issue. Address a Run by its Run ID or by `--issue <number>`; control verbs (approve, retry, rerun, pause, resume, stop) all live here.");
        AttachToArea(root, "agent", CommandCapability.Automation,
            summary: "Manage Agents and launch AgentSessions",
            boundary: "Agents are Project-scoped; the work result of a launch is an AgentJob, the conversation is an AgentSession.",
            seeAlso: "mo session to read a Session by its origin (--issue / --agent); mo activity to trace work across both.");
        AttachToArea(root, "session", CommandCapability.Automation,
            summary: "Locate and read Agent/Workflow Sessions",
            boundary: "Sessions are not owned by any other resource; read them by stable Session ID or by their origin (--issue, --agent).",
            seeAlso: "mo agent launch to start a new session; mo activity to trace it.");
        AttachToArea(root, "activity", CommandCapability.Automation,
            summary: "Inspect Activity across the active Project",
            boundary: "Activity is the read-only timeline that links Issues, Runs, Sessions, and Events; it never owns state.");
        AttachToArea(root, "routing", CommandCapability.Automation,
            summary: "Manage routing rules and event targets",
            boundary: "Routing rules bind an event pattern to an Agent and a response prompt; they always live inside one Project.");
        AttachToArea(root, "runner", CommandCapability.Operations,
            summary: "Inspect and operate Runners",
            boundary: "Runners are the execution plane; commands here read or steer them but do not own issue or run state.");
        AttachToArea(root, "server", CommandCapability.Operations,
            summary: "Inspect and operate the Mohist Server",
            boundary: "Server commands do not require a Project; they affect control-plane services.");
        AttachToArea(root, "service", CommandCapability.Operations,
            summary: "Install and operate Mohist as an OS service",
            boundary: "Service commands interact with systemd / Task Scheduler only; they never read or write Project state.");
        AttachToArea(root, "event", CommandCapability.Operations,
            summary: "Tail event streams and inspect the dead-letter queue",
            boundary: "Events are append-only; delivery state belongs to the dead-letter command, not to event producers.");
        AttachToArea(root, "notification", CommandCapability.Operations,
            summary: "Configure outgoing notification channels",
            boundary: "Notification setup is local to this CLI host; it does not contact the Server.");
        AttachToArea(root, "otel", CommandCapability.Operations,
            summary: "Query local OpenTelemetry traces",
            boundary: "Telemetry is local; commands do not need Server access and never modify remote state.");
        AttachToArea(root, "skill", CommandCapability.Tools,
            summary: "Manage coder agent skills",
            boundary: "Skills are local assets; commands list, view, and install them without Server access.",
            seeAlso: "mo skill view <name> for the decision entry Skill.");
        AttachToArea(root, "install", CommandCapability.Tools,
            summary: "Install the Mohist CLI and runtime");
        AttachToArea(root, "update", CommandCapability.Tools,
            summary: "Update the Mohist CLI and runtime");
        AttachToArea(root, "info", CommandCapability.Tools,
            summary: "Print local environment, project, and runtime information");
        AttachToArea(root, "help", CommandCapability.Tools,
            summary: "Read a shared CLI rule (output, environment, exit-codes)");
    }

    private static void AttachRoot(RootCommand root)
    {
        CommandPresentationCatalog.Attach(root, new CommandPresentation(
            Capability: CommandCapability.Work,
            Summary: "Mohist CLI — the control plane for issues, runs, sessions, agents, and operations"));
    }

    private static void AttachToArea(
        RootCommand root,
        string name,
        CommandCapability capability,
        string summary,
        string? boundary = null,
        string? seeAlso = null)
    {
        var cmd = root.Subcommands.FirstOrDefault(c => c.Name == name);
        if (cmd is null) return;
        CommandPresentationCatalog.Attach(cmd, new CommandPresentation(
            Capability: capability,
            Summary: summary,
            Boundary: boundary,
            SeeAlso: seeAlso));

        var leaf = Leaves.Get(cmd.Name);
        leaf?.Attach(cmd);
    }

    private static Command? Find(Command group, string action) =>
        group.Subcommands.FirstOrDefault(c => c.Name == action);

    private interface ILeafPresenter
    {
        void Attach(Command group);
    }

    private static class Leaves
    {
        public static ILeafPresenter? Get(string area) => area switch
        {
            "issue" => IssueLeaves.Instance,
            "run" => RunLeaves.Instance,
            "workflow" => WorkflowLeaves.Instance,
            "agent" => AgentLeaves.Instance,
            "session" => SessionLeaves.Instance,
            "project" => ProjectLeaves.Instance,
            "repo" => RepositoryLeaves.Instance,
            "epic" => EpicLeaves.Instance,
            "label" => LabelLeaves.Instance,
            "routing" => RoutingLeaves.Instance,
            "skill" => SkillLeaves.Instance,
            "runner" => RunnerLeaves.Instance,
            "server" => ServerLeaves.Instance,
            "service" => ServiceLeaves.Instance,
            "event" => EventLeaves.Instance,
            "notification" => NotificationLeaves.Instance,
            "otel" => OtelLeaves.Instance,
            "activity" => ActivityLeaves.Instance,
            _ => null,
        };

        private static void Attach(Command? cmd, CommandPresentation presentation)
        {
            if (cmd is null) return;
            CommandPresentationCatalog.Attach(cmd, presentation);
        }

        private static Command? Find(Command group, string action) =>
            group.Subcommands.FirstOrDefault(c => c.Name == action);

        private sealed class IssueLeaves : ILeafPresenter
        {
            public static readonly IssueLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "list"), new CommandPresentation(
                    CommandCapability.Work, "List Issues in the active Project",
                    Boundary: "List reads are scoped to one Project; --archived / --all toggle what is included."));
                CommandPresentationCatalog.Attach(Find(group, "create"), new CommandPresentation(
                    CommandCapability.Work, "Create a new Issue in the active Project",
                    Boundary: "New Issues start as drafts unless --ready is supplied; workflow profile is selected via --workflow-profile.",
                    JsonFields: IssueCommands.IssueDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "view"), new CommandPresentation(
                    CommandCapability.Work, "Read a single Issue by its number",
                    Boundary: "Issue view returns the canonical Issue record; resource-result commands list --json fields when called with no value.",
                    JsonFields: ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.Issue)).Fields));
                CommandPresentationCatalog.Attach(Find(group, "edit"), new CommandPresentation(
                    CommandCapability.Work, "Edit an Issue by its number",
                    Boundary: "Edits patch a single Issue; combining --ready and --draft is rejected locally.",
                    JsonFields: IssueCommands.IssueDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "start"), new CommandPresentation(
                    CommandCapability.Work, "Mark an Issue as started",
                    Boundary: "Issue lifecycle lives under `issue`; Run control belongs to `mo run`."));
                CommandPresentationCatalog.Attach(Find(group, "done"), new CommandPresentation(
                    CommandCapability.Work, "Mark an Issue as done"));
                CommandPresentationCatalog.Attach(Find(group, "close"), new CommandPresentation(
                    CommandCapability.Work, "Close an Issue (terminal in the open lifecycle)"));
                CommandPresentationCatalog.Attach(Find(group, "reopen"), new CommandPresentation(
                    CommandCapability.Work, "Reopen a closed Issue"));
                CommandPresentationCatalog.Attach(Find(group, "rebase"), new CommandPresentation(
                    CommandCapability.Work, "Rebase the Issue branch onto a new base"));
                CommandPresentationCatalog.Attach(Find(group, "archive"), new CommandPresentation(
                    CommandCapability.Work, "Archive one Issue or all completed Issues",
                    Boundary: "Archive is recoverable; use `mo issue restore <number>` to bring it back. --all-completed is mutually exclusive with a target number.",
                    JsonFieldGroups:
                    [
                        new("target issue", IssueCommands.IssueDescriptor.Fields),
                        new("--all-completed", IssueCommands.ArchiveCompletedDescriptor.Fields),
                    ]));
                CommandPresentationCatalog.Attach(Find(group, "restore"), new CommandPresentation(
                    CommandCapability.Work, "Restore an archived Issue"));
                CommandPresentationCatalog.Attach(Find(group, "logs"), new CommandPresentation(
                    CommandCapability.Work, "Tail Issue-scoped logs"));
                CommandPresentationCatalog.Attach(Find(group, "events"), new CommandPresentation(
                    CommandCapability.Work, "Tail Issue-scoped events"));
                CommandPresentationCatalog.Attach(Find(group, "diff"), new CommandPresentation(
                    CommandCapability.Work, "Show the Issue branch diff"));
                CommandPresentationCatalog.Attach(Find(group, "commits"), new CommandPresentation(
                    CommandCapability.Work, "List the Issue branch commits"));
                CommandPresentationCatalog.Attach(Find(group, "comment"), new CommandPresentation(
                    CommandCapability.Work, "Read or add Issue comments"));
                var comment = Find(group, "comment");
                if (comment is not null)
                {
                    CommandPresentationCatalog.Attach(Find(comment, "create"), new CommandPresentation(
                        CommandCapability.Work, "Add a comment to an Issue"));
                }
                CommandPresentationCatalog.Attach(Find(group, "template"), new CommandPresentation(
                    CommandCapability.Work, "Inspect Issue templates"));
                CommandPresentationCatalog.Attach(Find(group, "watch"), new CommandPresentation(
                    CommandCapability.Work, "Subscribe to Issue updates",
                    Boundary: "Watches belong to the Issue; --agent / --runner / --session are alternate observers of the same Issue.",
                    SeeAlso: "mo session for Session-scoped observers."));
                CommandPresentationCatalog.Attach(Find(group, "prereq"), new CommandPresentation(
                    CommandCapability.Work, "Manage Issue prerequisites"));
                CommandPresentationCatalog.Attach(Find(group, "workflow"), new CommandPresentation(
                    CommandCapability.Work, "Inspect or override the Workflow Profile bound to an Issue",
                    Boundary: "Profile selection lives here on Issues; the Profile catalog itself lives under `mo workflow`."));
                CommandPresentationCatalog.Attach(Find(group, "variable"), new CommandPresentation(
                    CommandCapability.Work, "Read or edit Issue-scoped Variables"));
            }
        }

        private sealed class RunLeaves : ILeafPresenter
        {
            public static readonly RunLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "approve"), new CommandPresentation(
                    CommandCapability.Automation, "Pass the approval gate for a WorkflowRun",
                    Boundary: "Address the Run by Run ID or `--issue <number>`. Project resolution only happens when --issue is used.",
                    JsonFields: RunCommands.RunControlDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "reject"), new CommandPresentation(
                    CommandCapability.Automation, "Reject a Run at its approval gate",
                    Boundary: "Reject requires a non-empty --message; the Run returns to the prior stage.",
                    JsonFields: RunCommands.RunControlDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "retry"), new CommandPresentation(
                    CommandCapability.Automation, "Retry the current failure point of a Run",
                    Boundary: "Retry restores the manual-retry budget; use `rerun --from-stage` for an arbitrary stage restart.",
                    JsonFields: RunCommands.RunControlDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "rerun"), new CommandPresentation(
                    CommandCapability.Automation, "Rerun the entire Run, or from a specific stage",
                    Boundary: "`rerun --from-stage <name>` invalidates that stage and every later stage; the value cannot be empty.",
                    JsonFields: RunCommands.RunControlDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "pause"), new CommandPresentation(
                    CommandCapability.Automation, "Pause a Run (resumable via `resume`)",
                    Note: "Pause is reversible and does not require --yes.",
                    JsonFields: RunCommands.RunControlDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "resume"), new CommandPresentation(
                    CommandCapability.Automation, "Resume a paused Run",
                    JsonFields: RunCommands.RunControlDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "stop"), new CommandPresentation(
                    CommandCapability.Automation, "Stop a Run permanently (terminal)",
                    Note: "Stop is irreversible; --yes is required in non-interactive mode. Use `pause` if you might want to resume later.",
                    JsonFields: RunCommands.RunControlDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "list"), new CommandPresentation(
                    CommandCapability.Automation, "List WorkflowRuns in the active Project",
                    JsonFields: RunCommands.RunListDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "view"), new CommandPresentation(
                    CommandCapability.Automation, "Read a WorkflowRun by its Run ID",
                    JsonFields: RunCommands.RunViewDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "watch"), new CommandPresentation(
                    CommandCapability.Automation, "Follow a WorkflowRun until it reaches a terminal state"));
                CommandPresentationCatalog.Attach(Find(group, "feedback"), new CommandPresentation(
                    CommandCapability.Automation, "Inspect or submit WorkflowRun feedback"));
                CommandPresentationCatalog.Attach(Find(group, "variable"), new CommandPresentation(
                    CommandCapability.Automation, "Read or edit WorkflowRun-scoped Variables"));
            }
        }

        private sealed class WorkflowLeaves : ILeafPresenter
        {
            public static readonly WorkflowLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "list"), new CommandPresentation(
                    CommandCapability.Automation, "List Workflow Profiles in the active Project"));
                CommandPresentationCatalog.Attach(Find(group, "view"), new CommandPresentation(
                    CommandCapability.Automation, "Read a single Workflow Profile"));
                CommandPresentationCatalog.Attach(Find(group, "create"), new CommandPresentation(
                    CommandCapability.Automation, "Create a Workflow Profile from a YAML definition"));
                CommandPresentationCatalog.Attach(Find(group, "edit"), new CommandPresentation(
                    CommandCapability.Automation, "Edit an existing Workflow Profile",
                    Note: "Profile edits can affect future stages of active Runs."));
                CommandPresentationCatalog.Attach(Find(group, "delete"), new CommandPresentation(
                    CommandCapability.Automation, "Delete a custom Workflow Profile",
                    Boundary: "Built-in Profiles cannot be deleted."));
                CommandPresentationCatalog.Attach(Find(group, "validate"), new CommandPresentation(
                    CommandCapability.Automation, "Validate a local Workflow Definition without contacting a server",
                    Boundary: "Validation is purely local and does not contact the Server."));
            }
        }

        private sealed class AgentLeaves : ILeafPresenter
        {
            public static readonly AgentLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "create"), new CommandPresentation(
                    CommandCapability.Automation, "Create a new Agent profile"));
                CommandPresentationCatalog.Attach(Find(group, "list"), new CommandPresentation(
                    CommandCapability.Automation, "List Agent profiles in the active Project"));
                CommandPresentationCatalog.Attach(Find(group, "view"), new CommandPresentation(
                    CommandCapability.Automation, "Read a single Agent profile"));
                CommandPresentationCatalog.Attach(Find(group, "edit"), new CommandPresentation(
                    CommandCapability.Automation, "Edit an existing Agent profile"));
                CommandPresentationCatalog.Attach(Find(group, "archive"), new CommandPresentation(
                    CommandCapability.Automation, "Archive an Agent profile"));
                CommandPresentationCatalog.Attach(Find(group, "launch"), new CommandPresentation(
                    CommandCapability.Automation, "Launch a generic AgentSession from an Agent profile",
                    Boundary: "Launch returns both the AgentJob id (work owner) and the AgentSession id (conversation owner)."));
                CommandPresentationCatalog.Attach(Find(group, "job"), new CommandPresentation(
                    CommandCapability.Automation, "Read AgentJobs (the work result owner)"));
                var job = Find(group, "job");
                if (job is not null)
                {
                    CommandPresentationCatalog.Attach(Find(job, "list"), new CommandPresentation(
                        CommandCapability.Automation, "List AgentJobs for an Agent profile"));
                    CommandPresentationCatalog.Attach(Find(job, "view"), new CommandPresentation(
                        CommandCapability.Automation, "Read an AgentJob's current status and result"));
                }
                CommandPresentationCatalog.Attach(Find(group, "install"), new CommandPresentation(
                    CommandCapability.Automation, "Install a built-in Agent preset"));
                CommandPresentationCatalog.Attach(Find(group, "model"), new CommandPresentation(
                    CommandCapability.Automation, "List available models for a runtime",
                    Boundary: "Models are surfaced by their runtime; there is no top-level runtime area."));
            }
        }

        private sealed class SessionLeaves : ILeafPresenter
        {
            public static readonly SessionLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "list"), new CommandPresentation(
                    CommandCapability.Automation, "List AgentSessions filtered by source. Exactly one of --agent, --issue, or --run is required.",
                    Boundary: "Sessions do not have a single owner; locate them by their origin and use the stable Session ID for follow-up.",
                    Examples: new[] { "mo session list --issue 42", "mo session list --agent supervisor" }));
                CommandPresentationCatalog.Attach(Find(group, "view"), new CommandPresentation(
                    CommandCapability.Automation, "Read a Session by its stable Session ID"));
                CommandPresentationCatalog.Attach(Find(group, "transcript"), new CommandPresentation(
                    CommandCapability.Automation, "Print the Session transcript"));
                CommandPresentationCatalog.Attach(Find(group, "followup"), new CommandPresentation(
                    CommandCapability.Automation, "Send follow-up text to an AgentSession. It joins an active turn or starts a user-initiated turn when idle without creating a TaskRun or AgentJob."));
                CommandPresentationCatalog.Attach(Find(group, "compact"), new CommandPresentation(
                    CommandCapability.Automation, "Compact the session in place"));
                CommandPresentationCatalog.Attach(Find(group, "reset"), new CommandPresentation(
                    CommandCapability.Automation, "Reset the session in place"));
                CommandPresentationCatalog.Attach(Find(group, "cancel"), new CommandPresentation(
                    CommandCapability.Automation, "Cancel a running Session"));
            }
        }

        private sealed class ProjectLeaves : ILeafPresenter
        {
            public static readonly ProjectLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "list"), new CommandPresentation(
                    CommandCapability.Operations, "List all Projects known to the local CLI"));
                CommandPresentationCatalog.Attach(Find(group, "create"), new CommandPresentation(
                    CommandCapability.Operations, "Create a new Project from a local Git working tree"));
                CommandPresentationCatalog.Attach(Find(group, "view"), new CommandPresentation(
                    CommandCapability.Operations, "Read a Project by name or ID"));
                CommandPresentationCatalog.Attach(Find(group, "use"), new CommandPresentation(
                    CommandCapability.Operations, "Set the active Project for subsequent commands"));
                CommandPresentationCatalog.Attach(Find(group, "delete"), new CommandPresentation(
                    CommandCapability.Operations, "Delete a Project"));
                CommandPresentationCatalog.Attach(Find(group, "workflow"), new CommandPresentation(
                    CommandCapability.Operations, "Set or read the Project default Workflow Profile",
                    Boundary: "Use `mo project workflow set-default <profile>` for the Project default; the Issue-specific selection lives on `mo issue create/edit`.",
                    SeeAlso: "mo workflow for the full Profile catalog."));
                CommandPresentationCatalog.Attach(Find(group, "variable"), new CommandPresentation(
                    CommandCapability.Operations, "Read or edit Project-scoped Variables"));
            }
        }

        private sealed class RepositoryLeaves : ILeafPresenter
        {
            public static readonly RepositoryLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "list"), new CommandPresentation(
                    CommandCapability.Operations, "List Repositories inside the active Project"));
                CommandPresentationCatalog.Attach(Find(group, "edit"), new CommandPresentation(
                    CommandCapability.Operations, "Edit a Repository"));
                CommandPresentationCatalog.Attach(Find(group, "set-default"), new CommandPresentation(
                    CommandCapability.Operations, "Set a Repository as the Project default"));
                CommandPresentationCatalog.Attach(Find(group, "delete"), new CommandPresentation(
                    CommandCapability.Operations, "Delete a Repository"));
            }
        }

        private sealed class EpicLeaves : ILeafPresenter
        {
            public static readonly EpicLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "list"), new CommandPresentation(
                    CommandCapability.Work, "List Epics in the active Project"));
                CommandPresentationCatalog.Attach(Find(group, "create"), new CommandPresentation(
                    CommandCapability.Work, "Create a new Epic",
                    JsonFields: EpicCommands.EpicDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "view"), new CommandPresentation(
                    CommandCapability.Work, "Read an Epic by its ID"));
                CommandPresentationCatalog.Attach(Find(group, "edit"), new CommandPresentation(
                    CommandCapability.Work, "Edit an Epic",
                    JsonFields: EpicCommands.EpicDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "add"), new CommandPresentation(
                    CommandCapability.Work, "Add an Issue to an Epic",
                    JsonFields: EpicCommands.EpicDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "remove"), new CommandPresentation(
                    CommandCapability.Work, "Remove an Issue from an Epic",
                    JsonFields: EpicCommands.EpicDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "start"), new CommandPresentation(
                    CommandCapability.Work, "Start autonomous progression on an Epic",
                    JsonFields: EpicCommands.EpicDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "pause"), new CommandPresentation(
                    CommandCapability.Work, "Pause autonomous progression on an Epic",
                    JsonFields: EpicCommands.EpicDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "resume"), new CommandPresentation(
                    CommandCapability.Work, "Resume autonomous progression on an Epic",
                    JsonFields: EpicCommands.EpicDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "done"), new CommandPresentation(
                    CommandCapability.Work, "Mark an Epic as done",
                    JsonFields: EpicCommands.EpicDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "close"), new CommandPresentation(
                    CommandCapability.Work, "Close an Epic",
                    JsonFields: EpicCommands.EpicDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "reopen"), new CommandPresentation(
                    CommandCapability.Work, "Reopen a closed Epic",
                    JsonFields: EpicCommands.EpicDescriptor.Fields));
            }
        }

        private sealed class LabelLeaves : ILeafPresenter
        {
            public static readonly LabelLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "list"), new CommandPresentation(
                    CommandCapability.Work, "List label definitions available in the active Project"));
                CommandPresentationCatalog.Attach(Find(group, "delete"), new CommandPresentation(
                    CommandCapability.Work, "Delete a label definition from the catalog"));
            }
        }

        private sealed class RoutingLeaves : ILeafPresenter
        {
            public static readonly RoutingLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "rule"), new CommandPresentation(
                    CommandCapability.Automation, "Manage the project's ordered routing rules"));
                CommandPresentationCatalog.Attach(Find(group, "test"), new CommandPresentation(
                    CommandCapability.Automation, "Dry-run recent project events through the routing table"));
            }
        }

        private sealed class SkillLeaves : ILeafPresenter
        {
            public static readonly SkillLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "list"), new CommandPresentation(
                    CommandCapability.Tools, "List packaged Mohist Skills"));
                CommandPresentationCatalog.Attach(Find(group, "view"), new CommandPresentation(
                    CommandCapability.Tools, "Read a packaged Mohist Skill",
                    Note: "Use `mo skill view <name>` to read the Skill; it does not change state."));
                CommandPresentationCatalog.Attach(Find(group, "install"), new CommandPresentation(
                    CommandCapability.Tools, "Install packaged Skills into local agent directories"));
                CommandPresentationCatalog.Attach(Find(group, "path"), new CommandPresentation(
                    CommandCapability.Tools, "Print the packaged path of a Mohist Skill"));
                CommandPresentationCatalog.Attach(Find(group, "sync"), new CommandPresentation(
                    CommandCapability.Tools, "Sync working-tree skill-data into the managed cache"));
            }
        }

        private sealed class RunnerLeaves : ILeafPresenter
        {
            public static readonly RunnerLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "list"), new CommandPresentation(
                    CommandCapability.Operations, "List registered Runners"));
                CommandPresentationCatalog.Attach(Find(group, "view"), new CommandPresentation(
                    CommandCapability.Operations, "Read a Runner by id"));
            }
        }

        private sealed class ServerLeaves : ILeafPresenter
        {
            public static readonly ServerLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "status"), new CommandPresentation(
                    CommandCapability.Operations, "Show overall Server status (aggregated across all Projects)."));
                CommandPresentationCatalog.Attach(Find(group, "health"), new CommandPresentation(
                    CommandCapability.Operations, "Check server health"));
                CommandPresentationCatalog.Attach(Find(group, "info"), new CommandPresentation(
                    CommandCapability.Operations, "Show server-side system diagnostics (identity, source, install, update, services, paths). Distinct from `mo info` (CLI-local environment)."));
                CommandPresentationCatalog.Attach(Find(group, "logs"), new CommandPresentation(
                    CommandCapability.Operations, "Show the connected Server's application logs (the Mohist server's own log tail). These are application logs and are not interchangeable with service-manager logs; use `mo service logs server` for service-manager logs (systemd journal or scheduled-task output)."));
            }
        }

        private sealed class ServiceLeaves : ILeafPresenter
        {
            public static readonly ServiceLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "start"), new CommandPresentation(
                    CommandCapability.Operations, "Start the local managed service for the given target"));
                CommandPresentationCatalog.Attach(Find(group, "stop"), new CommandPresentation(
                    CommandCapability.Operations, "Stop the local managed service for the given target"));
                CommandPresentationCatalog.Attach(Find(group, "restart"), new CommandPresentation(
                    CommandCapability.Operations, "Restart the local managed service for the given target"));
                CommandPresentationCatalog.Attach(Find(group, "uninstall"), new CommandPresentation(
                    CommandCapability.Operations, "Remove the Mohist OS service"));
                CommandPresentationCatalog.Attach(Find(group, "status"), new CommandPresentation(
                    CommandCapability.Operations, "Print the Mohist service status"));
                CommandPresentationCatalog.Attach(Find(group, "logs"), new CommandPresentation(
                    CommandCapability.Operations, "Tail Mohist service logs",
                    Note: "These are service-manager logs and are not interchangeable with application logs; use `mo server logs` for the latter."));
            }
        }

        private sealed class EventLeaves : ILeafPresenter
        {
            public static readonly EventLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "tail"), new CommandPresentation(
                    CommandCapability.Operations, "Tail the event bus (read-only stream)",
                    Note: "After subscription establishment, events are written as NDJSON."));
                CommandPresentationCatalog.Attach(Find(group, "dead-letter"), new CommandPresentation(
                    CommandCapability.Operations, "Inspect or redeliver dead-letter events"));
            }
        }

        private sealed class NotificationLeaves : ILeafPresenter
        {
            public static readonly NotificationLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "setup"), new CommandPresentation(
                    CommandCapability.Operations, "Configure outgoing notification channels"));
            }
        }

        private sealed class OtelLeaves : ILeafPresenter
        {
            public static readonly OtelLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "query"), new CommandPresentation(
                    CommandCapability.Operations, "Run a SQL query against otel.db directly (does not require the server)"));
                CommandPresentationCatalog.Attach(Find(group, "status"), new CommandPresentation(
                    CommandCapability.Operations, "Show OTel collector status and database statistics (requires server)"));
            }
        }

        private sealed class ActivityLeaves : ILeafPresenter
        {
            public static readonly ActivityLeaves Instance = new();
            public void Attach(Command group)
            {
                CommandPresentationCatalog.Attach(Find(group, "list"), new CommandPresentation(
                    CommandCapability.Operations, "List Activity entries in the active Project",
                    Boundary: "Activity has bounded recorded and snapshot views across Project and global scope.",
                    Note: "Each entry includes its provenance and scope.",
                    JsonFields: ActivityCommands.ActivityListDescriptor.Fields));
            }
        }
    }
}
