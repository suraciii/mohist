# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The `dashboard-pulse` spec requirement text ("Pulse zone consumes existing activity data sources") names both `useAgentActivity` and `useAgentStatus` as data sources, but the design (D5) correctly narrows the Pulse widget to `useActivityCards()` (which wraps `useAgentActivity`) only — capacity, status counts, and active cards all come from that single projection. `useAgentStatus` is already mounted at the `DashboardPage` level for the document-title running indicator and is not needed inside the Pulse widget. No scenario in the spec actually asserts data sourced from `useAgentStatus`, so the wording is a harmless over-mention rather than a contract breach. No change applied: the design already captures the correct decision, and the scenarios are the binding contract.
  Verification: Confirmed `useActivityCards()` (`packages/web/src/widgets/coder-session/model/activity-cards.ts`) returns `statusCounts` and `slotUsage` from `useAgentActivity().summary`, covering all four capacity-header values and the active card list without `useAgentStatus`.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: The design lists three Open Questions (overflow cap value N=4, status-pill styling reuse, overflow link target). These are implementation-detail decisions deferred to the build task, each with a leaning answer already recorded.
  SuggestedAction: Resolve during T-001 implementation; they do not block plan approval.
  Status: follow-up

---

Cross-check summary:

- **Alignment**: Every "What Changes" entry in `proposal.md` traces to an issue acceptance criterion (AC1 capacity summary, AC2 compact card fields, AC3 empty state, AC4 shared data source). All three Non-Goals (no Activity replacement, no session replay, no history curves) are respected by the specs and design.
- **Completeness**: 4 ADDED requirements in `dashboard-pulse/spec.md` (13 scenarios total) + 1 MODIFIED requirement in `dashboard-shell/spec.md` (4 scenarios). Every requirement has at least one `#### Scenario:`. Both capabilities listed in the proposal have corresponding spec files, and both spec files are referenced by tasks.
- **Consistency**: Proposal Capabilities (`dashboard-pulse` new, `dashboard-shell` modified) match the spec folders exactly. T-001 references `specs/dashboard-pulse/spec.md`; T-002 references `specs/dashboard-shell/spec.md#Dashboard provides four zone mount-point slots` — the fragment matches the `### Requirement:` header. Naming (`PulseZone`, `CompactSessionCard`, `DashboardZone`) is uniform across design and tasks.
- **Feasibility**: T-001 is a complete feature slice (widget module + co-located tests). T-002 is a complete feature slice (mount-point wrapper + wiring + dashboard-shell test updates). No over-fine tasks (no "define interface", "register DI", or standalone test tasks). Each task includes test verification in its acceptance criteria.
- **Dependencies**: T-002 `dependsOn: ["T-001"]`; T-001 priority 1 < T-002 priority 2; no cycles; every `dependsOn` points to an existing lower-priority task.

<promise>PASS</promise>
