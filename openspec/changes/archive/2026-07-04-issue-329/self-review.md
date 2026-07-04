# Self Review Report

## Result: PASS

## Repaired Items

_None — no safe mechanical repairs were required. The plan is internally consistent and faithfully traces the issue's acceptance criteria through proposal → design → specs → tasks._

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: consistency
  Evidence: The set of "duplicated envelope extraction blocks" is described inconsistently across artifacts. The design (Context point 2 and D3) lists exactly 6 internal methods carrying the full `success`/`error`/`code` extraction — `PrintResponseAsync`, `PrintRawResponseAsync`, `ReadPostResultAsync`, `ReadSuccessDataAsync`, `PrintProjectListAsync`, `PrintSystemInfoAsync` — and separately calls out 2 external rewrites (`Agent.cs:860`, `Otel.cs:181`). The proposal (Impact), the `cli-api-envelope` spec, and task T-002 additionally include `PrintRunnerShowAsync` in the list and use "6+". Source verification (`packages/cli/Mohist.Cli/MohistCliApi.cs`) confirms the `node["success"]?.GetValue<bool>() ?? response.IsSuccessStatusCode` pattern occurs at exactly 6 internal sites (L63, L533, L1011, L1030, L1054, L1080); `PrintRunnerShowAsync` (L286) does NOT contain the `success` fallback — it only duplicates the `error`/`code` printing (L317–319). Thus the design's narrower count (6, for the full `success`+`error`+`code` pattern) is technically accurate, while the proposal/spec/task are more inclusive (also covering the partial `error`/`code` duplication). The task T-002 is authoritative for implementation scope and is correctly inclusive (says "6+ scattered extraction blocks"), so the implementation will be source-driven and will not miss anything.
  SuggestedAction: Optional wording alignment only — add a one-line note in design D3 acknowledging that `PrintRunnerShowAsync` shares the `error`/`code` extraction (though not the `success` fallback) and will also delegate to `ExtractEnvelope`. This is a scope judgment, not a mechanical fix, so it was not applied during self-review to avoid altering the declared scope. No change to proposal/spec/task is needed (their "etc." / "6+" hedging already covers it).
  Status: follow-up

## Repaired Items

- [ID: item-2]
  Severity: info
  Scope: dependencies
  Evidence: Verified that T-002 (priority 2) has `dependsOn: []` despite being a "non-first" task. Confirmed this is appropriate, not a gap: T-002 edits `packages/cli/Mohist.Cli/MohistCliApi.cs` while T-001 edits `packages/server/.../ConfigService.cs` — different packages, zero compile coupling, safe to run in parallel. The dependency_completeness criterion asks for *appropriate* `dependsOn`; an empty list is the correct value here (forcing T-002→T-001 would introduce a false dependency). The genuine file-coupling chain is T-002 ← T-003 ← T-004 (all three edit `MohistCliApi.cs`), and that chain is correctly declared with priorities 2 < 3 < 4 and matching `dependsOn` entries. No repair needed; recorded as info to document the verification.
  Verification: Read tasks.json and confirmed T-003 `dependsOn: ["T-002"]`, T-004 `dependsOn: ["T-003"]`; both point to existing IDs with strictly lower priority; no cycle exists.
  Status: resolved (no change required)

---

## Detailed Review

### Alignment — PASS
- Issue AC1 (prelude helpers single implementation) → proposal What-Changes #1 → `cli-command-prelude` spec (both requirements) → T-004. ✓
- Issue AC2 (5 verb HTTP methods → one generic) → proposal What-Changes #2 → `cli-api-envelope` "Single generic HTTP request method" → T-003. ✓
- Issue AC3 (envelope parsing consolidated) → proposal What-Changes #3 → `cli-api-envelope` "Single envelope parsing implementation" → T-002. ✓
- Issue AC4 (legacy model fallback removed) → proposal What-Changes #4 → `agent-config-resolution` spec (all 3 requirements) → T-001. ✓
- Issue AC5 (no CLI spec regression) → `cli-api-envelope` "Unchanged success, failure, and not-found behavior" + every task's test acceptance criterion. ✓
- All Non-Goals honored: no command-tree regrouping (T-004 migrates call sites in place); no API-layer responsibility reshuffle (T-002/T-003 are internal refactors); no table-renderer changes; no observable behavior change except the one declared breaking exception (legacy `model` fallback).

### Completeness — PASS
- Every issue requirement is covered by at least one spec scenario.
- Every spec requirement is backed by a task whose acceptance criteria enumerate the scenarios:
  - T-001 AC ↔ all 7 scenarios of `agent-config-resolution` + the `GetAll_MasksSecrets` probe rework called out in design D4.
  - T-002 AC ↔ "Missing success field falls back to HTTP status" + "Unchanged behavior" + the PrintSystemInfoAsync/Otel.cs/ReadSuccessDataAsync edge cases from design Risks.
  - T-003 AC ↔ "All verbs route through one request path" + "Network failure prints the server-unavailable message" (including the newly-required bare-verb network-failure spec).
  - T-004 AC ↔ all 8 scenarios of `cli-command-prelude`.
- Edge cases enumerated: PrintSystemInfoAsync command-level recovery (kept就地), Otel.cs TaskCanceledException catch (untouched), ReadSuccessDataAsync ApiResponseException throw contract (preserved for its 7 callers), Agent.cs:868 potential `IsSuccessStatusCode ? null : null` bug (null semantics preserved), GetAll_MasksSecrets probe key swap (`model`→`logLevel`), bare-verb network-failure behavior change (crash→graceful exit 1, covered by spec).

### Consistency — PASS
- Capability names map 1:1 to spec directories: `cli-command-prelude`, `cli-api-envelope`, `agent-config-resolution`.
- All four `spec` anchor links resolve to actual `### Requirement:` headings in the spec files (slug match verified).
- Design decisions D1–D4 map to the four spec requirements; helper names (`ResolveOutputMode`, `ResolveProject`, `SendAsync`, `ExtractEnvelope`, `FailureExitCode`) are used consistently across design and tasks.
- The one inconsistency (PrintRunnerShowAsync membership in the extraction-block list) is recorded as follow-up item-1 and does not affect correctness since the task hedges with "6+".

### Feasibility — PASS
- No task title is a pure technical action ("define interface" / "extract class" / "register DI" / "create file" / pure rename). Each task delivers a complete feature slice with embedded verification.
- No task is a standalone "add tests" task — test rework is inline in each task's acceptance criteria (e.g. T-001 folds the ConfigServiceSpecs rework into the same task as the production change).
- Install/start/stop are not split out.
- T-002 and T-003 are deliberately separate (response parsing vs request sending — distinct concerns, sequenced by risk per design Risks); merging them would widen the regression surface, so the split is justified, not overly fine.
- Dependencies are all satisfiable: T-001 is server-only and independent; the CLI chain builds incrementally on `MohistCliApi.cs`.

### Dependency completeness — PASS
- T-001 `dependsOn: []` (priority 1, first task). ✓
- T-002 `dependsOn: []` (priority 2) — appropriate: different package from T-001, no compile coupling. ✓ (documented as item-2)
- T-003 `dependsOn: ["T-002"]` (priority 3) — both edit `MohistCliApi.cs`; T-002 lands first as lower-risk. ✓
- T-004 `dependsOn: ["T-003"]` (priority 4) — same file; largest sweep sequenced last. ✓
- All `dependsOn` IDs exist; all point to strictly lower priority; no cycle.

<promise>PASS</promise>
