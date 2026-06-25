# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/runner/`, `packages/web/`, and non-CLI server/web tests
  Evidence: The candidate includes product and test-config changes outside the issue-254 CLI refactor boundary: `packages/runner/src/runtime/executor.ts` changes workspace precheck behavior from `verify` to `ensure`; `packages/runner/vitest.config.ts` adds a runner-wide `testTimeout`; `packages/web/vite.config.ts` excludes `**/e2e/**`; `packages/web/src/pages/settings/ui/AgentSettingsSection.tsx` changes an error color; multiple web tests and server API/skills tests are modified. Issue 254 is scoped to splitting CLI module complexity, with explicit non-goal "不改 server / runner / web" in `openspec/changes/issue-254/design.md`. These changes are not workflow artifacts and are product/test-surface changes outside the candidate deliverable. [disallowed:reason] Reverting or deciding whether to keep these changes is broader scope cleanup and may discard intentional work from another task.
  SuggestedAction: Remove unrelated runner/web/server-test changes from the issue-254 candidate, or move them to a separate issue/change with its own review.
  Verification: `git diff --name-status master...HEAD` should contain only issue-254 workflow artifacts plus the CLI implementation/tests needed for the refactor.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/cli/Mohist.Cli/InfoVerboseCollector.cs`, `packages/cli/Mohist.Cli/InfoCollector.ExecPathParser.cs`, `openspec/changes/issue-254/specs/cli-module-structure/spec.md`
  Evidence: The current `scc packages/cli/Mohist.Cli --by-file --format csv --sort complexity` output still places issue-254 successor files in the CLI package top five by complexity: rank 4 is `InfoVerboseCollector.cs` with complexity 65 and rank 5 is `InfoCollector.ExecPathParser.cs` with complexity 58. The spec requires that the target files and successors are not in the top five (`spec.md` lines 120-124), and the issue acceptance criterion requires the three hotspots to leave the CLI top five.
  SuggestedAction: Further split or simplify the info verbose collection and exec path parsing responsibilities until no issue-254 successor file appears in the top five single-file complexity rows.
  Verification: Re-run `scc packages/cli/Mohist.Cli --by-file --format csv --sort complexity` and record the top five rows with no `InfoCollector*`, `InfoVerboseCollector`, `InfoRenderer`, `SystemdUnitParser`, `TableRenderer*`, `MohistCliCommands.Update*`, or `Update/*` files present.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: `packages/cli/Mohist.Cli/InfoCollector.ExecPathParser.cs`
  Evidence: `InfoCollector.ExecPathParser.cs` is an `internal sealed partial class InfoCollector` file that holds path/process heuristics as internal static methods. The approved spec says path and process heuristics should remain private static details of the collector (`spec.md` lines 65-86), while the broader issue principle says partial should not be used to split a god class except for the table-renderer peer-case scenario. This file reduces line count but keeps the collector partial and exposes heuristics internally, so the implementation does not match the stated responsibility boundary.
  SuggestedAction: Move the heuristics back into `InfoCollector.cs` as private static details, or promote them to a clearly named standalone collaborator and update the spec/acceptance evidence accordingly.
  Verification: Confirm `InfoCollector` is no longer partial for this purpose, or that the replacement standalone type is covered by spec and tests; confirm `rg "partial class InfoCollector|internal static string\? ExtractProjectPath|internal static string\? ResolveSourcePath" packages/cli/Mohist.Cli` matches the intended boundary.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: `openspec/changes/issue-254/acceptance-evidence.md`, CLI behavior preservation
  Evidence: `acceptance-evidence.md` contains placeholders rather than concrete post-refactor evidence: it says to run `scc ...` and lists "Expected evidence" but does not record actual top-five rows. It also claims existing CLI tests exercise update/info command behavior, but the issue acceptance requires byte-for-byte output/parameter/exit-code preservation, and the changed tests include added/updated internal specs rather than a documented before/after byte comparison. The current focused CLI suite does pass, but the evidence artifact does not substantiate the byte-for-byte acceptance criterion.
  SuggestedAction: Replace placeholders with actual `scc --by-file` top-five rows and explicit behavior-preservation evidence for representative `mo update`, `mo info`, and table-output commands, including stdout/stderr/exit-code comparison or an equivalent approved baseline mechanism.
  Verification: Review `acceptance-evidence.md` for concrete command outputs/values, then run the listed commands.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: warning
  Scope: npm dependencies
  Evidence: `dotnet build Mohist.sln` succeeded with `0 Warning(s)` and `0 Error(s)`, but the build step reported `9 vulnerabilities (3 moderate, 3 high, 3 critical)` from npm audit. This appears unrelated to the CLI refactor candidate.
  SuggestedAction: Track npm audit remediation separately.
  Status: out-of-scope

- [ID: item-6]
  Severity: info
  Scope: verification
  Evidence: `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj` passed: 151 passed, 0 failed. `dotnet build Mohist.sln` passed: 0 warnings, 0 errors.
  SuggestedAction: Keep these commands in the final candidate evidence after resolving blocking items.
  Status: out-of-scope

<promise>FAIL</promise>
