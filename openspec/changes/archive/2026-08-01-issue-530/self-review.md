# Self-Review (round 2) — issue-530 (`mo otel traces`)

Re-reviewed after the F-1/F-2/F-3 fixes: `proposal.md`, `specs/otel-cli-traces/spec.md`, `design.md`, `tasks.json`, against issue #530 and the codebase. Reviewer only; no files modified except this one.

## Status of prior findings

### F-1 (was BLOCKING) — RESOLVED

The contradiction is gone and the chosen path is technically sound.

- The spec's requirement ("the standard Server-unavailable message") was correct and is **unchanged**; the fix landed in the design.
- Design D1 now selects `GetDataOrPrintErrorAsync` (the `Run list` pattern), not `PrintResourceAsync`. Verified: `GetDataOrPrintErrorAsync` (`MohistCliApi.cs:308`) catches `HttpRequestException` and writes the literal `ServerUnavailableMessage` ("Server is not running. Start with: mo service start server") with exit 1 — the same contract asserted by `CliOtelCommandSpecs` (`query`/`status`), `CliSessionCommandSpecs`, `CliAgentCommandSpecs`, and the `MohistCliApiSendAsyncSpecs` family.
- The design's rejected-alternative and Risks/Open-Questions text now **accurately** characterizes `PrintResourceAsync` as emitting `<raw exception> (code=server-unavailable)` with no remediation hint (`CliResponseReader` → `CliFailure("server-unavailable", ex.Message)` at `CliExecutionContract.cs:271`, rendered at `:190`). The earlier false claim ("both render the same message") is removed.
- New verification performed this round: the raw-array projection the design relies on is valid. `GetDataOrPrintErrorAsync` → `GetDataAsync` → `ReadSuccessDataAsync` (`MohistCliApi.cs:1278`) returns `envelope.Data`, i.e. the **unwrapped** traces array, so `selection.Project(data, Collection)` + `RenderTableAsync(data, TableShape.OtelTracesList)` operate on the array directly — no `ProjectRunsFromIssues`-style transform needed (that transform is issue-specific in `Run list`). The `/otel/api/traces` envelope is the same `{success, data}` shape `otel query`/`status` already extract, so the array will come through.

### F-2 (was minor) — RESOLVED

The "A limit is applied" scenario no longer restates a tautology. It now asserts the `--limit` value is forwarded to the Server as a request parameter and that the CLI imposes no local upper bound tighter than the Server's — both testable against the captured request.

### F-3 (was nit) — RESOLVED

The duplicate ordering statement is gone: requirement 1's prose no longer says "ordered most-recent first" (ordering ownership is stated once, as Server-owned), and the populated-list scenario now asserts the observable recency order ("in the order the Server returns them (most-recent first)"). Consistent with `TraceQuerier.ListAsync`'s `ORDER BY start_time DESC`.

## Fresh review of the whole plan

No new blocking or material issues found:

- **Capability naming** `otel-cli-traces` is consistent across proposal, spec path, design references, and `tasks.json`.
- **Spec format** is intact: requirements `###`, scenarios exactly `####`, SHALL/MUST language, every requirement has ≥1 scenario, no delta headers, self-contained.
- **`tasks.json`** is valid JSON, acyclic (T-002 depends only on the lower-priority T-001), both tasks `passes:false`, tests live inside T-001, and T-001's notes now point at `Run.Reads.cs BuildList` and explicitly warn against `PrintResourceAsync`.
- **Design line references** re-checked accurate: route `OtelQueryRoutes.cs:50`, `ListAsync` `TraceQuerier.cs:81`, `ClampLimit` `:282`, `GetDataOrPrintErrorAsync` `MohistCliApi.cs:308`, `PrintResourceAsync` `:106`, `TableShape` `:1020`, `RenderActivityList` `TableRenderer.Events.cs:81`.
- **Scope** stays CLI-only; Server endpoint and `TraceQuerier` consumed unchanged; non-goals (single-trace detail, span tree, aggregation, time-range) respected.

## Conclusion

All three prior findings are resolved, the resolution is verified against the code, and no new problems were introduced. The plan is internally consistent and ready to build.

<promise>PASS</promise>
