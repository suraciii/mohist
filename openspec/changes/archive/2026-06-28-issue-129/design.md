## Context

#126 shipped a standalone `AgentJob` engine and #128 shipped a project-scoped `Agent` entity, but the two are disconnected from any product-level entry point:

- `AgentJobInput` (`packages/server/src/Mohist.Server/Agent/Grains/IAgentJobGrain.cs:40`) carries only a raw `Prompt` — no `Agent` reference, so Agent profiles are never consumed.
- `AgentJobGrain.BuildDispatch` (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:307`) hardcodes `WorkflowRunId: string.Empty` and never sets `ProjectId` on the dispatch envelope. As a result the runner sees `projectId = null` and `workflowRunId = ""`, which causes three runner sites to skip session recording and followup delivery:
  - `sessionTargetFromContext` (`packages/runner/src/actions/acp/session-events.ts:47`) returns `null` when `projectId` is empty → no runtime events recorded.
  - `runAcpWorkflowAgentSession` (`packages/runner/src/actions/acp/session-strategies.ts:79`) falls through to `runEphemeralWorkflowAgentSession` → session is never opened/attached on the server.
  - `handleFollowup` (`packages/runner/src/server/runner-signalr.ts:383`) hard-gates on `workflowRunId` non-empty → generic sessions could not receive followups even if they existed.
- The only product API surface today is the validation-only `POST /api/agent-jobs/validate` (`AgentJobController.cs`), which is explicitly a developer smoke-test: no auth, synchronous polling, no session, no agent reference.
- `AgentSession` is already a peer aggregate keyed by `sessionId` (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.cs:5`); `workflowRunId` is carried as the optional `mohist.io/source-id` label (`AgentSessionQueryMetadataKeys.cs:9`), so **no schema change is required** to model a workflow-less session.
- `ResolveFollowupTargetAsync` (`AgentSessionQuerier.cs:122`) is issue-anchored and returns `null` when the workflow-run label is blank, so it cannot serve generic sessions.

The in-flight identity already discriminates by `OwnerKind` (`WorkDispatchOwnerKinds.AgentJob` vs `Workflow`, `IRunnerGrain.cs:87`), so agent-job and workflow work items cannot collide on `workId` today — we only need to lift that same discrimination into the session-target axis.

