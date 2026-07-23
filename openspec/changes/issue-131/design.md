## Context

Workflow profiles currently use `mohist/opencode` or `mohist/pi` and repeat a role's instructions, runtime, model, and runtime options in every task. Issue 128 introduced project-scoped Agent definitions, but Workflow has no way to reuse one without taking ownership of an AgentJob or a direct AgentSession.

Issue 131 adds `mohist/agent` as a Workflow Action. The product contract is in [`docs/actions/agent.md`](../../../docs/actions/agent.md); the requirements are in [`specs/workflow-agent-action/spec.md`](specs/workflow-agent-action/spec.md). The controlling constraints are that WorkflowRun remains the authority for task state and recovery, templates are rendered only by Runner at execution time, and Workflow must not depend on Agent domain entities. Agent definitions are project-scoped, can be resolved by name or id, and archived definitions are not executable.

The existing `WorkflowItemTranslator` already constructs immutable attempt dispatch context while preserving raw `with` data for Runner rendering. OpenCode and Pi Actions already share the Workflow-origin AgentSession path and report their facts back to TaskRun. This change must reuse that path rather than introduce a third execution lifecycle.

Stakeholders are workflow-profile authors, who want reusable agent roles; Workflow operators, who need retry and recovery semantics to stay unchanged; and Agent owners, whose current active definition should be used by future attempts without changing attempts already in flight.

## Goals / Non-Goals

**Goals:**

- Accept `uses: mohist/agent` with only `name`, `prompt`, optional `session`, and optional `timeout` as its author-facing input contract.
- Resolve the referenced active Agent at each task dispatch by the same name-or-id rules as the Agent command surface.
- Freeze instructions, runtime, model, and execution configuration into the dispatched attempt while retaining the raw workflow prompt for Runner-side template rendering.
- Translate the resolved definition to the selected existing `mohist/opencode` or `mohist/pi` execution path and retain their timeout, session, error, result, and Workflow-origin session behavior.
- Return `agent_not_found` as a normal task-dispatch failure when resolution finds no active Agent.
- Keep static profile validation independent of the current set and lifecycle state of Agent definitions.

**Non-Goals:**

- Starting an AgentJob, creating a direct AgentSession, or adding a generic Agent launch API.
- Changing WorkflowRun state transitions, checks, recovery matching, result ownership, or Runner fact-reporting rules.
- Allowing a task to override the Agent-selected runtime, model, instructions, or Agent configuration.
- Resolving Agent definitions while saving or validating a profile.
- Changing existing inline `mohist/opencode` and `mohist/pi` task behavior.

## Decisions

### 1. `mohist/agent` is a server-resolved virtual Workflow Action

The profile-validation catalog will expose a static `mohist/agent` manifest with required `name` and `prompt`, optional `session` and `timeout`, closed input keys, and documented `agent_not_found` failure. It validates author intent but is not a Runner executable Action.

Before producing a task `WorkDispatch`, `WorkflowItemTranslator` will recognize `mohist/agent`, resolve the Agent through a narrow Agent read-side port, and replace the outbound `uses` and `with` with the selected Runtime Action contract. The runner therefore receives only `mohist/opencode` or `mohist/pi` and does not learn Agent ids, names, lifecycle, or storage shapes.

This separates the author-facing Action language from the execution Action language at the existing control-plane to execution-plane boundary. It also prevents an untransformed persisted work item from becoming a Runner-side Agent lookup.

Alternative considered: register `mohist/agent` as a Runner Action and have Runner resolve or accept an Agent snapshot. This leaks Agent-domain knowledge and definition lifecycle into the execution plane, duplicates the selected runtime dispatch logic, and makes the Runner responsible for a control-plane resolution failure. Alternative considered: make profile validation query Agent. This would make profile persistence depend on mutable, unrelated project data and violates the required independent validation semantics.

### 2. Resolve on every dispatch and make the transformed envelope the attempt snapshot

The translator will derive `projectId` from the WorkflowRun's issue metadata and ask the Agent read-side port to resolve `name` as id first or name using the command-surface resolver's canonical rules. It will accept only an active result. The resolver returns a small execution snapshot DTO, not an Agent entity: instructions, runtime, model/variant, and cloned execution configuration.

The translator serializes this DTO into the selected Runtime Action's `options` and retains the original `prompt` value verbatim. The resulting `WorkDispatch` is immutable for the life of that claimed attempt. A redelivery uses that already-created dispatch; a retry creates a new dispatch and therefore performs a fresh resolution. This gives in-flight work a stable definition while allowing definition edits to take effect at the next retry.

Alternative considered: store the Agent id or a snapshot in `WorkflowRun`/`TaskRun`. Storing the id alone would force Runner or later Workflow code to resolve it and would not freeze configuration. Persisting a new snapshot model in Workflow state duplicates dispatch data and expands the Workflow aggregate for a value required only at the server-to-runner boundary. The existing dispatch envelope is the narrowest snapshot boundary.

### 3. Compose the Agent role into runtime options; retain prompt as the workflow goal

The selected Runtime Action receives its usual `prompt`, optional `session`, and optional `timeout`. The translator maps the Agent snapshot into the runtime's established options/configuration shape, including instructions and model selection. Task inputs cannot supply runtime or model fields because the virtual Action manifest rejects them. Runtime-specific Action code remains the single authority for validating and executing those options.

The original `prompt` is not merged, prefixed, or rendered by the server. Runner renders it with the ordinary immutable attempt context immediately before Action validation and invocation. Runtime adapters then combine the resolved long-lived Agent instructions with that rendered prompt according to their existing instruction/input contract. This preserves the task's current workflow goal and the single Runner-side template-rendering boundary.

