# Self-Review: issue-559

## Review Context

This is a re-review of the plan after commits `34a8024e7` and `0463dd321`.
Issue 559 was read with:

```text
mo issue view 559 --project proj_f6c141d63b6243bfbb481737b2243b87
```

The issue is titled `可复用的 Agent Workflow Action`, remains in plan/in-progress
state, and has an empty body. Issue-level coverage is therefore checked against
the title and the proposal/spec contract. The current artifacts contain twelve
requirements mapped across T-001 through T-005. This review modifies only this
file.

## Findings

### 1. HIGH - Definitive handoff rejection does not specify how the TaskRun fails

The new transport contract correctly permits a transient `agent-handoff` claim,
but it stops short of specifying the Workflow state transition for a definitive
rejection. The spec still requires the task to fail with `agent_not_found` when
the Agent is unavailable (`specs/workflow-agent-action/spec.md:48-50`) and to
reject readiness failures (`specs/workflow-agent-action/spec.md:100-102`).

The design says that `accepted` and `rejected` are terminal transport
acknowledgements, that the Server retires the handoff obligation, and that the
handoff bypasses `/report`, `ReceiveTaskReportAsync`, and
`ReceiveCheckReportAsync` (`design.md:156-161`). It then says a failed preflight
returns a rejection and retires the already-claimed transport
(`design.md:185-191`), but names no `IWorkflowGrain` operation that fails the
still-running TaskRun or applies its recovery boundary.

The current Workflow grain has a separate
`RejectActiveWorkDispatchAsync(workerId, workId, error)` operation
(`packages/server/src/Mohist.Server/Workflow/Grains/IWorkflowGrain.cs:29-34`),
whose implementation calls `FailTask` and deletes the dispatch snapshot
(`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:103-121`).
The new handoff route is explicitly forbidden from using the ordinary report
path, so the plan must state whether it calls this operation or adds a
lineage-checked `RejectAgentHandoffAsync` equivalent, and must make that
operation idempotent under the command/fingerprint before returning `rejected`.
Without that contract, the Runner can retire transport while the Workflow task
remains `Running`, violating the `agent_not_found` and readiness acceptance
scenarios.

### 2. HIGH - Terminal delivery still has two incompatible contracts

The finalizer fix defines a typed `WorkflowAgentFinalizationRequest` and says
durable delivery invokes `IWorkflowGrain.ApplyAgentJobFinalizationAsync`
(`design.md:414-448`; `specs/workflow-agent-action/spec.md:127-144`). However,
the preceding terminal-delivery decision still stages a
`PendingWorkflowTerminalDelivery` containing only a generic “structured Agent
result” and retries `IWorkflowGrain.ReceiveAgentJobResultAsync`
(`design.md:346-352`). T-003 separately requires delivery to verify
`finalizerKey` before the Server-side finalizer runs
(`tasks.json:50-59`).

The plan does not say whether `PendingWorkflowTerminalDelivery` now carries the
complete finalization request, whether `ReceiveAgentJobResultAsync` is renamed
or becomes a wrapper, or which acknowledgement is persisted for the finalizer
receipt. An implementation following the older section can deliver the Agent
terminal result while dropping `capturedOutputs`, `artifactUploadIds`, and
`setVars`, so the Workflow result can become terminal without applying the
required side effects. One canonical delivery DTO, operation, and
Accepted/Stale/Retry contract is required before build.

### 3. HIGH - Uploaded artifacts are validated but never explicitly bound

The revised design adds an invocation-keyed upload route and a
`WorkflowAgentInvocationArtifactResolver` (`design.md:400-409`), but the
Server-side finalizer only says that it verifies upload ownership and records
the applied upload ids (`design.md:438-448`). Neither the design nor T-002's
acceptance criteria names the operation that converts those pending uploads
into visible Workflow artifacts.

This is a concrete gap in the current artifact contract. The existing upload
route documents that uploads remain hidden until Workflow result reporting
binds them (`packages/server/src/Mohist.Server/Api/WorkflowArtifactUploadRoutes.cs:9-28`).
The existing `IWorkflowArtifactBindService.BindAsync` is the operation that
creates `WorkflowArtifact` rows and removes pending uploads
(`packages/server/src/Mohist.Server/Workflow/Services/Artifacts/WorkflowArtifactBindService.cs:11-22,64-132`).
The current implementation derives that binding from active `(workflowRunId,
workId)` context, but the new design intentionally clears the Workflow Runner
assignment before AgentJob execution (`design.md:234-239`).

