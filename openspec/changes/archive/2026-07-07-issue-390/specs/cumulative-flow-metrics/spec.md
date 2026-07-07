### Requirement: The cumulative-flow HTTP endpoint MUST be removed

The server MUST NOT serve a cumulative-flow metrics read endpoint. The route `GET /api/projects/{projectRef}/issues/metrics/cumulative-flow` MUST NOT be registered, MUST NOT be re-routed or aliased to another path, and a request to that path MUST fail as an unmatched route. This pins the breaking contract: the route is gone, not merely unrendered on the client.

#### Scenario: Request to the removed route is unmatched
- **WHEN** a client issues `GET /api/projects/{projectRef}/issues/metrics/cumulative-flow` against a valid project reference, with or without a `range` query parameter
- **THEN** the server responds with an unmatched-route failure (HTTP 404), not a 200 response and not a redirect

#### Scenario: Other issue metrics endpoints remain available
- **WHEN** the cumulative-flow route has been removed
- **THEN** the other issue metrics endpoints (throughput, completion trend, cycle time, stage duration, AI quality, first-time-right, cost rollup, delivery-time, approval metrics) continue to be registered and respond as before

### Requirement: Server-side cumulative-flow read code MUST be deleted

The server assembly MUST NOT contain the `CumulativeFlowQuerier`, the `MapIssueCumulativeFlow` route mapper, or the `CumulativeFlowResponse` / `CumulativeFlowDayDto` response DTOs. The `MapIssueRoutes` registration MUST NOT call any cumulative-flow mapper.

#### Scenario: Route registration omits the cumulative-flow mapper
- **WHEN** the issue routes are mapped at application startup
- **THEN** no `MapIssueCumulativeFlow` call is made and no `/metrics/cumulative-flow` sub-route is attached to the issues route group

#### Scenario: Cumulative-flow read types and querier are absent from the server assembly
- **WHEN** the server assembly is inspected
- **THEN** the `CumulativeFlowQuerier` type, the `CumulativeFlowResponse` record, and the `CumulativeFlowDayDto` record are all absent

### Requirement: The frontend cumulative-flow hook and DTO types MUST be deleted

The web package MUST NOT export `useCumulativeFlow`, `fetchCumulativeFlow`, `cumulativeFlowQueryKey`, `CumulativeFlowResponse`, or `CumulativeFlowDayDto` from the issue entity barrel (or anywhere else). No code under `/insights` MAY reference a cumulative-flow query.

#### Scenario: Issue entity barrel omits cumulative-flow exports
- **WHEN** the `entities/issue` public API is inspected
- **THEN** none of `useCumulativeFlow`, `fetchCumulativeFlow`, `cumulativeFlowQueryKey`, `CumulativeFlowResponse`, or `CumulativeFlowDayDto` are exported

#### Scenario: Web package compiles without cumulative-flow references
- **WHEN** the web package typecheck runs
- **THEN** it succeeds with no import of the deleted cumulative-flow hook, query-key helper, or DTO types
