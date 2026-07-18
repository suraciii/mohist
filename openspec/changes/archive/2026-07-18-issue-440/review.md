# Review: issue-440

## Findings

### P1: Missing recovery prompt bodies escape as generic executor failures

`renderFieldString` throws a plain `Error` when a recovery handler contains a
whole-string `${{ prompts.<key> }}` reference that is absent from the dispatch
variables ([recovery.ts](/home/szf/.mohist/projects/mohist-local/workspaces/issue-440/packages/runner/src/runtime/recovery.ts:276)). `tryRecovery` only converts
`UnresolvedFailureReferenceError` into the required recovery-construction
failure ([recovery.ts](/home/szf/.mohist/projects/mohist-local/workspaces/issue-440/packages/runner/src/runtime/recovery.ts:47)); it rethrows this error instead.
The outer executor catch then reports a generic task failure, without the
recovery task id or a recovery-context diagnostic ([executor.ts](/home/szf/.mohist/projects/mohist-local/workspaces/issue-440/packages/runner/src/runtime/executor.ts:206)).

The approved plan explicitly requires missing `variables.prompts` or a missing
prompt key to fail loudly during recovery construction, and calls for a
diagnostic that identifies the recovery task. Convert this failure into the
same construction-result path used for unresolved `failure.*` references (or
otherwise return a failed `WorkItemResult` that names both the recovery task and
`${{ prompts.<key> }}`), and add coverage for the absent prompt registry/key.

## Verification

- `npm run typecheck -w packages/runner` passed.
- `npm test -w packages/runner` passed: 95 files, 1172 tests.

<promise>FAIL</promise>
