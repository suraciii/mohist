# Review Report

## Verdict: FAIL

## Dimensions

### Correctness: FAIL

- **[ERROR] Auto-fix/re-verify round failure returns immediately instead of counting as failed attempt** (`workflow-controller.ts:886-895, 916-925`): The spec (review-auto-fix/spec.md "Auto-fix round fails" scenario) says: "WHEN the auto-fix agent round fails, THEN the system SHALL count it as a failed attempt AND proceed to the next attempt or escalate if max reached." The implementation returns `{ success: false }` immediately on both auto-fix and re-verify ACP failures. This means a single transient ACP error kills the entire review stage instead of exhausting the retry budget. While this may be defensible for connection-level errors, it contradicts the spec's explicit "count it as a failed attempt" language.

- **[ERROR] Checkpoint key format deviates from spec** (`workflow-controller.ts:966, 845`): The spec says `checkpointRepo.upsert(issueNumber, 'review', ['no-auto-fix'], null)` and `checkpointRepo.get(issueNumber, 'review')`. The implementation uses `checkpointRepo.upsert(issue.number, 'no-auto-fix', ['exhausted'], null)` and `checkpointRepo.get(issue.number, 'no-auto-fix')`. The stage key is `'no-auto-fix'` instead of `'review'`, and the completedSteps value is `['exhausted']` instead of `['no-auto-fix']`. Internally consistent, but incompatible with the spec's stated API contract.

- **[WARNING] Comment content doesn't include original Fix Suggestions** (`workflow-controller.ts:936-939`): The spec says the comment shall summarize "List of issues that were fixed (from original review.md Fix Suggestions)". The implementation records auto-fix agent output snippets (`autoFixResult.text?.slice(0, 200)`) in `fixHistory`, not the original Fix Suggestions extracted from `review.md`. The comment body says "Auto-fix applied (attempt N)" followed by truncated agent output, not the original issue list.

- **[OK]** `parseVerdict` regex is correct: `/^##\s*Verdict\s*:\s*(PASS|FAIL)\s*$/im` with `.toUpperCase()` normalization handles case variations and whitespace correctly.

- **[OK]** Round index calculations are correct: `2 + attempt * 2` for auto-fix (R2, R4), `3 + attempt * 2` for re-verify (R3, R5).

- **[OK]** `conn.close()` is called in all exit paths (PASS, FAIL with checkpoint, auto-fix success, auto-fix failure, re-verify failure, escalation, catch block).

- **[OK]** `onSessionUpdate` callback correctly captures `roundState` by reference, so `plan_session_update` events use the correct `roundType` for all rounds including auto-fix and re-verify.

- **[OK]** The `run()` loop correctly handles `escalateToStage` by transitioning the issue to the target stage and breaking to continue the pipeline loop.

### Complexity: PASS

- **[WARNING]** `runPipelineReviewStage` is now ~280 lines (lines 710-994). The auto-fix loop adds ~140 lines of sequential logic. While the logic is straightforward and well-structured, the method is well beyond any reasonable function length limit (50 lines). Consider extracting the auto-fix loop into a private method like `runAutoFixLoop(conn, issue, changeDir, reviewReport, acpOptions, roundState)`.

- **[OK]** `buildAutoFixPrompt` and `buildReVerifyPrompt` are clean, follow the existing `buildReviewerPrompt` pattern exactly.

- **[OK]** `parseVerdict` is a pure 3-line utility function.

- **[OK]** No copy-pasted code. The auto-fix and re-verify round structures are sequential, not duplicated.

### Test Coverage: FAIL

- **[ERROR] Missing test for `plan_session_update` events with correct roundType** (`tests/review-auto-fix.test.ts`): T-003 acceptance criterion "plan_session_update events use correct roundType for auto-fix/re-verify rounds" is not tested. The test file only asserts `plan_round_start` events. Testing `plan_session_update` requires the mock `conn.prompt()` to fire the `onSessionUpdate` callback, which the current mock setup doesn't do. This acceptance criterion is unverified.

- **[OK]** `parseVerdict` tests: 8 cases covering PASS, FAIL, case-insensitive, whitespace, null, multiple verdicts, multi-line content, and FAIL with fix suggestions.

- **[OK]** PASS skips auto-fix: 1 test.

