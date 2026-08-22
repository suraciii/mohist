# Review: issue 621

## Verdict

**PASS** — no must-fix problem remains; the change is ready to merge.

## Re-review: previous findings

### MF-001 — advisory budget exceeded the issue limit: fixed

The previous review found that the default coordinator budget was two, violating the issue's requirement that the initial Slack turn receive at most one advisory. The current implementation sets `DEFAULT_REPLY_GUARD_REMINDER_BUDGET` to `1` in `packages/runner/src/runtime/reply-guard.ts:10`. The coordinator claims the reminder slot before invoking the advisory and closes after that single opportunity (`reply-guard.ts:218-247`). The focused tests now assert one advisory and a closed state (`packages/runner/tests/reply-guard.spec.ts:140-160` and `packages/runner/tests/agent-job-reply-guard.spec.ts:219-249`).

This disposition holds: the initial Pi and OpenCode integrations use the default budget and issue at most one advisory.

### MF-002 — follow-up behavior was changed despite being out of scope: fixed

The previous review found that the T-003 follow-up terminal-boundary and follow-up guard work violated the issue's explicit non-goal covering Slack follow-ups, Pi `steer`, and Pi idle follow-up completion. That work has been removed. The current `packages/runner/src/server/followup-handler.ts` has no reply-guard evaluation and records follow-up activity through the existing path; the follow-up-specific reply-guard test file and completion helper are gone. The no-signal follow-up paths in `packages/runner/src/runtime/pi/runtime.ts` and `packages/runner/src/runtime/opencode/runtime.ts` retain their pre-change admission/completion behavior. The remaining optional signal plumbing is used only by the initial-turn advisory path in `packages/runner/src/runtime/agent-job-turn.ts:354-393`; ordinary follow-up callers do not pass a signal.

The `packages/runner/src/runtime/pi/session-state.ts` extraction is behavior-neutral: it only moves the existing pure message-state helpers out of `pi/runtime.ts`.

## Re-review checks

- **Acceptance criteria:** checked. The guard is integrated only at the initial AgentJob terminal boundary for both Pi and OpenCode (`packages/runner/src/runtime/agent-job-turn.ts:137-199` and `251-342`). It reuses the existing session, work directory, Slack context, reply anchor, and collaboration skill; it returns the original `WorkItemResult` unchanged after advisory processing.
- **Reply-attempt correctness:** checked. `ReplyActionObservationTracker` observes normalized `tool_call.started` facts synchronously, recognizes the projected Pi and OpenCode shell-command shapes, and leaves the attempt marked when a later completion is rejected or fails (`packages/runner/src/runtime/reply-guard.ts:33-80`). Final text, unrelated tools, terminal facts, liveness, Server state, and delivery state are not used as evidence.
- **Eligibility and scope:** checked. Missing or malformed Slack context bypasses the coordinator, while the existing executor validation remains unchanged. No follow-up handler or Server-side unpublished-reply detection is involved.
- **Bounded/error behavior:** checked. The single advisory is bounded by the fixed `30_000` ms timeout, combines the original cancellation signal, aborts the internal runtime call on timeout/interruption/action attempt, and contains advisory failure. Late advisory completion cannot replace the original result.
- **Outcome and closeout preservation:** checked. The guard is evaluated only after the original runtime result and event-sink drain have been captured, and `evaluate()` returns that captured result unchanged. No liveness or terminal-reporting code was changed by the final implementation.
- **Tests:** checked and verified. Runner source typecheck, test typecheck, test-boundary validation, the focused reply-guard/AgentJob suites (22/22), and the full Runner CI suite (157 files, 1,705 tests) pass.

## Observations

- `ReplyGuardCoordinator` still accepts an explicit `reminderBudget` greater than one (`packages/runner/src/runtime/reply-guard.ts:118-126, 394-398`), although no production caller supplies it and the default/product path is exactly one. Clamping or removing that option would make the invariant harder to accidentally violate in a future caller; this is not a current acceptance failure.
- `packages/server/tests/Mohist.Server.SpecTests/Specs/SystemSpecs/Otel/MohistOpenTelemetryRegistrationSpecs.cs` contains an unrelated test-hermeticity adjustment. It does not affect issue 621 behavior, but it would be cleaner to review or land it separately.
- The recorded canonical verification encountered load-sensitive Server Spec/duration-gate failures, while the issue-specific Runner checks and the complete Runner CI suite pass. No issue-621 product failure was reproduced from those external diagnostics.

<promise>PASS</promise>