Alternative considered: server-render or concatenate instructions and prompt into one string. That would violate the documented render boundary, lose structured prompt support, and make instruction precedence implicit. Alternative considered: add runtime/model fields to `mohist/agent`. This conflicts with the reusable-definition model and permits task-level configuration to silently contradict the referenced Agent.

### 4. Translate resolution failure into a Workflow task failure before Runner invocation

Missing, malformed, or archived resolution produces `WorkflowDispatchRejectedException` (or its established dispatch-failure equivalent) carrying code `agent_not_found` and an actionable message naming the requested reference. The normal Workflow-to-Runner report path records the failure against the owning TaskRun, so recovery rules can evaluate it. No Runner claim, AgentSession open, or AgentJob is created for this case.

After successful transformation, runtime availability, input validation after template rendering, timeout, and runtime-specific errors are returned unchanged by `mohist/opencode` or `mohist/pi`. Workflow continues to decide retry, recovery, checks, and stage advancement from those facts.

Alternative considered: fail profile validation when the Agent is absent or archived. That prevents valid profiles from preceding their referenced Agent and makes profile lifecycle depend on a temporary definition state. Alternative considered: use a generic `invalid-input` error. That hides the actionable remediation and prevents targeted recovery matching on the contractually defined error code.

### 5. Preserve Workflow-origin session addressing and existing result ownership

The transformed Runtime Action keeps the action-level `session` input untouched. When it is absent, the existing runtime Action falls back to the Work ID; when supplied, it resolves within `(projectId, workflowRunId, sessionName)`. The dispatch retains `OwnerKind = Workflow` and no `AgentJobId`, so the existing Workflow session resolver and TaskRun report translator continue to be used.

No Agent-side launcher or AgentJob executor is called. Agent definition identity is configuration provenance only and is not added as a second owner, session source, or task lifecycle.

Alternative considered: reuse `IAgentLauncher` because it already snapshots Agent configuration. Its contract mints generic Agent sessions and submits AgentJob work, which would give the wrong owner and state authority. Reusing its small runtime/configuration extraction helpers is acceptable only after extracting them into a neutral read-side mapper with no AgentJob behavior.

### 6. Cover the new boundary with focused server and runner tests

Server definition tests cover the virtual manifest: required keys, rejected unknown keys, templates accepted for `prompt`, and validation succeeding when no matching Agent exists. Translator/spec tests use a fake Agent read-side port to verify name/id resolution, active-only behavior, snapshot transformation, fresh resolution on retry, `agent_not_found`, and unchanged Workflow dispatch ownership.

Runner tests cover only the resulting OpenCode and Pi option shapes and instruction-plus-prompt invocation through existing fake runtime hosts. They do not add a Runner test that resolves Agents, because Runner has no Agent dependency. Tests use fakes and injected time as required by `design/testing.md`.

Alternative considered: end-to-end tests using an actual runtime or database-backed Agent lookup. Those introduce external dependencies and obscure the control-plane boundary this change is intended to protect.

## Risks / Trade-offs

- [A virtual Action's profile contract can drift from transformed Runtime Action contracts] -> Keep its schema and transformation mapping in one server-owned module; add contract tests for both selected runtimes and fail unsupported Agent runtime values before dispatch.
- [An Agent edit between resolution and Runner claim could appear ambiguous] -> Treat the translated `WorkDispatch` as the attempt snapshot; retries create a new dispatch and re-resolve.
- [Agent name/id semantics could diverge from CLI/API behavior] -> Reuse or extract the existing Agent command-surface resolver rather than adding translator-local lookup logic.
- [Archived Agents might be accidentally accepted by a broad read query] -> Make the read-side port return only active execution snapshots; map any non-active or absent result to `agent_not_found`.
- [Instructions/configuration could be lost or overridden during runtime mapping] -> Use runtime-specific mapping tests that assert the selected runtime, model/variant, instructions, session, timeout, and rendered prompt arrive through the existing Action input contract.
- [Untransformed `mohist/agent` work items could reach Runner after an interrupted rollout] -> Runner does not register it as executable; server rejects or transforms it before dispatch, and deployment order keeps the server change ahead of profiles that use it.

## Migration Plan

1. Add the server-owned virtual Action manifest and include it in Workflow profile catalog validation.
2. Introduce the narrow Agent execution-snapshot resolver and the mapping from a resolved snapshot to OpenCode/Pi Action inputs.
3. Extend `WorkflowItemTranslator` to transform `mohist/agent` during task dispatch and surface `agent_not_found` through existing task-dispatch failure handling.
4. Add server definition/translator tests and runner regression tests for both transformed runtime paths.
5. Deploy server support before publishing or enabling workflow profiles using `mohist/agent`; no data migration is required because profiles retain YAML and attempt snapshots are dispatch envelopes.

Rollback consists of stopping new profiles from using `mohist/agent` and deploying the prior server version. Already dispatched attempts continue through their concrete `mohist/opencode` or `mohist/pi` envelopes; pending tasks with the virtual Action must be rerun with an inline Action profile after rollback. Existing inline Actions and Agent definitions are untouched.

## Open Questions

- Does the current Agent command-surface resolver already provide a reusable name-or-id, active-only execution lookup, or should issue 131 extract that behavior from its controller/service into the new read-side port?
- What exact runtime option field carries Agent instructions for each current OpenCode and Pi Action, and can a shared neutral snapshot mapper cover both without normalizing their runtime-specific configuration?
- Does the existing dispatch rejection path preserve a structured error code on `TaskReport`; if not, where should the minimal code-preserving mapping live so recovery can match `agent_not_found`?
