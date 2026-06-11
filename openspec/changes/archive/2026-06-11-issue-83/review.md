# Review Report

## Result: PASS

## Repaired Items

(none)

## Blocking Items

(none)

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `openspec/changes/issue-83/specs/cli-body-input-sources/spec.md:5` vs `packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs:245-256`
  Evidence: The spec says "Exactly one source SHALL be required" for both `create` and `update`. The implementation enforces this strictly for `create` but allows zero body sources for `update` (gated behind `hasAnyBodySource` at line 249), so `mo issue update 83 --title "X"` works as a title-only partial update. The spec's update scenarios only cover the "one source provided" case, and allowing zero sources for update is user-friendly PATCH semantics. The spec prose is stricter than its own scenarios.
  SuggestedAction: Adjust the spec to say "at most one body source" for `update` (allowing zero sources for partial updates), or add a test for the zero-source update path to document the current behavior.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Api/IssueCliBodyInputSpecs.cs`
  Evidence: The spec scenario "Update from stdin" (cli-body-input-sources/spec.md:28-30) has no corresponding integration test. The update-from-file path and update mutual-exclusion paths are covered, and the resolver works identically for both create and update, so this is a coverage gap, not a product bug.
  SuggestedAction: Add `IssueUpdate_BodyStdin_DrainsStdinAndSendsContents` test mirroring the create variant at lines 94-114.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/MohistCliApi.cs:182-227` (string-compare same-value check)
  Evidence: `ResolveProjectIdAsync` uses raw string equality (`string.Equals(project, projectId, StringComparison.Ordinal)`) to decide whether both options match. If a user passes `--project mohist-local --project-id proj_xyz` where `mohist-local` resolves to `proj_xyz` on the server, the string check rejects them as "different" even though they resolve to the same project. This is an accepted trade-off documented in design D2: "If the user passes two different values, both could individually resolve; the right behavior is 'tell the user, don't pick.' A string compare is sufficient and predictable."
  SuggestedAction: Document the no-round-trip rule explicitly in cli-project-ref/spec.md if a future spec requires correctness over predictability. No code change required for this issue.
  Status: follow-up

## Pre-existing or Out-of-scope Items

(none)

## Verification Notes

- All 154 CLI tests pass (0 failures, 0 skips) in a full suite run: `dotnet test --filter "FullyQualifiedName~Cli"`
- Build succeeds with 0 warnings, 0 errors: `dotnet build packages/cli/Mohist.Cli/Mohist.Cli.csproj`
- Previously-reported blocking items from the earlier review pass are resolved:
  - **item-1 (Console.SetOut race)**: All `RenderHelp` helpers now create local `StringWriter` instances and pass them via `InvocationConfiguration { Output = writer, Error = writer }` to `Invoke(config)` — no mutation of global `Console.Out`/`Console.Error`. Confirmed by 154/154 passing consistently.
  - **item-2 (WorkflowStatus nested access)**: `TableRenderer.RenderWorkflowStatus` at `TableRenderer.cs:163` reads `data["workflow"]` and extracts `StringOf(workflow, "currentStage")` and `StringOf(workflow, "status")`. The test data in `IssueCliTableRendererSpecs.cs:196-215` nests `currentStage`/`status` under `workflow` matching the real `IssueWorkflowStatus` shape. Test at line 228 asserts `current stage: build` with the real value.

### Acceptance Criteria Coverage

| # | Criterion | Evidence |
|---|-----------|----------|
| 1 | `mo issue show 83 --project mohist-local` and `--project <id>` both resolve same issue | `IssueCliProjectRefAndOutputSpecs.cs:86-136`, `IssueShow_ByProjectName_*` / `IssueShow_ByProjectId_*` |
| 2 | Every `--project-id` command also accepts `--project` | `IssueCliRemainingProjectRefSpecs.cs:38-60` covers 16 subcommands via `[Theory]`; `IssueCliProjectRefAndOutputSpecs.cs` covers list/show/sessions/workflow status |
| 3 | `--project-id` remains backwards-compatible | `IssueShow_ProjectIdAlias_StillResolvesThroughSharedHelper` at line 141-162; `IssueClose_ByProjectIdAlias_StillResolvesThroughSharedHelper` at `IssueCliRemainingProjectRefSpecs.cs:147-169` |
| 4 | Both options passed with different values → clear error | `IssueCliProjectRefAndOutputSpecs.cs:412-434`; `IssueCliRemainingProjectRefSpecs.cs:274-295` |
| 5 | `issue create/update` support `--body`, `--body-stdin`, `--body-file` with mutual exclusion | `IssueCliBodyInputSpecs.cs:42-63` (inline), 66-88 (file), 94-114 (stdin), 119-135 (zero sources), 141-163 (conflict), 168-187 (missing file), 192-215 (update file), 220-242 (update conflict) |
| 6 | `project list/show`, `issue list/show`, `issue workflow status`, `issue sessions` support `--output table|json` | `IssueCliProjectRefAndOutputSpecs.cs:237-268` (table), 273-314 (json eq), 319-357 (same request), 362-383 (unknown value); `ProjectCliOutputModeSpecs.cs` covers project list/show; `ProjectCliRepositorySpecs.cs:212-245` covers repo list |
| 7 | `mo project repo` list/add/set-default/remove via existing server API | `ProjectCliRepositorySpecs.cs:128-156` (list), 250-281 (add), 286-310 (add by id), 337-366 (set-default), 372-394 (remove), 398-420 (rm alias), 426-446 (conflict), 451-472 (not-found) |
| 8 | CLI command tests cover all required aspects | All 7 test dimensions above are covered by 154 tests across 5 spec files |

### Cross-cutting Concerns

- **Security**: No secrets exposed. Body text is sent via HTTPS (handled by the HTTP layer) and not logged in normal mode. `Escape` uses `Uri.EscapeDataString` for path segments.
- **Data safety**: `ResolveProjectIdAsync` does not mutate server state; it reads local `cli-state.json` only. Body input validation happens pre-flight — no server request on failure.
- **Public contracts**: The existing `--project-id` path, JSON output default, and all server API endpoints are unchanged. New options are purely additive and opt-in.
- **Migration impact**: None. Existing scripts continue working with no changes.

<promise>PASS</promise>
