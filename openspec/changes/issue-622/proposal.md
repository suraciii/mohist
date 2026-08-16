## Why

Recent Epic 67 build runs fail in the single `verify` task with `resource-containment` and no test failure output. Both built-in workflows run the complete `ci.verify` chain (`npm ci`, server tests, Web checks, and Runner checks) as one work item. On current master, the built-in task activates `with.resourceProfile: full-verify` in `packages/server/src/Mohist.Server/Workflow/Services/Profiles/mohist-local.workflow.yaml` and `packages/server/src/Mohist.Server/Workflow/Services/Profiles/mohist-github-pr.workflow.yaml`; `packages/runner/src/runtime/resource-containment.ts` defines that profile as `FULL_VERIFY_MEMORY_MB = 4096`, and `packages/runner/src/actions/built-in-core.ts` applies it only when `core/script` receives the profile input. The `vars.ci.verify` project variable supplies the command body and has no 4 GiB override. This profile-level value, like the Runner's ordinary 1 GiB default, is not an auditable budget derived from the workload and the host's available capacity. The containment protection must remain in place, but it must stop turning a legitimate full verification into an opaque false failure before blocked runs are retried.

## What Changes

- Define a workflow-aware resource-budget contract for expensive verification tasks. A task or validated verification partition must declare enough bounded capacity for its actual process tree, with an explicit source and effective budget that operators can audit.
- Make `resourceBudget.wallClockMs` the authoritative execution deadline for a declared verification task. Remove the independent `with.timeout: 300000` from both built-in `verify` tasks so the old five-minute Action deadline cannot preempt the declared budget; ordinary tasks retain their existing timeout behavior.
- Replace the current opaque fixed `full-verify` behavior with the chosen workflow-aware budget or validated phase/partition model. The solution must keep a finite bound and must not globally disable resource containment.
- Preserve the existing default containment behavior for ordinary work and the sibling-isolation guarantee: exceeding one work's budget terminates only that work and leaves the Runner and other work items operational.
- Give `mohist/local` and `mohist/github-pr` the same full-verification budget semantics and validate both profile definitions against the contract.
- Distinguish a genuine execution overrun from an invalid, unavailable, or insufficient budget/configuration. Surface the effective task identity, budget context, and actionable failure reason through the normal work-result path instead of presenting every case as an indistinguishable test failure.
- Add deterministic fake resource/process coverage and focused evidence using the current full verification command. Do not retry the currently blocked workflow runs until the budget and scheduling contract is implemented and verified.

## Capabilities

- `workflow-resource-budgeting`: Workflow task resource-budget declaration and resolution, including bounded execution for full verification, budget/configuration validation, host-capacity-aware scheduling or validated partitioning, and equivalent semantics across the built-in local and GitHub PR profiles.
- `resource-containment-diagnostics`: Classification and reporting of resource outcomes, distinguishing an actual per-work enforcement breach from a budget/configuration problem while preserving normal result settlement, ordinary-work containment, and sibling isolation.

## Impact

- **Runner:** resource-limit resolution (`packages/runner/src/runtime/resource-containment.ts`), command and process-tree enforcement (`packages/runner/src/system/process.ts`), `core/script` action result mapping, execution/reporting contracts, and related fake resource/process tests.
- **Workflow definitions and server validation:** `mohist-local.workflow.yaml` and `mohist-github-pr.workflow.yaml`, their profile-definition assertions, and any workflow/action contract validation needed for auditable task budgets or verification partitions.
- **Configuration and operations:** Runner deployment settings (`WORK_RESOURCE_*`), the project/workflow `ci.verify` command, and Runner documentation must explain how the effective verification budget is selected and how failures are diagnosed. No new dependency is expected.
- **Safety boundary:** ordinary work defaults, per-work containment, sibling isolation, and the existing non-retry behavior for actual `resource-containment` failures remain intact; only the full-verification budget contract and its diagnostics change.
