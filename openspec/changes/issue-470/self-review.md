# Self-Review - Issue 470

Reviewed the live issue, proposal, design, all three capability specs, the
eight-task graph, and the relevant current Server and Web implementation.

## Verdict

The plan is not ready to build. The prior review's storage-readiness,
protobuf-wire, injectable-duration, and production-composition concerns have
been addressed, but three contracts still permit contradictory behavior or
omit required startup work.

## Findings

### F1 - Mixed OTLP batches have contradictory retry and accounting semantics (high)

The metric contract defines `rejected` and `dropped` as non-retryable outcomes
(`specs/runtime-observability-metrics/spec.md:57`). D1 classifies every Span
before writing and preserves rejected/dropped classifications when the accepted
subset's transaction rolls back (`design.md:53`), while D1's response contract
returns HTTP 503 for a rolled-back write (`design.md:57`). That response asks the
exporter to retry the entire request.

For a batch containing a malformed or protection-rejected Span plus a valid
Span whose write rolls back, the plan would therefore count the first Span as
non-retryable loss while returning a retryable result for it. A retry could also
increment the rejected/dropped counters repeatedly. T-002 tests isolated
rejection, drop, and rollback cases but not this combination
(`tasks.json:33-40`). The plan must define precedence and accounting for mixed
classification plus rollback, and lock it with wire-level and counter tests.

### F2 - The host-runner refactor does not preserve database initialization (high)

The current primary and alternate startup paths both call
`DatabaseInitializer.InitializeAsync` before `StartAsync`
(`packages/server/src/Mohist.Server/Program.cs:62` and `:126`). That initializer
runs EF migrations and the repository data upgrade
(`packages/server/src/Mohist.Server/Infrastructure/Data/Db/DatabaseInitializer.cs:9`).

D7 reduces top-level `Program` to constructing adapters and invoking
`MohistHostRunner`, but its host interface exposes only services and ordinary
start/stop/dispose/wait lifecycle operations (`design.md:142`). Neither D7 nor
T-007 assigns database initialization to the runner, factory, or production
host adapter, and no acceptance criterion verifies its ordering or behavior for
primary and alternate attempts. An implementation following the plan can omit
migrations and data upgrades entirely. The design must explicitly place this
startup step and test its ordering and failure behavior.

### F3 - T-007 asks a lifecycle fake to prove an HTTP health contract (medium)

D7's fake host exposes services and lifecycle signals only (`design.md:142`),
but its fake runner tests must assert that core health is successful
(`design.md:148`). T-007 repeats the `/api/health` assertion "through the fake
lifecycle boundary" (`tasks.json:162`) while the production composition test is
explicitly non-starting (`tasks.json:164`). The actual health behavior exists as
a mapped HTTP endpoint
(`packages/server/src/Mohist.Server/Api/HealthRoutes.cs:7`), so neither test
surface can prove its status code or payload.

The core-health independence assertion already belongs to T-003's API test
(`tasks.json:67`). T-007 should restrict its fake assertions to lifecycle and
runtime-status projection, or define an explicit in-memory HTTP surface if it
must own an HTTP assertion.

## Coverage And Structure

- Proposal capabilities and spec directories match.
- The eight task dependencies are resolved and acyclic.
- The plan otherwise covers the issue's tri-state status, bounded route
  summary, process and storage pressure, telemetry outcomes, low-cardinality
  metric catalog, agent-path amplification, transition-only logs,
  self-observation exclusion, history-independent status cost, and core-health
  independence.

<promise>FAIL</promise>
