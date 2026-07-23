## Context

Workflow profiles currently use `mohist/opencode` or `mohist/pi` and repeat a role's instructions, runtime, model, and runtime options in every task. Issue 128 introduced project-scoped Agent definitions, but Workflow has no way to reuse one without taking ownership of an AgentJob or a direct AgentSession.

Issue 131 lands the already-finalized task-only product contract in [`docs/actions/agent.md`](../../../docs/actions/agent.md): it lets a workflow task reference a project Agent definition for its instructions and execution configuration. The product contract, the conceptual model in [`docs/agents.md`](../../../docs/agents.md), and the execution model in [`design/agent-execution.md`](../../../design/agent-execution.md) all define `mohist/agent` as task-only; this plan implements that contract and does not extend it. The requirements are in [`specs/workflow-agent-action/spec.md`](specs/workflow-agent-action/spec.md). The controlling constraints are that WorkflowRun remains the authority for task state and recovery, templates are rendered only by Runner at execution time, the published input contracts of `mohist/opencode` and `mohist/pi` must not change, and Workflow must not depend on Agent domain entities. Agent definitions are project-scoped, can be resolved by name or id, and archived definitions are not executable.

The existing `WorkflowItemTranslator` already constructs immutable attempt dispatch context while preserving raw `with` data for Runner rendering. OpenCode and Pi Actions already share the Workflow-origin AgentSession path and report their facts back to TaskRun. This change must reuse that path rather than introduce a third execution lifecycle, and must not alter the two runtime Actions' published input contracts or their existing handling of unknown `options` keys.

Stakeholders are workflow-profile authors, who want reusable agent roles; Workflow operators, who need retry and recovery semantics to stay unchanged; and Agent owners, whose current active definition should be used by future attempts without changing attempts already in flight.

## Goals / Non-Goals

**Goals:**

- Accept `uses: mohist/agent` with static `name`, template-renderable `prompt`, optional `session`, and optional `timeout` as its author-facing input contract for tasks.
- Resolve the referenced active Agent at task snapshot creation by the same name-or-id rules as the Agent command surface.
- Compose the Agent's instructions ahead of the task's raw prompt into a single dispatch prompt, and freeze runtime, model, and execution configuration into a durable active-work dispatch snapshot while retaining the raw workflow prompt for Runner-side template rendering.
- Translate the resolved definition to the selected existing `mohist/opencode` or `mohist/pi` execution path using their existing published input contract, and retain their timeout, session, error, result, and Workflow-origin session behavior.
- Return `agent_not_found` as a structured task dispatch failure when resolution finds no active Agent.
- Keep static profile validation independent of the current set and lifecycle state of Agent definitions.

**Non-Goals:**

- Supporting `mohist/agent` in workflow checks. Check support is a contract change that requires its own issue; this plan keeps `mohist/agent` task-only as the finalized contract states, and profile validation rejects it for check work.
- Starting an AgentJob, creating a direct AgentSession, or adding a generic Agent launch API.
- Changing WorkflowRun state transitions, recovery matching, result ownership, or Runner fact-reporting rules beyond adding a structured dispatch-rejection report for tasks.
- Allowing a task to override the Agent-selected runtime, model, instructions, or Agent configuration.
- Resolving Agent definitions while saving or validating a profile.
- Changing the published input contracts of `mohist/opencode` and `mohist/pi`, or their existing handling of unknown `options` keys.
- Changing existing inline `mohist/opencode` and `mohist/pi` task behavior.

## Decisions

### 1. `mohist/agent` is a server-resolved virtual Workflow Action for tasks only

The profile-validation catalog will expose a static `mohist/agent` manifest with required `name` and `prompt`, optional `session` and `timeout`, closed input keys, and documented `agent_not_found` failure, valid for tasks and rejected for checks. `prompt` is its only template-renderable input. The validator will reject a `name` containing a template token during profile validation for a task, before dispatch can attempt to resolve literal template text. It validates author intent but is not a Runner executable Action.

