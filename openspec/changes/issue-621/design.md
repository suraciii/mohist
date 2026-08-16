## Context

Issue 621 addresses Slack turns that complete useful Agent work without an accepted Agent reply. The proposal and `runner-reply-guard` spec require a best-effort advisory, not a Server-authored fallback: reply content remains Agent-owned and silence remains valid.

The current Runner has two orchestration paths that must be covered:

- Initial AgentJob turns are executed by `packages/runner/src/runtime/agent-job-executor.ts` and `agent-job-turn.ts`, with parallel Pi and OpenCode runtime branches.
- Slack follow-ups are dispatched by `packages/runner/src/server/followup-handler.ts`, which already resolves the persisted runtime binding, injects the Slack execution context and collaboration skill, and records the existing terminal `session.activity` fact.

`SlackExecutionContext` already contains a validated reply anchor and the collaboration instructions. The Agent reply action is `mo slack message send`; its Server endpoint calls `SlackOutboxStore.EnqueueAgentReplyAsync`. A successful call means the reply was committed or promoted into the Server-owned outbox. Adapter delivery is asynchronous and must not be part of the guard decision. `SlackTerminalDeliveryHandler` and `SlackStatusProjection.FinalizeLivenessAsync` already close out liveness without creating reply text.

The guard therefore belongs at the Runner turn boundary, after the original runtime result is known and before the existing terminal closeout is emitted. It must use the same runtime session and turn identity, and it must not convert runtime output, liveness facts, or a guard error into a Slack reply or a different execution result.

## Goals / Non-Goals

**Goals:**

- Detect an accepted Agent reply only for turns with a valid Slack execution context and reply anchor.
- Cover initial Pi/OpenCode AgentJob turns and Pi/OpenCode Slack follow-up turns.
- Give the same Agent session at most one bounded advisory that asks the Agent to publish a self-contained reply through the existing action or deliberately remain silent.
- Treat an outbox acceptance as publication even while provider delivery is pending, uncertain, or otherwise being handled by the existing delivery lifecycle.
- Preserve the original success, failure, cancellation, deadline, or unknown outcome and the existing liveness/terminal reporting.
- Make duplicate terminal signals and duplicate follow-up delivery notifications harmless.
- Add focused Runner and Server coverage without adding a persistence schema or external dependency.

**Non-Goals:**

- Generating, selecting, or sending a Server-authored fallback message.
- Changing the Slack reply command, destination rules, redaction, segmentation, outbox ownership, or adapter delivery protocol.
- Treating final assistant text, tool output, runtime events, or liveness status as proof of publication.
- Retrying the original turn, retrying the advisory, creating a second AgentSession turn, or changing a runtime binding.
- Applying the guard to non-Slack turns, malformed Slack contexts, workflow Agent turns without a Slack context, or runtime operations that have no usable session.

## Decisions

### 1. Add one shared guard coordinator at the Runner orchestration boundary

Add a small Runner module, for example `packages/runner/src/runtime/reply-guard.ts`, that owns eligibility, publication probing, one-shot state, advisory prompt construction, timeout handling, and error containment. It should receive a discriminated Pi/OpenCode runtime handle and a turn-specific observer rather than introducing a generic runtime interface across the two deep modules.

`AgentJobExecutor`/`agent-job-turn` will invoke it after the initial runtime result and event sink work have been captured. `followup-handler.ts` will invoke the same coordinator after the original follow-up result and runtime observer facts are captured, but before `recordFollowupActivity` publishes the existing terminal activity. This keeps the behavior identical across both entry points while leaving runtime-specific request construction in the existing `command-runtime.ts` helpers.

**Alternative considered:** Put the guard inside `PiRuntime` and `OpenCodeRuntime`. Rejected because the runtimes do not know Slack eligibility, reply anchors, outbox acceptance, or terminal reporting ownership. Putting it in each runtime would duplicate policy and risk diverging behavior.

### 2. Observe publication through a narrow Server-owned outbox probe

Add a Runner-authenticated, read-only publication check on the existing Runner-to-Server boundary, surfaced by a `ServerConnection` method. The Server implementation should delegate the matching predicate to `SlackOutboxStore`, returning only `{ accepted: boolean }` and no reply text or provider details.

The probe will use the validated context fields to identify the current turn's reply row. Matching will be centralized in the Server outbox layer and will support the existing forms:

- the anchor's dispatch reference when it directly identifies the reply;
- the canonical Slack progress reference derived from workspace, conversation, and triggering message, including the in-place progress-to-terminal promotion used by Agent replies; and
- the existing conversation/thread reply reference for the no-progress fallback path.

The query will consider the row accepted regardless of adapter state. It will never wait for Slack delivery confirmation. The Runner will check once at the terminal boundary and recheck immediately before starting the advisory to close the common race where the Agent publishes just after the first check.

**Alternative considered:** Infer publication by inspecting final assistant text, tool output, or runtime events. Rejected explicitly by the spec; these facts do not prove that `mo slack message send` reached the Server outbox. **Alternative considered:** Read the entire public deliveries list from the Runner. Rejected because it is broader than necessary, leaks unrelated delivery data into the Runner, and duplicates outbox matching logic outside `SlackOutboxStore`.

### 3. Issue an internal follow-up on the existing runtime session

The advisory will be a fixed, short prompt assembled with `buildExecutionEnvelope`, the validated `SlackExecutionContext`, and the existing `inlineSlackCollaborationSkill`. Its instruction will say, in substance: publish a self-contained conclusion, evidence summary, and next step with the existing reply action and supplied anchor, or deliberately leave the turn silent. It will not print or ask the Agent to infer a destination, and it will not include a Server-generated reply body.

