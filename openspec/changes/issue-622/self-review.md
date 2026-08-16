# Self-Review: Issue 622

Review mode: first review. I re-read the issue body and comments before reviewing the proposal, design, task breakdown, and both capability specs.

## Verdict

FAIL

## Must-Fix Findings

1. **The 4 GiB baseline is not grounded to the current-master calibration or an exact source.** `proposal.md:3` describes either the ordinary 1 GiB default or a "current hard-coded 4 GiB" override, and `design.md:3` repeats that the current implementation uses `full-verify`; `design.md:34-43` then presents `memoryMb: 4096` as the initial declaration. None of those plan artifacts identifies whether 4096 MiB comes from checked-in workflow input, Runner code, or deployment configuration, nor does it distinguish that from the issue's explicit calibration that the current `ci.verify` project variable has no 4 GiB override. The actual checked-in path is split across `packages/server/src/Mohist.Server/Workflow/Services/Profiles/mohist-local.workflow.yaml:171-177`, `packages/server/src/Mohist.Server/Workflow/Services/Profiles/mohist-github-pr.workflow.yaml:182-188`, `packages/runner/src/runtime/resource-containment.ts:15-16`, and `packages/runner/src/actions/built-in-core.ts:40-48`; the plan must state this source and activation context, or remove the claim that 4096 is the current baseline. It must also label 4096 strictly as a pre-evidence candidate, with the final value selected only by the focused complete `ci.verify` evidence.

   This violates the issue's explicit current-master calibration requirement and the goal that the verification budget be derived from the actual workload and enforceable host capacity. Leaving it unchanged can make implementation and evidence reason from a historical/profile-level override that is not the deployed baseline, and can prematurely treat the example value as the selected budget.

2. **The declared wall-clock budget is contradicted by the existing verify-task timeout, and the plan defines no precedence.** Both checked-in verify tasks retain `with.timeout: 300000` (`mohist-local.workflow.yaml:176` and `mohist-github-pr.workflow.yaml:187`). `core/script` passes that value as the independent `runCommand` `timeoutMs` (`packages/runner/src/actions/built-in-core.ts:46-49`), while the design's `resourceBudget.wallClockMs: 3600000` (`design.md:34-43`) is a separate resource limit. Therefore a complete `ci.verify` run lasting over five minutes is killed and classified as `timeout` before the proposed one-hour process-tree budget can apply. `design.md:135` only says both profiles keep the same command-deadline policy; it does not say whether that deadline is removed, changed, or required to equal the task budget.

   This violates the issue goal of providing enough bounded capacity for the actual complete verification workload and the spec requirement that complete verification execute under its effective finite memory and execution-duration budget (`specs/workflow-resource-budgeting/spec.md`, `Full verification remains bounded without changing ordinary-work policy`). The plan must define the effective deadline and its precedence over the existing Action timeout, and make the profile/task change and focused evidence prove that the current full command can run within it.

## Dimension Checks

- **Issue goals and acceptance criteria:** checked; the two must-fix findings above are direct violations of the issue's current-baseline and full-verification-budget requirements.
- **Coverage:** checked, no additional issue. The plan addresses explicit task declaration, capacity-aware admission, ordinary-work defaults, sibling isolation, profile parity, outcome classification, normal result settlement, recovery behavior, deterministic fakes, focused current-command evidence, and the no-retry constraint.
- **Correctness:** checked; Finding 2 is a concrete execution-path contradiction. The proposed observation-based classification, pre-start admission outcomes, and structured diagnostics otherwise match the capability scenarios.
- **Consistency with the current codebase and conventions:** checked, no additional must-fix issue. The planned model, dispatch, Orleans-surrogate, translator, Runner, journal, and profile changes align with the existing boundaries; the source locations above are the current implementation points that the plan must describe accurately.
- **Task breakdown, ordering, and verifiability:** checked, no additional must-fix issue. T-001 through T-006 have a coherent dependency order and include focused acceptance tests for the stated contracts, with evidence deferred until after implementation.

## Observations

- The unresolved exact memory and wall-clock values are acceptable as open implementation decisions because `T-006` requires the final checked-in values to be justified by isolated full-verification evidence. That remains valid only after Finding 1 is made explicit about candidate versus final values.
- The capability transport is still an open question in `design.md:166`. T-002 does state the required safety property, so this is an implementation choice rather than a must-fix plan gap, provided old Runners cannot claim declared-budget work.
- The design intentionally permits an ordinary process/host failure when an OS limiter cannot provide an authoritative breach event. The fake watchdog, OS-supported path, and false-positive tests in T-004/T-006 need to preserve that distinction; this is a verification focus, not an additional must-fix finding.

<promise>FAIL</promise>
