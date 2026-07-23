## Context

Workflow profiles currently use `mohist/opencode` or `mohist/pi` and repeat a role's instructions, runtime, model, and runtime options in every task or check. Issue 128 introduced project-scoped Agent definitions, but Workflow has no way to reuse one without taking ownership of an AgentJob or a direct AgentSession.

Issue 131 adds `mohist/agent` as a Workflow Action. The product contract is in [`docs/actions/agent.md`](../../../docs/actions/agent.md); the requirements are in [`specs/workflow-agent-action/spec.md`](specs/workflow-agent-action/spec.md). The controlling constraints are that WorkflowRun remains the authority for task state and recovery, templates are rendered only by Runner at execution time, and Workflow must not depend on Agent domain entities. Agent definitions are project-scoped, can be resolved by name or id, and archived definitions are not executable.

The existing `WorkflowItemTranslator` already constructs immutable attempt dispatch context while preserving raw `with` data for Runner rendering. OpenCode and Pi Actions already share the Workflow-origin AgentSession path and report their facts back to TaskRun. This change must reuse that path rather than introduce a third execution lifecycle.

Stakeholders are workflow-profile authors, who want reusable agent roles; Workflow operators, who need retry and recovery semantics to stay unchanged; and Agent owners, whose current active definition should be used by future attempts without changing attempts already in flight.

## Goals / Non-Goals

**Goals:**

- Accept `uses: mohist/agent` with only `name`, `prompt`, optional `session`, and optional `timeout` as its author-facing input contract for tasks and checks.
- Resolve the referenced active Agent at task and check snapshot creation by the same name-or-id rules as the Agent command surface.
- Freeze instructions, runtime, model, and execution configuration into a durable active-work dispatch snapshot while retaining the raw workflow prompt for Runner-side template rendering.
- Translate the resolved definition to the selected existing `mohist/opencode` or `mohist/pi` execution path and retain their timeout, session, error, result, and Workflow-origin session behavior.
- Return `agent_not_found` as a structured task or check dispatch failure when resolution finds no active Agent.
- Keep static profile validation independent of the current set and lifecycle state of Agent definitions.

**Non-Goals:**

- Starting an AgentJob, creating a direct AgentSession, or adding a generic Agent launch API.
- Changing WorkflowRun state transitions, recovery matching, result ownership, or Runner fact-reporting rules beyond adding structured dispatch-rejection reports for tasks and checks.
- Allowing a task to override the Agent-selected runtime, model, instructions, or Agent configuration.
- Resolving Agent definitions while saving or validating a profile.
- Changing existing inline `mohist/opencode` and `mohist/pi` task behavior.

## Decisions

### 1. `mohist/agent` is a server-resolved virtual Workflow Action for tasks and checks

The profile-validation catalog will expose a static `mohist/agent` manifest with required `name` and `prompt`, optional `session` and `timeout`, closed input keys, and documented `agent_not_found` failure. It validates author intent but is not a Runner executable Action.

Before producing a task or checks `WorkDispatch`, `WorkflowItemTranslator` will recognize every `mohist/agent` occurrence, resolve the Agent through a narrow Agent read-side port, and replace the outbound `uses` and `with` with the selected Runtime Action contract. `BuildChecksDispatchAsync` transforms each `checks[*]` entry before serializing the batch. The runner therefore receives only `mohist/opencode` or `mohist/pi` and does not learn Agent ids, names, lifecycle, or storage shapes.

This separates the author-facing Action language from the execution Action language at the existing control-plane to execution-plane boundary. It also prevents an untransformed persisted work item from becoming a Runner-side Agent lookup.

Alternative considered: register `mohist/agent` as a Runner Action and have Runner resolve or accept an Agent snapshot. This leaks Agent-domain knowledge and definition lifecycle into the execution plane, duplicates the selected runtime dispatch logic, and makes the Runner responsible for a control-plane resolution failure. Alternative considered: make profile validation query Agent. This would make profile persistence depend on mutable, unrelated project data and violates the required independent validation semantics.

### 2. Resolve once per attempt and persist the transformed envelope on active Workflow work

The translator will derive `projectId` from the WorkflowRun's issue metadata and ask the Agent read-side port to resolve `name` as id first or name using the command-surface resolver's canonical rules. It will accept only an active result. The resolver returns a small execution snapshot DTO, not an Agent entity: instructions and a cloned `AgentConfig` validated by `AgentConfigSchema`. The mapper resolves `runtime` (`opencode` default or `pi`), `model`, and `variant` from that cloned config.

