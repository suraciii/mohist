## Context

Mohist ships two built-in workflow profiles: `mohist/default` and `mohist/pr`.
The PR profile couples PR lifecycle, branch sync, and failure recovery through
several ad-hoc mechanisms that have drifted apart:

- `CheckFailureRepair.VerifyTask` (`WorkflowDefinition.cs:25`) lets a check
  declare a *verification* task that runs after the repair task
  (`WorkflowRun.Check.cs:113`). This duplicates what task-level `onFailure`
  already expresses.
- The combined `ready-github-pr` / `mohist/publish-via-pr` action mixes "push
  branch" with "mark PR ready", so retries and idempotency are ambiguous.
- The `mohist/pr` check stage carries `health`, `review-passed`, and
  `merge-ready` checks plus per-check `verifyTask` PR-update tasks, producing
  four parallel recovery shapes.
- Task-level `onFailure` exists (`TaskFailureAction` / `TaskFailureCase`,
  matched in `WorkflowGrain.ResolveTaskFailureRecovery` at
  `WorkflowGrain.cs:958`) but lacks `retry: self` and nested recovery, so it
  cannot express "fix → push → re-run the failed task".
- `expect.markers` supports `oneOf` in the runner
  (`expectations.ts:60`) but has no `failIf`, so a `FAIL` marker cannot drive
  task failure.
- `mohist/rebase` resolves conflicts internally via an inline
  `conflictResolver` agent input (`rebase.ts:38`); there is no way to surface
  a conflict as a task failure for explicit graph-level recovery.

The redesigned workflow committed in `63606d7305`
(`design/workflow/builtin-workflows/github-pr.md`,
`design/workflow/actions.md`) defines the target shape: one explicit task
graph, unified `onFailure` + `retry: self`, draft PR opened last in plan, and
`conflictMode: task` for rebase.

Stakeholders: server (workflow engine + profile catalog), runner (action
registry), and every test that pins the current `mohist/pr` shape.

## Goals / Non-Goals

**Goals:**
- Ship `mohist/github-pr` with the exact task graph in
  `design/workflow/builtin-workflows/github-pr.md`.
- Make `onFailure` the single recovery mechanism: support `retry: self`,
  nested (non-recursive) recovery, and `failIf` marker matching.
- Remove `verifyTask` from the domain, serializer, surrogates, scanner, and
  run pipeline.
- Add the runner actions `mohist/create-github-pr`, `mohist/mark-github-pr-ready`,
  `mohist/push` (exists, extend with `forceWithLease`),
  `mohist/merge-github-pr`, and `conflictMode: task` on `mohist/rebase`.
- Keep the engine error-code-agnostic: recovery matches action output by
  JSON-path only.

**Non-Goals:**
- No generic GitLab/GitHub provider abstraction.
- No stage hooks or hidden stage-boundary PR side effects.
- No recovery for configuration, authentication, PR-state, or unexpected
  GitHub API failures — those remain ordinary task failures.
- No migration of already-running workflow runs to the new profile id.

## Decisions

### D1 — Remove `VerifyTask` from `CheckFailureRepair`
Collapse `CheckFailureRepair(Limit, Task, VerifyTask?)` to
`CheckFailureRepair(Limit, Task)`. Update the Orleans surrogate
(`WorkflowDefinitionSurrogates.cs:244-261`), `WorkflowYamlSerializer`
(parse path at `:276`, emit at `:358`), `PromptReferenceScanner` (`:29`),
and `WorkflowRun.Check.cs:113` (stop appending `repair.VerifyTask`).
- *Alternative:* keep `VerifyTask` as a deprecated alias. Rejected — the issue
  explicitly removes the concept and the old field would remain a second
  recovery path, defeating the unification.
- All existing tests pinning `VerifyTask` (`MohistDefaultWorkflowProfileSpecs`,
  `MohistPrIssueWorkflowProfileSpecs`, `CheckRetrySpecs`,
  `PromptReferenceScannerSpecs`) are rewritten to assert its absence.

### D2 — `retry: self` appends a fresh attempt of the failed task
Extend `TaskFailureCase` with `bool RetrySelf`. In
`WorkflowGrain.ResolveTaskFailureRecovery` (`:958`), when the matched case
sets `RetrySelf`, append a clone of the failed task's `TaskDefinition` to the
returned recovery list. The clone preserves `OnFailure` so subsequent failures
re-enter recovery and count against `Limit` (already counted at `:963` via
`failedAttempts`). The new attempt reuses `TaskRun.MakeTask` and is inserted
after the case's recovery tasks.
- *Alternative:* model `retry: self` as a synthetic recovery task. Rejected —
  it would need a distinct id and would not preserve the original `onFailure`
  without special-casing, which the spec requires.

