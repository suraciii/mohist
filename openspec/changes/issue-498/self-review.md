# Plan Review

## Findings

### 1. [HIGH] `run timeline` has no defined independent behavior

The issue makes timeline migration conditional on its independent value, but the plan requires `mo run timeline` without establishing such a value or a stable output contract. The legacy CLI leaf requests `/api/projects/{project}/issues/{number}/workflow/timeline`, yet the current server maps only `/workflow/status`; there is no timeline route or server test for the requested path. Meanwhile, the existing WorkflowRun detail renderer already displays the ordered stage progression returned by `GET /api/workflow-runs/{id}`.

The spec requires `run timeline` to preserve the existing timeline, while the design says it will derive a new timeline presentation from the same detail response and explicitly excludes a new output format. It therefore leaves an implementer unable to determine whether `run timeline` should duplicate `run view`, introduce a distinct presentation, or be omitted because the old command has no working independent behavior. That ambiguity conflicts with the single canonical-path goal and prevents the task's timeline acceptance criteria from being objectively verified.

Revise the proposal/spec/design/tasks to make one explicit product decision: either retire timeline with the nonfunctional Issue subarea, or define its distinct Run-owned output, data source, and testable response/rendering contract. The decision must also state how it avoids duplicating `run view`.

<promise>FAIL</promise>
