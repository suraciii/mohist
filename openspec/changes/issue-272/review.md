# Review Report

## Result: FAIL

## Repaired Items

- (none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/runner/src/actions/workspace-prepare.ts`
  Evidence: Git probe failures are silently converted into clean state instead of failing with diagnostics. `captureSnapshot` discards a failed `git status --porcelain` by setting `porcelain` to an empty string (`workspace-prepare.ts:143-150`), `probePathExists` treats a failed `git rev-parse --git-path ...` as `false` (`workspace-prepare.ts:200-203`), and `isCleanAndAligned` then accepts empty porcelain plus false residual flags as success (`workspace-prepare.ts:210-217`). A workspace where `git status --porcelain` fails but `rev-parse --abbrev-ref HEAD` returns the expected branch can therefore return `status: success`, violating issue AC6 and spec lines 53-62 requiring any failing git command to fail the task with `failureKind`, failing step, current HEAD, and residual diagnostics. [disallowed:product-behavior-change]
  SuggestedAction: Make snapshot/probe helpers preserve git command failures and return a `workspace-setup` failure for failed status, head, or residual probes. Add focused tests for failed initial `status --porcelain`, failed `rev-parse HEAD`, failed `rev-parse --abbrev-ref HEAD`, and failed `rev-parse --git-path` during residual detection.
  Verification: Run `npm run typecheck -w packages/runner`, `npm test -w packages/runner`, and a targeted workspace-prepare spec covering probe failures.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/runner/src/actions/workspace-prepare.ts`
  Evidence: A workspace that is both on the wrong/detached branch and has local modifications can fail at checkout before the destructive cleanup step runs. The action checks out the expected branch first (`workspace-prepare.ts:96-103`) and only later checks porcelain and runs `git reset --hard HEAD` plus `git clean -fd` (`workspace-prepare.ts:105-121`). Git commonly rejects checkout when local changes would be overwritten, so this combined dirty/wrong-branch state fails without satisfying issue AC5's cleanup requirement. This is exactly the kind of dirty stage-start workspace the issue asks `workspace-prepare` to recover. [disallowed:product-behavior-change]
  SuggestedAction: Handle the combined state explicitly, for example by discarding local changes before branch checkout when no residual operation remains, then verifying the final branch and clean tree. Add a test where `status --porcelain` is dirty, `rev-parse --abbrev-ref HEAD` reports another branch or `HEAD`, and checkout would fail unless reset/clean happens first.
  Verification: Run `npm test -w packages/runner -- workspace-prepare` or the repo's supported equivalent, then `npm test -w packages/runner`.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: retry/recovery and stage-boundary behavior
  Evidence: Issue AC8 requires the reproduced scenario: integrate-stage rebase conflict fails, `rerun` starts with `workspace-prepare`, the residual rebase is cleaned, and subsequent tasks proceed. The candidate adds action-level fake-git tests (`packages/runner/tests/workspace-prepare.spec.ts`) and profile text/parse assertions (`packages/runner/tests/workflow-profile.spec.ts`, server profile specs), but there is no executor/workflow-level regression that exercises rerun after a failed stage or proves recovery tasks are not preceded by a fresh prepare in actual run scheduling. The profile assertions prove YAML shape, not the post-failure rerun behavior described by the acceptance criterion. [disallowed:test-coverage]
  SuggestedAction: Add a workflow/executor regression using fakes that drives a stage failure with residual rebase state, calls rerun, asserts the first dispatched task is `workspace-prepare`, and verifies the next business task can run after cleanup. Also assert recovery-inserted tasks are scheduled without an extra prepare.
  Verification: Run the new regression plus `npm test -w packages/runner` and `npm test`.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: `packages/runner/tests/workspace-prepare.spec.ts`
  Evidence: The fast-pass requirement says the clean path must complete in under one second. The current test only asserts no mutating git commands are issued (`workspace-prepare.spec.ts:101-127`); it does not measure or otherwise bound elapsed time. The implementation performs multiple git subprocess probes even on the fast path (`workspace-prepare.ts:143-148`, `workspace-prepare.ts:174-181`), so the timing claim is not verified against a real workspace.
  SuggestedAction: Add a small real-git or controlled integration benchmark for the clean path, or relax the acceptance criterion if wall-clock timing is not intended to be enforced in tests.
  Verification: Run the added timing/integration check and `npm test -w packages/runner`.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: workflow profile naming
  Evidence: The issue text names `mohist/default` in AC7, while the implementation and current codebase use `mohist/local` (`mohist-local.workflow.yaml`) and `mohist/github-pr`. The self-review records this as a stale issue-name mismatch, and the chosen target appears consistent with current server constants and tests.
  SuggestedAction: Update the issue/spec wording in a future cleanup so acceptance criteria use the canonical `mohist/local` profile id.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: existing `WorkspaceManager` prepare/verify path
  Evidence: The existing runner workspace path already performs implicit residual cleanup in `WorkspaceManager.reenterRunBranch` and `runHealthGate` before ordinary task dispatch (`workspace.ts:217-249`). This means the new explicit action is layered on top of an existing hidden cleanup path. That overlap is intentional per the design and not a candidate defect by itself, but it makes action-level tests insufficient to prove the user-visible rerun behavior.
  SuggestedAction: Keep the layering documented and prefer workflow-level regression tests for future changes to this area.
  Status: out-of-scope

<promise>FAIL</promise>