### D3 — Nested `onFailure` resolved at failure time, non-recursive
`TaskDefinition.OnFailure` already exists, so recovery tasks can declare it
structurally. `ResolveTaskFailureRecovery` recurses one level when a recovery
task fails, matching that recovery task's own `onFailure` against its output.
Enforce non-recursion at **parse time**: `WorkflowYamlSerializer` rejects any
`onFailure` declared on a task whose enclosing context is itself a recovery
case (`ParseTaskFailureAction` gains a `bool allowNestedOnFailure` flag; the
recovery-task branch passes `true`, its recovery tasks pass `false`).
- *Alternative:* allow arbitrary depth. Rejected by the design doc ("nested,
  non-recursive") and by the rebase-conflict scenario, which is the only
  nested case.

### D4 — `failIf` marker matching marks the task failed
Add `string? FailIf` to a new `TaskMarkerExpectation` record carried on
`TaskDefinition` (parsed from `expect.markers[*].failIf`). Marker matching
itself stays in the runner (`expectations.ts`), but when a matched marker
equals `failIf`, the runner reports the task result as `failed` with the
**action's own** `errorCode` (read from the action output), not an
engine-generated code. When no `failIf` is declared, current behavior is
unchanged.
- *Alternative:* evaluate `failIf` in the server after the runner reports
  success. Rejected — it would split the pass/fail decision across processes
  and require the server to re-read artifact contents.

### D5 — Profile rename and action split
- Archive `mohist-pr.workflow.yaml`; add `mohist-github-pr.workflow.yaml`
  matching the target graph exactly (draft PR last in plan, `self-review`
  with `failIf`, check stage = `ai-review` → `push` → `mark-pr-ready` +
  read-only `github-pr-status`, integrate = `spec-sync` → `archive-change` →
  `push` → `merge-pr` with two `onFailure` cases).
- Rename runner actions: `create-pull-request` → `create-github-pr`,
  `merge-pull-request` → `merge-github-pr`. Add `mark-github-pr-ready`
  (idempotent: no-op when already ready). Extend `mohist/push` with
  `forceWithLease`. Remove `publish-via-pr` / `merge-ready` registrations
  consumed only by the old profile (keep if `mohist/default` still uses
  `merge-ready` — it does, so `merge-ready` stays).
- Register the new profile id in the workflow profile catalog; keep
  `mohist/default` untouched.

### D6 — `conflictMode: task` on `mohist/rebase`
Add `conflictMode` input (`'resolve'` default, `'task'` new). In `task` mode,
on conflict the action does **not** invoke the inline `conflictResolver`,
does **not** abort, returns `{ failureKind: "conflict", ... }`, and leaves the
rebase in progress. The graph then triggers `recover:rebase.onFailure` →
`recover:resolve-rebase-conflicts` (an `acp-agent` task that resolves
conflicts, completes the rebase, and commits). `resolve` mode preserves
today's inline-agent behavior for `mohist/default`.
- *Alternative:* always abort on conflict and let a fresh rebase task retry.
  Rejected — aborting loses the in-progress rebase state the resolution agent
  needs.

### D7 — Consolidated plan check `mohist/openspec-artifacts`
Add a single read-only action that verifies the four plan artifacts
(`proposal.md`, `specs/`, `design.md`, `tasks.json`) exist under `changeDir`.
Replaces the four `core/artifact-exists` checks. Registered alongside the
other `mohist/openspec-*` actions.

## Risks / Trade-offs

- **[BREAKING profile id `mohist/pr` → `mohist/github-pr`]** → Issues with a
  persisted `mohist/pr` selection that have not started will fail profile
  resolution at start. Mitigation: the catalog can alias `mohist/pr` to
  `mohist/github-pr` during a deprecation window, or migration is accepted as
  in-flight since the project is pre-release.
- **[`retry: self` can loop up to `Limit`]** → A flapping failure (e.g.
  base keeps moving) consumes retries fast. Mitigation: `Limit` is profile-
  authored and per-case; the default profile sets `limit: 2`. Exhaustion
  surfaces an ordinary failure for user retry/rerun.
- **[Nested recovery resolved at failure time complicates `ResolveTaskFailureRecovery`]**
  → Mitigation: parse-time rejection of deeper nesting keeps the runtime
  recursion bounded to depth 1; add focused specs for the rebase-conflict
  path.
- **[`failIf` splits pass/fail across runner and server]** → Mitigation: the
  runner owns the decision and reports `failed` with the action's
  `errorCode`; the server treats it like any other task failure and applies
  `onFailure`.
- **[Leaving a rebase in progress on conflict can corrupt the worktree if the
  run is interrupted]** → Mitigation: `recover:resolve-rebase-conflicts` runs
  immediately after in the same stage; `mohist/rebase` already calls
  `abortRebaseIfInProgress` at the top of each invocation, so a re-run is
  self-healing.
- **[Large test surface rewrite]** → The `MohistPrIssueWorkflowProfileSpecs`
  and several check-retry specs pin the old shape. Mitigation: rewrite them
  against the new graph in the same change; this is expected churn, not risk.

## Migration Plan

1. Land engine changes first (D1–D4) behind the existing `mohist/pr` profile
   so `dotnet build` + `npm test` stay green: remove `VerifyTask`, add
   `RetrySelf`, add nested-recovery parse validation, add `failIf`.
2. Land runner changes (D5–D7): rename/split actions, add `conflictMode:
   task`, add `mohist/openspec-artifacts`. Keep old action ids as thin
   aliases until the profile swap if needed to avoid a broken window.
3. Swap the profile: add `mohist-github-pr.workflow.yaml`, register it,
   retire `mohist-pr.workflow.yaml`. Update `docs/workflow-profiles.md`.
4. Rewrite profile/retry specs to the new graph.

**Rollback:** revert the profile registration commit; the engine and runner
changes are additive (except `VerifyTask` removal). If `VerifyTask` removal
must be rolled back independently, restore the field with a nullable default
and re-add the serializer/scanner paths — the surrogate id slot is reused, so
no storage migration is needed.

## Open Questions

- **O1:** Should the catalog accept `mohist/pr` as an alias resolving to
  `mohist/github-pr`, or is a hard cutover acceptable given the project is
  pre-release? Proposal leans hard cutover; confirm during implementation.
- **O2:** Does `mohist/mark-github-pr-ready` need to wait for GitHub to
  finish transitioning the PR out of draft, or is the API call returning 200
  sufficient? The design says "idempotent"; confirm whether a follow-up state
  poll is required for the subsequent `github-pr-status` check to pass.
- **O3:** For `failIf`, when the action output has no `errorCode`, should the
  engine synthesize one (e.g. `marker-failed`) or leave it empty? The spec
  says the action defines the code; confirm the default-profile actions
  always emit one.
