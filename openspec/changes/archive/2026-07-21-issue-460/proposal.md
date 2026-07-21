## Why

AgentSession runtime events can be intentionally discarded because they target a stale physical runtime binding or use an unsupported transcript event type, but both paths are currently silent. Operators need warning-level evidence to explain missing conversation content without weakening the binding guard or event-type allowlist.

## What Changes

- Emit a warning when an AgentSession runtime-event batch is discarded because its reported runtime session identity is missing or differs from the session's current binding.
- Include the logical session identity, current expected runtime session identity, reported runtime session identity, and discarded event count in binding-mismatch warnings.
- Emit a warning when runtime events are discarded because their type is outside the transcript persistence allowlist, including the logical session identity, event type, and discarded count for that type.
- Preserve all existing discard behavior: rejected batches and unsupported event types remain unpersisted and unpublished, with no retry, buffering, metric, or alerting behavior added.

## Capabilities

- `agent-session-event-discard-observability`: Warning-level diagnostics for runtime events discarded by AgentSession physical-binding validation or transcript event-type filtering, while preserving the existing discard and persistence semantics.

## Impact

- **Server Session domain** (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs` and `Sessions/Services/TranscriptAccumulator.cs`): the existing discard boundaries gain structured warning logs; accepted event processing is unchanged.
- **Server tests** (`packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/`): focused coverage verifies warning level and diagnostic fields alongside unchanged state, transcript persistence, and realtime publication results.
- **APIs and persistence**: no endpoint, request/response, database schema, event allowlist, or persisted data change.
- **Dependencies and operations**: no new dependency, metric collection, retry queue, or alert integration; existing server logging is the only affected operational surface.
