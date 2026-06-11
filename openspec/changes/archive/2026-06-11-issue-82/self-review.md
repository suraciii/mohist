# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: T-002's acceptance specifies 6 lifecycle variants including `AgentSessionActivated`, but T-003's publish-trigger list omits `Activated` (only Started/Completed/Failed/Cancelled/StatusChanged are triggered from runtime). A reader might be confused about whether `AgentSessionActivated` is meant to be published.
  Verification: Cross-referenced `AgentSessionEventSerializer.Unwrap` (which has `AgentSessionActivated`) with `AgentSessionGrain.AppendRuntimeEventsAsync` (which has no `activated` transition). T-002 is correct: BusType mapping is a static table; T-003 is correct: only runtime-observable transitions are published. The contract is "all 6 have a mapping; only those that actually fire are published". No code change required, but the spec ambiguity is documented by the design decision (D3).
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: completeness
  Evidence: T-005 acceptance criterion "The querier accepts the `waiting` array as a parameter" is already true today (`AgentSessionQuerier.GetActivityAsync(..., IReadOnlyList<ActivityWaitingCardDto>? waiting = null, ...)` line 199), so the criterion reads as a no-op for the querier. The real work is in `AgentRoutes.MapGet("/activity", ...)` which currently does not pass `waiting`. Task description is correct (it calls out the routes change); acceptance is just a regression guard.
  Verification: Read `AgentSessionQuerier.cs:199` and `AgentRoutes.cs:31-36`. The DTO slot `ActivityDto.Waiting` is also already present (line 141 of `AgentSessionReadModels.cs`). Task is sound.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: consistency
  Evidence: T-001 says transcript channel forwards the 8 event types listed in the proposal. `agent_session_model_resolved` is in the proposal/spec list but is NOT currently in `AGENT_DETAIL_EVENTS` (`packages/web/src/entities/agent/model/events.ts:25-47`). T-007 acceptance explicitly says "The list includes the 8 transcript event types (so the new `OnTranscriptEvent` handler can be registered against the same subscription set)" — this adds `agent_session_model_resolved` to the canonical list, which is the right fix.
  Verification: Read `entities/agent/model/events.ts:25-47`; the new constant module in T-007 must include `agent_session_model_resolved` even though it's not yet in `AGENT_DETAIL_EVENTS`. The proposal text (line 11, design.md line 30) and `coder-session-tracking/spec.md` lines 330-333 all agree. Task is correct.
  Status: resolved

- [ID: item-4]
  Severity: info
  Scope: feasibility
  Evidence: T-003 acceptance "`agent_liveness_status` publishing `AgentSessionStatusChanged` only fires when the status actually changes (deduped by current status string)" requires reading the previous status before calling `MarkActive`. The current `AgentSessionGrain.AppendRuntimeEventsAsync` (line 130-135) calls `session.MarkActive(status, ...)` unconditionally. The task is asking the implementer to capture the prior status and compare — this is a concrete code change to be done in T-003.
  Verification: Inspected `AgentSessionGrain.cs:130-149`. The current `events.AddRange(session.MarkActive(...))` is called inside the loop with no "previous status" check. The task is feasible; the dedup is local to the same `AppendRuntimeEventsAsync` invocation since Grain activation is single-threaded, plus persistence handles reactivation.
  Status: resolved

- [ID: item-5]
  Severity: info
  Scope: consistency
  Evidence: Spec `event-bus/spec.md` (line 99) and `coder-session-tracking/spec.md` (line 313) both list `agent_liveness_status` under "transcript events that SHALL NOT flow through `IEventPublisher`". But `signalr-realtime-push/spec.md` (lines 101-104, scenario "Liveness status changes publish AgentSessionStatusChanged") and `coder-session-tracking/spec.md` (lines 355-358) explicitly say that the same `agent_liveness_status` row CAN trigger a `AgentSessionStatusChanged` domain event via `IEventPublisher` when the status actually changes.
  Verification: Reconciled: the spec distinguishes between the **row** (observation, transcript channel) and the **derived lifecycle event** (`StatusChanged`, domain bus). The dual-channel behavior is the intent. The wording in event-bus/spec.md is about the row itself not being a bus event, which is correct. No fix needed.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-6]
  Severity: follow-up
  Scope: feasibility
  Evidence: Design D6 says `BusType` mapping is enforced by a unit test. T-006 acceptance includes "Unit test: `AgentSessionEventSerializer.BusType` returns the correct reverse-DNS string for all 6 lifecycle variants". The unit test is folded into the Specs/Events test file. This is fine for spec coverage; a separate test class under a `Unit` directory would be more conventional if it ever needs to be reused, but the project doesn't currently use a `Unit/` subfolder.
  SuggestedAction: Keep unit test in `Specs/Events/` per current convention; do not move.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: consistency
  Evidence: OpenSpec design Q4 (line 192) notes that reverse-DNS event types' `EventMap` payload shape is not formally defined in the Web — the proposal assumes "server emits the same payload shape for both legacy and reverse-DNS types". T-009 maps reverse-DNS arms to the same invalidation/toast logic as the legacy arms, which means the same payload fields are read. If a future producer emits a different `data` shape for a reverse-DNS event, the switch would silently misbehave.
  SuggestedAction: Add a typed spec test asserting `envelope.data` shape is the same for both legacy and reverse-DNS variants of the same logical event. Defer to a follow-up issue if needed; current issue scope is "fix the three breaks", not "fully type all event payloads".
  Status: follow-up

