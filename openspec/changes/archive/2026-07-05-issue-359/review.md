# Review Report

## Result: PASS

Current candidate satisfies the issue acceptance criteria after one local documentation repair. Evidence checked:

- AC1/AC4: `GET /api/runner/{runnerId}/config` is implemented at `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:139` and projects server-bound `CleanupPolicyOptions` through `ToCleanupPolicyDto` at `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:142` and `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:483`.
- AC2/AC5: `RunnerHost.runCleanupOnce` fetches config on each cleanup pass and passes the fetched policy into `cleanupLoop.runOnce` at `packages/runner/src/runtime/host.ts:173`; `ServerConnection.fetchConfig` performs a plain GET and unwraps `cleanupPolicy` at `packages/runner/src/server/connection.ts:34`.
- AC3: `/poll` still returns 204 when idle at `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:96`, and `WorkDispatchResponse` no longer contains `CleanupPolicy` at `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:577`; the runner type similarly has no `cleanupPolicy` field at `packages/runner/src/core/types.ts:69`.
- AC6: Server endpoint, poll contract, runner fetch, idle cleanup, no-cache, all-null policy, and failure-skip/retry behavior are covered by `packages/server/tests/Mohist.Server.Tests/Specs/Runner/Api/RunnerConfigApiSpecs.cs:35`, `packages/runner/tests/server-connection-fetch-config.spec.ts:20`, and `packages/runner/tests/runner-host-cleanup-config.spec.ts:190`.
- AC7: The breaking wire-contract removal is explicit in `openspec/changes/issue-359/design.md:122` and implemented atomically on both server and runner.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: typos | stale-adjacent-doc-comment
  Evidence: `packages/server/src/Mohist.Server/Infrastructure/Config/CleanupPolicyOptions.cs:4` still described the cleanup policy as exposed through the runner poll response, which is stale after this issue split policy transport into `/config`. Updated the XML comment to say the policy is exposed through the dedicated runner config endpoint. No product behavior changed.
  Verification: `dotnet test Mohist.sln -p:SkipWebBuild=true --filter FullyQualifiedName~RunnerConfigApiSpecs` passed: 9 passed, 0 failed.
  Status: resolved

## Blocking Items

- None.

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: info
  Scope: branch integration state
  Evidence: `git status -sb` reports the review branch as ahead 9 and behind `origin/master` by 13 commits. The behind commits touch broad areas including docs, issue templates, server test support, and web issue-template code; no candidate-specific failure was observed in the reviewed snapshot.
  SuggestedAction: Rebase or merge latest `origin/master` before integration if the workflow does not do that automatically, then rerun the same server and runner verification.
  Status: out-of-scope

- [ID: item-3]
  Severity: info
  Scope: verification noise
  Evidence: A duplicate concurrent `npm test -w packages/runner` invocation timed out once in `runner-host-cleanup-config.spec.ts` while another overlapping full test run was executing. The affected spec then passed in isolation, and `npm test -w packages/runner` passed on a clean rerun. This is not treated as a candidate failure.
  SuggestedAction: Avoid running multiple full runner suites concurrently when using timing-sensitive fake-timer host specs.
  Status: out-of-scope

## Verification

- Read issue 359 via `mo issue show 359 --project-id proj_f6c141d63b6243bfbb481737b2243b87`.
- Read `openspec/changes/issue-359/proposal.md`, `design.md`, `tasks.json`, `self-review.md`, and all delta specs under `openspec/changes/issue-359/specs/`.
- Inspected `git diff master...HEAD`, all changed product files, all changed tests, and adjacent cleanup/recovery paths including `cleanup-loop.ts` and existing runner host mocks.
- `git diff --check master...HEAD` passed.
- `npm run typecheck -w packages/runner` passed.
- `npm test -w packages/runner -- tests/runner-host-cleanup-config.spec.ts` passed: 5 passed.
- `npm test -w packages/runner` passed: 67 files, 922 tests.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 269 files, 4299 passed, 1 skipped.
- `dotnet test Mohist.sln -p:SkipWebBuild=true` passed: 3821 passed, 13 skipped.
- `dotnet test Mohist.sln -p:SkipWebBuild=true --filter FullyQualifiedName~RunnerConfigApiSpecs` passed after the documentation repair: 9 passed.

<promise>PASS</promise>
