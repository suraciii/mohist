## Context

`AgentSession.handleSessionUpdate()` is the first shared point where ACP session notifications are observed, persisted, bridged to plan/review live events, and converted into `coder_tool_call` SSE events. Today it special-cases only `tool_call`, reads identity only from nested `update.toolCall.toolName`, and then computes a second observer-facing id with a separate `ToolCallIdGenerator`. This lets ACP payloads that use top-level fields, `name`, or `tool_call_update` lose the tool name and causes one real tool lifecycle to appear as separate started/completed records.

Existing transcript assembly already has heuristics for merging historical tool lifecycle fragments, but that is too late for live SSE events and persisted raw event quality. The fix should pull normalization down into the runtime boundary so all downstream consumers see the same stable tool identity.

## Goals / Non-Goals

**Goals:**

- Normalize both `tool_call` and `tool_call_update` notifications before `onSessionEvent`, `onRawNotification`, and `onToolCall` observers run.
- Preserve the best available tool name from nested and top-level ACP fields, including `toolName`, `name`, title, payload shape, and metadata.
- Use one stable `toolCallId` for persistence and live `coder_tool_call` emission, preferring provider ids and falling back to the existing session/name/counter correlation behavior.
- Keep the observer and SSE payload contracts unchanged while improving their field values.
- Add focused regression tests for split top-level/nested id and name shapes.

**Non-Goals:**

- Redesign transcript assembly, frontend rendering, or SSE event schemas.
- Migrate or rewrite historical session logs that already contain blank names or split ids.
- Change ACP SDK types or introduce a dependency on provider-specific notification classes.

## Decisions

### D1: Normalize tool notifications once at the AgentSession boundary

Add a small private normalization helper in `agent-session.ts` that accepts the mutable session update object and the event type. For `tool_call` and `tool_call_update`, it will ensure a nested `toolCall` object exists, copy canonical `toolName`, `toolCallId`, status, title, input, output, and metadata into that object when available, and return the normalized lifecycle data used to emit `ToolCallEvent`.

This keeps ACP shape handling close to the protocol boundary and prevents downstream observers from each needing to know every supported notification variant.

**Alternatives considered:** Leave raw logs untouched and rely on `session-transcript-service` heuristics. This helps historical replay but does not fix live SSE duplication or plan/review raw event bridges. Add normalization in `WorkflowSessionObserver`. This would miss `onRawNotification` bridges and would duplicate runtime parsing logic outside the session boundary.

### D2: Prefer provider identity, then synthesize once

Tool call identity resolution will prefer existing ids in this order: nested `toolCall.toolCallId`, nested `toolCall.id`, nested `toolCall.callId`, top-level `toolCallId`, top-level `id`, top-level `callId`. If no provider id exists, the helper will use the workflow observer's `nextToolCallId(acpSessionId, toolName, state)` when available, with the existing `ToolCallIdGenerator` as the local fallback.

The generated id will be written back to the canonical nested `toolCall.toolCallId` and reused for `onToolCall`. This removes the current split where persistence gets one synthetic id and SSE gets another.

**Alternatives considered:** Always replace ids with Mohist-generated ids. That would discard provider correlation data and make logs harder to audit. Keep separate ids for raw logs and live SSE. That preserves raw input more literally but is the root of the split-lifecycle bug.

### D3: Use deterministic best-name inference, but keep it conservative

Name resolution will prefer explicit fields before inference: nested `toolName`, nested `name`, top-level `toolName`, top-level `name`, output/input metadata `toolName` or `name`, then known payload-shape inference such as `patchText` to `apply_patch`, command/script to `bash`, file path to `read`, pattern/query to search-like tools, and todos to `todowrite`. Only when none of these are available will it use `unknown`.

The inferred name is written to `toolCall.toolName` so persisted events, plan session bridges, transcript replay, and live SSE all see the same normalized name.

**Alternatives considered:** Reuse the private transcript `inferNormalizedToolName()` directly. It is currently embedded in replay assembly and returns transcript-specific metadata, so pulling it into runtime would couple two layers. A future cleanup can extract shared normalization if more consumers need it, but for this fix a small runtime-local helper keeps the change contained.

### D4: Emit update events through the same ToolCallEvent path

`tool_call_update` with terminal status should create an `onToolCall` event with `state: 'completed'` or a failed terminal state mapped to the closest existing `ToolCallEvent.state` contract. Since `ToolCallEvent.state` currently supports only `started` and `completed`, failed/cancelled statuses will retain their original `status` field and use `completed` only when the status is explicitly `completed`; otherwise they remain `started` unless a contract change is introduced in specs.

**Alternatives considered:** Extend `ToolCallEvent.state` to include `failed` and `cancelled`. That would be cleaner semantically, but it changes a wider event contract and is not required to fix tool name/id loss.

## Risks / Trade-offs

- [Risk] Mutating raw ACP updates makes persisted logs less byte-for-byte raw. → Mitigation: only add/copy canonical identity fields; preserve original fields and payloads instead of deleting or renaming them.
- [Risk] Name inference from payload shape can choose a generic name such as `search` when a provider-specific name is unavailable. → Mitigation: prefer explicit provider fields first and use inference only to avoid blank/unknown live UI entries.
- [Risk] Completed updates without any prior start and without provider id still require synthetic correlation. → Mitigation: keep the existing observer `nextToolCallId` queue behavior so no-id completed updates can match the oldest in-flight tool with the same normalized name.
- [Risk] Plan/review stage live events consume full raw updates through `onRawNotification`. → Mitigation: normalize before both `onSessionEvent` and `onRawNotification` so bridge payloads and persisted logs converge.

## Migration Plan

1. Add runtime-local helpers in `agent-session.ts` for object extraction, best-name inference, id extraction, status-to-state mapping, and notification normalization.
2. Replace the two existing `tool_call` handling blocks in `handleSessionUpdate()` with one normalization call before observer dispatch and one `onToolCall` emission using the normalized result.
3. Cover regressions with backend tests using ACP-like updates where name/id are split between top-level and nested payloads and where completion arrives as `tool_call_update`.
4. Deploy as a code-only change. Rollback is reverting the runtime helper and tests; no data migration or config change is needed.

## Open Questions

- Should a later spec expand `ToolCallEvent.state` and `coder_tool_call.state` beyond `started` and `completed` so failed/cancelled tool updates can be represented without overloading `status`?
