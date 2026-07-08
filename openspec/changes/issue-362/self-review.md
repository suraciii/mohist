# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `proposal.md` line 32 (Impact) claimed "no new external dependencies (Polly already referenced)". Verified against the tree: `Polly` appears nowhere in `packages/server` source, `Directory.Packages.props`, or any csproj. `design.md` D6 already flags this ("The proposal asserts 'Polly already referenced.' **It is not**") and decides a hand-rolled per-handler attempt cap; `tasks.json` T-002 notes repeat "do NOT add Polly". The stale proposal line was the only artifact diverging. Changed the line to state retry is hand-rolled with a fixed cap and to reference D6, aligning proposal ↔ design ↔ tasks.
  Verification: Re-read the edited `proposal.md` Impact section; confirmed `design.md` D6 and `tasks.json` T-002 notes are unchanged and now consistent with the proposal. No product/architectural change.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: alignment
  Evidence: Issue AC #4 asks for test coverage of "投递后标记前崩溃 → 重投 → **幂等吸收**". The `event-dispatch` spec correctly encodes the idempotent-absorption requirement ("the handler SHALL absorb the redelivered duplicate idempotently by event id"). `tasks.json` T-002 acceptance lists "deliver-before-mark crash → row stays undelivered → re-delivered on next tick" but does not explicitly call out asserting the handler absorbs the duplicate by event id. The dispatcher's responsibility (re-deliver) is covered; idempotent absorption is a handler-side property best asserted in an integration spec.
  SuggestedAction: When implementing T-002's spec/integration tests, include an assertion that a re-delivered duplicate is absorbed idempotently by the handler (e.g. a handler that no-ops on a seen EventId), not just that the row is re-delivered.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: consistency
  Evidence: The issue body says "三张事件真相表" (`WorkflowRunEvents` + `IssueEvents` + `EpicEvents`); specs/proposal enumerate four (adding `AgentSessionEvents`). `design.md` D3/Open-Question #1 documents this divergence and resolves it sensibly (follow the specs; table-agnostic dispatcher; consistent with `AgentSubscriptionDispatchHandler [Subscription(Type="*")]`). `ListUndeliveredAsync` (`EventStore.cs:220`) already UNIONs all four, so no code diverges from the specs. This is a faithful interpretation of intent, not a defect — recorded only so the issue author can confirm the 4-table reading per Open Question #1.
  SuggestedAction: Confirm with the issue author that AgentSession event delivery is desired (default is safe per the `[Subscription(Type="*")]` contract).
  Status: follow-up

## Review Notes

- **Alignment:** Every issue acceptance criterion maps to a proposal "What Changes" entry and a spec requirement. AC1→event-dispatch (singleton + single-query); AC2→event-dispatch (fan-out + retry) + dead-letter (exhaustion); AC3→event-dispatch (FIFO + per-row mark); AC4→event-dispatch (crash recovery); AC5→dead-letter (queryable + manually re-deliverable). Non-Goals (no sharding, no UI channel, no broker) are respected — no task touches them.
- **Completeness:** Both specs fully covered by tasks. `dead-letter` requirements split correctly: store/query layer in T-001, dead-lettering flow + per-handler isolation + RedeliverAsync in T-002. `event-dispatch` all in T-002. Edge cases (deliver-before-mark crash, per-handler isolation, closed-generic inclusion, correctness-without-ping) appear in T-002 acceptance criteria.
- **Feasibility:** Two tasks at feature-slice granularity. T-001 ("Re-create the DeadLetters persistence layer") is a self-contained persistence+query module with its own tests; T-002 ("Implement the self-driven cluster-singleton event dispatcher") bundles grain + service + scan fix + TimeProvider injection + DI registration + tests into one coherent slice. No over-fine tasks ("定义接口"/"注册DI"/"创建文件"/standalone test tasks are absent). Dependencies are available: `IEventStore.ListUndeliveredAsync`/`MarkDispatchedAsync`, `[Subscription]` reflection scan, `CloudEventTypeMatcher`, `TimeProvider.System`, `UseAdoNetReminderService` all verified present in the tree.
- **Dependency completeness:** T-001 `dependsOn: []`, priority 1. T-002 `dependsOn: ["T-001"]`, priority 2. All `dependsOn` point to existing IDs with lower priority. No cycles. T-002 consuming `IDeadLetterStore` (built in T-001) justifies the ordering.
- **Facts spot-checked against the tree:** `ListUndeliveredAsync` is a single 4-way `UNION ALL` ordered by `(Source, Id)` (`EventStore.cs:220`); `MarkDispatchedAsync` exists (`EventStore.cs:180`); closed-generic bug confirmed at `CloudEventBusServiceCollectionExtensions.cs:21` (`typeof(ICloudEventHandler<>).IsAssignableFrom(t)` is always false for closed generics); wall-clock confirmed at `InMemoryEventBus.cs:74` (`DateTimeOffset.UtcNow`); `TimeProvider.System` registered in silo (`MohistSiloRegistration.cs:64`); `UseAdoNetReminderService` wired (`MohistSiloRegistration.cs:33`); `RegisterOrUpdateReminder` called nowhere in src; `IRemindable` only on `RunnerGrain`; `DeadLetters` absent from `MohistDbContext` (only in 3 frozen historical migration Designer snapshots). All consistent with the artifacts.

<promise>PASS</promise>
