# Self-Review — Issue 463 (pass 3)

Reviewing `proposal.md`, `design.md`, `tasks.json`, and `specs/` against the issue.

## Prior findings — verified fixed

- **P1 (activity-state spec wording):** scenarios and requirement body consistently say "new **active** round"; matches design D3. ✓
- **P2 (web `model.resolved` field):** "Web model.resolved event carries `resolvedModel`" requirement+scenario present; proposal/design/T-002 all cover the web live-event type alignment. ✓
- **P3 (follow-up terminal treated as session-terminal):** the convergence requirement is now operation-scoped — "Web converges the in-flight follow-up round and refreshes session status on a terminal event", with both scenarios stating the round converges and the session status is refreshed from the server, and an explicit "SHALL NOT mark the session itself as globally completed or failed". T-001 acceptance #3 and the proposal capability line match. This aligns with the server (follow-up terminals map to `TranscriptPartTypes.Status` and terminate the lease, `AgentSessionGrain.cs:729-730,1352`; the session is `inactive`, not `failed`, after `session.followup_failed`, `AgentSessionRecoveryGrainSpecs.cs:322`). ✓

## Verified correct

- **Delivery premise (D1):** transcript events are filtered by the per-connection subscription set (`SignalRTranscriptEventPublisher.cs:49`); adding the two types to the web canonical set unblocks delivery.
- **Pi upload premise (T-002):** Pi runtime events reach the server transcript (`pi.ts:122,147`; `agent-job-executor.ts:136-153`), so a Pi `model.resolved` surfaces via the summary. No active live consumer of the web `model.resolved` type today, so the field alignment is a latent-consistency fix.
- **Activity-state rationale (D3):** the follow-up input is enqueued before the terminal in every path, so server-side `LastDataAt` refresh would break the recovery invariant; the web-side gating on response events is the correct, low-risk fix and preserves the invariant.
- **Plan integrity:** `tasks.json` is valid JSON with an acyclic, strictly-lower-priority DAG (T-003 → T-001). Every spec requirement has ≥1 `#### Scenario` using SHALL + WHEN/THEN. All three issue acceptance criteria map to tasks (T-001/T-002/T-003); issue Non-Goals (no follow-up-flow or terminal-fallback changes) are respected.

## Non-blocking observation (no action required)

`proposal.md` bullet 3 lists two candidate mechanisms ("refresh active time on the server, or stop treating follow-up input as a new round on the web"). These are exactly the two alternatives design D3 evaluated and rejected in favor of a third (render the round, gate the active/thinking indicator on response events). This is consistent with a normal proposal→design flow (proposal poses options, design resolves) and the proposal explicitly delegates the mechanism to design, so no change is needed.

## Verdict

All prior findings are resolved; the plan is internally consistent, factually correct against the code, and ready to build.

<promise>PASS</promise>
