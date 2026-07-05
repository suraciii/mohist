# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/runner/src/system/process.ts:139
  Evidence: The per-command timeout path only resolves from the child `close` handler after `killProcess(child)` sends `SIGTERM` through the abort listener. `killProcess` is also SIGTERM-only (`process.kill(-child.pid, signal)` with default `SIGTERM`, then `child.kill(signal)`) at packages/runner/src/system/process.ts:214. A child or helper that traps/ignores SIGTERM can keep running and prevent `close`, so `runCommand` can remain pending past `timeoutMs` instead of returning the structured timeout result required by issue AC1 and `specs/command-timeout/spec.md`. The new tests cover cooperative hanging children but not an uncooperative child. [disallowed:product-behavior-change]
  SuggestedAction: Add a bounded hard-kill path for timed-out commands, e.g. SIGTERM group kill then SIGKILL group kill after a short grace, and resolve/reject according to the final enforced timeout contract. Add a controlled child test that installs a SIGTERM handler and proves `runCommand` still settles and the process tree is gone.
  Verification: `npm run typecheck -w packages/runner` passed. `npm test -w packages/runner` did not pass in the default full run; focused `tests/system-process-timeout.spec.ts` passed during the full run but lacks this case.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/runner/src/actions/github-pr-runtime.ts; packages/runner/src/actions/create-github-pr.ts; packages/runner/src/actions/mark-github-pr-ready.ts; packages/runner/src/actions/merge-github-pr.ts
  Evidence: `runGhPrecheck` preserves timeout metadata (`status`, `timeoutMs`) for `gh --version` / `gh auth status` failures, but callers always map any precheck failure to `config-error` (`create-github-pr.ts:86`, `mark-github-pr-ready.ts:58`, `merge-github-pr.ts:49`). A timed-out `gh auth status` therefore bypasses the required retry-safe classification path and is reported as a configuration problem, despite issue AC4 and `specs/network-command-timeout/spec.md` requiring network timeouts to classify as `retry-safe`. [disallowed:product-behavior-change]
  SuggestedAction: Classify precheck timeout results as `retry-safe` while preserving true installation/auth failures as `config-error`. Add tests for a D4-shaped timeout from `gh auth status` (and, if kept in scope, `gh --version`) in create, mark-ready, and merge actions.
  Verification: Existing timeout tests cover `gh pr create`, `gh pr ready`, and `gh pr merge`, but not precheck timeouts. `npm test -w packages/runner` failed in the default full run.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: packages/runner/src/actions/github-pr-status.ts
  Evidence: `githubPrStatusAction` applies `timeoutMs` to `gh pr view` and records timeout metadata in `steps`, but on failure it returns a plain `status: "failed"` output with no `errorCode` / `failureKind` and never calls the retry-safe classifier (`github-pr-status.ts:152`). The test `GhPrViewTimeout_SurfacesStepNameAndDuration` explicitly asserts this remains an unclassified plain failure, which conflicts with `specs/network-command-timeout/spec.md` lines 58-72 for network timeout classification. [disallowed:public-contract-change]
  SuggestedAction: Decide whether `mohist/github-pr-status` is inside the retry-safe contract. If yes, extend its output contract with a classification field or route timeout failures through a shared classified failure shape; if no, narrow the spec/task so this action is explicitly excluded.
  Verification: `tests/github-pr-status.spec.ts` passes but encodes the unclassified behavior; the default full runner test command failed elsewhere.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: packages/runner default test gate
  Evidence: The required verification command `npm test -w packages/runner` failed in the current snapshot with two 5s timeouts: `tests/executor-workspace-boundary.spec.ts > FirstDispatchPrepares_ThenReentriesReuseWithoutRecloning` and `tests/runner-host-cleanup-config.spec.ts > FetchesConfigOnEachCleanupTick_AndRunsEviction_WhenPollStays204`. Focused reruns of both files passed (`npm test -w packages/runner -- tests/executor-workspace-boundary.spec.ts` and `npm test -w packages/runner -- tests/runner-host-cleanup-config.spec.ts`), which points to suite-level flakiness/load sensitivity, but the default gate still does not pass. [disallowed:test-infrastructure-stabilization]
  SuggestedAction: Stabilize the timed-out specs or adjust their fake-timer/resource assumptions so the default `npm test -w packages/runner` gate passes reliably under full-suite load. Re-run the full default command after the fix.
  Verification: `npm run typecheck -w packages/runner` passed. `npm test -w packages/runner` failed; focused reruns of the two timed-out files passed.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: openspec/changes/issue-291/specs/network-command-timeout/spec.md; packages/runner/src/actions/git.ts
  Evidence: The spec and implementation include `gh --version` in the network timeout policy even though it is a local CLI version probe. This is mostly harmless but makes the network/local boundary less precise.
  SuggestedAction: Either keep it intentionally as part of the precheck timeout policy or narrow the spec/comment wording to call it a bounded precheck rather than a network command.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: warning
  Scope: packages/runner/src/actions/create-github-pr.ts
  Evidence: PR edit step summaries include the full PR body in `steps.command` (`pr edit ... --body "${body}"`). This predates the timeout metadata change, but it weakens the spec claim that command summaries are secret-free if issue/PR body text ever contains credentials or private tokens.
  SuggestedAction: Consider redacting large/free-form text values from command summaries and keeping full content only in the actual command args passed to `gh`.
  Status: pre-existing

<promise>FAIL</promise>
