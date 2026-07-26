## Why

`/api/agent/status` is a high-frequency polling path, but it currently deserializes every Session in a project before determining which sessions can contribute to active work. As historical sessions accumulate, unchanged active work becomes increasingly expensive to report, contrary to the polling-cost boundary and the amplification visibility added in #470.

## What Changes

- Select only active direct Agent Sessions and Sessions associated with running Workflows at the persistence boundary before deserializing Session state for `/api/agent/status`.
- Preserve the current active-agent response content, ordering, and the existing definition of which direct and Workflow Sessions are active.
- Read each relevant Workflow's status at most once, even when multiple candidate Sessions reference it.
- Keep the existing `candidates`, `processed`, and downstream-call amplification measures aligned with the actual status-query work.
- Prove that adding completed, failed, or cancelled historical Sessions does not change the response or operation counts for the same active work.

## Capabilities

- `agent-status-active-selection`: `/api/agent/status` reports the existing active direct and Workflow-backed Agent Sessions from a persistence-bounded candidate set, with de-duplicated Workflow status reads and truthful amplification accounting that do not grow with irrelevant historical Sessions.

## Impact

- **Server status query** (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowActivityQuerier.cs` and `packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuery.cs`): add a purpose-specific persistence selection path for active-agent status and retain the current response assembly in `AgentStatusHandlers`.
- **Server API** (`/api/projects/{projectRef}/agent/status` and the `/api/agent/status` alias): response schema, ordering, and active-session semantics remain unchanged.
- **Observability**: preserve #470's request-work amplification metrics, including their correspondence to selected candidates, processed Sessions, database work, and downstream Workflow reads.
- **Tests** (`packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/AgentPathAmplificationSpecs.cs` and related status specs): cover large unrelated historical Session populations and repeated Workflow references with operation counters rather than wall-clock assertions.
- **Dependencies and persistence**: no new dependency; add a stored direct-activity projection and status-selection index through an EF migration. No business-state or public-schema change.
