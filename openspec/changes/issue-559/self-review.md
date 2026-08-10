# Self-Review: issue-559

## Artifacts Reviewed

- `proposal.md` - motivation, scope, breaking boundary, and impact
- `design.md` - ownership boundary, handoff protocol, admission, lineage, recovery, projections, migration, and open questions
- `tasks.json` - five implementation tasks (`T-001` through `T-005`) and acceptance criteria
- `specs/workflow-agent-action/spec.md` - ten requirements and their scenarios

The issue was read with `mo issue view 559 --project proj_f6c141d63b6243bfbb481737b2243b87`. Its body is empty, so issue-level coverage can only be checked against the title and the proposal/spec contract. The artifacts were also cross-checked against the current Server and Runner contracts.

## Issue Coverage Check

- The proposal's requested durable AgentJob/AgentSession lineage, immutable snapshot, shared admission, lifecycle projection, and Workflow arbitration are represented in the spec.
- Every spec requirement has at least one task reference. T-001 covers validation, selection, lineage, snapshots, and admission; T-002 covers execution and task side effects; T-003 covers terminal arbitration and recovery; T-004 covers read projections; T-005 covers cutover, documentation, and regression verification.
- The task dependency graph is valid and linear: `T-001 -> T-002 -> T-003 -> T-004 -> T-005`.
- The unchanged-boundary requirement covers Inline Agent Actions, checks, unrelated Actions, direct Agent launches, Slack/Connection behavior, and external Agent API allowlists.

## Codebase Accuracy Check

