# Review — issue-530 (`mo otel traces`)

Reviewed the change as it stands now (branch `mohist/run-wr_eb9614a72a01417a8bdc785841dded67`,
commits `9f5260408..72f162aaa`) against the issue's acceptance criteria and the plan artifacts
under `openspec/changes/issue-530/`. Reviewer only; no files modified except this one.

Change scope (product deliverables, excluding `openspec/` workflow artifacts):

- `packages/cli/Mohist.Cli/MohistCliCommands.Otel.cs` — new `traces` subcommand (`BuildTraces`,
  `RunTracesAsync`, `BuildTracesPath`), `TracesDescriptor`, group doc + `otel --help` wiring.
- `packages/cli/Mohist.Cli/MohistCliApi.cs` — new `TableShape.OtelTracesList` enum entry.
- `packages/cli/Mohist.Cli/ResourceOutput.cs` — `OtelTracesList` → `Collection` cardinality and
  field catalog `["trace_id","service_name","start_time","end_time","span_count"]`.
- `packages/cli/Mohist.Cli/TableRenderer.cs` — dispatcher switch arm.
- `packages/cli/Mohist.Cli/TableRenderer.Events.cs` — `RenderOtelTracesList`.
- `packages/cli/tests/Mohist.Cli.Tests/CliOtelCommandSpecs.cs` — 11 new spec cases + 2 tightened
  command-surface assertions.
- `packages/server/tests/Mohist.Server.UnitTests/Api/CliFieldContractTests.cs` — synthetic
  registration for `OtelTracesList`.
- `docs/cli-reference.md` — removed the closed `otel traces` gap line (T-002).

## Verification performed

- **Build:** `Mohist.Cli.csproj` and `Mohist.Server.UnitTests.csproj` build clean under
  `TreatWarningsAsErrors` (`-p:SkipWebBuild=true`, 0 warnings / 0 errors).
- **CLI specs:** `CliOtelCommandSpecs` — 32/32 pass (incl. the 11 new `OtelTraces_*` cases and the
  tightened `OtelRoot_Help_DescribesServerRoutedCommands` / `OtelQuery_Help_ListsSubcommands`
  command-surface assertions).
- **Resource output contract:** `CliResourceOutputSpecs` — 17/17 pass
  (`EveryTableShapeHasAnOutputDescriptor` would fail without the new catalog entry).
- **Field contract:** `CliFieldContractTests` — 4/4 pass
  (`EveryTableShapeHasOneRegistration` would fail without the `OtelTracesList` registration).
- **Server contract re-check (read-only):** `GET /otel/api/traces` (`OtelQueryRoutes.cs:50`)
  binds `int? limit` + `string? service` and returns `ApiResults.Ok(IReadOnlyList<TraceSummary>)`;
  `TraceSummary` (`TraceQuerier.cs:421`) keys are exactly `trace_id/service_name/start_time/end_time/span_count`;
  `ClampLimit` (`:282`) is authoritative (`<=0`→50, cap 1000). The CLI's parameter names, field list,
  and "forward raw, no local clamp" behavior all match.
- **Envelope unwrapping:** `GetDataOrPrintErrorAsync` → `GetDataAsync` → `ReadSuccessDataAsync`
  (`MohistCliApi.cs:1279`) returns the unwrapped `envelope.Data` (the traces array), so
  `selection.Project(data, Collection)` and `RenderTableAsync(data, OtelTracesList)` operate on the
  array directly — consistent with the `Run list` template (`MohistCliCommands.Run.Reads.cs:62`).

## Acceptance criteria

| AC (issue body) | Status | Evidence |
|---|---|---|
| `mo otel traces` lists recent traces; `--limit`/`--service` | MET | `RunTracesAsync` + `BuildTracesPath`; `OtelTraces_PopulatedResponse_RendersCompactTableFromServer`, `OtelTraces_ServiceAndLimit_ForwardAsQueryParameters` |
| `--json` field selection; bare `--json` lists fields; default compact table | MET | `TracesDescriptor` + shared `JsonSelection`; `OtelTraces_BareJson_*`, `OtelTraces_SelectedJson_*`, `OtelTraces_InvalidJsonField_*` |
| Server offline → non-zero exit, actionable stderr | MET (connection-refused) | `GetDataOrPrintErrorAsync` writes `ServerUnavailableMessage`; `OtelTraces_ServerUnreachable_SurfacesStandardServerUnavailableMessage` |
| Leaf help explains split with `otel query` | MET | `traces` description names `--service`/`--limit` and references `mo otel query`; `OtelTraces_Help_NamesOptionsAndReferencesQuery` |
| Docs gap closed; `traces` in command map | MET | `docs/cli-reference.md:129` lists `status`、`query <sql>`、`traces`; gap line removed (T-002) |