Before producing a task `WorkDispatch`, `WorkflowItemTranslator` will recognize a `mohist/agent` task `uses`, resolve the Agent through a narrow Agent read-side port, compose the resolved instructions with the task prompt, and replace the outbound `uses` and `with` with the selected Runtime Action contract. The runner therefore receives only `mohist/opencode` or `mohist/pi` and does not learn Agent ids, names, lifecycle, or storage shapes.

This separates the author-facing Action language from the execution Action language at the existing control-plane to execution-plane boundary. It also prevents an untransformed persisted work item from becoming a Runner-side Agent lookup. It keeps the finalized task-only contract intact; checks remain unsupported and are rejected at profile validation.

Alternative considered: register `mohist/agent` as a Runner Action and have Runner resolve or accept an Agent snapshot. This leaks Agent-domain knowledge and definition lifecycle into the execution plane, duplicates the selected runtime dispatch logic, and makes the Runner responsible for a control-plane resolution failure. Alternative considered: make profile validation query Agent. This would make profile persistence depend on mutable, unrelated project data and violates the required independent validation semantics. Alternative considered: extend the contract to checks. The controlling product and design specs define `mohist/agent` as task-only and no epic #58 workstream consumes a check form; extending the contract is a separate decision and is deliberately deferred.

### 2. Resolve once per attempt and persist the transformed envelope on active Workflow work

The translator will derive `projectId` from the WorkflowRun's issue metadata and ask the Agent read-side port to resolve `name` using the command-surface resolver's canonical rules: an `agent_*` reference is an id lookup only; every other reference is looked up by name first and then by id only when no matching name exists. It will accept only an active result. The resolver returns a small execution snapshot DTO, not an Agent entity: instructions and a cloned `AgentConfig` validated by `AgentConfigSchema`. The mapper resolves `runtime` (`opencode` default or `pi`), `model`, and `variant` from that cloned config.

The translator composes the resolved instructions and the task's raw prompt into a single composed prompt (instructions first, then the raw prompt) and maps each resolved reference to `{ prompt: composedPrompt, session?, timeout?, options: { model?, variant? } }` — the existing published input contract of the two runtime Actions. Composition is performed against the unrendered raw prompt; template expressions in the raw prompt are therefore still rendered by the Runner against the immutable attempt context, exactly as for inline Actions. The composed prompt is a plain concatenation (`instructions + "\n\n" + rawPrompt`) when instructions are non-empty; when the Agent has no instructions the raw prompt is passed through unchanged. After claim and before the poll response, `DispatchService` asks `IWorkflowGrain` to atomically store that concrete `WorkDispatch` on the owning active work if it has no snapshot, then returns the stored value. The snapshot belongs in durable `WorkflowRun` active-work state, not `RunnerWork`: it includes the complete wire envelope (`uses`, raw `with`, raw `expect`, variables, ownership, and task metadata) and survives server restart and grain activation. `RenderActiveWorkAsync` returns this stored envelope without invoking the translator. A retry has no snapshot, so it resolves again and stores a new one.

The active-work snapshot is cleared only when the work reaches a terminal report/requeue transition; it is never reused by retry. A concurrent poll may race to render a newly claimed work, but the grain returns the first stored snapshot after validating `(workerId, workId)` ownership, so all offers converge. A crash before storage leaves no snapshot and permits a fresh resolution because no envelope was offered; a crash after storage always reoffers that stored envelope.

Alternative considered: store only the Agent id or a partial snapshot in `TaskRun`. An id would force a later resolve and would not freeze configuration; a partial model would duplicate dispatch assembly. The full active-work `WorkDispatch` is the narrowest durable boundary that the poll reconciler can reoffer unchanged.

### 3. Compose instructions at the server and keep the runtime Action contracts unchanged

The selected Runtime Action receives only its already-published input: `prompt` (now the server-composed instructions-plus-task-prompt), optional `session`, optional `timeout`, and `options: { model?, variant? }`. The virtual Action manifest rejects runtime, model, instructions, and arbitrary configuration at task level. The server maps the Agent snapshot into this existing payload; the runner receives no new `options` key, no new validation surface, and no instructions field.

