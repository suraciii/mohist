## Why

Recent Epic 67 build runs fail in the single `verify` task with `resource-containment` and no test failure output. Both built-in workflows run the complete `ci.verify` chain (`npm ci`, server tests, Web checks, and Runner checks) as one work item, while the Runner's default 1 GiB per-work bound, or the current hard-coded 4 GiB `full-verify` override, is not an auditable budget derived from that workload and the host's available capacity. The containment protection must remain in place, but it must stop turning a legitimate full verification into an opaque false failure before blocked runs are retried.

## What Changes

- Define a workflow-aware resource-budget contract for expensive verification tasks. A task or validated verification partition must declare enough bounded capacity for its actual process tree, with an explicit source and effective budget that operators can audit.
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
