# Self Review Report

## Result: PASS

## Repaired Items

(none)

## Blocking Items

(none)

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: alignment
  Evidence: The issue's Product Shape and Acceptance Criteria explicitly say "三张事件真相表" and list `WorkflowRunEvents` + `IssueEvents` + `EpicEvents`. The proposal, design, and spec expand this to four tables by including `AgentSessionEvents` — the proposal notes "(and `AgentSessionEvents`)", the spec states four tables as definitive ("from all event truth tables (`WorkflowRunEvents`, `IssueEvents`, `EpicEvents`, `AgentSessionEvents`)"), and the design acknowledges the expansion as Open Question 2 with a default of "fan out all (simplest; handlers' Filter can reject)." Verified that the existing `IEventStore.ListUndeliveredAsync` (`EventStore.cs:220-250`) already queries all four tables via `UNION ALL`, so reusing it naturally includes `AgentSessionEvents`. The design's Open Question 2 and risk section (line 203) both flag that high-frequency `AgentSessionEvents` lifecycle types (`UsageRecorded`, `ContextHealthUpdated`) will hit catch-all handlers (`AgentSubscriptionDispatchHandler` `Type="*"`, `EventBridge` `Type="com.mohist.*"`) on every tick. No repair applied: the design's default is reasonable (reuse existing query, Filter can reject), the open question is documented, and the spec reflecting the design's default is correct spec behavior — open questions belong in the design, not the spec. Noting as follow-up so the implementer is aware the issue's "三张" scope is expanded to four, and that Open Question 2 should be resolved (confirm or add an origin filter) if AgentSession lifecycle events cause handler noise during testing.
  SuggestedAction: During T-002 implementation, verify whether fanning out `AgentSessionEvents` to catch-all handlers causes excessive noise. If so, add an origin/type filter to the dispatcher's undelivered query or handler matching, and resolve design Open Question 2.
  Status: follow-up

## Verification Summary

### Alignment
- Proposal addresses the actual issue: a self-driving dispatcher grain to restore at-least-once delivery after #361 severed synchronous fan-out. All five Acceptance Criteria are covered:
  1. Cluster singleton grain + single undelivered query → `event-dispatcher` spec, D1+D2
  2. Per-event-type fan-out with retry + dead letter on exhaustion → `event-dispatcher` spec D3, `dead-letter-queue` spec D4
  3. Per-row marking + per-stream FIFO → `event-dispatcher` spec, D2
  4. At-least-once test coverage (crash → redeliver → idempotent) → `event-dispatcher` spec scenarios, T-002 acceptance criteria
  5. Poison → dead letter, queryable, manually retryable → `dead-letter-queue` spec, T-001 acceptance criteria
- Non-Goals respected: no parallel sharding, no UI push changes, no broker.

### Completeness
- All requirements covered by specs: `event-dispatcher` (9 requirements, 16 scenarios) + `dead-letter-queue` (4 requirements, 7 scenarios).
- All specs have tasks: `dead-letter-queue` → T-001, `event-dispatcher` → T-002 + T-003.
- Edge cases: no-match rows marked delivered, crash-after-delivery-before-mark, each failing handler gets own dead letter entry, dead letter preserved after manual retry, in-memory retry state lost on crash — all covered.

### Consistency
- Specs align with proposal Capabilities (`event-dispatcher`, `dead-letter-queue`).
- Tasks reference correct spec files (T-001 → dead-letter-queue, T-002 → event-dispatcher, T-003 → event-dispatcher#best-effort-immediate-trigger-from-producers).
- Design decisions D1–D6 map to spec requirements.
- Naming consistent across all artifacts: `DeadLetterRow`, `IDeadLetterStore`, `EventDispatcherOptions`, `IEventDispatcherGrain`, `EventDispatcherGrain`.

### Feasibility
- Task granularity appropriate: T-001 (dead letter persistence slice), T-002 (dispatcher grain slice), T-003 (producer wiring slice). No tasks too fine — no standalone "define interface", "register DI", "create file", or "add tests" tasks. Tests embedded in each task's acceptance criteria.
- Dependencies available: T-002 needs `IDeadLetterStore` (T-001), T-003 needs `IEventDispatcherGrain.DispatchNowAsync` (T-002).
- No circular dependencies.

### Dependency Completeness
- T-001: `dependsOn: []`, priority 1 — first task, no deps needed.
- T-002: `dependsOn: ["T-001"]`, priority 2 — T-001 exists, priority 1 < 2.
- T-003: `dependsOn: ["T-002"]`, priority 3 — T-002 exists, priority 2 < 3.
- No cycles. All `dependsOn` entries point to existing IDs with lower priority.

### Codebase Claims Verified
- `IEventStore.ListUndeliveredAsync` (`EventStore.cs:220-250`): single `UNION ALL` across four tables, `WHERE "DispatchedAt" IS NULL`, `ORDER BY "Source", "Id"`, `LIMIT @limit`. Confirmed.
- `IEventStore.MarkDispatchedAsync` (`EventStore.cs:180-218`): source-prefix routing to stamp `DispatchedAt`. Confirmed.
- `IEnumerable<Subscription>` + `DispatchDelegate` (`CloudEventBusServiceCollectionExtensions.cs:52-63`): nine handlers registered, `DispatchDelegate` calls `Filter` + `HandleAsync`. Confirmed.
- `CloudEventTypeMatcher.Matches` (`CloudEventTypeMatcher.cs:28-47`): exact / `|` / `*` / `prefix.*`. Confirmed.
- `InMemoryEventBus.PublishAsync` (`InMemoryEventBus.cs:38`): write-only, delegates to `IEventStore.AppendAsync`. Confirmed.
- Orleans ADO.NET reminder service (`MohistSiloRegistration.cs:33-37`): configured, SQLite. Confirmed.
- `TimeProvider` registered as singleton (`MohistSiloRegistration.cs:64`). Confirmed.
- `DeadLetters` ghost: present in three migration `.Designer.cs` snapshots, absent from current `MohistDbContextModelSnapshot.cs`. No `DeadLetterRow` source class or `DbSet` exists. Confirmed.
- `RunnerGrain.ReceiveReminder` (`RunnerGrain.cs:122-129`): documented no-op. Confirmed.
- `GrainTestConfig` (`GrainTestConfig.cs:211-212`): `UseInMemoryReminderService` + `ControllableReminderTable`. Confirmed.
- Nine handler classes confirmed (including `EpicCancelledReconcileHandler` in `EpicAutoDoneHandler.cs`).
- `AgentSubscriptionDispatchHandler` "future replay" comment at lines 89-92. Confirmed.
- `RunnerRegistryKeys.Global = "__global__"` (`IRunnerRegistryGrain.cs:32-34`). Confirmed.
- `RecordingEventStore` (`RecordingEventStore.cs`): `MarkDispatchedAsync` is a no-op (line 81), `ListUndeliveredAsync` returns empty (lines 83-84) — T-002 plans to extend it. Confirmed.
- `Events/` directory has `Hosting/`, `Hub/`, `Subscriptions/` — adding `Grains/` is consistent. Confirmed.
- `Infrastructure/Data/Events/` contains event row models — placing `DeadLetterRow.cs` there is consistent. Confirmed.
- `architecture.md:90`: "Authority grains: no `[Reentrant]`." Confirmed.
- `design/eventbus.md:77`: "Future: N dispatchers keyed by `hash(Source) % N`." Confirmed.
- `design/testing.md:108`: planned ban of `Task.Delay` / `Thread.Sleep`. Confirmed — design's cross-tick retry rationale aligns.

<promise>PASS</promise>
