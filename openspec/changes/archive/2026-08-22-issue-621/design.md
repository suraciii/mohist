## Context

Issue 621 addresses Slack turns that complete useful Agent work without an Agent reply action attempt. The proposal and `runner-reply-guard` spec require a best-effort advisory, not a Server-authored fallback: reply content remains Agent-owned and silence remains valid.

The current Runner has two orchestration paths that must be covered:

- Initial AgentJob turns are executed by `packages/runner/src/runtime/agent-job-executor.ts` and `agent-job-turn.ts`, with parallel Pi and OpenCode runtime branches.
- Slack follow-ups are dispatched by `packages/runner/src/server/followup-handler.ts`, which resolves the runtime binding, injects the Slack execution context and collaboration skill, and records the existing terminal `session.activity` fact.

`SlackExecutionContext` already contains a validated reply anchor and the collaboration instructions. The Agent reply action remains `mo slack message send`. Pi and OpenCode already project tool-call observations to Runner observers. The guard must record a reply-action attempt from that local observation at tool-call start, before the action can return a success or failure. A non-zero reply-action result is still an attempt and must suppress the guard.

The guard therefore belongs at the Runner turn boundary, after the original runtime result and observer facts are terminal, and before follow-up terminal activity is emitted. It must use the same runtime session and turn identity. It must not inspect Server outbox state, infer publication from assistant text, or convert runtime output, liveness facts, or a guard error into a Slack reply or a different execution result.

## Goals / Non-Goals

**Goals:**

- Detect a reply-action attempt only for turns with a valid Slack execution context and reply anchor.
- Cover initial Pi/OpenCode AgentJob turns and Pi/OpenCode Slack follow-up turns.
- Mark the attempt when the existing `mo slack message send` invocation starts, regardless of whether the command is later accepted, rejected, or interrupted.
- Give the same Agent session at most two bounded advisory reminders by default. Each reminder asks the Agent to publish a self-contained reply through the existing action or deliberately remain silent.
- Preserve the original success, failure, cancellation, deadline, or unknown outcome and the existing liveness/terminal reporting.
- Make duplicate terminal signals and duplicate follow-up delivery notifications harmless.
- Use a real terminal completion signal for follow-ups, including Pi idle admission and Pi streaming `steer` admission, rather than treating command admission as turn completion.
- Add focused Runner coverage without adding a Server API, persistence schema, or external dependency.

**Non-Goals:**

- Generating, selecting, or sending a Server-authored fallback message.
- Changing the Slack reply command, destination rules, redaction, segmentation, reply-anchor format, outbox ownership, or adapter delivery protocol.
- Querying the Server, `SlackOutboxStore`, provider delivery state, or any other Server fact to decide whether a reply was attempted.
- Treating final assistant text, tool output, runtime events that are not the reply-action invocation, or liveness status as proof of a reply attempt.
- Retrying the original turn or retrying a failed advisory. The second default reminder is a separate bounded reminder only when the prior advisory completed without an attempt and the budget remains.
- Creating a second user-originated AgentSession input, AgentTurn, liveness progress row, or runtime binding for the advisory.
- Applying the guard to non-Slack turns, malformed Slack contexts, workflow Agent turns without a Slack context, or operations that have no usable session.

## Decisions

### 1. Add one shared Runner guard coordinator at the orchestration boundary

Add a small Runner module, for example `packages/runner/src/runtime/reply-guard.ts`, that owns eligibility, local attempt observations, per-turn state, advisory prompt construction, timeout handling, and error containment. It should receive a discriminated Pi/OpenCode runtime handle and a turn-specific observation tracker rather than introducing a generic runtime interface across the two deep modules.

The state is explicit and process-local to the active operation:

```text
ReplyGuardState {
  replyActionAttempted: boolean
  remindersIssued: number
  phase: not-evaluated | evaluating | advisory-running | closed
}
```

`DEFAULT_REPLY_GUARD_REMINDER_BUDGET` is `2`. The coordinator increments `remindersIssued` before starting an advisory, so a late completion, duplicate terminal signal, or exception cannot open another reminder slot. A reply-action attempt closes guard work immediately. If an advisory completes without an attempt, the coordinator may issue the next reminder until the budget is exhausted. Timeout, failure, interruption, or unavailable runtime closes the guard without retrying that advisory.

`AgentJobExecutor`/`agent-job-turn` invokes the coordinator after the initial runtime result and event sink work have been captured. `followup-handler.ts` invokes it only after the follow-up's actual terminal completion has been observed and before `recordFollowupActivity` publishes the existing terminal activity. This keeps policy shared while leaving runtime-specific request construction in the existing `command-runtime.ts` helpers.

