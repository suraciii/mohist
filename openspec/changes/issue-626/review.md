# Issue 626 Review

## Verdict

PASS. No must-fix problems were found. The change is ready to merge.

## Review Basis

Reread the issue (626: "工作流恢复重放的任务被 schema 拒绝：core/script 未知输入 resourceProfile") before the diff. The issue's acceptance criteria are:

1. A `core/script` task (verify, or any task carrying a resource-profile input) that fails and is replayed through the recovery chain must no longer be rejected with `invalid-input`.
2. A regression test must pin the "replayed task definition is field-for-field identical to the first dispatch" contract.
3. Issue 617's run must be able to continue via retry once this fix ships.

Reviewed the plan artifacts (`proposal.md`, `design.md`, `specs/workflow-replay-input-compatibility/spec.md`, `tasks.json`, `self-review.md`, `progress.txt`) and the 8 changed product files against the base `bb0e0a1f2` (the issue-626 branch's merge base). This is a first review (no prior `review.md` exists).

## Must-Fix Findings

None.

## Dimension Checks

### Acceptance Coverage — checked, no issue

- **AC1 (replay/recovery no longer rejected):** implemented and covered. `normalizeWorkflowActionInput` ([packages/runner/src/actions/input-compatibility.ts](/home/szf/.mohist/projects/workspaces/wr_c68ac2ab5859460796a800bc7accae75/packages/runner/src/actions/input-compatibility.ts:9)) strips only the top-level `resourceProfile` key from the cloned execution input for Workflow `core/script` task dispatches, applied in `WorkExecutor.executeOne` ([executor.ts:164](/home/szf/.mohist/projects/workspaces/wr_c68ac2ab5859460796a800bc7accae75/packages/runner/src/runtime/executor.ts:164)) before unresolved-reference checks, deferred rendering, `validateActionInput`, work-directory resolution, cleanup-input construction, and handler invocation. Direct redelivery and `retrySelf` continuations both flow through this single rule. Covered by `executor-replay-compatibility.spec.ts` (direct replay for `ownerKind` omitted and `workflow`, and retrySelf continuation).
- **AC2 (field-for-field replay contract pinned):** the Runner spec asserts `work.with` deep-equals the raw declaration after execution, the continuation retains raw `with`/metadata and decrements `recoveryRemaining` exactly once, and the Server specs (`WorkflowItemTranslatorSpecs.TranslateToDispatch_PersistedCoreScriptTask_PreservesRawDeclarationAndTaskContract`, `DispatchSnapshotPersistenceSpecs.HistoricalCoreScript_RedeliveryPreservesRawDeclarationAndTaskContract`) assert `dispatch.With == JSON.Serialize(rawWith)`, `RecoveryRemaining` and recovery declaration preservation, and byte-identical persisted state before/after redelivery and grain deactivation.
- **AC3 (617 run can retry):** the mechanism that blocked the retry (Runner schema rejection of the persisted `resourceProfile`) is removed at the execution boundary, so a retried replay of the affected run now executes. The remaining part of this criterion (a live 617 run proceeding after deployment) is inherently operational — see Observations.

### Correctness — checked, no issue

Adversarial cases attempted:

- **Unresolved/invalid retired data:** a `resourceProfile` value containing `${{ vars.missing }}` or invalid types is removed before `unresolvedReferences` and schema validation; the run completes ([spec test](/home/szf/.mohist/projects/workspaces/wr_c68ac2ab5859460796a800bc7accae75/packages/runner/tests/executor-replay-compatibility.spec.ts) `ignores unresolved and invalid retired data`). Verified in code ordering: normalization at executor.ts:164 precedes the unresolved-reference check at :166-170 and `validateActionInput` at :175.
- **Strictness retained:** an unrelated unknown input alongside `resourceProfile` still yields `invalid-input` with no handler invocation; invalid `run`/`shell`/`timeout` values still yield type-validation failures; the `core/script` manifest and catalog still expose exactly `run` (required), `shell`, `timeout` (optional), no `resourceProfile`.
- **Scope:** `agent-job` dispatches, `checks` work, other Actions, and nested `resourceProfile` keys are untouched; `DispatchWorkItem.with` is never mutated (normalization operates on a `structuredClone` shallow projection).
- **New definitions:** `ActionContractValidatorTests` and `WorkflowProfileYamlParserTests` prove a new definition declaring `resourceProfile` is rejected as unknown input with path `stages[0].tasks[0].with.resourceProfile`, and the parser's catalog fixture matches the real Runner catalog.
- **Legacy envelope shapes:** the gate includes omitted/empty `ownerKind` (parse defaults `dispatch.ownerKind ?? undefined`) and case/whitespace variants of `uses` (registry resolves case-insensitively, gate trims+lowercases); server owner kinds are exactly `workflow`/`agent-job` and `WorkType` exactly `task`/`checks`.
- **Recovery budget/identity:** `tryRecovery` is untouched (raw `with` cloning, budget decrement), verified by the continuation test with `recoveryRemaining: 1 → 0`.

### Consistency — checked, no issue

The change follows the codebase's existing boundaries: execution-boundary normalization rather than persistence rewriting or a generalized validator exception; strict Server definition validation retained as the authority for new declarations; no database migration, public API, catalog, or syntax change; no in-place rewrite of persisted `TaskRun`/snapshot state. The Server production code is untouched — T-002 verified the existing translator/snapshot preservation behavior and added regression coverage only.

### Tests — checked, no issue

Independently re-ran on this tree at HEAD (`c730b0bc3`): full Runner suite 156 files / 1,689 tests pass (includes the 9 new replay-compatibility tests and existing executor/recovery suites); `Mohist.Server.UnitTests` 2,769 pass (includes `ActionContractValidatorTests`, `WorkflowProfileYamlParserTests`); `Mohist.Server.SpecTests` 3,875 pass in 2m07s (includes `WorkflowItemTranslatorSpecs`, `DispatchSnapshotPersistenceSpecs`, and the two modified OTLP registration specs). Runner typecheck and `biome check` on the changed files pass; working tree clean.

The branch's two verify-gate-only changes are sound: `test-duration.config.jsonc` final content is byte-identical to origin/master's version, and the OTLP change swappes only the exporter's `HttpClientFactory`/processor for an in-memory local-handler variant via `PostConfigure` on the same `"tracing"`/`"metrics"` option names production registers, leaving the registration and option assertions intact.

## Observations

1. **Checks path not covered:** a historical *check group* whose per-item `with` carried `resourceProfile` would still be rejected, because `executeCheckDispatch` ([check-execution.ts](/home/szf/.mohist/projects/workspaces/wr_c68ac2ab5859460796a800bc7accae75/packages/runner/src/runtime/check-execution.ts:93)) validates items without the normalizer. The issue and spec scope this to tasks ("携带资源剖面输入的任务"), the domain distinguishes tasks from checks, and no evidence exists that per-check `resourceProfile` was ever persisted, so this is speculative — recorded as an observation, not a must-fix.
2. **Gate asymmetry:** `workType === 'task'` is matched case-sensitively while `uses`/`ownerKind` are trimmed/lowercased. Server emission is exactly `"task"` today and historically, so no correctness impact; worth noting only for future retirements.
3. **AC3 is deployment-gated:** enabling a live 617 retry requires the fixed Runner to be deployed to the pool executing affected runs (also called out in the design's rollout/rollback notes). The code path is fixed; the criterion itself can only be confirmed operationally after release.
4. **No strip observability:** the design's open question about a low-cardinality diagnostic counter for stripped fields was not implemented. Not required for correctness; useful for planning removal of the compatibility rule later.
5. **Merge hygiene:** the branch re-derives origin/master's Spec-partitioning file change (`44af765da`) because the branch base predates master's `07d7d8aeb`; final file content matches master byte-for-byte, so the eventual merge should be clean. The OTLP test-isolation fix is unique to this branch and unrelated to the issue's behavior but required for a green gate.

<promise>PASS</promise>