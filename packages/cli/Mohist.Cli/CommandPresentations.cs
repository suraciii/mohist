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
        AttachToArea(root, "webhook", CommandCapability.Automation,
            summary: "Manage outbound webhook subscriptions",
            boundary: "Webhook subscriptions are Project-scoped event filters that deliver CloudEvents to configured downstream URLs.");
        AttachToArea(root, "runner", CommandCapability.Operations,
            summary: "Inspect and operate Runners",
            boundary: "Runners are the execution plane; commands here read or steer them but do not own issue or run state.");
        AttachToArea(root, "server", CommandCapability.Operations,
            summary: "Inspect and operate the Mohist Server",
            boundary: "Server commands do not require a Project; they affect control-plane services.");
        AttachToArea(root, "auth", CommandCapability.Operations,
            summary: "Manage authentication: personal access tokens for scripts, CI and external agents",
            boundary: "PATs belong to the calling account; the full token value is printed only once at issuance, and list never echoes it.");
        AttachToArea(root, "audit", CommandCapability.Operations,
            summary: "Inspect the authentication audit trail",
            boundary: "Audit records cover authentication events and never contain token values.");
        AttachToArea(root, "github", CommandCapability.Operations,
            summary: "Connect GitHub repositories to Projects",
            boundary: "GitHub integrations bind repository intake and review approval to a Project.");
        AttachToArea(root, "slack", CommandCapability.Operations,
            summary: "Manage Slack integrations for Agents and Projects",
            boundary: "Slack commands manage the connection and delivery lifecycle for Project Agents.");
        AttachToArea(root, "workspace", CommandCapability.Work,
            summary: "Manage named workspaces in the active Project",
            boundary: "Workspaces group Project Repositories and provide the execution context for Sessions.");
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
            summary: "Query OpenTelemetry traces through the Server",
            boundary: "Telemetry queries and status are served by the configured Server; the CLI does not read local telemetry storage.");
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

        AttachAdditionalCoverage(root);
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

    private static void AttachAdditionalCoverage(RootCommand root)
    {
        AttachGroup(root, ["auth"], CommandCapability.Operations, "Manage authentication and personal access tokens",
            ("login", "Sign in to the Mohist Server and store the local session"),
            ("status", "Show the current credential source and session state"),
            ("logout", "Revoke the local session and clear it from this machine"),
            ("token", "Manage personal access tokens"));
        AttachGroup(root, ["auth", "token"], CommandCapability.Operations, "Manage personal access tokens",
            ("create", "Issue a personal access token and show its value once"),
            ("list", "List personal access tokens without revealing full values"),
            ("revoke", "Revoke a personal access token immediately"));
        AttachGroup(root, ["audit"], CommandCapability.Operations, "Inspect authentication events in newest-first order",
            ("list", "List authentication audit events"));
        AttachGroup(root, ["github"], CommandCapability.Operations, "Manage GitHub repository connections for a Project",
            ("connect", "Connect a GitHub repository and print its webhook configuration"),
            ("update", "Update a GitHub connection's approver list"));
        AttachGroup(root, ["slack"], CommandCapability.Operations, "Manage Slack connections, Agent installations, and delivery recovery",
            ("setup", "Install or resume the workspace Slack App"),
            ("status", "Show the workspace Slack integration status"),
            ("install-agent", "Install or resume an Agent's Slack installation"),
            ("list", "List Slack Connections"),
            ("view", "View a Slack Connection"),
            ("claim-owner", "Generate a one-time Slack owner claim code"),
            ("edit", "Edit Slack Connection presentation fields and channel access policy"),
            ("transfer-owner", "Generate a one-time Slack owner transfer code"),
            ("enable", "Enable a Slack Connection"),
            ("disable", "Disable a Slack Connection"),
            ("remove-binding", "Remove the Mohist Connection binding while retaining Agent App facts"),
            ("permanent-delete", "Permanently delete the managed Agent App after its Connection binding was removed"),
            ("deliveries", "List outbound Slack deliveries for a Connection"),
            ("resend-delivery", "Re-queue an uncertain Slack delivery"),
            ("clear-gap", "Dismiss a possible-messages-missed notice"),
            ("reconcile-create", "Reconcile an unknown managed Agent App create"),
            ("reconcile-delete", "Reconcile an unknown managed Agent App delete"),
            ("message", "Send or read Slack messages on behalf of an Agent"));
        AttachGroup(root, ["slack", "message"], CommandCapability.Operations, "Send or read Slack messages on behalf of an Agent",
            ("send", "Send a reply to a Slack conversation"));
        AttachGroup(root, ["workspace"], CommandCapability.Work, "Manage named workspaces in the active Project",
            ("list", "List workspaces"),
            ("view", "Read a workspace by name"),
            ("create", "Create a workspace"),
            ("close", "Close a workspace"),
            ("repo", "Manage workspace Repository membership"));
        AttachGroup(root, ["workspace", "repo"], CommandCapability.Work, "Manage workspace Repository membership",
            ("add", "Add a Repository to a Workspace"),
            ("remove", "Remove a Repository from a Workspace"));

        AttachGroup(root, ["server"], CommandCapability.Operations, "Inspect and operate the Mohist Server",
            ("status", "Show overall Server status"),
            ("health", "Check Server health"),
            ("info", "Show Server-side system diagnostics"),
            ("logs", "Show the connected Server's application logs"));
        AttachGroup(root, ["runner"], CommandCapability.Operations, "Inspect and operate registered Runners",
            ("list", "List registered Runners"),
            ("view", "Read a Runner by id"),
            ("status", "Show online runner summary (id, heartbeat, idle/busy state)"),
            ("revoke", "Revoke a Runner's machine credential"));
        AttachGroup(root, ["service"], CommandCapability.Operations, "Install and operate Mohist as an OS service",
            ("start", "Start a local managed service"),
            ("stop", "Stop a local managed service"),
            ("restart", "Restart a local managed service"),
            ("status", "Print the Mohist service status"),
            ("logs", "Tail Mohist service logs"),
            ("uninstall", "Remove the Mohist OS service"));
        AttachGroup(root, ["install"], CommandCapability.Tools, "Install Mohist components from source",
            ("server", "Install the Server as a managed service"),
            ("runner", "Install the Runner as a managed service"),
            ("slack", "Install the mohist-slack adapter as a managed service"));
        AttachGroup(root, ["update"], CommandCapability.Tools, "Update Mohist components from source",
            ("cli", "Update the mo CLI from source"),
            ("server", "Update the Server from source"),
            ("runner", "Update the Runner from source"),
            ("slack", "Update the mohist-slack adapter from source"));
        AttachGroup(root, ["skill"], CommandCapability.Tools, "Manage coder Agent skills",
            ("install", "Install packaged Skills into local Agent directories"),
            ("list", "List packaged Mohist Skills"),
            ("view", "Read a packaged Mohist Skill"),
            ("path", "Print the packaged path of a Mohist Skill"),
            ("sync", "Sync working-tree Skill data into the managed cache"));

        AttachGroup(root, ["run"], CommandCapability.Automation, "Control and read WorkflowRuns",
            ("approve", "Pass the approval gate for a WorkflowRun"),
            ("reject", "Reject a WorkflowRun at its approval gate"),
            ("retry", "Retry the current failure point of a WorkflowRun"),
            ("rerun", "Rerun a WorkflowRun"),
            ("pause", "Pause a WorkflowRun"),
            ("resume", "Resume a paused WorkflowRun"),
            ("stop", "Stop a WorkflowRun permanently"),
            ("list", "List WorkflowRuns in the active Project"),
            ("view", "Read a WorkflowRun by its Run ID"),
            ("watch", "Follow a WorkflowRun until it reaches a terminal state"),
            ("feedback", "Inspect or submit WorkflowRun feedback"),
            ("variable", "Read or edit WorkflowRun-scoped Variables"));
        AttachGroup(root, ["run", "feedback"], CommandCapability.Automation, "Inspect or submit WorkflowRun feedback",
            ("list", "List approval feedback records"),
            ("view", "Read one approval feedback record"));
        AttachGroup(root, ["run", "variable"], CommandCapability.Automation, "Read or edit WorkflowRun-scoped Variables. Run-only --effective exposes the Project → Issue → Run merge.",
            ("list", "List WorkflowRun Variables"),
            ("get", "Read one WorkflowRun Variable"),
            ("set", "Set one WorkflowRun Variable"),
            ("unset", "Delete one WorkflowRun Variable"));
        AttachGroup(root, ["workflow"], CommandCapability.Automation, "Manage Workflow Profiles in the active Project",
            ("list", "List Workflow Profiles"),
            ("view", "Read a Workflow Profile"),
            ("create", "Create a Workflow Profile"),
            ("edit", "Edit a Workflow Profile"),
            ("delete", "Delete a custom Workflow Profile"),
            ("validate", "Validate a local Workflow Definition"));
        AttachGroup(root, ["event"], CommandCapability.Operations, "Tail event streams and recover failed deliveries",
            ("tail", "Tail the event bus"),
            ("dead-letter", "Inspect or recover dead-letter events"));
        AttachGroup(root, ["event", "dead-letter"], CommandCapability.Operations, "Inspect current failed event deliveries",
            ("list", "List current unresolved event deliveries for operator recovery. Redeliver retries the recorded failing handler and may repeat delivery side effects"),
            ("redeliver", "Redeliver a failed event delivery and repeat its recorded handler side effects"));
        AttachGroup(root, ["activity"], CommandCapability.Automation, "Inspect Activity across the active Project",
            ("list", "List Activity entries"));
        AttachGroup(root, ["routing"], CommandCapability.Automation, "Manage routing rules and event targets",
            ("rule", "Manage the ordered routing rules"),
            ("test", "Dry-run recent events through the routing table"));
        AttachGroup(root, ["routing", "rule"], CommandCapability.Automation, "Manage the Project's ordered routing rules",
            ("create", "Create a routing rule"),
            ("list", "List routing rules in table order"),
            ("view", "Read a routing rule"),
            ("edit", "Edit a routing rule"),
            ("archive", "Archive a routing rule"),
            ("move", "Move a routing rule before or after another rule"));
        AttachGroup(root, ["webhook"], CommandCapability.Automation, "Manage outbound webhook subscriptions",
            ("subscription", "Manage Project webhook subscriptions"),
            ("event-types", "List event types available to webhook subscriptions"));
        AttachGroup(root, ["webhook", "subscription"], CommandCapability.Automation, "Manage Project webhook subscriptions",
            ("create", "Create a webhook subscription"),
            ("list", "List webhook subscriptions"),
            ("view", "Read a webhook subscription"),
            ("edit", "Edit a webhook subscription"),
            ("enable", "Enable a webhook subscription"),
            ("disable", "Disable a webhook subscription"),
            ("delete", "Delete a webhook subscription"),
            ("rotate-secret", "Rotate a webhook subscription secret"),
            ("failures", "List webhook delivery failures"));

        AttachGroup(root, ["project"], CommandCapability.Work, "Manage Projects and their state",
            ("list", "List Projects known to the local CLI"),
            ("create", "Create a Project from a local Git working tree"),
            ("view", "Read a Project by name or ID"),
            ("use", "Set the active Project"),
            ("delete", "Delete a Project"),
            ("repo", "Manage the Project's Repositories"),
            ("workflow", "Set or read the Project default Workflow Profile"),
            ("variable", "Read or edit Project-scoped Variables"));
        AttachGroup(root, ["project", "repo"], CommandCapability.Work, "Manage Project Repository settings",
            ("set-default", "Set a Repository as the Project default"));
        AttachGroup(root, ["project", "workflow"], CommandCapability.Work, "Manage Project Workflow references and Prompts",
            ("set-default", "Set the Project default Workflow Profile"),
            ("prompt", "Manage Project Workflow Prompts"));
        AttachGroup(root, ["project", "workflow", "prompt"], CommandCapability.Work, "Manage Project Workflow Prompts",
            ("get", "Read a Project Workflow Prompt"),
            ("set", "Set a Project Workflow Prompt"),
            ("clear", "Clear a Project Workflow Prompt"),
            ("preview", "Preview a Project Workflow Prompt"));
        AttachGroup(root, ["project", "variable"], CommandCapability.Work, "Read or edit Project-scoped Variables",
            ("list", "List Project Variables"),
            ("get", "Read one Project Variable"),
            ("set", "Set one Project Variable"),
            ("unset", "Delete one Project Variable"));
        AttachGroup(root, ["repo"], CommandCapability.Work, "Manage Repositories inside the active Project",
            ("list", "List Repositories"),
            ("create", "Create a Repository"),
            ("edit", "Edit a Repository"),
            ("set-default", "Set a Repository as the Project default"),
            ("delete", "Delete a Repository"));

        AttachGroup(root, ["issue"], CommandCapability.Work, "Track and drive Issue work",
            ("list", "List Issues"),
            ("create", "Create an Issue"),
            ("view", "Read an Issue"),
            ("edit", "Edit an Issue"),
            ("start", "Mark an Issue as started"),
            ("done", "Mark an Issue as done"),
            ("close", "Close an Issue"),
            ("reopen", "Reopen an Issue"),
            ("rebase", "Rebase an Issue branch"),
            ("archive", "Archive an Issue or completed Issues"),
            ("restore", "Restore an archived Issue"),
            ("logs", "Tail Issue-scoped logs"),
            ("events", "Tail Issue-scoped events"),
            ("diff", "Show an Issue branch diff"),
            ("commits", "List Issue branch commits"),
            ("prereq", "Manage Issue start prerequisites"),
            ("comment", "Read or add Issue comments"),
            ("template", "Inspect Issue templates"),
            ("watch", "Subscribe to Issue updates"),
            ("variable", "Read or edit Issue-scoped Variables"));
        AttachGroup(root, ["issue", "prereq"], CommandCapability.Work, "Manage Issue start prerequisites",
            ("add", "Add a start prerequisite to an Issue"),
            ("remove", "Remove a start prerequisite from an Issue"));
        AttachGroup(root, ["issue", "comment"], CommandCapability.Work, "Read or add Issue comments",
            ("create", "Add a comment to an Issue"));
        AttachGroup(root, ["issue", "template"], CommandCapability.Work, "Inspect Issue templates",
            ("list", "List available Issue templates"),
            ("view", "Read an Issue template by name"));
        AttachGroup(root, ["issue", "watch"], CommandCapability.Work, "Subscribe to Issue updates",
            ("add", "Add an Issue watching declaration"),
            ("remove", "Remove an Issue watching declaration"),
            ("list", "List Issue watching and muted Agents"));
        AttachGroup(root, ["issue", "variable"], CommandCapability.Work, "Read or edit Issue-scoped Variables",
            ("list", "List Issue Variables"),
            ("get", "Read one Issue Variable"),
            ("set", "Set one Issue Variable"),
            ("unset", "Delete one Issue Variable"));

        AttachGroup(root, ["agent"], CommandCapability.Automation, "Manage Agents and launch AgentSessions",
            ("create", "Create an Agent profile"),
            ("list", "List Agent profiles"),
            ("view", "Read an Agent profile"),
            ("edit", "Edit an Agent profile"),
            ("archive", "Archive an Agent profile"),
            ("launch", "Launch an AgentSession from an Agent profile"),
            ("spawn", "Spawn an allowed child AgentSession"),
            ("job", "Read AgentJobs"),
            ("install", "Install a built-in Agent preset"),
            ("subscription", "Manage an Agent's event subscriptions"),
            ("model", "List available models for an Agent runtime"));
        AttachGroup(root, ["agent", "job"], CommandCapability.Automation, "Read AgentJobs",
            ("list", "List AgentJobs for an Agent profile"),
            ("view", "Read an AgentJob's status and result"));
        AttachGroup(root, ["agent", "subscription"], CommandCapability.Automation, "Manage an Agent's event subscriptions",
            ("list", "List an Agent's subscriptions"),
            ("create", "Create an Agent subscription"),
            ("edit", "Edit an Agent subscription"),
            ("delete", "Delete an Agent subscription"));
        AttachGroup(root, ["agent", "model"], CommandCapability.Automation, "List available models for an Agent runtime",
            ("list", "List available coder model IDs for the runtime (one per line; use with --model)"));

        AttachGroup(root, ["session"], CommandCapability.Automation, "Locate and read Agent and Workflow Sessions",
            ("list", "List AgentSessions by source"),
            ("tree", "Show the AgentSession tree rooted at a session"),
            ("view", "Read a Session by its stable ID"),
            ("transcript", "Print a Session transcript"),
            ("followup", "Send follow-up text to an AgentSession"),
            ("compact", "Compact a Session in place"),
            ("reset", "Reset a Session in place"),
            ("stop", "Stop a Turn or cascade a Session tree"),
            ("detach", "Detach a child Session"),
            ("schedule", "Manage scheduled inputs for an AgentSession"));
        AttachGroup(root, ["session", "schedule"], CommandCapability.Automation, "Manage scheduled inputs for an AgentSession",
            ("create", "Schedule a follow-up input"),
            ("list", "List scheduled inputs"),
            ("cancel", "Cancel a scheduled input"));
        AttachGroup(root, ["epic"], CommandCapability.Work, "Group Issues under Epics",
            ("list", "List Epics"),
            ("create", "Create an Epic"),
            ("view", "Read an Epic"),
            ("edit", "Edit an Epic"),
            ("add", "Add an Issue to an Epic"),
            ("remove", "Remove an Issue from an Epic"),
            ("start", "Start autonomous progression on an Epic"),
            ("pause", "Pause autonomous progression on an Epic"),
            ("resume", "Resume autonomous progression on an Epic"),
            ("done", "Mark an Epic as done"),
            ("close", "Close an Epic"),
            ("reopen", "Reopen a closed Epic"));
        AttachGroup(root, ["label"], CommandCapability.Work, "Define label vocabulary",
            ("list", "List label definitions"),
            ("create", "Create a label definition"),
            ("edit", "Edit a label definition"),
            ("delete", "Delete a label definition"));
        AttachGroup(root, ["notification"], CommandCapability.Operations, "Configure outgoing notification channels",
            ("setup", "Configure outgoing notification channels"));
        AttachGroup(root, ["otel"], CommandCapability.Operations, "Query OpenTelemetry traces through the Server",
            ("query", "Run a SQL query against OpenTelemetry traces"),
            ("status", "Show OTel collector status and database statistics"),
            ("traces", "List recent traces (most-recent first) through the Server. Use --service to restrict to one service and --limit to request more rows; for arbitrary SQL exploration use 'mo otel query'."));
        AttachGroup(root, ["help"], CommandCapability.Tools, "Read a shared CLI rule",
            ("output", "Read the output and field-selection rule"),
            ("environment", "Read the CLI environment rule"),
            ("exit-codes", "Read the CLI exit-code rule"));
    }

    private static void AttachGroup(
        RootCommand root,
        string[] path,
        CommandCapability capability,
        string summary,
        params (string Name, string Summary)[] children)
    {
        var group = FindPath(root, path);
        if (group is null)
            return;

        AttachIfMissing(group, new CommandPresentation(capability, summary));
        foreach (var child in children)
            AttachIfMissing(Find(group, child.Name), new CommandPresentation(capability, child.Summary));
    }

    private static Command? FindPath(Command root, IReadOnlyList<string> path)
    {
        Command current = root;
        foreach (var name in path)
        {
            current = Find(current, name)!;
            if (current is null)
                return null;
        }
        return current;
    }

    private static void AttachIfMissing(Command? command, CommandPresentation presentation)
    {
        if (command is not null && !CommandPresentationCatalog.Has(command))
            CommandPresentationCatalog.Attach(command, presentation);
    }

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
            "webhook" => WebhookLeaves.Instance,
            "skill" => SkillLeaves.Instance,
            "runner" => RunnerLeaves.Instance,
            "server" => ServerLeaves.Instance,
            "auth" => AuthLeaves.Instance,
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
                    JsonFields: IssueCommands.IssueViewDescriptor.Fields));
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
                    CommandCapability.Automation, "Read or edit WorkflowRun-scoped Variables. Run-only --effective exposes the Project → Issue → Run merge."));
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
                CommandPresentationCatalog.Attach(Find(group, "history"), new CommandPresentation(
                    CommandCapability.Automation, "List canonical Agent turn history for an Agent profile",
                    Boundary: "History is a Server-owned projection of canonical SessionInput and AgentTurn records; it does not arbitrate lifecycle state.",
                    JsonFields: ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentHistoryList)).Fields));
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
                CommandPresentationCatalog.Attach(Find(group, "stop"), new CommandPresentation(
                    CommandCapability.Automation, "Stop a Turn or cascade a Session tree"));
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
                    JsonFields: EpicCommands.EpicMembershipDescriptor.Fields));
                CommandPresentationCatalog.Attach(Find(group, "remove"), new CommandPresentation(
                    CommandCapability.Work, "Remove an Issue from an Epic",
                    JsonFields: EpicCommands.EpicMembershipDescriptor.Fields));
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
        private sealed class WebhookLeaves : ILeafPresenter
        {
            public static readonly WebhookLeaves Instance = new();

            public void Attach(Command group)
            {
                var subscription = Find(group, "subscription");
                CommandPresentationCatalog.Attach(subscription, new CommandPresentation(
                    CommandCapability.Automation, "Manage project outbound webhook subscriptions"));
                if (subscription is null) return;
                var output = ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.WebhookSubscription));
                var listOutput = ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.WebhookSubscriptionList));
                var failuresOutput = ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.WebhookDeliveryFailureList));
                foreach (var action in new[] { "create", "view", "edit", "enable", "disable", "delete", "rotate-secret" })
                    CommandPresentationCatalog.Attach(Find(subscription, action), new CommandPresentation(
                        CommandCapability.Automation, $"{action} a webhook subscription", JsonFields: output.Fields));
                CommandPresentationCatalog.Attach(Find(subscription, "list"), new CommandPresentation(
                    CommandCapability.Automation, "List webhook subscriptions", JsonFields: listOutput.Fields));
                CommandPresentationCatalog.Attach(Find(subscription, "failures"), new CommandPresentation(
                    CommandCapability.Automation, "List webhook delivery failures", JsonFields: failuresOutput.Fields));
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

        private sealed class AuthLeaves : ILeafPresenter
        {
            public static readonly AuthLeaves Instance = new();
            public void Attach(Command group)
            {
                var token = Find(group, "token");
                CommandPresentationCatalog.Attach(token, new CommandPresentation(
                    CommandCapability.Operations, "Manage personal access tokens",
                    Boundary: "The full token value appears exactly once, at issuance; list shows only name and prefix."));
                if (token is null) return;
                CommandPresentationCatalog.Attach(Find(token, "create"), new CommandPresentation(
                    CommandCapability.Operations, "Issue a personal access token (full value shown once)",
                    Note: "Tokens always expire: default 90 days, max 1 year."));
                CommandPresentationCatalog.Attach(Find(token, "list"), new CommandPresentation(
                    CommandCapability.Operations, "List personal access tokens (name and prefix only)"));
                CommandPresentationCatalog.Attach(Find(token, "revoke"), new CommandPresentation(
                    CommandCapability.Operations, "Revoke a personal access token (immediate)"));
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
                    CommandCapability.Operations, "Run a SQL query against OpenTelemetry traces through the Server"));
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