The translator maps each resolved reference to `{ prompt, session?, timeout?, options: { instructions, model?, variant? } }`, retaining the original `prompt` value verbatim. After claim and before the poll response, `DispatchService` asks `IWorkflowGrain` to atomically store that concrete `WorkDispatch` on the owning active work if it has no snapshot, then returns the stored value. The snapshot belongs in durable `WorkflowRun` active-work state, not `RunnerWork`: it includes the complete wire envelope (`uses`, raw `with`, raw `expect`, variables, ownership, and task/check metadata) and survives server restart and grain activation. `RenderActiveWorkAsync` returns this stored envelope without invoking the translator. A retry or a newly-created checks work has no snapshot, so it resolves again and stores a new one.

The active-work snapshot is cleared only when the work reaches a terminal report/requeue transition; it is never reused by retry. A concurrent poll may race to render a newly claimed work, but the grain returns the first stored snapshot after validating `(workerId, workId)` ownership, so all offers converge. A crash before storage leaves no snapshot and permits a fresh resolution because no envelope was offered; a crash after storage always reoffers that stored envelope.

Alternative considered: store only the Agent id or a partial snapshot in `TaskRun`. An id would force a later resolve and would not freeze configuration; a partial model would duplicate dispatch assembly and still leave check batches without a canonical envelope. The full active-work `WorkDispatch` is the narrowest durable boundary that the poll reconciler can reoffer unchanged.

### 3. Use one closed runtime payload and compose instructions at the Runner Action boundary

The selected Runtime Action receives its usual `prompt`, optional `session`, and optional `timeout`, plus `options: { instructions, model?, variant? }`. The virtual Action manifest rejects runtime, model, instructions, and arbitrary configuration at task/check level. The server maps the Agent snapshot into this one closed Action payload; the runner rejects unknown `options` keys and invalid types for both runtimes before an invocation.

The original `prompt` is not merged, prefixed, or rendered by the server. Runner renders it with the ordinary immutable attempt context immediately before Action validation and invocation. Both `mohist/opencode` and `mohist/pi` validate `options`, compose `instructions + "\n\n" + renderedPrompt` when instructions are non-empty, and submit that result as the turn prompt. Instructions always precede the rendered prompt; the rendered prompt is therefore the current workflow goal. The existing parent-issue context composition remains outside this contract and wraps the composed workflow prompt exactly as it does for inline Actions.

OpenCode extends `AgentTurnRequest.options` and its runtime turn options with `instructions`; Pi extends `PiTurnOptions` likewise. The actions pass the validated `timeout` through as their per-turn deadline: OpenCode continues to use `DEFAULT_TURN_DEADLINE_MS` when absent, while Pi replaces its fixed `PI_TURN_DURATION_MS` request duration with the validated input or the same existing one-hour default. Runtime errors, including timeout, retain their existing codes and messages.

Alternative considered: server-render or concatenate instructions and prompt into one string. That would violate the documented render boundary, lose structured prompt support, and make instruction precedence implicit. Alternative considered: add runtime/model fields to `mohist/agent`. This conflicts with the reusable-definition model and permits task-level configuration to silently contradict the referenced Agent.

### 4. Translate resolution failure into structured Workflow task or check reports before Runner invocation

Missing, malformed, or archived resolution produces `WorkflowDispatchRejectedException` carrying an `ExecutionError` with code `agent_not_found` and an actionable message naming the requested reference. `IWorkflowGrain.RejectActiveWorkDispatchAsync` accepts this structured error rather than a bare message. For task work it calls the existing failed `TaskResult` path with the `ExecutionError`, so `TaskRun.Error` and recovery `failure.error.code` preserve `agent_not_found`.

For checks, the exception identifies the unresolved check. The grain records a `CheckReport`: that named check is failed with the `agent_not_found` error; every other check in the claimed batch is failed with `ExecutionError("check-not-run", "Check was not run because another check could not resolve its Agent.")`. It then follows the existing check report/requeue/recovery path. No Runner claim, AgentSession open, or AgentJob is created for either variant.

After successful transformation, runtime availability, input validation after template rendering, timeout, and runtime-specific errors are returned unchanged by `mohist/opencode` or `mohist/pi`. Workflow continues to decide retry, recovery, checks, and stage advancement from those facts.

Alternative considered: fail profile validation when the Agent is absent or archived. That prevents valid profiles from preceding their referenced Agent and makes profile lifecycle depend on a temporary definition state. Alternative considered: use a generic `invalid-input` error. That hides the actionable remediation and prevents targeted recovery matching on the contractually defined error code.

### 5. Preserve Workflow-origin session addressing and existing result ownership

The transformed Runtime Action keeps the action-level `session` input untouched. When it is absent, the existing runtime Action falls back to the Work ID; when supplied, it resolves within `(projectId, workflowRunId, sessionName)`. The dispatch retains `OwnerKind = Workflow` and no `AgentJobId`, so the existing Workflow session resolver and TaskRun report translator continue to be used.