The plan must define an invocation-keyed bind command/resolver using
`(workflowRunId, taskRunId, jobId, finalizerKey)`, and include the bind receipt
in the finalizer's idempotency fence. Otherwise an AgentJob can report success
and the finalizer can record upload ids while the artifacts remain pending or
expire, violating the proposal's Workflow task artifact semantics.

## Previous Finding Dispositions

- Finding 1, unavailable-Agent semantics: **fixed**. The plan now distinguishes
  temporary non-execution transport from accepted Agent execution and updates
  the spec and T-001 criteria accordingly (`design.md:88-104`,
  `specs/workflow-agent-action/spec.md:20-35`, `tasks.json:12-16`). The rejection
  transition gap is reported separately above.
- Finding 2, handoff transport/report contract: **mostly fixed but not closed**.
  The endpoint, request fields, command identity, fingerprint, acknowledgement
  states, replay fence, retirement, and ordinary-report bypass are now explicit
  (`design.md:116-168`; `specs/workflow-agent-action/spec.md:20-35`). Finding 1
  above remains in this area because the rejected acknowledgement still lacks
  its required Workflow failure operation.
- Finding 3, AgentJob-to-Workflow finalizer bridge: **not fully fixed**. The
  owner, finalizer key, envelope, artifact resolver, and keyed variable command
  are now named, but Findings 2 and 3 show that the terminal delivery payload
  and artifact binding operation are still incomplete.

## Observations

### 4. MEDIUM - `session` and `timeout` semantics remain open

The spec says optional `session` and `timeout` retain existing semantics
(`specs/workflow-agent-action/spec.md:1-18`), while the design intentionally
uses a new physical Session per attempt and leaves timeout range, default
normalization, and delivery margin as open questions (`design.md:300-312`,
`design.md:595-606`). Existing OpenCode and Pi paths do not currently normalize
these inputs identically. This remains a non-blocking design follow-up for the
current issue review, but T-002 should resolve it before implementation.

### 5. LOW - Read-surface privacy and API shape remain unresolved

The design still lists `workspaceName/workspacePath` in Session metadata while
the proposal/spec prohibit raw workspace paths and existing Session read models
expose Runner/work-directory fields (`design.md:314-325,502-507`). T-004 also
leaves embedded-versus-dedicated read routing open (`tasks.json:70-88`,
`design.md:595-600`). This is recorded as an observation because the task
requires a sanitized projection, but the first implementation should settle the
route and filtering boundary explicitly.

### 6. LOW - Issue-level acceptance criteria are absent

`mo issue view` returned an empty issue body. The proposal and twelve normative
requirements provide an inferred acceptance source, but the issue itself has no
independent Done When criteria. This is non-blocking for the artifact review
because the plan declares its proposal/spec contract, but it remains a product
tracking gap.

## Dimension Checks

- **Coverage:** checked. The proposal goals are represented by twelve spec
  requirements, and every requirement is referenced by at least one task. The
  three findings above are implementation-completeness gaps in otherwise covered
  requirements.
- **Correctness:** failed. Findings 1-3 leave rejection, terminal side effects,
  or artifact visibility without a complete owner/operation contract.
- **Current codebase consistency:** checked. The review traced the current
  dispatch claim path, Runner `awaitingAck` reporting, AgentJob report contract,
  Workflow rejection operation, and pending-artifact bind path. The artifacts'
  migration target matches the current `mohist/agent` translator behavior, but
  the new routes and DTOs are not implemented yet.
- **Task breakdown:** checked with gaps. The dependency graph remains linear
  (`T-001 -> T-002 -> T-003 -> T-004 -> T-005`), and focused test categories are
  listed, but Findings 1-3 need explicit acceptance criteria and test cases for
  rejection application, finalization delivery payload replay, and artifact
  binding replay.

## Verification Notes

- No product tests or build gates were run during this review; this was an
  artifact/code-contract review only.
- Static evidence came from the current issue output, the four plan artifacts,
  commits `34a8024e7` and `0463dd321`, and the cited Server/Runner sources.
- The prior local verification limitation remains `tsx: not found`; it is an
  environment gap, not evidence for or against the findings above.

## Verdict

The plan is not ready to build. The new transport and finalizer contracts
resolve much of the previous review, but Findings 1-3 still violate the
required rejection semantics and Workflow task side-effect guarantees.

<promise>FAIL</promise>
