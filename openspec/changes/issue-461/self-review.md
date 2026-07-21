# Self Review: Issue 461

## Findings

### 1. Critical: the universal receipt rule can permanently fence accepted follow-up terminal events

The design routes follow-up terminal outcomes through the shared outbox and removes every record only after a matching receipt (`design.md:34`, `design.md:55-59`, `tasks.json:39-43`). That is incompatible with the current operation-fenced terminal path: first acceptance removes the pending follow-up lease and commits the evidence (`AgentSessionGrain.cs:1074-1106`), while replay after a lost response finds no lease and returns an empty receipt (`AgentSessionGrain.cs:1077-1102`).

The outbox would therefore retain an already-applied terminal record forever and block every later event for that logical Session. Imported `followup-failures.json` records have the same exposure. The plan must define a terminal-specific acknowledgement/reconciliation rule or explicitly change the Server contract; the current design cannot preserve terminal durability and its matching-receipt rule simultaneously.

### 2. High: the synchronous runtime observer cannot provide the promised per-event crash durability

The specs require every generated Workflow event to be durably retained and restart-recoverable (`specs/agent-session-runtime-event-delivery/spec.md:1-5`, `specs/workflow-agent-session-transcript/spec.md:11-15`). The design instead enqueues observed events asynchronously and waits for local persistence only after `runTurn` completes (`design.md:41-47`, `tasks.json:17`).

Today `RuntimeTurnObserver.onEvent` returns `void` and `runTurn` invokes it synchronously without awaiting persistence (`packages/runner/src/runtime/opencode/types.ts:79-82`, `packages/runner/src/runtime/opencode/turn.ts:141-145`). A runner crash between observation and the queued snapshot write loses the event, so the stated restart guarantee is not implementable as written. The plan must either introduce an awaited local-persistence/event-pump boundary or narrow the normative durability point and its tests.

### 3. High: follow-up persistence order is internally contradictory

The design and T-002 require durable local enqueue before invoking `runtime.followup` (`design.md:47`, `tasks.json:37`). The normative scenario says the follow-up executes "without waiting for the input event to be persisted" (`specs/agent-session-runtime-event-delivery/spec.md:72-76`), while the same spec requires every follow-up input to be durably retained (`specs/agent-session-runtime-event-delivery/spec.md:1-5`).

The contract must distinguish local outbox persistence from Server transcript persistence and state what happens when local persistence fails. Otherwise an implementation cannot know whether to delay/reject runtime invocation for local durability or execute immediately and accept a loss window.

### 4. High: runner readiness behavior lacks acceptance coverage at the actual gates

The design makes outbox health a prerequisite for claiming work and accepting follow-up commands (`design.md:79-83`), but T-001 asks only for outbox-level store-failure readiness tests (`tasks.json:16`, `tasks.json:20`). Current claim readiness checks only `OpenCodeRuntime.ready()`, and follow-up registration is independent of outbox health (`packages/runner/src/runtime/host.ts:372-440`, `packages/runner/src/server/runner-signalr.ts:139-156`).

The tasks need explicit host and SignalR-handler acceptance tests proving that initial corruption and later persistence failure stop new claims/commands while existing pending delivery and already-running work retain the specified behavior. Without those tests, the new readiness requirement can be omitted while all listed outbox unit cases pass.

### 5. Medium: the concrete durability and migration adapter is deliberately left untested

The plan relies on atomic rename, owner-only permissions, legacy-file import, and crash-safe migration in the physical store (`design.md:43`, `design.md:90-92`, `tasks.json:11`, `tasks.json:41-42`), but also states that no test will instantiate that adapter (`design.md:99-103`, `tasks.json:12`). In-memory outbox tests cannot verify serialization, file mode, temporary-file ordering, or legacy marker behavior.

The design needs a fake file-I/O boundary beneath the concrete snapshot/import adapter, or equivalent pure serialization and recorded-operation tests, so the repository's no-real-filesystem rule is preserved without leaving the persistence mechanism unverified.

## Review Summary

- The proposal covers the issue's three acceptance criteria and preserves the stated no-server-deduplication and no-cross-runner-transfer boundaries.
- The two-task dependency graph is valid and acyclic: T-002 consumes T-001 and references a strictly earlier priority.
- The blocking defects are in delivery semantics and implementability, not task graph syntax. The follow-up terminal path can deadlock after an ambiguous success, and the planned observer boundary cannot guarantee all generated events survive a crash.

## Verdict

The plan requires correction before implementation.

<promise>FAIL</promise>