**Alternative considered:** Put the guard inside `PiRuntime` and `OpenCodeRuntime`. Rejected because the runtimes do not own Slack eligibility, reply anchors, or terminal reporting policy. Putting the policy in each runtime would duplicate behavior and risk diverging budgets and failure handling.

### 2. Detect the Runner-local reply-action attempt at tool-call start

Add a narrow Runner-side predicate for the existing reply action invocation, next to the normalized Pi/OpenCode runtime-event observation code. It matches the canonical `mo slack message send` tool/action invocation in the projected `tool_call.started` fact, including the current structured or command-input representation used by each runtime. The predicate must not inspect assistant prose, final output, tool output, liveness status, Server responses, or outbox rows.

The per-turn observation tracker is updated synchronously when the matching start fact is received. It is shared with the guard coordinator and is idempotent by runtime session, turn identity, and tool-call identity. The action is considered attempted at invocation start; a later rejected or non-zero result does not clear the marker. The tracker is attached to both initial-turn observers and follow-up observers. For a Pi follow-up that steers an already-running turn, the tracker is attached to the active session's existing turn subscription so events after `steer` are included in the same terminal observation.

This is the authoritative publication-related fact for the feature. There is no `ServerConnection` method, Server endpoint, `SlackOutboxStore` predicate, or delivery-state probe in the plan.

**Alternative considered:** Infer a reply from final assistant text, tool output, a terminal runtime event, or successful delivery. Rejected because none proves that the Agent attempted the reply action, and a failed action must already be visible to the Agent as non-zero feedback.

### 3. Issue an internal advisory on the existing runtime session

The advisory is a fixed, short prompt assembled with `buildExecutionEnvelope`, the validated `SlackExecutionContext`, and the existing `inlineSlackCollaborationSkill`. Its instruction says, in substance: the Agent's reasoning and tool output are invisible to the Slack user; if this turn has useful content, publish a self-contained conclusion, evidence summary, and next step through the existing reply action and supplied anchor; otherwise deliberately remain silent.

The advisory uses the current `runtimeSessionId`, work directory, runtime selection, and observation tracker associated with the original turn. It does not emit a second user-originated `session.input`, create a new AgentTurn, rotate a binding, or create a second liveness progress row. Any reply produced after the advisory therefore goes through the unchanged `mo slack message send` action. If the first advisory completes silently, the coordinator may issue one second advisory under the default budget; after that it closes normally.

**Alternative considered:** Call the reply endpoint directly from the Runner. Rejected because the Agent owns reply content and the Runner must not synthesize or publish on the Agent's behalf. **Alternative considered:** Start a new AgentJob or fresh runtime session. Rejected because it would lose turn context and create a second user-visible execution.

### 4. Make the advisory bounded and abortable

Use one fixed initial timeout, proposed as `30_000` ms per advisory, with an abort signal combined from the original turn signal and the guard timeout. Extend the internal Pi/OpenCode follow-up call surface only as needed to accept that signal and expose terminal completion; this is not a new wire contract. OpenCode passes it through its existing turn execution path. Pi uses its existing interruption path and terminal session observation for both an idle follow-up and a streaming follow-up.

The guard claims `advisory-running` synchronously before awaiting the runtime. A timeout, invocation error, unavailable runtime, interruption, or late result closes the guard and is logged as diagnostic information only. No guard condition can trigger a retry. If a runtime cannot confirm interruption, the original turn result still wins and no replacement turn is started.

**Alternative considered:** Race an unabortable runtime promise against a timer and leave it running. Rejected because a late model turn could publish after the turn is closed and consume runtime capacity. Runtime-specific cancellation and a terminal completion handle are required; the one-shot state remains a defensive backstop for late completion.

### 5. Preserve the original terminal outcome and liveness sequence

The original result is captured before guard processing and returned unchanged. The initial AgentJob still reports the same `WorkItemResult` to `AgentJobGrain`; the existing Slack terminal handler finalizes liveness independently. Guard output is never projected as the turn result.

For follow-ups, the existing terminal activity is enqueued after the runtime has reached its real terminal point and after bounded guard processing. The status and output payload are unchanged, and the record is emitted exactly once. The guard's finite bound may delay closeout, but it does not change liveness semantics: silent turns still close as before and no fallback reply is created.

If a reply action attempt occurs during the original turn or any advisory, guard processing stops. Whether that action succeeds, fails, or is interrupted is left to the existing Agent/runtime feedback path; the guard never probes or second-guesses it.

**Alternative considered:** Emit liveness closeout first and then run the advisory. Rejected because a follow-up advisory would then execute after the turn had been reported terminal and could race terminal delivery. Waiting for the real terminal point and delaying the unchanged closeout by the bounded guard interval keeps one coherent Runner operation.

### 6. Use an actual terminal boundary for every follow-up branch

