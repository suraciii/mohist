# Self-Review: Issue 625 Plan

Round: **re-review**. I retrieved the canonical issue with `mo issue view 625 --project proj_f6c141d63b6243bfbb481737b2243b87`, including the acceptance criteria and the additional #621 live sample. I reviewed `proposal.md`, `design.md`, `tasks.json`, both specification files, and `progress.txt`, then checked the current built-in profiles, workflow binding and stage-resolution paths, `core/script` timeout handling, Runner result journaling, and slot admission.

## Verdict

**PASS** — no must-fix problem remains; the plan is ready to build against the issue goals and acceptance criteria.

## Re-Review Dispositions

- **M-1 — fixed properly.** `tasks.json:48` explicitly forbids adding, restoring, configuring, or referencing resource profiles, cgroups, memory limits, process-tree containment policy, resource budgets, and resource-containment failure codes. It preserves only the existing process-group termination, result protocol, and Runner slot policy, matching the issue's non-negotiable constraints.
- **M-2 — fixed properly.** `design.md:56-60,92-96` and `tasks.json:12-14,62` define the complete `BoundWorkflowStart.DefinitionJson` snapshot, the write-once `WorkflowRun.BoundWorkflowDefinitionJson` boundary, snapshot-backed stage and lock resolution, explicit legacy mode, retained aggregate definitions, binding idempotency, and mixed-version coverage. This addresses the prior concern that profile hot reload could change a run's lane mode or task materialization.
- **M-3 — fixed properly.** `design.md:66-74`, `tasks.json:60,80`, and `specs/verification-recovery/spec.md:1-15` require every lane to carry the profile-specific `fix-ci` repair/retry declaration, preserve the underlying timeout or failure beneath a recovery scheduling envelope, keep `recover:fix-ci` outside the lane catalog, and require a later direct same-lane success before `pass`.
- **M-4 — fixed properly.** `proposal.md:9`, `design.md:13,36`, `tasks.json:54,57,60,70`, and `specs/verification-lanes/spec.md:55-68` put `export DOTNET_ROOT=/home/szf/.dotnet` in each `verify-dotnet` lane before the unchanged `dotnet test Mohist.sln --nologo -m:1 -p:UseSharedCompilation=false` command. This matches the live project variable and accounts for each `core/script` task's fresh shell.

## Dimension Sweep

- **Issue goals and acceptance criteria — checked, no issue.** The plan covers six ordered lanes, independent finite budgets, durable pass/fail/timeout evidence, first-non-passing recovery, preservation of earlier passes, all-pass gating, downstream idempotency, unchanged strictness, and removal of the enclosing full-suite timeout. It also addresses the concrete #621 failure evidence and report-delivery retry path.
- **Coverage — checked, no issue.** Both built-in profiles are covered, including the exact required verification scopes, the .NET runtime setup, per-lane recovery declarations, Server state and projection, Runner timeout/journal behavior, mixed-version binding, clean runs, timeout recovery, stale reports, and downstream side-effect tests.
- **Correctness — checked, no issue.** The ordinary `core/script` task boundary fits the existing serial workflow scheduler. The existing structured `error.code=timeout`, process-group termination, report fencing, and `WorkResultJournal` can provide lane-scoped execution and delivery without introducing a new generic task status or changing Runner slot policy. The explicit lane outcome gate prevents a recovery helper or a completed outer scheduling envelope from opening downstream work.
- **Consistency with the current codebase and conventions — checked, no issue.** The plan reuses `StageRun`, `TaskRun`, `WorkId`, existing report identity fences, `WorkflowYamlSerializer`, `WorkflowStageInitializer`, `WorkflowStageLockCoordinator`, `core/script`, `runCommand`, and `WorkResultJournal`. Its legacy compatibility branch preserves old aggregate runs without rewriting or rerunning historical attempts.
- **Task breakdown, ordering, and verifiability — checked, no issue.** T-001 and T-002 independently establish control-plane state and Runner behavior; T-003 depends on both before changing built-in profiles; T-004 depends on the completed lane contract before validating recovery and downstream effects. Each task has concrete acceptance criteria and focused test coverage.

## Observations

- Initial per-lane timeout values remain an open question in `design.md:98-101`. This is a tuning decision rather than a must-fix plan defect because the plan requires literal positive finite values and representative clean-run tests must validate that the chosen values are sufficient.
- The plan spells the equivalent npm commands as `npm run <script> -w <workspace>` and uses Runner `test:run`, while the current `vars.ci.verify` variable uses `npm run -w <workspace> <script>` and Runner `test`. The package scripts currently resolve to the same typecheck and Vitest commands. The profile contract tests should make that intentional command-form compatibility explicit because the issue calls for preserving the live command mapping.
- The #621 comment specifically asks that a timeout release the build slot. The plan preserves the existing slot policy and the current journal/dispatch path releases execution admission after the terminal result is durably held or acknowledged, but T-004 does not name slot release as a separate end-to-end assertion. This is an implementation watchpoint, not a must-fix issue under the listed acceptance criteria.
- The current status query can use the live profile when displaying an uninitialized stage, while the plan's immutable snapshot requirement is stated explicitly for stage initialization and lock resolution. Materialized lane state and gating are covered, but status tests should also ensure pre-initialization projections do not misleadingly show post-rollout lanes for a legacy run or aggregate tasks for a lane-enabled bound run.

<promise>PASS</promise>