- **[OK]** FAIL enters auto-fix: 5 tests (succeed first, exhaust 2, succeed second, comment added, comment content).

- **[OK]** no-auto-fix checkpoint skips loop: 1 test.

- **[OK]** Auto-fix/re-verify round failure: 2 tests.

- **[OK]** run() loop escalation: 1 test.

- **[OK]** SSE plan_round_start events: 1 test.

### Security: PASS

- No security concerns. All data flows through internal APIs. No user input is interpolated into SQL or shell commands. The `reviewReport` content is read from filesystem but only parsed by regex, never executed.

### Spec Compliance: FAIL

#### T-001: Extend interfaces and add verdict parser — PASS

| Criterion | Result |
|-----------|--------|
| StageResult has optional escalateToStage field of type Stage | PASS |
| WorkflowControllerOptions has optional commentRepo field | PASS |
| WorkflowController constructor stores commentRepo | PASS |
| parseVerdict('## Verdict: PASS') returns 'PASS' | PASS |
| parseVerdict('## Verdict: FAIL') returns 'FAIL' | PASS |
| parseVerdict('no verdict here') returns null | PASS |
| parseVerdict is case-insensitive and handles whitespace variations | PASS |
| Typecheck passes | PASS |

#### T-002: Create auto-fix and re-verify prompt templates — PASS

| Criterion | Result |
|-----------|--------|
| auto-fix.md instructs: read review.md, apply each Fix Suggestion, add tests, run build | PASS |
| re-verify.md instructs: targeted verification, run build, update review.md verdict | PASS |
| buildAutoFixPrompt includes reviewContent | PASS |
| buildReVerifyPrompt includes reviewContent | PASS |
| Both functions follow same pattern as buildReviewerPrompt | PASS |
| File path constants AUTO_FIX_PROMPT_PATH and RE_VERIFY_PATH defined | PASS |
| Typecheck passes | PASS |

#### T-003: Implement auto-fix loop, escalation, and SSE events — FAIL

| Criterion | Result | Notes |
|-----------|--------|-------|
| After R1 self-check, review.md parsed for Verdict | PASS | |
| Verdict PASS returns requiresApproval without auto-fix loop | PASS | |
| Verdict FAIL with no-auto-fix checkpoint skips auto-fix | PASS | |
| Verdict FAIL without checkpoint enters auto-fix loop (max 2) | PASS | |
| Auto-fix round emits plan_round_start roundType 'auto-fix' | PASS | |
| Re-verify round emits plan_round_start roundType 're-verify' | PASS | |
| plan_session_update events use correct roundType | PASS | via onSessionUpdate closure (line 728-741, roundState mutated in-place before each round) |
| Successful auto-fix adds comment via commentRepo | PASS | |
| Failed auto-fix writes no-auto-fix checkpoint + escalateToStage: Build | **FAIL** | Checkpoint uses wrong key format: `upsert(number, 'no-auto-fix', ['exhausted'], null)` instead of spec's `upsert(number, 'review', ['no-auto-fix'], null)`. Read also uses wrong key: `get(number, 'no-auto-fix')` instead of `get(number, 'review')` |
| run() loop handles escalateToStage | PASS | |
| commentRepo passed in agent-runner-service.ts | PASS | |
| conn.close() in all exit paths | PASS | |
| Typecheck passes | PASS | |

#### T-004: Add tests — PASS

| Criterion | Result | Notes |
|-----------|--------|-------|
| parseVerdict tests cover: exact PASS, exact FAIL, case-insensitive, whitespace variations, null on no match | PASS | 8 test cases |
| Test: review stage with Verdict PASS returns requiresApproval without auto-fix | PASS | |
| Test: review stage with Verdict FAIL enters auto-fix loop | PASS | |
| Test: auto-fix loop stops after 2 failed attempts and returns escalateToStage: Stage.Build | PASS | |
| Test: no-auto-fix checkpoint causes Verdict FAIL to skip auto-fix loop | PASS | |
| Test: successful auto-fix (re-verify returns PASS) adds issue comment | PASS | |
| Test: run() loop handles escalateToStage by transitioning to Build stage | PASS | |
| All tests pass | PASS | 18/18 |
| Typecheck passes | PASS | |

