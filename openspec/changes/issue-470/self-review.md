# Self-Review - Issue 470

Reviewed the current proposal, all three capability specs, design, and
nine-task graph against the live issue #470 details and the current Server/CLI
implementation.

## Verdict

The plan is not ready to build. Product coverage is complete and the task DAG is
structurally valid, but one task boundary is not independently deliverable and
three implementation contracts still require an autonomous agent to invent
important lifecycle or suppression behavior.

## Findings

### F1 - The status baseline is not independently deliverable (high)

T-003 replaces the production status API and CLI but explicitly leaves enabled
runtime `storage_unverified` until T-004 (`tasks.json:55,73`). Collector success,
process publication, and the first real storage sample also belong to T-004.
After T-003, a normally configured enabled Server therefore cannot reach the
spec's `healthy` state or provide the required current process values.

This conflicts with the task-generation rule that every task leave its feature
module usable. Merge T-003 and T-004, or move enough startup publication into
T-003 that all three states and required fields work in production while T-004
adds only independently optional recurring behavior.

### F2 - Trace feedback suppression has no technical design (high)

The runtime spec excludes both runtime metrics and existing traces for OTel
ingest, query, status, and maintenance
(`specs/runtime-observability-metrics/spec.md:129-142`). Design D2 only rejects a
new metrics exporter, and D3 only prevents creation of the new request-counting
scope (`design.md:59-88`). Neither explains how existing ASP.NET Core, EF Core,
Orleans, and HttpClient instrumentation is suppressed for those operations.

T-006 consequently asks an AFK implementer to invent an "explicit OTel
execution-suppression scope" and integrate it across four instrumentation
families (`tasks.json:124-145`). The design must name the suppression primitive,
where it begins/ends for HTTP and background storage work, and how existing
ASP.NET/exporter filters compose with it. Tests alone are not an implementation
boundary.

### F3 - Bind-fallback lifecycle tests lack an injectable host boundary (medium)

Fallback currently lives in top-level `Program.cs` around concrete
`WebApplication.StartAsync` and alternate construction. D7 and T-008 require
deterministic start/stop/dispose/failure tests without real ports
(`design.md:132-140`; `tasks.json:172-192`) but identify no host factory or
lifecycle interface through which tests trigger a classified bind failure and
observe both host graphs.

Define the narrow injectable boundary that owns primary/alternate construction,
start, stop, dispose, and bind-failure classification. Otherwise T-008 combines
a high-impact startup refactor with an unspecified test architecture.

### F4 - Sampler failure invalidation exists only in task prose (medium)

The specs require failed process/storage samples to expose null rather than
stale values. T-004 correctly requires invalidation and growth-baseline reset
(`tasks.json:81-87`), but D5 describes publishing independent failures without
stating that `PublishProcess(failure)` clears all cached process fields or that
`PublishStorage(failure)` clears usage, growth, window, and the growth baseline
(`design.md:102-114`). D6 discusses source activation only.

Make atomic cache invalidation and recovery-baseline behavior part of the design
contract so the task is implementing an agreed model rather than defining it.

## Coverage And Structure

- Proposal capabilities and spec directories match.
- The specs contain 16 requirements and 52 correctly formed scenarios.
- `tasks.json` contains nine AFK/WRITE tasks with `passes=false`; all anchors
  resolve and every dependency points to a lower priority in an acyclic graph.
- The plan otherwise covers the live issue's tri-state status, bounded route
  summary, telemetry outcomes, low-cardinality catalog, agent amplification,
  transition logs, no-scan status, core-health independence, and non-persistent
  diagnostics.

<promise>FAIL</promise>
