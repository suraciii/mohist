## Context

`WorkflowActivityQuerier.ListActiveAgentsResultAsync` currently loads every Session for a project through `AgentSessionQuery.ListByLabelsAsync`, which deserializes every selected row before the querier can reject idle direct Sessions or Sessions for non-running Workflows. The Workflow status lookup is already de-duplicated after this broad load, but candidate materialization still grows with project history.

The status route is a high-frequency AgentOps read. It must retain the current `AgentStatusResponse`, global creation-descending order, direct-session activity semantics, Workflow pending-work match, and #470 request-work counters. The Session table already projects project, source kind, source ID, and work labels from JSON; direct activity is currently only in serialized state. Workflow runs project and running status are persisted projections.

## Goals / Non-Goals

**Goals:**

- Select and deserialize only Sessions that can contribute to project agent status.
- Make selection cost scale with active direct Sessions and running Workflow Sessions, rather than historical Session rows.
- Preserve the current API response and active-agent decision logic after selection.
- Read each selected Session once and each relevant Workflow status once per request.
- Keep `candidates`, `processed`, database-call, and downstream-call accounting truthful.

**Non-Goals:**

- Change the agent status page, polling interval, route aliases, DTO schema, or ordering.
- Cache status responses or Workflow status to conceal a broad query.
- Generalize or refactor unrelated Session query APIs.
- Change the semantics of direct Session activity or Workflow pending work.

## Decisions

### Add an indexed direct-activity projection

Add a stored `Activity` projection to `AgentSessionRow`, sourced from the persisted Session state, and a composite index covering direct status selection by project, source kind, activity, and creation order. Add a migration for the projection and index.

This is required because the authoritative direct activity value is currently inside `State`; filtering it with `json_extract` would avoid application deserialization but still evaluate historical direct-session JSON on every poll. A stored, indexed projection gives the database a bounded direct-session access path without changing the domain model.

Alternative considered: use `json_extract(State, '$.status.activity')` in the new query without a migration. Rejected because it leaves database work proportional to historical direct Sessions. Alternative considered: reuse `AgentSessionId`, `Status`, or `LastDataAt`. Rejected because those fields describe binding or received data, not the domain's active/idle state.

### Query status candidates in one purpose-specific persistence read

Add a status-only candidate query at the Session persistence boundary. It returns rows in the existing global creation-descending order from two database-side branches:

- direct Sessions for the requested project whose source kind is `agent-launch` and whose projected activity is `active`;
- non-direct Sessions whose projected source ID joins a Workflow Run in the requested project with persisted status `running`.

The query materializes the combined ordered rows once and passes them through the existing Session JSON-to-record mapping once. `WorkflowActivityQuerier` will call this path for a project-scoped status request, while its unscoped behavior remains unchanged. Its response assembly continues to apply the current direct-session and pending-work checks, preserving visibility during races between selection and Workflow status loading.

Alternative considered: load running Workflow IDs, then issue a Session query for each ID plus a direct query. Rejected because it adds query count with active Workflows and complicates preserving one global order. Alternative considered: expand `ListByLabelsAsync` with status-specific switches. Rejected because the joined, two-branch status predicate is not a reusable label-list concern and would make that general API ambiguous.

### Retain one Workflow status read per distinct selected run

After candidate rows are materialized, derive distinct Workflow Run IDs from their persisted source IDs. Load the current status through `WorkflowQuerier` once for each distinct ID and retain the resulting lookup for response assembly. The candidate query's running-run predicate is selection only; the subsequent status read remains the authority for matching pending work and safely excludes a Run that terminalizes between the two reads.

Alternative considered: treat the persisted running predicate as sufficient and skip `WorkflowQuerier`. Rejected because the active-agent DTO needs current pending-work and stage progress, and a concurrent state transition must not produce a stale active entry.

### Count selected work at its ownership boundaries

`WorkflowActivityQuerier` returns the selected candidate count with the active-agent results. `AgentStatusHandlers` continues to add that count as `candidates` and the emitted active-agent count as `processed`. Existing request interceptors remain the sole source for database and downstream-call counts; the single candidate query and each distinct `WorkflowQuerier` invocation are therefore counted automatically. Do not synthesize or normalize counters after the fact.

Alternative considered: expose deserialization as a new public amplification field. Rejected because the existing metric shape is a compatibility constraint. Tests will use an internal query/materialization probe or fake boundary to count deserialized rows, alongside the existing response counters, without expanding the API.

### Test selection cost as operations, not elapsed time

Extend `AgentPathAmplificationSpecs` with a fixed active dataset and a second dataset that adds thousands of terminal or idle historical rows. Assert identical active-agent JSON, candidate count, materialized-row count, database-call count, and downstream-call count. Add a case with multiple selected Sessions for one running Workflow and assert one Workflow status invocation. Keep current alias and visibility cases unchanged.

Alternative considered: use elapsed-time assertions or a benchmark. Rejected because execution time is environment-dependent and does not prove which work was performed.

## Risks / Trade-offs

- [A concurrent Workflow transition can occur after candidate selection.] -> Re-read each distinct Workflow through `WorkflowQuerier` and keep the existing pending-work match before emitting an active agent.
- [The new stored projection and index add write and database-size overhead.] -> Limit them to the single activity scalar and one status-query index; Session writes already persist the authoritative `State` from which the projection is derived.
- [A legacy or malformed Session state can have no projected activity.] -> It is not selected as a direct active candidate, matching the existing behavior in which only explicit `Active` activity is emitted.
- [A future source kind could be incorrectly treated as Workflow-backed.] -> Restrict the joined branch to non-direct Sessions with both a source ID and work ID, and retain the current response-level validation.
- [Counter changes can hide extra work if they are hand-maintained.] -> Keep database and downstream counters interceptor-owned and test them against the small and large history datasets.

## Migration Plan

1. Add the `Activity` computed projection and status-query index in an EF migration; existing persisted Session JSON populates the projection when the table is rebuilt or migrated.
2. Add the purpose-specific candidate query and switch only project-scoped `WorkflowActivityQuerier` status reads to it.
3. Extend status amplification and Workflow-read tests with operation counters, then run the server typecheck and test suite.
4. Deploy with the normal Server database migration before serving the new query path.

Rollback: deploy the prior Server version after the migration. The added projection and index are additive and can remain unused; no business data or response schema requires reversal.

## Open Questions

- None. The initial proposal's no-schema-change estimate is superseded by the indexed activity projection: without it, database work would still grow with historical direct Sessions and would not satisfy this change's cost contract.
