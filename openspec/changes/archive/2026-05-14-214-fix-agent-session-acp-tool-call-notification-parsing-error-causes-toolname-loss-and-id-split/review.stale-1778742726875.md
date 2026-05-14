## Findings

1. ERROR: Split top-level completion payload fields are still lost during normalization, so `tool_call_update` does not satisfy the requirement that completed output and metadata remain available to observers and persisted logs.
File: `packages/cli/src/agent-runtime/agent-session.ts:177-220`
Evidence: `normalizeToolCallNotification()` reads `status`, `title`, `input`, and `output` only from `toolCall`, except for defaulting update status to `'completed'`. When an ACP update arrives in the allowed split form like `{ sessionUpdate: 'tool_call_update', id, status: 'completed', output, metadata }`, the helper creates `update.toolCall` and writes only `toolCall.toolCallId`, `toolCall.toolName`, and maybe `toolCall.status`. It never copies top-level `title`, `input`, `output`, or `metadata` into `toolCall`, and the returned normalized event uses `toolCall.input` / `toolCall.output`. As a result, `onToolCall` emits `rawOutput: undefined` and raw notification/persisted observers do not see canonical nested completion details. This violates `specs/agent-runtime/spec.md:22-25` and `specs/coder-session-tracking/spec.md:16-18`.
Suggested fix: In `packages/cli/src/agent-runtime/agent-session.ts` around `normalizeToolCallNotification()`, copy canonical top-level tool fields into `toolCall` when nested fields are absent: `status`, `title`, `input`, `output`, and `metadata`/`output.metadata`. Then build `NormalizedToolCall` from the canonical nested object so `onToolCall`, `onSessionEvent`, and `onRawNotification` all receive the same completed payload.

## Spec Compliance

### REQ-AR-214 ACP tool notifications are normalized before observer dispatch

- PASS: Top-level tool identity is preserved.
Evidence: `packages/cli/src/agent-runtime/agent-session.ts:175-207` extracts top-level `toolName`/`name` and `toolCallId`/`id`/`callId`, writes canonical `toolCall.toolCallId` and `toolCall.toolName`. Covered by `packages/cli/tests/agent-session-boundary.test.ts:320-349`, `449-495`, `546-580`.
- PASS: Nested and provider ids are preferred.
Evidence: `packages/cli/src/agent-runtime/agent-session.ts:152-159,182-188` prefers nested/top-level provider ids before synthetic generation. Covered by `packages/cli/tests/agent-session-boundary.test.ts:497-544,584-630`.
- PASS: Missing id is synthesized once for observer lifecycle.
Evidence: `packages/cli/src/agent-runtime/agent-session.ts:185-188,196-207` uses `nextToolCallId()` before dispatch and writes the same canonical id back to `toolCall.toolCallId` and emitted `ToolCallEvent.toolCallId`. The generator correlation logic is in `packages/cli/src/agent-runtime/agent-session.ts:77-112` and workflow observer fallback is in `packages/cli/src/services/session-observers.ts:220-238`.
- FAIL: Tool call updates are normalized with completed output and metadata retained.
Evidence: `packages/cli/src/agent-runtime/agent-session.ts:177-220` does not canonicalize top-level `output`, `input`, `title`, or `metadata` into `toolCall`, and returns `outputMetadata` only from `toolCall.output.metadata`. No test asserts retention of split top-level completion payload details.

### REQ-CST-214 Tool lifecycle identity remains stable across live and replayed coder sessions

- PASS: Live coder tool event carries recovered name.
Evidence: `packages/cli/src/agent-runtime/agent-session.ts:176-207,711-720` plus `packages/cli/tests/agent-session-boundary.test.ts:320-349,546-580`.
- PASS: Start and update share one id.
Evidence: `packages/cli/src/agent-runtime/agent-session.ts:175-188,196-220` plus `packages/cli/tests/agent-session-boundary.test.ts:398-447,497-544,584-630`.
- FAIL: Raw payload details remain available.
Evidence: same gap as above in `packages/cli/src/agent-runtime/agent-session.ts:177-220`; replayed transcript assembly can only preserve what was persisted, and no regression test covers split top-level completion `output`/`metadata` retention.

### REQ-PSE-214 Pipeline session tool updates expose normalized identity

- PASS: Raw notification bridge receives normalized tool update.
Evidence: normalization runs before raw notification dispatch in `packages/cli/src/agent-runtime/agent-session.ts:700-739`. Covered by `packages/cli/tests/agent-session-boundary.test.ts:893-1005`.
- PASS: Live and persisted tool identity agree.
Evidence: canonical `toolCall.toolCallId` is written before both `onToolCall` and `onSessionEvent`/`onRawNotification` in `packages/cli/src/agent-runtime/agent-session.ts:700-739`, and stream persistence happens from normalized data in `packages/cli/src/services/session-observers.ts:139-156`.

## Review Dimensions

- Correctness: FAIL due to dropped split top-level completion payload fields on `tool_call_update`.
- Complexity: PASS. New helper logic is small and localized; `normalizeToolCallNotification()` is about 58 lines, slightly above the requested under-50 guideline but still straightforward and low-branch.
- Test Coverage: WARNING. Focused runtime tests are strong for name/id recovery, but they miss the failing case where completion `output`/`metadata` exists only at the top level. `npx vitest run packages/cli/tests/agent-session-boundary.test.ts` passed.
- Security: PASS. No new injection or secret-handling risk observed; the change only normalizes in-memory ACP payloads.
- Build/Typecheck: PASS. `npm run build` passed in `packages/cli`.

## Overall

Overall result: FAIL. The main identity bug is fixed, but one acceptance criterion remains unmet because split top-level completion details are not preserved through normalization.

<promise>FAIL</promise>