Follow-up admission and follow-up completion are separate internal facts. The SignalR handler may acknowledge a follow-up once its input is durably queued and the runtime has admitted it, but it must not evaluate the guard or record terminal activity at that admission point.

- OpenCode's follow-up promise already represents `runTurn` completion. Its result and observer facts form the terminal input to the guard.
- Pi idle follow-up `preflight(true)` only proves prompt admission. The runtime must expose a completion handle or callback that settles after the background `session.prompt` continuation has finished, reconciled its terminal events, and released its session lock.
- Pi streaming follow-up `steer` only proves injection into the active turn. The runtime must expose a completion handle or callback keyed to the same session/operation that settles after the active turn reaches its terminal state (`isStreaming` becomes false and the terminal event/result has been observed). The handler must use the active turn's shared observation tracker, not the immediate `steer` result, to detect a reply action attempt.

The follow-up handler marks the operation submitted at admission as it does today, then waits for the runtime-specific terminal completion to flush observer facts, run the shared guard, and finally call `recordFollowupActivity`. Duplicate delivery remains protected by `inFlight` and the operation journal; duplicate terminal callbacks are harmless because the guard state and terminal-record claim are keyed by the follow-up operation and turn identity.

**Alternative considered:** Treat the `PiRuntime.followup` return value as terminal for both branches. Rejected because the current implementation returns after `steer` or `preflight(true)` while model work is still active. That would inject the advisory too early and can emit terminal activity before the model turn has ended.

### 7. Enforce one evaluation per turn

Use a turn key based on stable Runner identity: `workId` plus `initialTurnId` or AgentJob identity for initial work, and `sessionId` plus `turnId`/`operationId` for follow-ups. The guard state transitions from `not-evaluated` to `evaluating`, `advisory-running`, and `closed` before asynchronous work begins. A shared active-turn observation tracker prevents duplicate tool-call facts from changing the result, and the existing follow-up `inFlight`/journal protection remains the outer defense against duplicate delivery.

No state is added to Slack or session persistence schemas. State is scoped to the active Runner operation. A Runner restart may lose an in-memory reminder count, but reconciliation must not run the guard without a live terminal operation and a fresh local observation; this feature does not add cross-restart reminder persistence.

## Risks / Trade-offs

- [A reply-action start fact can race the terminal check] -> Keep the attempt marker in the same synchronous observer path that receives normalized tool-call events, drain pending observer work before evaluation, and check the marker again immediately before each advisory.
- [The first advisory completes silently and the second adds latency] -> Use the explicit default-two budget, a fixed finite bound per advisory, and stop immediately on any reply-action attempt or runtime failure.
- [Pi admission and terminal completion can be confused] -> Keep separate admission and completion handles, test both idle/preflight and streaming/steer branches, and only run guard/closeout from the completion handle.
- [Pi or OpenCode interruption may not be confirmed cleanly] -> Reuse each runtime's existing abort/cleanup semantics, detach late guard completion, log the diagnostic, and never replace the original turn result.
- [A tool-call shape changes between runtime adapters] -> Test the shared attempt predicate against the actual normalized Pi and OpenCode event shapes and keep the predicate limited to the existing reply action identity.
- [The Agent may still choose silence or publish an incomplete reply] -> The guard is an advisory, not a correctness guarantee. It must not invent content, retry indefinitely, or convert valid silence into failure.
- [Advisory runtime events may add transcript noise] -> Reuse the current runtime session and observation identity, emit no second user-originated input event, and keep the advisory prompt internal to the Runner operation. Add an explicit event-correlation test.

## Migration Plan

1. Add and test the Runner-local reply-action attempt predicate, observation tracker, explicit reminder budget, and shared coordinator. No Server code or endpoint changes are needed.
2. Add the runtime-specific bounded advisory completion/abort support, then integrate the coordinator into initial Pi/OpenCode AgentJob terminal handling.
3. Integrate follow-ups using separate admission and terminal completion paths, including Pi idle and streaming cases. Run focused Runner coverage for both runtimes and the existing liveness closeout.
4. Roll out the Runner change. Existing Slack reply action, outbox, delivery retries, liveness reactions, and terminal delivery handlers remain unchanged.
5. To roll back, stop or deploy the prior Runner build. No Server rollback, data migration, or schema change is required.

## Open Questions

- Confirm the operational value of the initial `30_000` ms advisory bound against deployed Pi and OpenCode model latency. Keep it as a code-level constant for the first release unless production evidence justifies configuration.
- Confirm the exact normalized tool-call input forms for `mo slack message send` in both runtime adapters and keep the Runner predicate aligned with those existing forms.
- Decide whether guard timeout and guard-unavailable outcomes need a dedicated Runner metric/event name. They should remain logs or internal diagnostics unless an operator-facing surface is required; they must not become user-visible Slack messages.
