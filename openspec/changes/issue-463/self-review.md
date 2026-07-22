# Self-Review — Issue 463 (pass 2)

Reviewing `proposal.md`, `design.md`, `tasks.json`, and `specs/` against the issue.

## Prior findings — verified fixed

- **P1 (activity-state spec wording):** `agent-session-followup-activity-state` Requirement 1 now consistently says "new **active** round", the body clarifies "active/thinking round, not the mere rendering of the prompt," and the mechanism parenthetical is mechanism-agnostic. It now matches design D3. ✓
- **P2 (web `model.resolved` field):** a "Web model.resolved event carries `resolvedModel`" requirement+scenario was added to `agent-session-model-resolution-event`; design D2/Context, proposal (What Changes/Capability/Impact), and T-002 (description, acceptance, output, notes) all now cover aligning the web live-event type to `resolvedModel`. ✓

## Verified correct (no change needed)

- D1 delivery premise holds — transcript events are filtered by the per-connection subscription set (`SignalRTranscriptEventPublisher.cs:49`).
- T-002 upload premise holds — Pi runtime events reach the server transcript (`pi.ts:122,147`; `agent-job-executor.ts:136-153`).
- D3 rationale holds — the follow-up input is enqueued before the terminal in every path, so server-side `LastDataAt` refresh would break the recovery invariant (`AgentSessionRecoveryGrainSpecs.cs:322`).
- `tasks.json` is valid JSON with an acyclic, strictly-lower-priority DAG; every spec requirement has ≥1 `#### Scenario` with SHALL + WHEN/THEN. All three issue acceptance criteria map to tasks.

## Problem that must be fixed

### P3 — Follow-up terminal spec/task conflates a follow-up-operation terminal with a session terminal

A follow-up terminal is **operation-scoped**, not session-scoped. The server maps `session.followup_completed` / `session.followup_failed` to `TranscriptPartTypes.Status` and uses them only to terminate the follow-up *lease* (`AgentSessionGrain.cs:729-730`, `TerminatesFollowupLease` at `:1352`), never to close the session. Only `session.closed` makes a session globally terminal. The recovery spec even proves the session is reported `inactive` — not `failed` — after `session.followup_failed` (`AgentSessionRecoveryGrainSpecs.cs:322`). A session stays usable for further follow-ups after either terminal.

Despite this, the plan asserts the **session** flips to a global terminal:

- `specs/agent-session-followup-terminal-visibility/spec.md`, Requirement "Web converges session state on a follow-up terminal event":
  - Scenario "Completed follow-up converges to completed state" → "the session's presented state SHALL become completed"
  - Scenario "Failed follow-up converges to failed state" → "the session's presented state SHALL become failed"
- `tasks.json` T-001 acceptance criterion: "On receiving `session.followup_completed` the session's presented state becomes completed; on `session.followup_failed` it becomes failed".

This contradicts both the server's behavior and T-001's own description / design D1, which say the right thing: "close the in-flight follow-up **round** to the corresponding outcome … invalidate the `agent-session`/`agent-activity` queries so the session card/list **refetches the server-derived status**". An implementer following the spec scenarios would flip the session card/list to "completed"/"failed" on every follow-up — a visible bug (a ready-for-next-follow-up session shown as finished, or a still-usable session shown as globally failed).

**Fix:** reword the "Web converges session state …" requirement and its two scenarios (and T-001's matching acceptance criterion) so that the in-flight follow-up **round/operation** converges to completed/failed, and the session's presented **status is refreshed from the server** (reflecting activity/inactivity, not a global completed/failed terminal). The session SHALL NOT become globally completed/failed from a follow-up terminal.

## Verdict

P3 is a correctness defect in the spec contract that is contradicted by the server's own behavior and would produce a visible UI bug if implemented as written. It must be fixed before building.

<promise>FAIL</promise>
