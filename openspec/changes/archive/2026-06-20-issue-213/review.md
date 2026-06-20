# Review Report

## Result: PASS

## Repaired Items

_None._ No small local repairs were applied during review.

## Blocking Items

_None._ The post-build candidate satisfies the issue acceptance criteria after review.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/web/src/pages/runners/ui/RunnersPage.test.tsx:104`
  Evidence: The no-project acceptance criterion is implemented by `useRunners()` using `enabled: !!projectId` in `packages/web/src/entities/runner/api/queries.ts:11`, but the new no-project test only asserts the mocked `rows` array remains empty. That does not prove the query function was not enabled or invoked, so a regression in the hook/query wiring could escape this page test.
  SuggestedAction: Add a test with the real query hook or a query-client spy/MSW handler that proves no `/api/projects/{projectRef}/runners` request is issued when no project is selected.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/MohistCliApi.cs:187`
  Evidence: `mo runner list -o json` emits the filtered runner array directly, while most existing CLI JSON output prints the API `data` object. This is consistent with the implemented tests and still meets the issue criterion of valid JSON without table borders or color codes, but it is a subtle public-output shape difference for the new subcommand.
  SuggestedAction: Document the array output shape for `mo runner list -o json`, or switch to an object such as `{ "runners": [...] }` before users rely on the new command.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/TableRenderer.cs:838`
  Evidence: ANSI color escapes are inserted before table padding/truncation. The current four statuses are short and tests cover them, so this is not a current defect, but future longer colored values or different escape sequences could misalign/truncate table cells because `PadRight` and `Truncate` count escape bytes as visible width.
  SuggestedAction: If more colored table cells are added, centralize ANSI-aware width handling instead of embedding escape codes in cell strings.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: warning
  Scope: dependency audit / `dotnet build Mohist.sln`
  Evidence: The build succeeded, but the invoked web build printed `npm audit` results showing 9 vulnerabilities (3 moderate, 3 high, 3 critical). This appears unrelated to the runner-listing change and did not fail the build.
  SuggestedAction: Triage dependency vulnerabilities separately from issue 213.
  Status: pre-existing

- [ID: item-5]
  Severity: info
  Scope: .NET SDK / `dotnet build Mohist.sln`
  Evidence: The build printed NETSDK1057 preview-SDK informational messages for .NET 11 projects. This is expected for the repository's configured stack and did not produce warnings or errors.
  SuggestedAction: No action for this change.
  Status: out-of-scope

## Verification

- `mo issue show 213 --project-id proj_f6c141d63b6243bfbb481737b2243b87` confirmed the current issue acceptance criteria and scope.
- `git diff --name-only origin/master...HEAD` identified all changed files reviewed, including Web, CLI, tests, and workflow artifacts under `openspec/changes/issue-213/`.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 121 files, 1763 passed, 1 skipped.
- `dotnet build Mohist.sln` passed with 0 warnings and 0 errors.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj` passed: 114 tests.

<promise>PASS</promise>
