## Findings

### P1: AgentJob acknowledges a terminal delivery before its transcript fact is durable

`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:830` only queues the terminal `session.activity` for deferred persistence and returns success at line 834. `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:1159` then clears `PendingSessionClose` immediately. If the Session crashes or transcript persistence fails before the timer flushes, the AgentJob has already discarded the only retry obligation and the terminal fact is lost. This violates the issue requirement to retain terminal persistence and delivery-idempotency. Persist the terminal activity before acknowledging the close, retain the pending delivery on a failed first write, and add coverage for failure followed by retry.

### P2: Routed Issue events lose the terminal decision timestamp

`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:1144` supplies the authoritative `recordedAt`, and `packages/server/src/Mohist.Server/AgentOps/Services/IssueEventFeedAssembler.cs:145` projects that field, but `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:818` rebuilds the terminal activity payload without `recordedAt`. Consequently every routed Issue event exposes `recordedAt: null` instead of the terminal decision time, dropping expected terminal context. Persist `command.RecordedAt` as `recordedAt` and assert it in the routed AgentJob-to-Issue-feed spec.

<promise>FAIL</promise>