- The current `WorkflowItemTranslator` still resolves `mohist/agent` to `mohist/opencode` or `mohist/pi` (`packages/server/src/Mohist.Server/Runner/Services/WorkflowItemTranslator.cs:111-137,268-320`), matching the problem statement and the migration target.
- `WorkDispatch` currently supports only the `task`/`checks` work types and `workflow`/`agent-job` owner kinds (`packages/server/src/Mohist.Server/Runner/Grains/IRunnerGrain.cs:142-219`).
- The Runner currently prepares a workspace and routes every non-`agent-job` non-check work item through `ActionRegistry` (`packages/runner/src/runtime/executor.ts:84-140`).
- The current Runner report route sends every non-`agent-job` report to `WorkflowReportService`, which applies the ordinary Workflow task/check report path (`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:184-245`, `packages/server/src/Mohist.Server/Runner/Services/WorkflowReportService.cs:29-83`).
- AgentJob dispatches currently carry an empty `WorkflowRunId` and use the AgentJob owner/work identity (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:1317-1389`).
- The current AgentJob execution branch bypasses the ordinary Workflow completion, artifact, and variable side-effect pipeline (`packages/runner/src/runtime/executor.ts:128-138,220-244`).

## Findings

### 1. HIGH - Unavailable-Agent semantics contradict the handoff sequence

Evidence: `specs/workflow-agent-action/spec.md:20-33` requires `agent_not_found` with no accepted AgentJob, AgentSession, SessionInput, AgentTurn, or Runner work. `tasks.json:12-16` repeats that missing, archived, or readiness-rejected Agents create no accepted Runner work.

However, `design.md:75-109` defines `agent-handoff` as a Runner dispatch, and `design.md:118-138` orders the flow as `handoff request -> resolve active Agent/readiness/workspace`. The current dispatch path claims and stores Workflow Runner work before the Runner can send that handoff command (`packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:275-347`). A missing or readiness-rejected Agent is therefore discovered after a Runner work item has already been accepted/claimed. The design itself acknowledges that the temporary handoff occupies a Runner slot at `design.md:408-417`.

This makes the stated acceptance criterion impossible as written. The plan must either perform the live Agent/readiness preflight before claiming any Runner work, or explicitly redefine the transient `agent-handoff` as permitted non-execution transport work and revise the spec and T-001 acceptance criteria to distinguish it from accepted Agent execution.

### 2. HIGH - The `agent-handoff` transport and report contract is not specified enough to implement safely

Evidence: `design.md:75-109` says the Runner sends an internal Server handoff command and retires the handoff after an accepted or definitive rejection acknowledgement. T-002's first acceptance criterion (`tasks.json:31-37`) repeats this behavior, but neither the task nor the design defines the command DTO, endpoint/grain operation, acknowledgement states, ownership of the retry fence, or the exact Runner report path.

The existing boundary cannot carry this behavior by default: `WorkExecutor` would resolve `mohist/agent` through `ActionRegistry` (`packages/runner/src/runtime/executor.ts:128-165`), `ServerConnection.report` sends all non-AgentJob work to the generic report endpoint (`packages/runner/src/server/connection.ts:106-136`), and that endpoint sends Workflow-owned work to ordinary `WorkflowReportService` (`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:206-244`). A handoff result routed there would be interpreted as a normal TaskRun result instead of an acceptance/rejection for the coordinator.

T-001/T-002 need an explicit cross-language handoff contract covering command identity and fingerprint, accepted/rejected/retry acknowledgements, lost-response replay, Runner work retirement, and the guarantee that handoff reports never enter `ReceiveTaskReportAsync` or `ReceiveCheckReportAsync`. Without it, the most natural implementation of the described dispatch shape violates Workflow ownership and idempotency.

### 3. HIGH - Workflow completion side effects lack an idempotent AgentJob-to-Workflow bridge

Evidence: `design.md:313-345` requires `expect`, `_output`, artifact binding, and `setVars` to be finalized under `(workflowRunId, taskRunId, jobId)` idempotency. T-002 requires the same behavior (`tasks.json:30-37`). The current AgentJob path has none of the required Workflow identity or side-effect contract:

- `AgentJobGrain.BuildDispatchAsync` emits `WorkflowRunId: string.Empty`, uses a stable AgentJob work id, and sets `OwnerKind=agent-job` (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:1317-1389`).
- Runner artifact routing therefore uses `/api/agent-jobs/{agentJobId}/work/{workId}/artifact-uploads` (`packages/runner/src/runtime/artifact-side-effects.ts:101-127`). The current AgentJob upload resolver treats the owner id as the AgentJob key and derives `TaskRunId` from the AgentJob work id, not the originating Workflow task (`packages/server/src/Mohist.Server/Infrastructure/Hosting/AgentJobArtifactUploadService.cs:42-60`).
- The ordinary Workflow artifact route derives its task identity from active Workflow work (`packages/server/src/Mohist.Server/Workflow/Services/Artifacts/WorkflowArtifactUploadService.cs:316-341`), but the design explicitly clears that active Runner assignment before AgentJob execution (`design.md:181-186`).
- The existing `setVars` helper calls the unkeyed Workflow variable patch endpoint (`packages/runner/src/runtime/set-vars-apply.ts:8-30`; `packages/server/src/Mohist.Server/Api/WorkflowRoutes.cs:125-138`), and `WorkflowRunVariablesStore.PatchVariablesAsync` has no Job/Task idempotency fence (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowRunVariablesStore.cs:33-72`).

The broad word "finalizer" in T-002 does not identify the required owner, wire fields, artifact resolver, variable mutation command, or durable no-op key. The plan must add those contracts and specify whether the finalizer is Runner-side or Server-side before implementation; otherwise duplicate delivery can bind artifacts to the wrong owner or repeat Workflow variable effects.

### 4. MEDIUM - `session` and `timeout` semantics are inconsistent or unresolved

The spec requires `session` and `timeout` to retain existing template and runtime semantics (`specs/workflow-agent-action/spec.md:1-18`). The design narrows `session` to a logical label and deliberately prevents physical Session continuity (`design.md:246-259`), which is a behavioral change from the current Workflow Agent Action path. The design also says `timeout` is a separate per-turn deadline (`design.md:235-244`) but leaves its allowed range, default normalization, and delivery margin as an open question (`design.md:486-497`).

The current runtimes are not uniform: OpenCode applies a numeric timeout/default (`packages/runner/src/actions/opencode.ts:27-39`), while the current Pi Action rejects unknown top-level `timeout` input (`packages/runner/src/actions/pi.ts:191-218`). T-002 asks for timeout/default tests but does not settle this contract. The spec must clarify that the new AgentJob path preserves only rendering/validation semantics, or define the exact per-runtime normalization and physical Session behavior.

### 5. LOW - The read-surface privacy boundary is not resolved

Requirement 10 and T-004 prohibit exposing Runner identity, Runtime Binding, raw workspace paths, prompts, transcript parts, or provider payloads (`specs/workflow-agent-action/spec.md:144-153`, `tasks.json:70-89`). Yet the design proposes storing `workspaceName/workspacePath` in Session metadata (`design.md:261-272`), and the existing Workflow Session read DTO already exposes `RunnerId` and `WorkDir` (`packages/server/src/Mohist.Server/Sessions/AgentSessionReadModels.cs:173-194`). The API shape is also left open between an embedded task field and a dedicated route (`design.md:486-491`).

T-004 should specify a sanitized new projection and whether existing Workflow Session routes are unchanged, filtered for AgentJob-backed sessions, or extended with a separate internal-only read model. Otherwise the same invocation can satisfy the privacy criterion through one route and violate it through another.

### 6. LOW - The issue has no explicit acceptance criteria

`mo issue view 559 --project proj_f6c141d63b6243bfbb481737b2243b87` reports an empty body. The proposal and ten spec requirements provide a coherent inferred contract, but there are no issue-level goals or Done When statements to verify independently. The issue should either receive explicit acceptance criteria or declare the proposal/spec as its authoritative acceptance source.

## Verification Notes

- The artifact and task references above were checked against the current files and line ranges.
- The repository-wide verification gate was not green in the preceding validation attempt because `npm run docs:check` stopped at `tsx: not found`; this remains an environment limitation for T-005 rather than evidence that the plan is implementable.

## Verdict

The plan is not ready to build. Findings 1-3 are blocking because they affect the stated acceptance semantics, the cross-owner protocol, and durable Workflow side effects. Findings 4-6 should be resolved or explicitly accepted before implementation begins.

<promise>FAIL</promise>
