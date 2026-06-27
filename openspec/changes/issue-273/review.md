# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/runner/src/actions/openspec.ts
  Evidence: `archiveChangeAction` now trusts `_actions.archiveChange.destination[sourceName]` when it is any string (`packages/runner/src/actions/openspec.ts:229-231`) and later passes that string through `resolve(join(archiveDir, baseName))` in `uniqueDestination` / `findExistingArchive` (`packages/runner/src/actions/openspec.ts:437-453`). A user-supplied or corrupted run variable such as `{ "_actions.archiveChange.destination": { "issue-127": "../../escaped" } }` makes the archive destination resolve outside `openspec/changes/archive`, so the action can move the change directory to an unintended path. This is a path traversal/data-safety regression in the new mid-execution variable read path. [disallowed:data-safety behavior and validation policy]
  SuggestedAction: Validate persisted archive names before using them as path segments. Accept only runner-generated safe basenames, or at minimum reject `.`/`..`, path separators, absolute paths, and any resolved destination that is not inside `archiveDir`. Add regression tests for malicious persisted values like `../escaped`, `../../escaped`, and `nested/name`.
  Verification: Inspected `packages/runner/src/actions/openspec.ts:229`, `packages/runner/src/actions/openspec.ts:437`, and `packages/runner/src/actions/openspec.ts:446`. Existing archive tests cover happy-path persisted names but no unsafe persisted-name case.
  Status: open

- [ID: item-2]
  Severity: cleanup
  Scope: packages/runner/tests and packages/runner/tsconfig.json
  Evidence: `ActionContext.writeVars` is now a required field (`packages/runner/src/core/types.ts:88-94`), but multiple typed runner test helpers still return `ActionContext` objects without it, for example `packages/runner/tests/create-github-pr.spec.ts:39-62`, `packages/runner/tests/merge-github-pr.spec.ts:32-55`, `packages/runner/tests/expectations.spec.ts:9-22`, and `packages/runner/tests/acp/support.ts:83-104`. The normal runner typecheck does not catch this because `packages/runner/tsconfig.json:15` includes only `src`. A direct strict compile of one affected test file fails with `TS2741: Property 'writeVars' is missing ... but required in type 'ActionContext'`. [disallowed:broad test cleanup outside the focused review repair scope]
  SuggestedAction: Add a no-op `writeVars` to all typed `ActionContext` test helpers and add a test typecheck target or tsconfig that covers `packages/runner/tests`.
  Verification: `npm run typecheck -w packages/runner` passed because tests are excluded. `npx tsc --noEmit --target ES2022 --module NodeNext --moduleResolution NodeNext --lib ES2022,DOM --types node --strict --skipLibCheck "packages/runner/tests/create-github-pr.spec.ts"` failed with TS2741.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: openspec/changes/issue-273/tasks.json
  Evidence: `openspec/changes/issue-273/tasks.json:23` and `openspec/changes/issue-273/tasks.json:47` still mark T-001 and T-002 with `passes: false`, while `openspec/changes/issue-273/progress.txt:1-32` records those tasks as implemented and verified. This does not change the product deliverable, but it weakens workflow traceability for the build artifacts used as review evidence.
  SuggestedAction: Keep task pass status synchronized with build completion when these artifacts are used for traceability.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- None.

## Acceptance Evidence

- AC1/AC2: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-github-pr.workflow.yaml:280-291` no longer declares `conflictMode`, and the inner `when: conflict` handler has no `retrySelf`; the parent `base-moved` handler still has `retrySelf: true` at line 300.
- AC3/AC4: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-github-pr.workflow.yaml:292-300` switches only the base-moved `recover:push` to `force: true`; `packages/runner/src/actions/push.ts:47-54` emits `--force` and skips `ls-remote` when `force` is true.
- AC5: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml:272-281` uses `mohist/archive-change` and has no matching rebase/push recovery config to sync.
- AC6/AC7: `packages/runner/src/actions/openspec.ts:229-271` reads/persists the archive name, and `packages/runner/src/runtime/executor.ts:578-594` wires `writeVars` to `connection.patchRunVars` before task completion. Item-1 blocks acceptance because the persisted name is not validated before path use.
- AC8: Runner push/archive tests and server workflow profile assertions were updated (`packages/runner/tests/push.spec.ts`, `packages/runner/tests/openspec.spec.ts`, `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Profile/MohistPrIssueWorkflowProfileSpecs.cs`). Item-2 leaves test-source type hygiene incomplete.

## Verification

- `npm run typecheck -w packages/runner`: passed.
- `npm test -w packages/runner`: passed, 659 passed / 23 skipped.
- `dotnet test Mohist.sln -p:SkipWebBuild=true --no-restore`: passed, 2785 passed / 12 skipped.
- `npx tsc --noEmit --target ES2022 --module NodeNext --moduleResolution NodeNext --lib ES2022,DOM --types node --strict --skipLibCheck "packages/runner/tests/create-github-pr.spec.ts"`: failed with TS2741, confirming item-2.

<promise>FAIL</promise>
