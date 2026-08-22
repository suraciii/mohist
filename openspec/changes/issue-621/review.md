# Review: issue 621

First review of the current change. The canonical issue was re-read before reviewing the diff. Its current scope is one bounded advisory for initial Slack AgentJob turns only; Slack follow-ups are explicitly out of scope.

## Verdict

**FAIL** — two must-fix problems make the change wrong relative to the current acceptance criteria.

## Must-fix findings

### MF-001 — The guard can issue two advisories, but the issue permits at most one

**Violated criteria:**

- “advisory 最多一次、最长 30 秒、可由原 Turn cancellation 中断，不形成循环”
- Non-goal: “不发送第二次 advisory”

**Evidence:**

- `packages/runner/src/runtime/reply-guard.ts:10` sets `DEFAULT_REPLY_GUARD_REMINDER_BUDGET` to `2`.
- `packages/runner/src/runtime/reply-guard.ts:218-247` loops until the budget is exhausted and starts a second advisory after a silent first advisory.
- `packages/runner/src/runtime/agent-job-turn.ts:354-398` uses that coordinator for both initial Pi and OpenCode AgentJob turns, so every eligible unpublished initial turn can receive two reminders.
- `packages/runner/tests/agent-job-reply-guard.spec.ts:250-271` explicitly asserts two initial advisories, confirming this is implemented behavior rather than dead configuration.

This is a direct product-contract violation, not merely an extra test or implementation detail. A silent first advisory must close the guard; a second advisory must never be started. The coordinator, integrations, and tests need to be changed so one initial Slack turn has a maximum of one advisory while preserving the original outcome on all advisory exits.

### MF-002 — The change modifies Slack follow-up behavior that the current issue requires to remain unchanged

**Violated criterion:**

- “非 Slack、无效 Slack context 和所有 follow-up turn 保持现有行为”
- Non-goal: “不覆盖 Slack follow-up、Pi steer 或 idle follow-up completion”

**Evidence:**

- `packages/runner/src/server/followup-handler.ts:306-385` now waits for a runtime-specific completion, evaluates `ReplyGuardCoordinator`, and only then records terminal activity.
- `packages/runner/src/server/followup-handler.ts:406-443` adds a reply guard to follow-up turns.
- `packages/runner/src/server/command-runtime.ts:129-137` adds `awaitFollowupCompletion`.
- `packages/runner/src/runtime/pi/runtime.ts:426-471`, `packages/runner/src/runtime/pi/followup.ts`, and `packages/runner/src/runtime/pi/types.ts` change Pi follow-up admission/completion semantics and expose completion handles.
- `packages/runner/tests/runner-signalr-followup-reply-guard.spec.ts` asserts the new follow-up advisories and terminal gating.

These changes affect exactly the follow-up, Pi idle, and Pi streaming paths that the current issue excludes. They also change non-Slack Pi follow-up orchestration because the completion handle is part of the shared runtime path. Leaving them in means follow-ups no longer retain existing behavior, regardless of whether the initial-turn guard is otherwise correct. Remove the follow-up guard, terminal-boundary refactor, and associated production/test changes from this issue’s deliverable; follow-up work requires separate scope and evidence.

## First-review dimension sweep

- **Acceptance criteria — FAIL.** The initial-turn integration is present, but MF-001 violates the one-advisory limit and MF-002 violates the explicit follow-up/non-goal boundary.
- **Coverage — FAIL.** The tests cover the broader stale plan shape, but they assert two reminders and new follow-up behavior instead of verifying the current one-reminder, initial-only contract.
- **Correctness — FAIL.** The local attempt observation and original-result preservation are directionally correct, but the observable behavior is incorrect for the current budget and follow-up scope.
- **Consistency with the current issue and surrounding change boundary — FAIL.** The implementation is internally consistent with the older OpenSpec artifacts, but those artifacts conflict with the canonical issue after its scope reset.
- **Tests and verification — FAIL against the acceptance criteria.** Runner source/test typechecks, test-boundary validation, the focused issue-621 suites (49/49), and the full Runner suite (158 files, 1,709 tests) pass. However, passing tests do not rescue the contract failures: the issue-621 tests encode the two-reminder/follow-up behavior that must be removed or revised.

## Observations

- The artifacts under `openspec/changes/issue-621/` describe the superseded broader design: two reminders and follow-up terminal handling. The current issue comments and acceptance criteria reset the scope to one initial-turn advisory. This explains the implementation mismatch, but those workflow artifacts are not product deliverables and do not change the verdict.
- `packages/server/tests/Mohist.Server.SpecTests/Specs/SystemSpecs/Otel/MohistOpenTelemetryRegistrationSpecs.cs` is an unrelated test-hermeticity change. It may be useful independently, but it is not part of the current issue’s initial Slack reply guard and should be reviewed or landed separately.

<promise>FAIL</promise>