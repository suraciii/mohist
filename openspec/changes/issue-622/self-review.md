# Self-Review: Issue 622

Review mode: re-review. I read the current issue body and comments with `mo issue view 622 --project proj_f6c141d63b6243bfbb481737b2243b87` before reviewing the current proposal, design, task breakdown, and both capability specs. I also checked the current resource-profile, workflow-profile, dispatch, and process-enforcement code paths.

## Verdict

PASS

## Previous Findings

1. **Finding 1, current 4 GiB baseline/source: fixed properly.** The revised proposal and design identify the exact checked-in sources: both built-in workflow profiles activate `with.resourceProfile: full-verify`, `packages/runner/src/runtime/resource-containment.ts` defines `FULL_VERIFY_MEMORY_MB = 4096`, and `packages/runner/src/actions/built-in-core.ts` applies it. The design also explicitly distinguishes this profile-level code path from the confirmed operational baseline: the ordinary default is 1 GiB when no deployment override is configured, and the `vars.ci.verify` command has no 4 GiB override. Design lines 39-43 and Open Question 1 label 4096 MiB and one hour as pre-evidence candidates only; T-006 requires the final finite values to be selected from isolated complete `ci.verify` evidence and the supported host contract. This satisfies the issue's requirement for an exact source without treating an old candidate or deployment difference as the current calibrated baseline.

2. **Finding 2, independent five-minute deadline: fixed properly.** The proposal explicitly removes `with.timeout: 300000` from both built-in `verify` tasks. Design lines 59-61 make `resourceBudget.wallClockMs` the sole deadline for declared verification, reject an independent Action timeout on that task, and preserve existing timeout behavior for ordinary tasks. T-001, T-004, and T-006 repeat this contract in their acceptance criteria and evidence requirements. A complete verification run can therefore use the declared wall-clock budget instead of being preempted by the current five-minute Action timeout.

## Regression Check

Checked, no issue. The fixes do not weaken the safety boundary: the plan retains finite process-tree memory and wall-clock enforcement, ordinary-work defaults, process-group isolation, sibling isolation, Runner liveness, durable result settlement, and terminal handling for genuine `resource-containment`. The plan also keeps legacy profile dispatch distinguishable during migration and prevents an old Runner from silently executing a new declared-budget task under ordinary limits.

## Dimension Checks

- **Issue goals and acceptance criteria:** checked, no issue. The plan addresses the confirmed 1 GiB ordinary-work failure mode, explicit auditable verification budgets, host-capacity-aware admission, finite bounds, equivalent local/GitHub PR semantics, distinct diagnostic outcomes, ordinary-work preservation, focused fake/process evidence, current complete-command evidence, and the no-retry constraint.
- **Coverage:** checked, no issue. T-001 through T-006 cover the definition contract, both profile migrations, all dispatch and persistence boundaries, capability compatibility, host-capacity resolution, reservation admission/release, observation-based containment, structured result translation, recovery boundaries, documentation, and focused evidence.
- **Correctness:** checked, no issue. The proposed deadline precedence matches the existing `core/script` and `runCommand` paths; admission occurs before shell invocation; the resolved memory and wall-clock values are passed to descendant-process enforcement; only an observed or authoritative bound breach receives `resource-containment`; and configuration failures use distinct pre-start results.
- **Consistency with the current codebase and conventions:** checked, no issue. The plan follows the existing `TaskDefinition`/YAML parser and renderer, Orleans surrogate, `WorkItem`/`WorkDispatch` translation, Runner result journal, Action error, and normal report protocol boundaries. It adds serializer fields rather than changing existing field identities and keeps `resourceBudget` outside Action `with` input.
- **Task breakdown, ordering, and verifiability:** checked, no issue. The dependency chain T-001 -> T-002 -> T-003 -> T-004 -> T-005 -> T-006 is coherent. Definition and dispatch contracts precede Runner admission; admission precedes process integration; result/recovery behavior follows enforcement; final budget selection and full-command evidence run after the implementation pieces are available. Acceptance criteria include deterministic tests and prohibit mutating blocked workflow runs.

## Observations

- The exact final memory and wall-clock values remain open until T-006 evidence. This is consistent with the issue's explicit prohibition on selecting final values before focused full-verification evidence; the plan supplies finite candidates only to establish the contract and requires the checked-in values to be finalized from measured workload and enforceable host capacity.
- The transport for the `workflow-resource-budget-v1` capability is an open implementation choice. T-002 still fixes the required safety property and requires capability-gate tests, so the open transport field does not leave the issue goal incomplete.
- Authoritative OS-level memory-limit events are platform-dependent. The design preserves OS protection, records watchdog or supported limiter observations, and deliberately reports an ordinary process/host failure when no authoritative event exists rather than guessing `resource-containment`; the specified fake and false-positive tests make that boundary verifiable.
- The plan's dispatch propagation must include the HTTP `WorkDispatchResponse` mapping in addition to the domain and TypeScript types because that is the live Runner boundary. T-002's acceptance criterion requiring the budget on the dispatched task makes this necessary, so the omission of the DTO name from the task prose is an implementation detail rather than a must-fix plan gap.

<promise>PASS</promise>