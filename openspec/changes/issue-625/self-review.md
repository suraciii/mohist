# Self-Review: Issue 625 Plan

Round: **re-review**. I retrieved the canonical issue with `mo issue view 625 --project proj_f6c141d63b6243bfbb481737b2243b87`, including its acceptance criteria and the additional #621 live sample. I reviewed `proposal.md`, `design.md`, `tasks.json`, `specs/verification-lanes/spec.md`, `specs/verification-recovery/spec.md`, and `progress.txt`, then checked the current workflow binding, stage-resolution, profile, Runner timeout, journal, and slot-admission paths.

## Verdict

**FAIL** — one must-fix omission makes the planned clean verification run incomplete relative to the issue's live command contract.

## Must-Fix Findings

- **M-4 — the plan drops the required `DOTNET_ROOT` setup when splitting the live verify script.** The current `mohist-local` project variable returned by `mo project variable list --project proj_f6c141d63b6243bfbb481737b2243b87` is a `set -e` script that exports `DOTNET_ROOT=/home/szf/.dotnet` before running `dotnet test`. The six lane values in `design.md:11-18,34` and `tasks.json:54-60` contain only `dotnet test Mohist.sln --nologo -m:1 -p:UseSharedCompilation=false`; they do not preserve that environment setup. Each `core/script` task runs in its own shell, so an export in `verify-install` would not reach `verify-dotnet`. This is a known repository failure mode: `openspec/changes/archive/2026-07-23-issue-481/progress.txt:41-44` records that the .NET apphosts fail immediately without this export and that the fix belongs in the workflow profile rather than the worktree. Leaving the omission can make the clean representative run fail before the required .NET checks execute, violating the issue acceptance criterion that a representative run completes every required lane and the non-negotiable requirement to retain the live `vars.ci.verify` mapping. The plan must explicitly preserve the setup for the .NET lane and cover it in the profile/clean-run contract tests.

## Re-Review Dispositions

- **M-1 — fixed properly.** `tasks.json:48` explicitly forbids adding, restoring, configuring, or referencing resource profiles, cgroups, memory limits, process-tree containment policy, resource budgets, and resource-containment failure codes while preserving only the existing process-group termination, result protocol, and Runner slot policy.
- **M-3 — fixed properly.** `design.md:64-74`, `tasks.json:60`, and the recovery specification require every lane to carry the profile-specific `fix-ci` declaration, preserve the underlying timeout or failure beneath a recovery scheduling envelope, keep `recover:fix-ci` outside the lane catalog, and require a later direct lane success before `pass`.
- **M-2 — fixed properly.** `design.md:54-60,90-96` and `tasks.json:12-14,62` define the complete `BoundWorkflowStart.DefinitionJson` snapshot, the write-once `WorkflowRun.BoundWorkflowDefinitionJson` boundary, snapshot-backed stage and lock resolution, explicit legacy mode, retained aggregate definitions, and both mixed-version tests. The plan also requires snapshot-aware binding idempotency.

## Dimension Sweep

- **Issue goals and acceptance criteria — checked, issue found.** The plan covers ordered lanes, durable outcomes, recovery preservation, all-pass gating, downstream idempotency, unchanged strictness, and removal of the aggregate timeout. M-4 leaves the clean-run/live-command requirement incomplete.
- **Coverage — checked, issue found.** The plan covers both built-in profiles and the requested Server, Runner, profile, and end-to-end tests, but no criterion preserves the live `DOTNET_ROOT` setup after the aggregate script is split.
- **Correctness — checked, issue found.** The ordinary-task boundary, existing timeout primitive, journal fence, and snapshot gate fit the current codebase. The planned .NET lane can nevertheless fail in the current Runner environment because its required runtime setup is omitted.
- **Consistency with the current codebase and conventions — checked, issue found.** The plan correctly reuses `StageRun`, `TaskRun`, report fencing, `core/script`, `runCommand`, `WorkResultJournal`, and existing slot admission. Its new literal lane commands conflict with the current project-scoped verify contract unless the .NET environment setup is carried forward.
- **Task breakdown, ordering, and verifiability — checked, issue found.** T-001 through T-004 are ordered coherently and have substantial focused coverage, but T-003's profile contract criteria do not verify preservation of the live environment prelude, so the omission can pass planning tests while breaking the representative workflow.

## Observations

- `tasks.json:57` spells the Web and Runner commands with the `npm run <script> -w <workspace>` form and uses `test:run` for Runner, while the current project variable uses `npm run -w <workspace> <script>` and Runner `test`. These currently resolve to equivalent package scripts, but the plan calls its strings exact; the implementation tests should make that compatibility decision explicit.
- Initial per-lane timeout values remain an open question in `design.md:98-101`. This is a tuning concern because the plan requires literal positive finite values and a clean representative run; it is not an additional must-fix finding.
- The #621 comment asks that a timeout release the build slot. The existing journal and dispatch contracts provide a plausible path after durable acknowledgement, but T-004 does not make slot release an explicit end-to-end assertion. Treat that as an implementation watchpoint unless the acceptance contract is expanded.
- `vars.ci.verify` may be retained as an unused compatibility variable or removed, as noted in `design.md:101`; either choice is acceptable if it is absent from both built-in verification gates.

`jq empty openspec/changes/issue-625/tasks.json` and `git diff --check` passed before this review update.

<promise>FAIL</promise>