#### Spec file: pipeline-model/spec.md — PASS

| Scenario | Result | Notes |
|----------|--------|-------|
| CHECK internal auto-fix succeeds | PASS | Maps to Review stage auto-fix loop; PASS verdict → awaiting-user |
| CHECK internal auto-fix fails, fallback to BUILD | PASS | `escalateToStage: Stage.Build` returned on exhaustion (line 976) |
| CHECK second entry skips auto-fix | PASS | `no-auto-fix` checkpoint checked at line 845, skips loop |
| CHECK pass completes Issue | PASS | Review PASS → `done` stage via existing pipeline loop |

#### Spec file: pipeline-session-events/spec.md — PASS

| Requirement | Scenario | Result |
|-------------|----------|--------|
| Session update events | Review stage uses same mechanism | PASS |
| Session update events | Auto-fix round emits events | PASS |
| Session update events | Re-verify round emits events | PASS |
| Round start events | Auto-fix round starts | PASS |
| Round start events | Re-verify round starts | PASS |

#### Spec file: review-auto-fix/spec.md — FAIL

| Requirement | Scenario | Result | Deviation |
|-------------|----------|--------|-----------|
| Auto-fix loop on Verdict FAIL | Verdict FAIL triggers auto-fix loop | PASS | |
| Auto-fix loop on Verdict FAIL | Verdict PASS skips auto-fix | PASS | |
| Auto-fix loop on Verdict FAIL | Max 2 attempts | PASS | |
| Auto-fix round applies Fix Suggestions | Structured prompt | PASS | |
| Auto-fix round applies Fix Suggestions | Auto-fix round fails | **FAIL** | Returns immediately instead of counting as failed attempt and continuing |
| Re-verify round validates fixes | Confirms all fixes | PASS | |
| Re-verify round validates fixes | Finds remaining issues | PASS | |
| Re-verify round validates fixes | Targets known issues only | PASS | |
| Successful auto-fix records fix history | Comment with fix summary | **FAIL** | Comment includes auto-fix agent output, not original Fix Suggestions from review.md |
| Escalation on persistent failure | Escalate to build stage | PASS | |
| Escalation on persistent failure | Second review skips auto-fix | PASS | |
| Checkpoint no-auto-fix marker | Written on escalation | **FAIL** | Uses `upsert(number, 'no-auto-fix', ['exhausted'], null)` instead of `upsert(number, 'review', ['no-auto-fix'], null)` |
| Checkpoint no-auto-fix marker | Checked before auto-fix loop | **FAIL** | Uses `get(number, 'no-auto-fix')` instead of `get(number, 'review')` and checking for `no-auto-fix` in completedSteps |
| Checkpoint no-auto-fix marker | Cleared on pipeline completion | PASS | `deleteAll` called at done stage (line 422) |

## Fix Suggestions

1. **`workflow-controller.ts:845`**: Change checkpoint read from `this.checkpointRepo?.get(issue.number, 'no-auto-fix')` to `this.checkpointRepo?.get(issue.number, 'review')` and check if `completedSteps` includes `'no-auto-fix'`: `const cp = this.checkpointRepo?.get(issue.number, 'review'); const hasNoAutoFixCheckpoint = cp?.completedSteps?.includes('no-auto-fix') ?? false;`

2. **`workflow-controller.ts:966`**: Change checkpoint write from `this.checkpointRepo?.upsert(issue.number, 'no-auto-fix', ['exhausted'], null)` to `this.checkpointRepo?.upsert(issue.number, 'review', ['no-auto-fix'], null)`.

3. **`workflow-controller.ts:886-895`**: On auto-fix round failure, instead of returning immediately, count it as a failed attempt and `continue` to the next iteration (or fall through to escalation if max reached). Same for re-verify failure at lines 916-925.

4. **`workflow-controller.ts:897,936-939`**: Extract Fix Suggestions from the original `reviewReport` and include them in the comment body. For example: parse the "## Fix Suggestions" section from review.md and include the list in the comment alongside the attempt count.

5. **`tests/review-auto-fix.test.ts`**: Add a test that verifies `plan_session_update` events are emitted with correct `roundType` for auto-fix and re-verify rounds. This requires the mock `conn.prompt()` to invoke the `onSessionUpdate` callback during execution.