The original task `prompt` is not rendered by the server. The server concatenates the resolved instructions and the raw prompt; the Runner renders template expressions in that composed string with the ordinary immutable attempt context immediately before Action validation and invocation. Because composition happens on the raw prompt and rendering happens at the Runner, the existing render boundary is preserved. The existing parent-issue context composition remains outside this contract and wraps the composed workflow prompt exactly as it does for inline Actions.

Both runtime Actions keep their current behavior verbatim, including OpenCode's current treatment of unknown `options` keys and Pi's current behavior of recording unknown keys as diagnostics. No `options.instructions` field is introduced; no runtime request type is extended for instructions.

Alternative considered: introduce `options.instructions` as a new published key on both runtime Actions and have the Runner compose `instructions + prompt`. That would require changing two already-shipped, stable Action contracts and their existing (deliberately lenient) handling of unknown keys, and would force a decision about whether direct inline use of those Actions can also submit `instructions`. Server-side composition avoids all of that while producing the identical prompt the runtime ultimately receives. Alternative considered: add runtime/model fields to `mohist/agent`. This conflicts with the reusable-definition model and permits task-level configuration to silently contradict the referenced Agent.

### 4. Translate resolution failure into a structured Workflow task report before Runner invocation

Missing, malformed, or archived resolution produces `WorkflowDispatchRejectedException` carrying an `ExecutionError` with code `agent_not_found` and an actionable message naming the requested reference. `IWorkflowGrain.RejectActiveWorkDispatchAsync` accepts this structured error rather than a bare message. It calls the existing failed `TaskResult` path with the `ExecutionError`, so `TaskRun.Error` and recovery `failure.error.code` preserve `agent_not_found`. No Runner claim, AgentSession open, or AgentJob is created.

After successful transformation, runtime availability, input validation after template rendering, timeout, and runtime-specific errors are returned unchanged by `mohist/opencode` or `mohist/pi`. Workflow continues to decide retry, recovery, and stage advancement from those facts.

Alternative considered: fail profile validation when the Agent is absent or archived. That prevents valid profiles from preceding their referenced Agent and makes profile lifecycle depend on a temporary definition state. Alternative considered: use a generic `invalid-input` error. That hides the actionable remediation and prevents targeted recovery matching on the contractually defined error code.

### 5. Preserve Workflow-origin session addressing and existing result ownership

The transformed Runtime Action keeps the action-level `session` input untouched. When it is absent, the existing runtime Action falls back to the Work ID; when supplied, it resolves within `(projectId, workflowRunId, sessionName)`. The dispatch retains `OwnerKind = Workflow` and no `AgentJobId`, so the existing Workflow session resolver and TaskRun report translator continue to be used.

No Agent-side launcher or AgentJob executor is called. Agent definition identity is configuration provenance only and is not added as a second owner, session source, or task lifecycle.

Alternative considered: reuse `IAgentLauncher` because it already snapshots Agent configuration. Its contract mints generic Agent sessions and submits AgentJob work, which would give the wrong owner and state authority. Reusing its small runtime/configuration extraction helpers is acceptable only after extracting them into a neutral read-side mapper with no AgentJob behavior.

### 6. Cover the new boundary with focused server tests

Server definition tests cover the virtual manifest in tasks: required keys, rejected unknown keys, templates accepted for `prompt`, templates rejected for `name`, rejection when used for a check, and validation succeeding when no matching Agent exists. Translator/spec tests use a fake Agent read-side port to verify active-only behavior, task transformation, the instructions-before-prompt composition against the raw (unrendered) prompt, unchanged Workflow dispatch ownership, and the canonical resolver order when a non-prefixed legacy id collides with a name. Poll/reconciliation specs restart or deactivate the server-side grain after an offer and prove redelivery returns the persisted envelope after an Agent edit; retry proves a new snapshot is resolved.