No Agent-side launcher or AgentJob executor is called. Agent definition identity is configuration provenance only and is not added as a second owner, session source, or task lifecycle.

Alternative considered: reuse `IAgentLauncher` because it already snapshots Agent configuration. Its contract mints generic Agent sessions and submits AgentJob work, which would give the wrong owner and state authority. Reusing its small runtime/configuration extraction helpers is acceptable only after extracting them into a neutral read-side mapper with no AgentJob behavior.

### 5. Cover the new boundary with focused server and runner tests

Server definition tests cover the virtual manifest in tasks and checks: required keys, rejected unknown keys, templates accepted for `prompt`, and validation succeeding when no matching Agent exists. Translator/spec tests use a fake Agent read-side port to verify name/id resolution, active-only behavior, task and check transformation, and unchanged Workflow dispatch ownership. Poll/reconciliation specs restart or deactivate the server-side grain after an offer and prove redelivery returns the persisted envelope after an Agent edit; retry proves a new snapshot is resolved.

Runner Action tests cover the closed option shapes, unknown-key rejection, instruction-before-prompt composition, model/variant forwarding, and supplied/default timeout behavior for OpenCode and Pi through existing fake runtime hosts. They do not add a Runner test that resolves Agents, because Runner has no Agent dependency. Workflow specs prove a rejected task persists `TaskRun.Error.code = agent_not_found` and recovery sees that code; rejected checks preserve the named `agent_not_found` row and the `check-not-run` companion rows. Tests use fakes and injected time as required by `design/testing.md`.

Alternative considered: end-to-end tests using an actual runtime or database-backed Agent lookup. Those introduce external dependencies and obscure the control-plane boundary this change is intended to protect.

## Risks / Trade-offs

- [A virtual Action's profile contract can drift from transformed Runtime Action contracts] -> Keep its schema and transformation mapping in one server-owned module; add contract tests for both selected runtimes and fail unsupported Agent runtime values before dispatch.
- [An Agent edit between resolution and Runner claim could appear ambiguous] -> Persist the translated `WorkDispatch` on active Workflow work before offering it; reoffers return it verbatim and retries create a new dispatch.
- [Agent name/id semantics could diverge from CLI/API behavior] -> Reuse or extract the existing Agent command-surface resolver rather than adding translator-local lookup logic.
- [Archived Agents might be accidentally accepted by a broad read query] -> Make the read-side port return only active execution snapshots; map any non-active or absent result to `agent_not_found`.
- [Instructions/configuration could be lost or overridden during runtime mapping] -> Use a closed `{ instructions, model?, variant? }` Action options contract and runtime-specific tests that assert selected runtime, model/variant, instructions, session, timeout, and rendered prompt arrive at the runtime.
- [Untransformed `mohist/agent` work items could reach Runner after an interrupted rollout] -> Runner does not register it as executable; server rejects or transforms it before dispatch, and deployment order keeps the server change ahead of profiles that use it.

## Migration Plan

1. Add the server-owned virtual Action manifest and include it in Workflow profile catalog validation for tasks and checks.
2. Introduce the narrow Agent execution-snapshot resolver and the mapping from a resolved snapshot to the closed OpenCode/Pi Action payload.
3. Persist a transformed `WorkDispatch` on active Workflow work and change poll reconciliation to reoffer it; extend `WorkflowItemTranslator` to transform task and check occurrences before that persistence.
4. Extend both runtime Actions and their runtime request types for validated instructions and timeout forwarding; retain existing inline Action behavior when instructions are absent.
5. Add structured task/check dispatch rejection reports that preserve `ExecutionError`, then add server and runner regression tests for every transformed runtime path.
6. Deploy server and runner support before publishing or enabling workflow profiles using `mohist/agent`; no data migration is required because old active work has no snapshot and new snapshots are optional persisted fields.

Rollback consists of stopping new profiles from using `mohist/agent` and deploying the prior server and runner versions together. Already dispatched attempts require the runner version that recognizes the closed `options.instructions` payload; pending work with the virtual Action must be rerun with an inline Action profile after rollback. Existing inline Actions and Agent definitions are untouched.

## Resolved Interfaces

- Extract the name-or-id active-only behavior from `AgentRefResolver` behind a server-side `IAgentExecutionSnapshotResolver`; it returns no Agent entity and is registered only at the Workflow-to-Agent read boundary.
- Persist the optional `WorkDispatch` snapshot on the WorkflowRun active-work model. `IWorkflowGrain` owns compare-and-store/read operations for it; `DispatchService` never writes Workflow storage directly.
- Change `WorkflowDispatchRejectedException`, `IWorkflowGrain.RejectActiveWorkDispatchAsync`, and the grain report implementation to carry `ExecutionError`, with a check name when the rejected work is checks.