Stakeholders: Server (Agent + Sessions + API + SignalR), Runner (ACP session lifecycle + followup), CLI. Web UI is explicitly out of scope (#132).

## Goals / Non-Goals

**Goals:**
- Product API: launch a generic `AgentSession` from a project-scoped `Agent` profile, send a follow-up, and cancel — observable through existing session read paths.
- `AgentJob` consumes an `Agent` definition (Instructions + AgentConfig + prompt) rather than only a raw prompt.
- Runner records runtime events for generic sessions and supports follow-up delivery, without breaking the workflow-session contract.
- Reuse the existing `AgentSession` aggregate unchanged; only add lookup labels and a new followup-target resolution path.
- CLI entry: `mo agent session launch|followup|cancel`.

**Non-Goals:**
- Web UI (#132), session listing/visibility (#130), workflow stage tasks referencing Agent profiles (#131), approval gate (#103).
- No new `AgentTask`/`AgentThread` user-visible model.
- No issue/epic/project/repository mount or supervisor lifecycle — context refs are metadata only.
- No LLM-provider calls from Mohist; the runner still invokes the installed external agent over ACP.
- No workflow `TaskRun` changes.

## Decisions

### D1. Resolve the Agent at launch time on the server; snapshot into the job input
The server resolves the `Agent` by project-scoped id/name via `AgentGrain`, then composes `{Instructions, AgentConfig, prompt}` and stores the **resolved snapshot** on `AgentJobInput`. The runner consumes the composed values from the dispatch payload — it never calls back to resolve the Agent.

`AgentJobInput` gains two fields:
- `AgentId` (`string?`) — the resolved agent identity, carried through to dispatch for traceability.
- `AgentInstructions` / `AgentConfig` (`string?` / `JsonElement?`) — the snapshot used at execution time. Stored as already-composed values so the runner's `resolvePrompt` path is unchanged in shape (it still reads `with.prompt`, now composed server-side).

**Alternatives considered:**
- *Resolve on the runner.* Rejected: the runner has no `AgentGrain` access and would need a new server RPC; also makes the executed instructions non-deterministic w.r.t. concurrent agent edits.
- *Resolve at job-execution time on the server.* Rejected: the launch API must return the session id synchronously, and snapshotting at launch gives auditable "what ran" semantics.
- *Pass only `AgentId` and have `AgentJobGrain` load the Agent just-in-time.* Partially adopted: the grain still records `AgentId` for traceability, but the **executed** bytes are the launch-time snapshot.

### D2. The server mints the `sessionId` at launch, before dispatch
The launch handler creates the `AgentSession` (via `AgentSessionGrain.OpenAsync`) up front with `source-kind = agent-launch` labels + agent id/name + context-ref annotations, then submits the `AgentJob` carrying the minted `sessionId`. The runner receives the `sessionId` in the dispatch envelope and uses it verbatim in runtime-events calls.

**Alternatives considered:**
- *Runner mints the sessionId (as it does today for workflow sessions via `openWorkflowAgentSession`).* Rejected: the launch HTTP response (`201 { sessionId, ... }`) must return the id synchronously and reliably; round-tripping through the runner adds a race and forces the API to poll.
- *Let the grain mint the session lazily on first event.* Rejected: the session must exist before the runner reports its first event, and lazy creation complicates cancel/followup before the first event arrives.

### D3. Generalize the session target as a discriminated shape; keep workflow path bit-compatible
Introduce one unified `SessionTarget` concept used in payloads and runner caches:

```
SessionTarget =
  | { kind: "workflow"; projectId; workflowRunId; sessionName }
  | { kind: "generic";  projectId; sessionId }
```

Concretely:
- **Runner `AcpSessionManager.key`** (`acp-connection.ts:25`) becomes prefixed: `workflow:{workflowRunId}:{sessionName}` or `generic:{sessionId}`. Different prefixes make cross-shape collision impossible (closes the `":<sessionName>"` ambiguity that agent jobs would hit today).
- **`sessionTargetFromContext`** (`session-events.ts:43`) returns the generic shape when `OwnerKind = agent-job` and a `sessionId` is present on the context; otherwise the workflow shape. The `projectId` gate stays, but the new dispatch envelope (D4) supplies a non-empty `projectId` for agent jobs.
- **Runner→server session methods** (`connection.ts`): add `openAgentSession(projectId, sessionId, body)`, `getAgentSession(projectId, sessionId)`, `attachAgentSession(projectId, sessionId, body)`, and `agentSessionRuntimeEvents(projectId, sessionId, body)` as **new** methods hitting new generic-session URLs. The existing `*WorkflowAgentSession` methods and URLs stay unchanged so workflow sessions are byte-identical.
- **`ReceiveFollowup` payload** generalizes to `{ target: SessionTarget, text }`. The workflow-shaped `{ workflowRunId, sessionName, text }` fields remain populated for workflow sessions (so an older runner still works against a workflow followup), but the resolver uses `target` when present.
- **`handleFollowup` + `resolveFollowupTarget`** drop the `workflowRunId`-non-empty gate and instead branch on `target.kind`. For `generic` they look up `AcpSessionManager` by `generic:{sessionId}`.

**Alternatives considered:**
- *Two separate SignalR methods (`ReceiveFollowup` + `ReceiveGenericFollowup`).* Rejected: doubles the runner surface and the resolver plumbing for no real gain.
- *Key the cache by `sessionId` only.* Rejected: workflow sessions are not universally addressed by sessionId on the runner today (they're addressed by the wrid+name pair the runner derived from context), and forcing sessionId-everywhere is a larger blast radius than a prefixed key.
- *Drop the workflow/generic distinction and route everything through sessionId.* Rejected: violates the "workflow behavior preserved unchanged" requirement and would require backfilling sessionId onto every workflow dispatch.

### D4. Plumb `projectId` and `sessionId` onto the agent-job dispatch envelope
`WorkDispatch` (`IRunnerGrain.cs:68`) gains `ProjectId` (string?) and `AgentSessionId` (string?). `AgentJobGrain.BuildDispatch` populates both from the launch context. `RunnerRoutes` propagates them into `WorkDispatchResponse` so the runner's `RenderedWorkItem` carries authoritative `projectId` + `sessionId` for agent jobs. Workflow dispatch continues to leave them null/empty (its projectId is still sourced from `work.Issue?.ProjectId`).

The `OwnerKind = AgentJob` discriminator is already in place; combined with the minted `sessionId`, an agent-job work item and a workflow work item can never collide on the same in-flight session key even if `workId` matched (different `kind` prefix in `AcpSessionManager`).

**Alternatives considered:**
- *Reuse the existing `WorkflowRunId` slot to carry the sessionId for agent jobs.* Rejected: overloads a field named for a different concept and breaks the "agent-job never carries a workflowRunId" invariant in the spec.
- *Add a new `OwnerKind` per session shape.* Rejected: `OwnerKind` already discriminates sufficiently; the session shape is a separate axis carried by `sessionId` presence.

### D5. Split followup-target resolution into two paths
Keep `ResolveFollowupTargetAsync(projectId, issueNumber, sessionName)` unchanged for the issue-scoped route. Add `ResolveGenericFollowupTargetAsync(projectId, sessionId)` which loads the `AgentSessionGrain` by `sessionId`, reads `Runtime.RunnerId` + status, and returns a `GenericFollowupTarget(runnerId, sessionId, isActive)`. The new generic followup endpoint uses the new resolver; the issue-scoped endpoint and its payload shape stay byte-identical.

**Alternatives considered:**
- *One unified resolver keyed by sessionId only.* Rejected: workflow followup today is reached through `{issueNumber, sessionName}` without a sessionId on the URL; unifying would force the issue route to resolve sessionId first, changing its failure semantics.

### D6. Cancel routes through a new SignalR method with honest state reporting
Add `CancelAgentSession(target: SessionTarget)` as a server→runner invocation. The runner attempts `connection.cancel?(sessionId)` if the ACP connection advertises cancellation, and replies with `{ state: "cancelled" | "not-cancellable" | <terminal-state> }`. The server short-circuits without calling the runner when the session is already terminal (read from `AgentSessionGrain`). The HTTP response mirrors the runner's reported state so the API can never pretend success.

**Alternatives considered:**
- *Reuse `ReceiveFollowup` to carry a cancel intent.* Rejected: cancel needs a return path (runner → server → client) for the honest state, whereas followup is strictly fire-and-forget.
- *Abort the work item via `RunnerRegistry`/`AgentJobGrain` only.* Rejected: that terminates the job but not the in-flight ACP turn, leaving the external agent running.

### D7. CLI: new `mo agent session` subgroup, uniform POST pattern
Add a `session` subgroup under `agent` (`MohistCliCommands.Agent.cs:11`) with `launch`, `followup`, `cancel`. Reuse the existing `PrintPostAsync` / `PrintPostWithOutputAsync` helpers and `ProjectRefOption()`. Prompt/text input supports `--prompt`/`--prompt-file`/`--prompt-stdin` (and the `--text*` equivalents for followup) using a shared "read body from flag/file/stdin" helper.

**Alternatives considered:**
- *Put the commands under `mo session` (top-level).* Rejected: these are agent-scoped (`mo agent session launch <agent>`); a top-level group would imply session management for non-agent sources that don't exist yet.
- *Reuse `mo issue session`.* Rejected: that group is issue-anchored and these sessions have no issue.

## Risks / Trade-offs

- **[Internal runner↔server session contract changes]** -> The `ReceiveFollowup` payload gains a `target` field. Workflow sessions keep populating `workflowRunId`/`sessionName`, so an older runner still handles workflow followups; generic followup simply no-ops on an older runner (dropped silently per the existing unknown-session behavior). Document the contract bump in the change log.
- **[`AcpSessionManager` key collision]** -> Prefixed keys (`workflow:` vs `generic:`) eliminate the `":<sessionName>"` collision agent jobs would otherwise hit. Covered by a unit test asserting both shapes coexist.
- **[Agent definition edited mid-flight]** -> D1 snapshots Instructions/Config at launch time, so the executed bytes are stable. Trade-off: a launch will not pick up edits made after submission — acceptable and auditable.
- **[Launch creates a session before the runner accepts the job]** -> If no runner is online, the `AgentJob` backoff/timeout (`AgentJobOptions`) eventually fails the job; the session is left in a non-terminal state. Mitigation: on job timeout, the grain transitions the session to a terminal `failed` state (reuses existing AgentJob timeout path, extended to close the session).
- **[Cancel is best-effort over ACP]** -> Some external agents do not support cancellation; D6 reports `not-cancellable` honestly rather than faking success. Documented as a known limitation.
- **[Generic followup delivered after session terminated but before status propagates]** -> The server re-checks status from `AgentSessionGrain` right before pushing; a race window remains. Mitigation: the runner silently drops followups for unknown sessions (already the behavior), and the next status read shows the terminal state.
- **[WorkDispatch envelope widening]** -> Adding `ProjectId`/`AgentSessionId` to `WorkDispatch` is additive; older-field consumers ignore them. Workflow path leaves them unset.

## Migration Plan

1. **Server first**: add `AgentId`/snapshot fields to `AgentJobInput`; widen `WorkDispatch` with `ProjectId`/`AgentSessionId`; add the new session-target labels (`source-kind = agent-launch`, agent id/name); add `ResolveGenericFollowupTargetAsync`; add the three new HTTP endpoints; add the `CancelAgentSession` SignalR method. Workflow paths remain unchanged — deploy is safe before the runner changes.
2. **Runner second**: generalize `AcpSessionManager.key` (prefixed), `sessionTargetFromContext`, add the generic `*AgentSession` connection methods, teach `runAcpWorkflowAgentSession` (or a sibling strategy) to take the generic branch when `OwnerKind = agent-job` + `sessionId` present, generalize `resolveFollowupTarget` + `handleFollowup` to branch on `target.kind`, and handle `CancelAgentSession`. Workflow branches must keep their current call sequence.
3. **CLI last**: add `mo agent session launch|followup|cancel` (depends on the new endpoints).
4. **Data**: no migration — generic sessions are new; workflow sessions keep their existing labels.
5. **Rollback**: disable the new HTTP endpoints and SignalR method; workflow sessions and the validation-only agent-jobs route continue to work. Already-created generic sessions become unreadable via the product API but remain in the store; they can be cleaned up by label query if needed.

## Open Questions

- Should the launch API block until the session reaches an observable state, or return `201` immediately after the job is submitted (current design)? Leaning toward immediate return to match the "I'm talking to an agent" UX, with status observable via the existing session read path.
- For the cancel path, do we need a server-side timeout on the runner's reply, or is the existing AgentJob timeout sufficient to reconcile a stuck session? Propose reusing the AgentJob timeout and marking the session `failed` on expiry.
- Should `AgentConfig` from the profile override the launch caller's `--model` flag, or vice-versa? Current design: profile `AgentConfig` is the baseline; an explicit caller override wins. Needs a spec clarification if the conflict matters in practice.