- [ID: item-8]
  Severity: follow-up
  Scope: completeness
  Evidence: Design D6 / R7 mention that the extended `LiveTaskProvider` switch could be refactored into `dispatchWorkflowStageEvent`, `dispatchIssueLifecycleEvent`, `dispatchAgentSessionLifecycleEvent` for ~15 new arms. Current spec/task scope does not require this refactor.
  SuggestedAction: Leave as-is; refactor in a follow-up issue if the switch exceeds ~300 lines.
  Status: follow-up

- [ID: item-9]
  Severity: follow-up
  Scope: feasibility
  Evidence: Design Q2 (line 188) raises the question of whether the Web's canonical `EVENT_TYPES` should be split into `DOMAIN_EVENTS` (OnEvent) and `TRANSCRIPT_EVENTS` (OnTranscriptEvent). Current task design keeps them in a single list, with `SetSubscriptionsAsync` covering both and the dispatcher filtering by per-connection subscription set. This works, but the Web has to register both `OnEvent` and `OnTranscriptEvent` listeners (T-008).
  SuggestedAction: No change for this issue. If the Web needs per-channel subscription set on the client side (e.g. for debugging), split into two arrays in a follow-up.
  Status: follow-up

- [ID: item-10]
  Severity: follow-up
  Scope: completeness
  Evidence: `MohistHub.OnConnectedAsync` currently calls `_registry.SetSubscriptions(Context.ConnectionId, Array.Empty<string>())` on best-effort grain-replay failure (line 112). T-004 says "An empty initial subscription set is the expected default for a freshly opened tab (not an error state)" — that is already the existing behavior, so the change is mostly about clarifying the documentation and possibly a no-op. Acceptance criteria confirm: opening a connection without `SetSubscriptionsAsync` should not error and not block.
  SuggestedAction: The actual code change is small (probably just docstring + an explicit log); the test (`Spec` test for "Empty default is the correct initial state") is the value. Keep T-004 as planned.
  Status: follow-up

## Summary

- Alignment: proposal addresses the three concrete breaks identified in the issue (frontend never subscribed, reverse-DNS names don't match, no transcript channel) and adds the ActivityPage Waiting fix as a fourth concrete improvement. Every "What Changes" entry traces to an issue requirement; nothing is missing.
- Completeness: all issue requirements are covered by `signalr-realtime-push`, `event-bus`, `coder-session-tracking`, and `web-ui` specs. Each spec requirement has at least one task assigned to it.
- Consistency: spec capabilities align with proposal. Tasks reference the correct spec files via section anchors. All anchors resolve to existing sections. Naming is consistent across proposal, design, specs, and tasks (`com.mohist.*` for new emits, snake_case for legacy).
- Feasibility: dependencies are all in place or created by earlier tasks. The dedup logic for `agent_liveness_status` requires capturing the prior status string before calling `MarkActive`; this is a localized change. The `ITranscriptEventPublisher` and `OnTranscriptEvent` are net-new surfaces; existing `ConnectionSubscriptionRegistry` is reused. `ActivityRoutes` waiting-array is a single integration point.
- Dependency completeness: every non-first task has a `dependsOn`. All `dependsOn` point to lower-priority tasks. No cycles. Cross-task references in task descriptions (e.g. T-004 references T-001, T-008 references T-007, T-009 references T-007) are consistent with the `dependsOn` graph.

The change set is internally consistent, all references resolve, and the implementation plan is concrete enough to begin work.

<promise>PASS</promise>
