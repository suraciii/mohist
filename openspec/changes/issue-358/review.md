# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs
  Evidence: The issue requires preserving the unconfigured `Mohist:SystemUpdate:Enabled` default behavior, and the delta spec requires unset, null, empty, and whitespace values to avoid `update_disabled` (`openspec/changes/issue-358/specs/system-update-start-gate/spec.md:25`). The candidate implementation does return the preserved default (`packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs:607` through `:616`), but the new start-path specs only cover explicit `"false"` and explicit `"true"` (`SystemUpdateServiceSpecs.cs:203`, `:230`, `:258`). Existing `CreateService` calls still inject explicit `"true"` by default (`SystemUpdateServiceSpecs.cs:1631` through `:1638`), and the new configurable overload always adds the `Mohist:SystemUpdate:Enabled` key with the supplied value (`SystemUpdateServiceSpecs.cs:1647` through `:1651`). A grep of `SystemUpdateServiceSpecs.cs` finds no `enabled: null`, empty-string, whitespace, or omitted-key start-path spec. This leaves the AC "Do not change Enabled unconfigured default behavior" and the spec scenarios at `spec.md:29` and `:34` unprotected. [disallowed:review repair would add new behavioral coverage beyond small formatting/expectation cleanup]
  SuggestedAction: Add focused `SystemUpdateServiceSpecs` coverage for missing key, null, empty string, and whitespace `Enabled` values. The assertions should show `StartAsync` does not return `Code = "update_disabled"`; for an otherwise startable local-source install, it should reach the same command execution/start decision as the current default-enabled path. The factory should be able to omit the key entirely, not only set it to null.
  Verification: Run `dotnet test Mohist.sln -p:SkipWebBuild=true --filter FullyQualifiedName~SystemUpdateServiceSpecs` and then `npm test`.
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: openspec/changes/issue-358/tasks.json
  Evidence: `tasks.json` line 25 reports `packages/server/src/MohistServer/SystemInfo/SystemUpdateService.cs`, but the real path is `packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs`. This does not affect the product deliverable or merge safety because the actual committed file is correct, but it weakens artifact traceability for later readers.
  SuggestedAction: Correct the path in the workflow artifact during a future artifact cleanup pass.
  Status: follow-up

## Pre-existing or Out-of-scope Items

None.

## Verification

- Read current issue 358 via `mo issue show 358 --project-id proj_f6c141d63b6243bfbb481737b2243b87`.
- Read proposal, design, tasks, delta spec, self-review, and all changed product files.
- Inspected committed diff against `master`: only workflow artifacts, `SystemUpdateService.cs`, and `SystemUpdateServiceSpecs.cs` changed.
- Ran `git diff --check master...HEAD`: no whitespace errors.
- Ran `npm test`: `dotnet test` passed with `3770` passed, `13` skipped; workspace tests passed with `65` files and `908` tests.

<promise>FAIL</promise>
