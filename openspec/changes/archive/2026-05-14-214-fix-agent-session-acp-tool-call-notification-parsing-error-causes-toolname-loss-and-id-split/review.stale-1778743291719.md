## Findings

1. Error: `packages/cli/src/agent-runtime/agent-session.ts:187`
The new normalization path writes `outputObject.metadata = topLevelMetadata` whenever top-level `metadata` is present, but `outputObject` is derived from `toolCall.output ?? update.output` and is only cast to `Record<string, unknown>`. If a provider sends `tool_call_update` with `output: 'patch applied'` and top-level `metadata`, this assignment targets a primitive string and throws in strict mode. That breaks `REQ-AR-214` "Tool call updates are normalized" for a valid ACP payload shape and prevents observers/log persistence from seeing the completed update. Suggested fix: guard the assignment with an object check, e.g. `const outputObject = getObject(toolCall.output ?? topLevelOutput)` or inline `typeof outputObject === 'object' && outputObject !== null` before assigning `.metadata`.

## Spec Compliance

### REQ-AR-214 ACP tool notifications are normalized before observer dispatch

- Top-level tool identity is preserved: PASS
Evidence: `packages/cli/src/agent-runtime/agent-session.ts:189-216` normalizes `toolCall.toolCallId` and `toolCall.toolName` before observer dispatch, using top-level fields via `extractProviderId()` (`152-159`) and `inferToolName()` (`124-149`). Regression coverage exists in `packages/cli/tests/agent-session-boundary.test.ts:320-349`, `514-559`, and `611-645`.
- Nested and provider ids are preferred: PASS
Evidence: `extractProviderId()` prefers nested ids before top-level ids in `packages/cli/src/agent-runtime/agent-session.ts:152-159`, and `normalizeToolCallNotification()` reuses that provider id directly at `196-205`. Covered by `packages/cli/tests/agent-session-boundary.test.ts:649-679` and `463-512`.
- Missing id is synthesized once: PASS
Evidence: when no provider id exists, `normalizeToolCallNotification()` uses `wfObserver.nextToolCallId()` or `ToolCallIdGenerator.nextToolCallId()` and writes the resulting id back to `toolCall.toolCallId` before both `onToolCall` and `onSessionEvent`/`onRawNotification` paths (`packages/cli/src/agent-runtime/agent-session.ts:199-205`, `709-749`).
- Tool call updates are normalized: FAIL
Evidence: completed update normalization now copies top-level `status`, `title`, `input`, `output`, and `metadata` into `toolCall` at `packages/cli/src/agent-runtime/agent-session.ts:180-187`, and test coverage exists for object output in `packages/cli/tests/agent-session-boundary.test.ts:398-460`. However line `187` can throw when `output` is a primitive string plus top-level `metadata`, so normalization is not safe for all valid payloads.

### REQ-CST-214 Tool lifecycle identity remains stable across live and replayed coder sessions

- Live coder tool event carries recovered name: PASS
Evidence: `handleSessionUpdate()` emits `ToolCallEvent` from normalized data in `packages/cli/src/agent-runtime/agent-session.ts:709-729`, and `WorkflowSessionObserver.onToolCall()` forwards that name unchanged to `coder_tool_call` in `packages/cli/src/services/session-observers.ts:120-136`. Covered by `packages/cli/tests/agent-session-boundary.test.ts:320-349` and `611-645`.
- Start and update share one id: PASS
Evidence: the same normalized `toolCallId` is used for `ToolCallEvent` (`packages/cli/src/agent-runtime/agent-session.ts:720-729`) and persisted session events (`737-749`). Transcript replay consumes `toolCall.toolCallId` from persisted data via `parseToolCallStart()` / `parseToolCallUpdate()` in `packages/cli/src/services/session-transcript-service.ts:338-374`. Covered by `packages/cli/tests/agent-session-boundary.test.ts:463-512` and `562-609`.
- Raw payload details remain available: PASS with risk
Evidence: top-level completion fields are copied into `toolCall` at `packages/cli/src/agent-runtime/agent-session.ts:183-187`, then transcript replay reads nested `title`, `input`, `output`, and `metadata` from `packages/cli/src/services/session-transcript-service.ts:347-354` and `366-373`. Covered for object output by `packages/cli/tests/agent-session-boundary.test.ts:398-460`.
Risk: the primitive-output bug above can still prevent these details from being persisted.

### REQ-PSE-214 Pipeline session tool updates expose normalized identity

- Raw notification bridge receives normalized tool update: PASS
Evidence: normalization runs before `onRawNotification` in `packages/cli/src/agent-runtime/agent-session.ts:709-749`, so plan/check bridges receive the mutated canonical `toolCall` fields. Covered by `packages/cli/tests/agent-session-boundary.test.ts:351-395` and `514-559`.
- Live and persisted tool identity agree: PASS
Evidence: the same normalized payload is sent to both observer paths in `packages/cli/src/agent-runtime/agent-session.ts:720-749`, and replay reads the same canonical nested ids from persisted logs in `packages/cli/src/services/session-transcript-service.ts:338-374`.

## Complexity

- PASS
Evidence: the touched helper `normalizeToolCallNotification()` remains 66 lines and straightforward, though it is near the preferred size ceiling.

## Test Coverage

- PASS with gap
Evidence: added regression coverage in `packages/cli/tests/agent-session-boundary.test.ts:398-460`; `npm test -- agent-session-boundary.test.ts` passed.
Gap: no test covers `tool_call_update` with primitive `output` plus top-level `metadata`, which is the broken edge case above.

## Security

- PASS
Evidence: changes only normalize in-memory ACP payload fields and do not introduce command execution, deserialization, or secret-handling risks.

## Verification

- `npm test -- agent-session-boundary.test.ts`: PASS
- `npm run build`: PASS

## Overall

- FAIL due to the error-level normalization bug in `packages/cli/src/agent-runtime/agent-session.ts:187`.

<promise>FAIL</promise>
