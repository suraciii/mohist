# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/cli/Mohist.Cli/MohistCliCommands.Epic.cs:19
  Evidence: The issue requires `mo epic pause {n}` and `mo epic resume {n}` for AC #5/#6 and the delta spec says the CLI SHALL expose `start`, `pause`, and `resume` lifecycle commands (`openspec/changes/issue-263/specs/epic-lifecycle/spec.md:248`). The candidate only registers `BuildStart(api)` plus existing list/create/show/update/link/unlink/done/close, with no `pause` or `resume` subcommands in `Build`; `BuildPause`/`BuildResume` are absent. [disallowed:product-behavior-change]
  SuggestedAction: Add `mo epic pause {id|number}` and `mo epic resume {id|number}` commands mirroring `start`, POSTing to `/api/projects/{project}/epics/{id}/pause` and `/resume`, honoring project/output options, and printing the resulting epic state.
  Verification: Add CLI specs for pause success, resume success, id/number resolution, output formatting, and API error surfacing; run `dotnet test Mohist.sln -p:SkipWebBuild=true` and verify `mo epic --help` lists `pause` and `resume`.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: packages/cli/tests/Mohist.Cli.Tests/CliEpicCommandSpecs.cs:338
  Evidence: The CLI help test now asserts only `list`, `create`, `show`, `update`, `link`, `unlink`, `start`, `done`, and `close`, so the test suite encodes the missing pause/resume commands instead of catching the spec violation. There are no `mo epic pause` or `mo epic resume` command tests in the CLI suite. [disallowed:test-coverage-gap]
  SuggestedAction: Update the help test to require `pause` and `resume`, and add request-path/output/error tests for both commands.
  Verification: Run the CLI test project and confirm the new tests fail before implementation and pass after implementation.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Events/Hosting/EpicReconciliationService.cs:99
  Evidence: The safety-net sweep now covers `running` epics, which correctly recovers missed terminal events, but it still uses the existing daily cadence. A missed cancel/done event can therefore leave a running epic idle for up to one day, reducing autonomy but not violating immediate event-driven correctness.
  SuggestedAction: Consider a shorter reconciliation cadence for running epics while keeping the slower idle auto-done sweep.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: validation
  Evidence: `npm test` passed the .NET server suite (`2671` passed, `10` skipped) but then exceeded the 120s command timeout while entering workspace web tests. Targeted validation was conclusive: `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, and `npm test -w packages/runner` passed.
  SuggestedAction: If an aggregate CI-equivalent result is required, rerun `npm test` with a longer timeout.
  Status: out-of-scope

<promise>FAIL</promise>