The advisory uses the current `runtimeSessionId`, work directory, runtime selection, and observer associated with the original turn. It does not emit a second `session.input`, create a new AgentTurn, rotate a binding, or create a second liveness progress row. Any Agent reply produced by the advisory therefore goes through the unchanged `mo slack message send` and outbox path.

**Alternative considered:** Call the reply endpoint directly from the Runner. Rejected because the Agent owns reply content and the Runner must not synthesize or publish on the Agent's behalf. **Alternative considered:** Start a new AgentJob or fresh runtime session. Rejected because it would lose turn context and could duplicate work.

### 4. Make the advisory explicitly bounded and abortable

Use one fixed initial timeout, proposed as `30_000` ms, with an abort signal combined from the original turn signal and the guard timeout. Extend the internal Pi/OpenCode follow-up call surface only as needed to accept that signal; this is not a new wire contract. OpenCode will pass it through its existing turn execution path. Pi will use its existing interruption path for an idle follow-up and settle the guard as interrupted when the signal fires.

The guard claims its `advisoryStarted` state synchronously before awaiting the runtime. A timeout, invocation error, unavailable runtime, interruption, or late result closes the guard and is logged as diagnostic information only. No guard condition can trigger a retry. If a runtime cannot confirm interruption, the original turn result still wins and no replacement turn is started.

**Alternative considered:** Race an unabortable runtime promise against a timer and leave it running. Rejected as the default because a late model turn could publish outside the advisory window and consume runtime capacity after the turn has been closed. Runtime-specific cancellation is required; the one-shot state remains a defensive backstop for any late completion.

### 5. Preserve the original terminal outcome and liveness sequence

The original result is captured before guard processing and returned unchanged. The initial AgentJob still reports the same `WorkItemResult` to `AgentJobGrain`; the follow-up handler still records the same `session.activity` status and original output. Guard output is never projected as the turn result.

For follow-ups, the existing terminal activity is enqueued after guard processing, so the working state may remain visible for at most the guard bound, but the closeout payload and status are unchanged. For initial AgentJobs, the existing report and `SlackTerminalDeliveryHandler` continue to finalize liveness independently. If the advisory publishes a reply, the outbox promotion is already visible when liveness is finalized. If it does not, liveness still closes without a message.

**Alternative considered:** Emit liveness closeout first and then run the advisory. Rejected because the advisory would appear as an untracked second turn and could race the terminal handler. Delaying the unchanged closeout by a bounded interval keeps one coherent turn without coupling reply content to liveness.

### 6. Enforce one evaluation per turn in the orchestration lifecycle

Use a turn key based on the stable work/turn identity: `workId` plus `initialTurnId` or AgentJob identity for initial work, and `sessionId` plus `turnId`/`operationId` for follow-ups. The guard state transitions from `not-evaluated` to `evaluating`, `advisory-started`, and `closed` before asynchronous work begins. Existing follow-up `inFlight` de-duplication remains the outer protection for duplicate SignalR delivery. A durable accepted outbox row suppresses a later evaluation after a reply has already been accepted.

No state is added to the Slack or session persistence schemas. State is scoped to the active Runner operation; a Runner restart cannot cause a duplicate accepted reply because the publication probe is durable, while an unavailable or failed advisory remains best effort and is not retried by reconciliation.

## Risks / Trade-offs

- [The outbox probe can race an Agent reply that is being accepted] -> Probe immediately before advisory invocation, make the advisory one-shot, and treat any accepted row found during or after the advisory as sufficient without starting another advisory.
- [The advisory adds latency to terminal reporting] -> Use a fixed finite bound, skip it when the original signal is cancelled or the runtime/session is unavailable, and preserve the original result regardless of elapsed time.
- [Pi or OpenCode interruption may not be confirmed cleanly] -> Reuse each runtime's existing abort/cleanup semantics, detach late guard completion, log the diagnostic, and never replace the original turn result.
- [A previous outbox row could be mistaken for the current turn] -> Centralize matching in `SlackOutboxStore` and prefer the trigger-specific progress dispatch reference or anchor dispatch reference over a broad conversation-only lookup.
- [The Agent may still choose silence or publish an incomplete reply] -> The guard is an advisory, not a correctness guarantee. It must not invent content, retry indefinitely, or convert a valid silent outcome into failure.
- [Advisory runtime events may add transcript noise] -> Reuse the current turn observer and identity, emit no second input event, and keep the advisory prompt internal to the runtime turn. Add an explicit test for event correlation.

## Migration Plan

1. Add and test the read-only Server publication probe and its `SlackOutboxStore` predicate. Deploying this first is backward-compatible because older Runners do not call it.
2. Add the shared Runner guard, runtime-specific abort support, and initial/follow-up orchestration hooks. Run focused unit and integration coverage for both runtimes.
3. Roll out the Runner change. Existing Slack outbox rows, delivery retries, liveness reactions, and terminal delivery handlers require no data migration.
4. To roll back, stop or deploy the prior Runner build first. The probe endpoint and any additive internal runtime signal support can remain deployed and are inert. No database rollback is required. If a new Runner encounters a missing or failing probe, it must treat guard processing as unavailable and preserve the original turn outcome.

## Open Questions

- Confirm the operational value of the initial `30_000` ms advisory bound against the deployed Pi and OpenCode model latency. Keep it a code-level constant for the first release unless production evidence justifies configuration.
- Confirm the exact DM/thread matching cases for the publication probe, especially when the Slack anchor's normalized thread root equals the triggering message but the existing DM progress row has a null thread timestamp.
- Decide whether guard timeout and guard-unavailable outcomes need a dedicated metric/event name. They should remain logs or internal diagnostics unless an operator-facing surface is required; they must not become user-visible Slack messages.
