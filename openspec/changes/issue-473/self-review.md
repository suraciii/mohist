# Self-Review — issue-473

## Summary

The plan correctly diagnoses the bug and prescribes the right fix: stop mirroring the run-owned
`WorkflowRepositoryContext` into the `mohist/rebase` Action's `with` payload so it conforms to the
manifest and passes issue 444's dispatch-time input validation, while preserving the two unrelated
entry-point responsibilities of `runSnapshot` (missing-context rejection, base-branch defaulting).

Verified against the code:
- `mohist/rebase` declares only `baseBranch`, `remote`, `squash`, `message`, `messageFrom` — no
  `repository` (`packages/runner/src/actions/built-ins.ts:236-242`).
- `BuildRebaseTaskWith` currently emits the `repository` object
  (`packages/server/src/Mohist.Server/Api/IssueRoutes.Helpers.cs:127-142`).
- `validateActionInput` rejects unknown fields before the Action body runs
  (`packages/runner/src/actions/input-validation.ts:41-50`, invoked from
  `packages/runner/src/runtime/executor.ts:139-146`).
- Base-branch defaulting and missing-context rejection live at the route call site
  (`packages/server/src/Mohist.Server/Api/IssueRoutes.Rebase.cs:37-39,48-50`), not inside the helper.

Specs: four requirements map 1:1 to the issue's four acceptance criteria; every requirement has a
`#### Scenario` (exactly four hashtags); SHALL/MUST language used; describes target behavior directly
(no delta headers). tasks.json is valid JSON, single tightly-coupled task (correct granularity, not
over-split), `dependsOn: []` (trivially acyclic), and includes test coverage inside the task.

## Finding (must-fix)

### F-1: Proposal contradicts design/tasks on whether the `WorkflowRepositoryContext` parameter is kept

`proposal.md` line 18 (Impact, Server Issue API helpers bullet) states:

> `BuildRebaseTaskWith` stops emitting the `repository` object; the run-owned
> `WorkflowRepositoryContext` parameter **remains in use only for base-branch defaulting**.

This is wrong in two ways:

1. **Factually wrong about the current code.** The helper's `repository` parameter is used *only* to
   populate the `repository` object in the payload (`IssueRoutes.Helpers.cs:133-137`). Base-branch
   defaulting happens at the *call site* (`IssueRoutes.Rebase.cs:48-50`,
   `var baseBranch = ... req?.BaseBranch ... : runSnapshot.BaseBranch`) before the helper is invoked,
   using `runSnapshot` directly — the helper's parameter never participates in defaulting.

2. **Contradicts the design and the task.** `design.md` Decision 2 explicitly drops the parameter
   ("signature collapses to `BuildRebaseTaskWith(string baseBranch)`"), and `tasks.json` T-001
   (description + output + AC#1) encodes the single-parameter signature. The proposal says the
   parameter *remains*; the design and tasks say it is *removed*.

The design and tasks are correct; the proposal Impact bullet is the outlier. It should be reconciled
to state that the helper's `repository` parameter is removed (it was only ever payload data), while
`runSnapshot` stays in scope at the *route handler* for missing-context rejection and base-branch
defaulting. An implementer following design+tasks will produce correct code, but the artifacts should
not contradict each other on what the code change actually is.

## Not findings (verified OK)

- Spec coverage is complete: all four issue acceptance criteria map to a spec requirement.
- The spec's "carries only declared inputs" is correctly a subset constraint; the scenario pins it to
  `baseBranch`+`remote`, both of which the manifest accepts.
- Single-task split is correct (helper + call site + test are one atomic slice; splitting them would
  be the over-granular anti-pattern the task instruction forbids).
- The claim that existing spec tests assert only the HTTP response `data.baseBranch` (not
  `with.repository`) is accurate (`IssueWorkspaceRepositoryResolutionSpecs.cs:143,168`,
  `ApiContractSpecs.cs:414-416`).
- Migration/rollback are correctly stateless (no schema/flag/persistence change).

<promise>FAIL</promise>
