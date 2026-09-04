## Why

Configuring an Agent today is infrastructure-shaped, not task-shaped: the form leads with runtime and a raw `provider/model` string, while the task language users actually have — purpose, responsibility, permissions, collaborators, concurrency intent — is scattered or missing. The Web `AgentProfileEditor` cannot set description, collaborators (`allowedSubagentAgentIds`), or concurrency (`maxConcurrentRuns`) at all (only the CLI can), and permissions do not exist anywhere in the Agent definition. Launch has the inverse problem: context references (`--issue`, `--epic`, `--repo`, `--workspace`) are documented as "optional context reference: record the issue number on the session metadata", so the user cannot confirm this execution's repository, workspace, context, and permission scope before starting work.

## What Changes

- Reorganize Agent definition authoring around the task. Purpose, description, instructions, permissions, collaborators, and concurrency intent become first-class, task-language fields on the definition, editable in both the Web editor and `mo agent create/edit` (closing today's CLI/Web authoring parity gap).
- Introduce an Agent permission declaration on the definition (vocabulary defined in design) that states what the Agent may operate on; it is echoed and confirmed at launch.
- Split Agent executability into four distinct product states in list and detail — not-configured (setup incomplete), not-executable (configured but blocked, e.g. execution-config failure), unknown, and executable — each with actionable gaps and next actions. Readiness (definition state) and Availability (transient runner/execution condition) remain separate signals that are never merged into one badge.
- Present model selection by task purpose: understandable recommendations during Agent creation/edit, while keeping the full catalog entry (`mo agent model list`, full model picker) available.
- Confirm the launch scope before starting: the resolved repository, workspace, Issue or Epic context, and permission scope are shown to the caller and persisted as explicit facts of that launch — not loose session metadata — before dispatch. The same confirmed-scope projection serves the Web composer and the CLI launch path (see #556).
- State effective-time semantics after saving an Agent: definition edits apply to Jobs launched afterwards; already-running Jobs keep their launch facts unchanged.
- Render the same Agent product identity and execution scope consistently in CLI and Web, from one Server-authoritative projection.

## Capabilities

- `agent-task-profile`: Task-oriented Agent definition authoring — purpose, description, instructions, permissions, collaborators, and concurrency intent as definition fields, their Web and CLI editing surfaces, and the save-time effective-scope statement for new vs running Jobs.
- `agent-executability-status`: The four-state executability diagnosis (not-configured, not-executable, unknown, executable) derived from definition gaps and execution history, kept separate from Availability, and rendered with actionable next steps in Agent list and detail across Web and CLI.
- `agent-model-guidance`: Purpose-aware model recommendation in Agent creation/editing, with the full model catalog remaining reachable.
- `agent-launch-scope`: Pre-launch confirmation and durable recording of one launch's resolved repository, workspace, Issue/Epic context, and permission scope as explicit per-launch execution facts, consumed identically by the Web composer and CLI launch.

## Impact

- **Server (`packages/server`):** `Agent` domain model, `AgentRow`/`AgentStore`/`AgentGrain`/`AgentInfo` (new definition fields incl. permissions); `AgentConfigSchema` (permission vocabulary); `AgentReadinessService`/`AgentReadinessDeriver` (four-state split, config-failure vs structural gap); `AgentAvailabilityService` composition; `AgentDefinitionRoutes` (create/update payloads); `AgentSessionLaunchRoutes` (context refs become persisted launch facts); launch observation projection.
- **Web (`packages/web`):** `pages/agent-list`, `pages/agent-detail`, `pages/agent-session-composer`, `widgets/agent-profile-editor`, `entities/agent` API/model.
- **CLI (`packages/cli`):** `MohistCliCommands.Agent.cs` (create/edit/view/launch), `MohistCliCommands.AgentModel.cs`, agent table renderers.
- **Boundaries:** No new model provider or runtime; no Server-side concurrency claim/release mechanism; no Slack installation flow inside the create form (issue non-goals). The composer still accepts only prompt, context references, and attachments — launch scope confirmation must not become a launch-time configuration override surface. #556 (CLI launch preview) builds on the `agent-launch-scope` contract; #555's external Agent API keeps its own public projection; #558 covers post-execution history, not creation/launch.
