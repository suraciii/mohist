## Verdict

PASS with warnings.

## Findings

1. Warning: `packages/cli/src/agent-runtime/agent-session.ts:228` `normalizeToolCallNotification()` is about 74 lines long, which exceeds the stated review target of functions under 50 lines. Suggested change: extract one or two small helpers for top-level field copying and id/name resolution to keep the normalization path easier to audit.

## Correctness

- PASS: normalization runs before downstream observers. `handleSessionUpdate()` calls `normalizeToolCallNotification()` before `onToolCall`, `onSessionEvent`, and `onRawNotification` dispatch in `packages/cli/src/agent-runtime/agent-session.ts:779-819`.
- PASS: provider ids are preserved via `extractProviderId()` and written back to `toolCall.toolCallId` in `packages/cli/src/agent-runtime/agent-session.ts:211-218,254-270`.
- PASS: top-level `name`/`toolName` recovery is implemented in `inferToolName()` in `packages/cli/src/agent-runtime/agent-session.ts:137-170`.
- PASS: no correctness defect found in the tested provider-id and no-id completion correlation paths. Regression coverage exists in `packages/cli/tests/agent-session-boundary.test.ts:669-734`.

## Complexity

- Warning: `normalizeToolCallNotification()` exceeds the preferred function size target. I did not find a cyclomatic-complexity-driven bug, but the function is denser than ideal.

## Test Coverage

- PASS: targeted regressions cover split top-level/nested name and id shapes, `tool_call_update`, raw bridge normalization, provider-id preference, and transcript replay behavior.
- Evidence:
  - `packages/cli/tests/agent-session-boundary.test.ts:320-395`
  - `packages/cli/tests/agent-session-boundary.test.ts:398-518`
  - `packages/cli/tests/agent-session-boundary.test.ts:521-904`
  - `packages/cli/tests/agent-session-boundary.test.ts:1166-1319`
  - `packages/cli/tests/session-transcript-service.test.ts:252-320`
- Verification:
  - `npm test -- agent-session-boundary.test.ts session-transcript-service.test.ts` passed.
  - `npm run typecheck` passed.

## Security

- PASS: this change is internal normalization of ACP session notifications. I found no new injection surface, secret exposure, or unsafe command/data handling.

## Spec Compliance

### REQ-AR-214 ACP tool notifications are normalized before observer dispatch

- PASS: top-level tool identity is preserved.
  - Evidence: `inferToolName()` reads nested and top-level `toolName`/`name` fields in `packages/cli/src/agent-runtime/agent-session.ts:137-145`.
  - Evidence: canonical nested `toolCall.toolCallId` is assigned in `packages/cli/src/agent-runtime/agent-session.ts:254-270`.
  - Test: `packages/cli/tests/agent-session-boundary.test.ts:320-349,776-812`.
- PASS: nested and provider ids are preferred.
  - Evidence: `extractProviderId()` prefers nested `toolCall.toolCallId`, `id`, `callId`, then top-level fields in `packages/cli/src/agent-runtime/agent-session.ts:211-218`.
  - Test: `packages/cli/tests/agent-session-boundary.test.ts:857-903`.
- PASS: missing id is synthesized once and reused.
  - Evidence: fallback uses workflow observer or local generator once, then writes canonical `toolCall.toolCallId` before dispatch in `packages/cli/src/agent-runtime/agent-session.ts:259-270`.
  - Test: `packages/cli/tests/agent-session-boundary.test.ts:521-570`.
- PASS: `tool_call_update` is normalized and keeps output/metadata available.
  - Evidence: update normalization shares the same path in `packages/cli/src/agent-runtime/agent-session.ts:236-252,779-819`.
  - Test: `packages/cli/tests/agent-session-boundary.test.ts:398-518`.

### REQ-CST-214 Tool lifecycle identity remains stable across live and replayed coder sessions

- PASS: live coder tool events carry recovered names.
  - Evidence: normalized `ToolCallEvent.toolName` is emitted from `packages/cli/src/agent-runtime/agent-session.ts:790-799` and bridged unchanged in `packages/cli/src/services/session-observers.ts:120-136`.
  - Test: `packages/cli/tests/agent-session-boundary.test.ts:320-349,572-618,776-812`.
- PASS: start and update share one id across live and replayed views.
  - Evidence: identical normalized `toolCallId` is used for observer events and persisted session events in `packages/cli/src/agent-runtime/agent-session.ts:270,790-799,807-819` and `packages/cli/src/services/session-observers.ts:139-156`.
  - Test: `packages/cli/tests/agent-session-boundary.test.ts:521-570,620-734`.
  - Replay test: `packages/cli/tests/session-transcript-service.test.ts:272-310`.
- PASS: raw payload details remain available.
  - Evidence: normalization copies `title`, `input`, `output`, and metadata into nested `toolCall` without deleting original fields in `packages/cli/src/agent-runtime/agent-session.ts:243-252,291-300`.
  - Test: `packages/cli/tests/agent-session-boundary.test.ts:398-518`.

### REQ-PSE-214 Pipeline session tool updates expose normalized identity

- PASS: raw notification bridges receive normalized tool updates.
  - Evidence: plan/check bridges emit `notification.update` from `onRawNotification` in `packages/cli/src/workflow/plan-stage-runner.ts:274-284,315-323` and `packages/cli/src/workflow/check-stage-runner.ts:183-192`.
  - Evidence: normalization happens before `onRawNotification` dispatch in `packages/cli/src/agent-runtime/agent-session.ts:779-819`.
  - Test: `packages/cli/tests/agent-session-boundary.test.ts:1166-1211`.
- PASS: live and persisted tool identity agree.
  - Test: `packages/cli/tests/agent-session-boundary.test.ts:1213-1277` verifies raw bridge ids equal `onToolCall` ids.
  - Persistence path: `packages/cli/src/services/session-observers.ts:139-156` stores the same normalized update payload.

## Suggested Fixes

- `packages/cli/src/agent-runtime/agent-session.ts:228`
  - Suggested change: split `normalizeToolCallNotification()` into small helpers such as `copyCanonicalTopLevelToolFields()` and `resolveCanonicalToolIdentity()` to stay within the preferred function-size target and reduce future review risk.

<promise>PASS</promise>
