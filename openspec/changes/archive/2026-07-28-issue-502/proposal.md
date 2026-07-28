## Why

`design/eventbus.md` is marked converged but contradicts the running event dispatcher, so readers cannot safely use it as the system contract. Two event producers also miss the best-effort wake-up used by the other producers, creating avoidable and source-dependent delivery latency while blocked sources remain invisible to operators.

## What Changes

- Align `design/eventbus.md` with the dispatcher actually in use: handwritten, option-configured exponential backoff; best-effort post-commit producer pokes; reminder-based correctness; and process-local retry state that resets after a process restart.
- Make every persisted event source, including Epic and AgentJob, issue the same best-effort post-commit dispatcher poke as WorkflowRun, Issue, and AgentSession.
- Expose the number of sources blocked by pending handler retries so operators can identify FIFO delivery stalls.
- Preserve event meaning, per-source FIFO order, at-least-once delivery, retry limits, and the existing DLQ behavior. Retry-attempt persistence and DLQ replay surfaces remain out of scope.

## Capabilities
- `event-dispatcher`: Durable event delivery behavior, including uniform producer wake-ups, reminder-backed recovery, retry/backoff and dead-letter semantics, per-source FIFO blocking visibility, and the corresponding design contract.

## Impact

- Server event-dispatch infrastructure: `EventDispatcherService`, its OpenTelemetry metric catalog/registration, and `EventDispatcherPoke` call sites in Epic and AgentJob persistence paths.
- `design/eventbus.md` and event-dispatcher server specs, including immediate-trigger and blocked-source behavior.
- No public API, dependency, event schema, ordering guarantee, or delivery-semantics breaking change.
