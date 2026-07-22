# Self-Review — issue-473 (round 2)

## Outcome of round 1

- **F-1 (proposal/design contradiction on the `WorkflowRepositoryContext` parameter): RESOLVED.**
  `proposal.md` line 18 now states the helper's `repository` parameter is *removed* (signature collapses
  to `BuildRebaseTaskWith(string baseBranch)`, since the parameter only ever fed the payload object and
  defaulting happens at the call site), and line 19 places `runSnapshot` at the *route handler* for
  missing-context rejection + base-branch defaulting. This now agrees with `design.md` Decision 2 and
  `tasks.json` T-001 (description, output, AC#1). All three artifacts are internally consistent.

## Verified against the code

- `mohist/rebase` manifest declares only `baseBranch`, `remote`, `squash`, `message`, `messageFrom` —
  no `repository` (`packages/runner/src/actions/built-ins.ts:236-242`).
- `BuildRebaseTaskWith` currently emits the `repository` object
  (`packages/server/src/Mohist.Server/Api/IssueRoutes.Helpers.cs:127-142`); its `repository` parameter
  feeds only that object (lines 133-137), confirming defaulting is not done in the helper.
- `validateActionInput` rejects unknown fields before the Action body runs
  (`packages/runner/src/actions/input-validation.ts:41-50`, invoked from
  `packages/runner/src/runtime/executor.ts:140`).
- Missing-context rejection and base-branch defaulting live at the route call site
  (`packages/server/src/Mohist.Server/Api/IssueRoutes.Rebase.cs:37-39,48-50`), and the single helper
  call site is at `IssueRoutes.Rebase.cs:56`.
- Existing spec tests assert only the HTTP response `data.baseBranch`/`status`/`taskId`, never
  `with.repository` (`IssueWorkspaceRepositoryResolutionSpecs.cs:143,168`,
  `ApiContractSpecs.cs:414-416`).

All design.md and proposal.md file/line references checked out.

## Issue-number attribution (investigated, not a defect)

The issue 473 body attributes the input-validation tightening to "issue 447" (twice). The plan
artifacts attribute it to "issue 444". Verified against the archive: issue **444** introduced
`validateActionInput`, the `invalid-input` unknown-field rejection, and the `action-input-validation`
capability spec; issue **447** is about Action *capabilities* (capability-gated host, deferred inputs,
result effects), not input validation. The plan's "issue 444" attribution is correct; the issue body's
"447" is a slip. The plan should not be changed to match it.

## Completeness checks

- All four issue acceptance criteria map to a spec requirement (AC1→Req1 Scn2, AC2→Req1 Scn1,
  AC3→Req2+Req3, AC4→Req4).
- Spec quality: 4 requirements, each with ≥1 scenario; scenarios use exactly four hashtags (`####`);
  SHALL/MUST language; describes target behavior directly (no `## ADDED/MODIFIED/REMOVED` headers).
- `tasks.json`: valid JSON; single tightly-coupled task (correct granularity — splitting helper /
  call-site / test would be the forbidden over-granular pattern); `dependsOn: []` (trivially acyclic);
  priority 1; test coverage embedded in the task; `spec` references match the spec requirement titles.
- All issue Non-Goals are reflected as plan Non-Goals / tasks AC#5 (no Action manifest/validation/
  recovery/CLI/persistence change; no audit of other tasks; no validation relaxation).

## Findings

None. Round-1 finding F-1 is fixed, and no new problems were found on this pass.

<promise>PASS</promise>