## Findings

None of the findings below must be fixed before merge; they are reported for transparency so a
follow-up task can act on them if desired.

### F-1 (low / informational) — Timeout does not yield the standard Server-unavailable diagnostic

`traces` fetches via `api.GetDataOrPrintErrorAsync(path)` (`MohistCliCommands.Otel.cs:109`), which
only catches `ApiResponseException` and `HttpRequestException` (`MohistCliApi.cs:308-324`). It does
**not** catch `TaskCanceledException`. Its `otel` group siblings do:

- `query` — `catch (TaskCanceledException)` at `MohistCliCommands.Otel.cs:171` → `ServerUnavailableMessage`.
- `status` — `catch (TaskCanceledException)` at `MohistCliCommands.Otel.cs:318` → `ServerUnavailableMessage`.

So if the Server hangs (request times out), `mo otel traces` surfaces an unhandled
`TaskCanceledException` (raw non-zero exit / stack) instead of the clean
"Server is not running. Start with: mo service start server" message that `mo otel query`/`status`
produce for the same condition.

**Why this is not a blocker:**

- The spec's only unavailability scenario is "Server is not running" (connection refused)
  (`specs/otel-cli-traces/spec.md:75`), and that path is correct and tested
  (`OtelTraces_ServerUnreachable_SurfacesStandardServerUnavailableMessage`).
- The behavior is identical to the deliberately-chosen `Run list` template
  (`MohistCliCommands.Run.Reads.cs` has no `TaskCanceledException` handling either), and design D1
  explicitly selected this path. The design Risks section (`design.md:62`) accurately scopes its
  parity claim to "on connection failure" rather than claiming timeout parity.
- There is no test asserting timeout behavior for `traces`, and the spec contains no timeout scenario.

**If a fixer wants to close the gap:** either catch `TaskCanceledException` in `GetDataOrPrintErrorAsync`
(cross-cutting; would also benefit `Run list`/`run view`/etc.), or add a local `try/catch` around the
`GetDataOrPrintErrorAsync` call in `RunTracesAsync` mirroring `RunQueryAsync`/`RunStatusAsync`, plus a
`OtelTraces_ServerTimeout_SurfacesStandardServerUnavailableMessage` spec mirroring the existing
`OtelQuery_ServerTimeout_*` case (`CliOtelCommandSpecs.cs:285`).

### F-2 (nit) — Projection spec case omits the `--service` filter the spec scenario names

`specs/otel-cli-traces/spec.md:45` specifies the selected-projection scenario as
`mo otel traces --service <name> --json trace_id,span_count`. The implemented test
`OtelTraces_SelectedJson_ProjectsOnlyChosenFields` (`CliOtelCommandSpecs.cs:606`) invokes
`otel traces --json trace_id,span_count` **without** `--service`. Filter forwarding and projection are
each tested independently (`OtelTraces_ServiceAndLimit_ForwardAsQueryParameters` and the projection
case), and the code path composes them correctly (`BuildTracesPath` runs before the fetch regardless
of JSON selection), so coverage is adequate. Adding `--service` to the projection case would make the
test read identically to the spec scenario; purely cosmetic.

### F-3 (nit) — `--service` value is trimmed before forwarding

`BuildTracesPath` (`MohistCliCommands.Otel.cs:142`) does `Uri.EscapeDataString(service!.Trim())`,
i.e. it strips surrounding whitespace before forwarding. Design D3 (`design.md:45`) phrases the
contract as forwarding `value` verbatim with exact-match semantics; trimming is a small, defensible
deviation (a whitespace-padded service name is almost certainly a mistake) but is not documented.
No behavioral risk; note only so a fixer can decide whether to align the implementation with the
design text or update the design text to mention the trim.

## Conclusion

The change correctly implements `mo otel traces` on the documented `GetDataOrPrintErrorAsync` path,
matches the Server's `TraceSummary` contract exactly, adds the required `TableShape`/`ResourceOutput`/
`CliFieldContract` registrations, ships focused spec coverage, closes the docs gap, and builds/tests
clean under warnings-as-errors. All acceptance criteria are met. The findings are non-blocking
observations consistent with the deliberately-chosen template and documented trade-offs.

<promise>PASS</promise>
