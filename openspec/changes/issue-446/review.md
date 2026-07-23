# Review

## Findings

### F1 [high] Number validation does not enforce finite values

`packages/server/src/Mohist.Server/Workflow/Services/ActionContractValidator.cs:220-229` treats every `JsonValueKind.Number` as matching the catalog `number` kind. The acceptance criteria and design D6 require the save-time rule to accept only finite numbers, matching Runner `validateActionInput` (`Number.isFinite`). A JSON numeric token such as `1e9999` can retain `JsonValueKind.Number` while not being representable as a finite runtime number, so this Profile can pass save and then fail at dispatch, defeating the save-time contract check. The server matcher must verify finiteness before accepting a number, with regression coverage for an out-of-range numeric value.

### F2 [high] Recovery handler tasks are skipped when their parent action is unresolved

`packages/server/src/Mohist.Server/Workflow/Services/ActionContractValidator.cs:83-90` returns before traversing `task.Recovery` when the parent task has an empty, unknown, or tombstoned `uses`. Consequently, a valid Definition containing a parent task with an unknown Action and a recovery-handler task with another unknown Action reports only the parent error; the nested task is never judged. The same applies to approval-feedback tasks because they use `ValidateTask` as well. This violates the requirement that every task position, including tasks nested inside recovery handlers, receives catalog validation. Recovery traversal needs to be independent of whether the enclosing task's own Action resolves, and a spec test should cover an unresolved parent plus an invalid nested recovery task.

<promise>FAIL</promise>
