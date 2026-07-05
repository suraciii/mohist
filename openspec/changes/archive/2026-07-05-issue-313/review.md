# Review Report

## Result: PASS

## Repaired Items

None.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: packages/runner/src/server/followup-handler.ts / openspec/changes/issue-313/specs/runner-signalr-push-handlers/spec.md
  Evidence: The spec scenario says whitespace-only followup text should be dropped (`openspec/changes/issue-313/specs/runner-signalr-push-handlers/spec.md:73-76`), but the runner guard only rejects non-string or zero-length text (`packages/runner/src/server/followup-handler.ts:54`), and the regression test only covers `text: ""` (`packages/runner/tests/runner-signalr.spec.ts:701-716`). This is not blocking for this refactor because the issue explicitly required preserving followup behavior and the merge-base implementation used the same length-only guard.
  SuggestedAction: Align the spec/test wording with the intentionally preserved runner behavior, or explicitly decide to treat whitespace-only SignalR followups as unusable in a separate behavior-change issue.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: packages/runner/src/server/workspace-git-handlers.ts / packages/runner/tests/runner-signalr.spec.ts
  Evidence: The git handler cluster is now extracted into `registerWorkspaceGitHandlers`, but handler coverage still reaches it through `RunnerSignalRClient` and the SignalR builder mock (`packages/runner/tests/runner-signalr.spec.ts:1262-2175`). This satisfies the issue's test-first contract and passed, but it leaves the new free-function dependency surface without direct unit coverage.
  SuggestedAction: Add a small `workspace-git-handlers.spec.ts` later that registers the handlers against a minimal fake connection and injected deps, keeping the large SignalR client spec focused on integration-level contract checks.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: warning
  Scope: packages/runner/src/runtime/workspace-query.ts / packages/runner/src/server/workspace-removal-handler.ts
  Evidence: `isUnderRunnerRoot` intentionally returns true for the runner root itself (`packages/runner/src/runtime/workspace-query.ts:37-42`), so `RemoveWorkspace` would accept `workspacePath === runnerRoot` and call `deleteDirectory` on the whole runner root (`packages/runner/src/server/workspace-removal-handler.ts:49-61`). The merge-base helper had the same `rel === ""` behavior, so this is not introduced by issue 313, but it is a data-safety edge near the touched removal path.
  SuggestedAction: Consider a future tightening that distinguishes "inside runner root" from "the runner root itself" for destructive workspace removal.
  Status: pre-existing

## Verification

- `npm run typecheck -w packages/runner` passed.
- `npm test -w packages/runner` passed: 71 files, 997 tests.
- `npm test -w packages/runner -- tests/runner-signalr.spec.ts tests/runner-signalr-workflow-status.spec.ts tests/workspace-registry-integration.spec.ts` passed: 3 files, 105 tests.
- `git diff --check origin/master...HEAD` passed.
- Static inspection confirmed the prior failed review items are resolved: outside-root `RemoveWorkspace` now checks containment before registry mutation, and the reconnect interval sequence is asserted in `runner-signalr.spec.ts`.

<promise>PASS</promise>
