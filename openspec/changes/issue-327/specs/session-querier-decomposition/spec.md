### Requirement: Usage and cost reporting is independent of the core session query service

The usage/cost reporting capability — comprising usage timeseries (`GetUsageTimeseriesAsync`), cumulative cost rollup (`GetCostRollupAsync`), and windowed cost (`GetCostWindowedAsync`) together with their private aggregation helpers (pre-window spend computation, cumulative-cost-per-ship computation, completed-issue-count loading, per-window figure construction, usage-bucket data, and the usage-presence predicate) — SHALL reside in a service or method group that is separate from the core session query class. The core session query class (`AgentSessionQuerier`) SHALL NOT contain usage/cost reporting methods or their private aggregation helpers after this change. The HTTP routes that serve activity, usage-timeseries, cost-rollup, and cost-windowed endpoints SHALL continue to resolve their respective services through dependency injection and SHALL return responses that are byte-for-byte identical to those produced before the split.

#### Scenario: Usage timeseries endpoint behaves identically after decomposition

- **WHEN** the usage-timeseries endpoint is called with the same project, session data, and injected time
- **THEN** the response SHALL be identical to the pre-decomposition response (same buckets, same cumulative-cost-per-ship points, same range, same currency resolution)

#### Scenario: Cost rollup endpoint behaves identically after decomposition

- **WHEN** the cost-rollup endpoint is called with the same project and session data
- **THEN** the total-cost and today-cost metrics SHALL be identical to the pre-decomposition values

#### Scenario: Windowed cost endpoint behaves identically after decomposition

- **WHEN** the cost-windowed endpoint is called with the same project, session data, and injected time
- **THEN** the current-window and previous-window spend and per-issue-cost figures SHALL be identical to the pre-decomposition values, including independent emptiness evaluation per metric per window

#### Scenario: Core query class no longer carries usage or cost methods

- **WHEN** the core session query service class is inspected after the change
- **THEN** it SHALL NOT contain `GetUsageTimeseriesAsync`, `GetCostRollupAsync`, `GetCostWindowedAsync`, or any of their private cost-aggregation helpers

### Requirement: Activity feed assembly is independent of the core session query service

The activity feed assembly capability — comprising `GetActivityAsync` together with its private helpers (task-progress map construction, activity-card projection, issue-title loading, latest-event loading, preview extraction, and text truncation) — SHALL reside in a service or method group that is separate from the core session query class. The core session query class SHALL NOT contain activity-feed assembly methods or their private helpers after this change. The activity endpoint SHALL continue to return responses that are byte-for-byte identical to those produced before the split.

#### Scenario: Activity feed endpoint behaves identically after decomposition

- **WHEN** the activity endpoint is called with the same project, limit, waiting cards, runner capacity, and session data
- **THEN** the response (summary counts, session cards with their usage/event-summary/work-item/task-progress projections, and waiting cards) SHALL be identical to the pre-decomposition response

#### Scenario: Core query class no longer carries activity methods

- **WHEN** the core session query service class is inspected after the change
- **THEN** it SHALL NOT contain `GetActivityAsync`, `ToActivityCard`, `BuildTaskProgressMapAsync`, or any other activity-feed-specific private helper

### Requirement: Dead DTO mapping code is removed

The zero-call DTO mapping method `ToAgentSessionDto` and its return type `AgentSessionDto` SHALL be deleted. Neither the method nor the record type SHALL exist in the codebase after this change. No HTTP route, test, or other code SHALL reference `AgentSessionDto`.

#### Scenario: AgentSessionDto record type is gone

- **WHEN** the codebase is searched for the `AgentSessionDto` record type after the change
- **THEN** zero definitions and zero references SHALL be found

#### Scenario: ToAgentSessionDto method is gone

- **WHEN** the codebase is searched for the `ToAgentSessionDto` method after the change
- **THEN** zero definitions and zero call sites SHALL be found

### Requirement: Session query, followup, and cancel behavior is preserved

The core session query methods (workflow session listing/detail, generic session listing/summary, followup target resolution, cancel target resolution, session metadata, and transcript retrieval) SHALL remain in the core query service and SHALL produce responses identical to those before decomposition. Followup and cancel target resolution methods SHALL remain in their current location (the core query class); they are out of scope for relocation.

#### Scenario: Workflow session queries are unchanged

- **WHEN** workflow session list-by-workflow, list-by-issue, or get-by-workflow endpoints are called with the same inputs
- **THEN** the responses SHALL be identical to the pre-decomposition responses

#### Scenario: Generic agent session queries are unchanged

- **WHEN** the agent-scoped session list, generic session summary, or context-association endpoints are called with the same inputs
- **THEN** the responses SHALL be identical to the pre-decomposition responses

#### Scenario: Followup and cancel target resolution remain in the core query class

- **WHEN** the core session query service class is inspected after the change
- **THEN** `ResolveFollowupTargetAsync`, `ResolveGenericFollowupTargetAsync`, `ResolveGenericCancelTargetAsync`, and `ResolveIssueSessionIdAsync` SHALL still be present in that class
