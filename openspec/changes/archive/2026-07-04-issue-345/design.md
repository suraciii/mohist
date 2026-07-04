## Context

A generic AgentSession launched from the Web Agent workbench executes for real
(tokens consumed, tools called, runner capacity held) but the session detail
page shows "0 turns / Waiting for activity..." and never leaves `running`, while
`GET .../agent-sessions/{sessionId}/transcript` returns turns with
`messages: 0, events: 0`. The launch→dispatch→runner pipeline is wired
end-to-end (verified 2026-07-03), so this is a real break inside a connected
pipe, not a missing segment. The change must **reproduce → localize → fix → lock
down** on the generic (`sessionId`) axis, kept distinct from the
issue/workflow axis (#47).

### Current state (verified by code reading)

Two independent defects are in play. One is confirmed; one is a hypothesis
pending reproduction.

**D1 — Terminal state (CONFIRMED).** The runner suppresses the job-completion
`session.closed` for a *succeeding* generic agent job
(`packages/runner/src/actions/acp-agent.ts:47-49`):

```ts
if (context.ownerKind !== "agent-job" || !context.agentSessionId || !ok) {
  await emitSessionEvent(context, "session.closed", { status: ok ? "completed" : "failed", ... })
}
```

The suppression exists so the cached ACP session can be reused for a follow-up
turn (`AcpSessionManager`, keyed `generic:<sessionId>`, written on success in
`session-strategies.ts:163/168`). But the server derives list status purely from
the transcript: `AgentSessionQuerier.ResolveAgentSessionListStatus`
(`AgentSessionQuerier.cs:276-285`) returns `"running"` whenever there is **no**
persisted `SessionClosed` transcript part **and** the runner has bound its ACP id
(`/attach`). The success path `AgentJobGrain.ReportResultAsync` (lines 121-175)
records **no** close — only the failure path
`CloseGenericSessionOnFailureAsync` (lines 456-483) does. Net: a successful
generic session never reaches `completed`. This is locked in by test
`session-strategies-generic.spec.ts:238-248`, which asserts no close after a
successful turn.

**D2 — Transcript emptiness (HYPOTHESIS, root cause not confirmed).** Every
session event funnels through one chokepoint, `emitSessionEvent`
(`session-events.ts:63-82`), which silently early-returns when
`sessionTargetFromContext` (lines 48-61) resolves `null`:

```ts
if (!target || !context.serverConnection) return   // session-events.ts:65
```

For an agent-job owner, the target is null iff `agentSessionId` or `projectId`
is falsy. Crucially, static analysis shows the product launch path **does**
mint and propagate a non-null id: `AgentSessionLaunchRoutes.cs:68-88` sets
`AgentJobInput.AgentSessionId = sessionId`, `AgentJobGrain.BuildDispatch`
(line 357) forwards it onto `WorkDispatch`, and `connection.ts:330`
(`dispatch.agentSessionId ?? undefined`) reads it onto the action context. The
only path that nulls it is the raw-prompt-only *validation* endpoint
(`AgentJobController.cs:90-95`), explicitly not the product API. Because
`AgentSessionId` is a newly added field (`Id(16)`), the original "messages: 0"
report may predate the wiring. **Whether transcript emptiness persists after
the current wiring is unverified and must be reproduced** — hence D2 is treated
as a hypothesis to confirm-or-refute, not an assumed root cause.

### Constraints / stakeholders

- Cross server↔runner contract; high blast radius; risk rated **high**.
- Must not touch issue/workflow session transcript behavior (#47).
- Must not rewrite the transcript persistence model (#100,
  `TranscriptAccumulator` deferred flush stays).
- No interactive/resume capability (#133).
- No new endpoints or schema changes anticipated.

## Goals / Non-Goals

**Goals:**

- **G1 (D1, confirmed):** A generic session reaches `completed`/`failed` on job
  completion, **including the success path**, with the terminal signal
  decoupled from the runner's cached ACP-session lifetime.
- **G2 (D2, verify-then-fix):** A generic session records a **non-empty**
  transcript (assistant text, tool calls, usage) for the initial and follow-up
  turns. Localize the actual loss point via reproduction; if the pipe is now
  sound, lock it down so it cannot silently regress.
- **G3 (contract hardening):** A generic launch always produces a dispatch
  carrying a non-null `AgentSessionId`, and an unresolved session target is
  **observable** (logged), not silently swallowed — so this failure class can
  never again present as "runs but records nothing".
- **G4:** Regression coverage on the generic (`sessionId`) axis, isolated from
  the issue/workflow axis (#47).

**Non-Goals:**

- Changing issue/workflow session transcript or terminal-state behavior (#47).
- Interactive / resume agent sessions (#133).
- Rewriting `TranscriptAccumulator` deferred persistence (#100).
- New public APIs or data-schema migrations.
- Changing the runner's ACP-session caching strategy for follow-ups (the cache
  stays; only its coupling to the terminal signal is broken).

## Decisions

### D1 — Server records the terminal close on BOTH paths (job completion is authoritative)

**Decision.** `AgentJobGrain` becomes the single authority for a generic
session's terminal state. Add a success-side mirror of the existing failure
close: on successful completion (`ReportResultAsync`), when
`_input.AgentSessionId` is non-empty, append a `session.closed` runtime event
with `status = "completed"` to the session grain — exactly symmetrical to
`CloseGenericSessionOnFailureAsync` (`status = "failed"`). The runner's
success-path suppression at `acp-agent.ts:47-49` is **left in place** (it
preserves the cached ACP session and avoids a redundant close); the server now
closes regardless.

**Rationale.** The spec
(`generic-agent-session-terminal-state`) requires the terminal signal to be
"the agent job's completion, decoupled from the runner's cached ACP-session
lifetime." The job grain is the only component that knows completion
definitively and is independent of the runner event emitter — the very channel
under suspicion in D2. Mirroring the already-proven failure path keeps the
change small and symmetric. The failure path already tolerates a duplicate
close (runner emits `session.closed/failed` at `acp-agent.ts:48` **and** the
server appends one via `CloseGenericSessionOnFailureAsync`); the success path
simply gains the same server-side authority, so no new duplication problem is
introduced.

**Alternatives considered.**

- *A1 — Runner always emits `session.closed` on generic success (delete the
  suppression).* Symmetric at the runner, but **depends on the runner event
  emitter** — the channel we are debugging. If `emitSessionEvent` drops on a
  null target (D2), the close vanishes too and the session still hangs. Does
  not robustly fix terminal state. Rejected as the primary fix.
- *A2 (chosen) — Server records close on success.* Authoritative, robust
  against runner-emitter failures, mirrors the failure path, fully decouples
  from the ACP cache.
- *A3 — Derive terminal state by joining `AgentJob` state into the session
  read query, without recording a `session.closed` transcript part.* Avoids a
  transcript mutation but breaks the existing "terminal-state derivation keys
  off a persisted `session.closed` part" contract (which the spec explicitly
  keeps), requires cross-grain joins in the read path, and has larger blast
  radius. Rejected.

**Follow-up interaction.** Between turns the latest persisted
`session.closed/completed` dominates `ResolveAgentSessionListStatus`, so the
session observably holds `completed` while idle — exactly what the spec's
"Follow-up after a completed session observes the prior terminal state"
scenario mandates. A follow-up submits a new job; its `session.input` opens a
new turn, deltas flow, and its eventual success records a fresh close. No
re-open-to-running is required.

### D2 — Reproduce first; harden the chokepoint regardless of root cause

**Decision.** Treat transcript emptiness as **verify-then-fix**, because static
analysis shows the launch path propagates `agentSessionId` (D2 may already be
resolved by the recent wiring). The work is ordered:

1. **Reproduce.** Add a fake-agent harness that drives a generic launch through
   to a polled `WorkDispatchResponse` and a fake ACP agent through
   `runAcpGenericAgentSession`, asserting: (a) the polled envelope carries a
   non-null `AgentSessionId`; (b) `message.delta` / `tool_call.*` /
   `usage.updated` are POSTed to `agentSessionRuntimeEvents`; (c) the persisted
   transcript turn is non-empty. Then confirm with a real opencode run.
2. **Localize** using the decision tree below if (b)/(c) fail.
3. **Fix** the confirmed cause.

**Investigation decision tree (if transcript is still empty):**

| Observation | Likely cause | Fix locus |
|---|---|---|
| Polled `AgentSessionId` is null/whitespace | Launch/dispatch contract gap (D2 null-dispatch) | `AgentSessionLaunchRoutes.cs` / `BuildDispatch` |
| Runner context `agentSessionId` falsy but envelope non-null | `connection.ts:330` normalization or context wiring | `connection.ts` / `executor.ts:248` |
| Runner never POSTs (target null) | `sessionTargetFromContext` null (projectId/agentSessionId missing) | `session-events.ts:48-61` |
| Runner POSTs but server transcript empty | endpoint gate `IsGenericAgentSessionInProjectAsync` (`RunnerRoutes.cs:353-364`) rejects, or `TranscriptAccumulator` drops the type | `RunnerRoutes.cs` / `TranscriptAccumulator.cs` |

### D3 — Make the silent drop observable (hardening, applied unconditionally)

**Decision.** Replace the silent `return` at `session-events.ts:65` with an
**observable** drop: when `ownerKind === "agent-job"` and the target cannot be
resolved (e.g. `agentSessionId` missing), emit a warning via `context.log`
(`TaskLogger`) carrying `workId`/`agentJobId` and a clear "unresolved generic
session target — events dropped" message. Keep it a non-throwing drop (throwing
would abort the prompt loop for a condition that is by-design for ephemeral
jobs), but make it loud. Scope the log to the agent-job owner only, so
by-design null targets (ephemeral/validation dispatches) stay quiet.

**Alternatives.** *Throw on null target* — rejected: would break execution for
ephemeral jobs and over-couple. *No-op + metrics counter only* — rejected: the
spec requires the condition be diagnosable from a log, not just a counter.

### D4 — Lock the launch→dispatch contract with a regression assertion

**Decision.** Add a spec on the **launch route** (not just the grain) asserting
that `POST /api/projects/{projectRef}/agents/{agentRef}/sessions` mints a
session id and that the resulting agent job's **polled** `WorkDispatchResponse`
carries that id verbatim as a non-null `AgentSessionId` (and no
`workflowRunId`). Existing grain-level specs
(`SubmitAsync_AgentJobWithAgentSessionId_PopulatesSessionIdOnDispatchEnvelope`)
cover `BuildDispatch`; this adds the end-to-end launch→poll guard the spec
`agent-session-launch` requires, so a future null-dispatch regression fails a
test instead of presenting as an empty transcript.

## Risks / Trade-offs

- **[Duplicate `session.closed` events]** On the failure path the runner and
  the server already both emit a close; D1 makes success symmetric. `latest
  wins` in `ReadTerminalStateAsync` (ordered by sequence/id) keeps status
  consistent. -> Ensure the success payload is a single, well-formed
  `status=completed` event; reuse `CloseGenericSessionOnFailureAsync`'s payload
  shape. No dedup logic change needed.
- **[D1 depends on the job reaching `ReportResultAsync`]** If a job hangs
  without ever reporting success, the session still sticks. -> That is the same
  surface the failure path already covers (timeouts → `FailWithReasonAsync` →
  failure close). Liveness/timeout hardening is out of scope here.
- **[D2 root cause unconfirmed]** If a deeper loss exists beyond the wiring, it
  may not surface in a unit harness. -> D3's observable drop converts any
  null-dispatch regression into a logged, diagnosable event; the decision tree
  bounds the search space.
- **[Follow-up status shows `completed` mid-flight]** A follow-up running
  against a prior `completed` session keeps showing `completed` until it
  records its own close. -> Accepted and mandated by the spec (observed
  terminal state between turns). Transcript turns still append unconditionally;
  only status derivation reads the latest close.
- **[Observable-drop logging noise]** Logging on every dropped event could
  spam. -> Log once per unresolved target (not per event) and scope to the
  agent-job owner; ephemeral/validation dispatches stay silent.

## Migration Plan

- **No schema/API migration.** Pure behavior fix on existing transcript /
  terminal-state contracts; no new endpoints, fields, or stores.
- **Deploy order.** Ship server and runner together. D1 is server-side and does
  not require a runner change, but deploying both is safe and keeps the
  contracts aligned. The runner change (D3) is backward-compatible (adds a log
  only).
- **Existing in-flight sessions at deploy.** A session already stuck in
  `running` from a job that already finished will not be retroactively closed
  (D1 fires on the *next* completion). Such legacy rows are a pre-existing
  condition; a one-off reaper is out of scope unless reproduction shows a large
  population.
- **Rollback.** Revert the commit; no persistent state change beyond normal
  transcript writes. The suppression behavior is restored unchanged.

## Open Questions

- **Does transcript emptiness persist after the current `AgentSessionId`
  wiring?** Resolved by the D2 reproduction harness. If yes, follow the
  decision tree; if no, D3+D4 are the durable guard.
- **Should the runner also emit `session.closed` on success for symmetry, or
  rely solely on the server (D1)?** Design chooses server-authoritative. Revisit
  only if event-sourcing purity (runner as sole event source) becomes a goal.
- **Follow-up turn append while a prior `completed` close is latest:** confirm
  runtime events still append to a new turn (expected — `AppendRuntimeEventsAsync`
  appends unconditionally; only status derivation reads the latest close). Verify
  in the follow-up spec.