Server tests also assert the transformed envelope matches the existing published input contract of the selected runtime Action — `options` contains at most `model` and `variant`, and no `instructions` key is emitted — so the runner-side Action contracts are provably unchanged. There are no Runner changes in this plan, so no Runner tests are added; existing Runner tests for `mohist/opencode` and `mohist/pi` continue to guard their unchanged behavior.

Workflow specs prove a rejected task persists `TaskRun.Error.code = agent_not_found` and recovery sees that code. Tests use fakes and injected time as required by `design/testing.md`.

Alternative considered: end-to-end tests using an actual runtime or database-backed Agent lookup. Those introduce external dependencies and obscure the control-plane boundary this change is intended to protect.

## Risks / Trade-offs

- [A virtual Action's profile contract can drift from the transformed Runtime Action contracts] -> Keep its schema and transformation mapping in one server-owned module; add contract tests that assert the transformed envelope stays within the selected runtime's published input, and fail unsupported Agent runtime values before dispatch.
- [An Agent edit between resolution and Runner claim could appear ambiguous] -> Persist the translated `WorkDispatch` on active Workflow work before offering it; reoffers return it verbatim and retries create a new dispatch.
- [Agent name/id semantics could diverge from CLI/API behavior] -> Reuse or extract the existing Agent command-surface resolver rather than adding translator-local lookup logic: `agent_*` is id-only; other references are name-first with an id fallback. Cover a name/legacy-id collision.
- [Archived Agents might be accidentally accepted by a broad read query] -> Make the read-side port return only active execution snapshots; map any non-active or absent result to `agent_not_found`.
- [Instructions/configuration could be lost or overridden during runtime mapping] -> Compose instructions into the dispatch prompt at the server and assert via tests that the selected runtime, model/variant, composed prompt, session, and timeout arrive correctly; keep the runtime Action `options` contract unchanged.
- [Server-side prompt composition could be mistaken for template rendering] -> Compose against the raw, unrendered prompt only; document and test that Runner-side template rendering of the composed string still occurs at the execution boundary exactly as for inline Actions.
- [Untransformed `mohist/agent` work items could reach Runner after an interrupted rollout] -> Runner does not register it as executable; server rejects or transforms it before dispatch, and deployment order keeps the server change ahead of profiles that use it.

## Migration Plan

1. Add the server-owned virtual Action manifest and include it in Workflow profile catalog validation for tasks (accepted) and checks (rejected), rejecting template tokens in `name` while retaining Runner-side rendering for `prompt`.
2. Introduce the narrow Agent execution-snapshot resolver and the mapping from a resolved snapshot to the existing OpenCode/Pi Action payload, including server-side instructions-plus-raw-prompt composition.
3. Persist a transformed `WorkDispatch` on active Workflow work and change poll reconciliation to reoffer it; extend `WorkflowItemTranslator` to transform task occurrences before that persistence.
4. Add the structured task dispatch rejection report that preserves `ExecutionError`, then add server regression tests for the transformed path and the unchanged runtime Action contracts.
5. Deploy server support before publishing or enabling workflow profiles using `mohist/agent`; no runner change and no data migration are required because old active work has no snapshot and new snapshots are optional persisted fields.

Rollback consists of stopping new profiles from using `mohist/agent` and deploying the prior server version. Already dispatched attempts require no runner-side change (the transformed envelope uses the existing runtime Action contract); pending work with the virtual Action must be rerun with an inline Action profile after rollback. Existing inline Actions and Agent definitions are untouched.

## Resolved Interfaces

- Extract the name-or-id active-only behavior from `AgentRefResolver` behind a server-side `IAgentExecutionSnapshotResolver`: `agent_*` is id-only; other references are name-first with an id fallback. It returns no Agent entity and is registered only at the Workflow-to-Agent read boundary.
- Persist the optional `WorkDispatch` snapshot on the WorkflowRun active-work model. `IWorkflowGrain` owns compare-and-store/read operations for it; `DispatchService` never writes Workflow storage directly.
- Change `WorkflowDispatchRejectedException`, `IWorkflowGrain.RejectActiveWorkDispatchAsync`, and the grain report implementation to carry `ExecutionError`.
