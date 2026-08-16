## Context

Issue 622 covers the P1 failure mode where the built-in `verify` task runs the complete `ci.verify` command under an ordinary per-work limit and is reported as an opaque `resource-containment` failure. On current master, ordinary work defaults are defined in `packages/runner/src/runtime/resource-containment.ts`, while `packages/server/src/Mohist.Server/Workflow/Services/Profiles/mohist-local.workflow.yaml` and `packages/server/src/Mohist.Server/Workflow/Services/Profiles/mohist-github-pr.workflow.yaml` activate a separate `with.resourceProfile: full-verify` input on `build.verify`. `packages/runner/src/runtime/resource-containment.ts` defines that profile's `FULL_VERIFY_MEMORY_MB` as 4096, and `packages/runner/src/actions/built-in-core.ts` calls `resolveActionResourceProfile` and applies the value to the `core/script` command. The `vars.ci.verify` project variable supplies only the command body; it has no 4 GiB override. Thus 4096 MiB is the current profile-level code path for these built-in tasks, not a calibrated or auditable Workflow Definition baseline. The new declaration must make the source explicit and select the final value only after focused complete-verification evidence.

The Runner already owns process-group execution, Linux `prlimit` support, aggregate RSS watchdog enforcement, per-work wall-clock protection, sibling isolation, and durable result/report retry. `WorkItemResult` currently carries only an error code and message, while `WorkflowItemTranslator` maps that result into the normal `TaskReport` protocol. The existing containment path also treats some signals, exit codes, and resource-like output as evidence of containment even when no enforcement event was observed.

The change affects the shared Workflow Definition model and validator, task dispatch serialization, Runner resource resolution and admission, process enforcement, `core/script` result mapping, recovery matching, built-in profiles, and Runner documentation. The two built-in profiles must remain semantically equivalent. Existing ordinary-work defaults and the per-work process-tree boundary are safety constraints, not behavior to remove.

## Goals / Non-Goals

**Goals:**

- Make the full verification budget a first-class, finite Workflow task declaration with explicit dimensions, values, source, and task identity.
- Resolve the declared budget before the command starts using validated Runner configuration and enforceable host capacity.
- Admit only a finite budget that fits the current capacity reservation. Report invalid, unavailable, or insufficient budget conditions before invoking the verification shell.
- Keep `mohist/local` and `mohist/github-pr` on the same declaration and resolution contract.
- Preserve ordinary-work defaults, per-work process-tree enforcement, sibling isolation, Runner liveness, normal report acknowledgement, and terminal handling of genuine containment overruns.
- Report resource outcomes through the existing result path with stable machine-readable codes and structured budget context.
- Classify `resource-containment` only when the enforcement mechanism observed or authoritatively recorded that the effective bound caused termination.
- Provide deterministic fake resource/process tests and focused evidence for the current complete `ci.verify` command without retrying blocked workflow runs.

**Non-Goals:**

- Do not disable containment globally, make full verification unbounded, or raise the ordinary-work default for every task.
- Do not split `ci.verify` into partitions in this change. A single task preserves the existing install, workspace, output, and failure semantics and is the smallest durable change.
- Do not redesign Runner slot scheduling or introduce a general-purpose resource scheduler for arbitrary future workload types.
- Do not change the test command, test selection, or Workflow recovery behavior for ordinary command failures except where an explicit error-code match is needed to avoid treating budget failures as test failures.
- Do not retry, replay, or mutate the currently blocked issue 622 workflow runs as evidence.

## Decisions

### 1. Represent the verification budget as a first-class task declaration

Add an optional `resourceBudget` field to the semantic `TaskDefinition` rather than keeping a named profile inside Action input. Its declaration shape is:

```yaml
resourceBudget:
  source: workflow.ci.verify
  memoryMb: 4096    # provisional pre-evidence candidate only
  wallClockMs: 3600000 # provisional pre-evidence candidate only
```

The numbers shown above are candidates, not the selected budget. `4096` is the current profile-level constant described in Context, while `3600000` matches the existing one-hour ordinary wall-clock default; neither is evidence that the complete command fits the supported host. T-006 must run the isolated complete `ci.verify` command, record its resolved host capacity and workload envelope, and select the final finite values from that evidence before the profile change is considered complete. The final checked-in values may retain or replace these candidates, but they must never be treated as calibrated merely because they were present in the old profile/default path. The important contract is that the final values are explicit finite numbers in the profile; they are not looked up from `full-verify` or silently replaced by Runner defaults. `source` is a validated source identifier and is part of the diagnostic context. The declaration applies to the complete process tree of the task. The watchdog interval remains deployment configuration because it controls sampling rather than workload capacity.

`resourceBudget` is distinct from the existing Stage `resources` lock declaration. The Definition parser accepts it only on tasks, validates positive finite integer values and a supported source, and reports the exact task path for malformed declarations. The built-in profile validator additionally asserts that the build-stage `verify` task uses `core/script`, declares the `workflow.ci.verify` source, and has the same budget shape in both profiles. A budget declaration on an unsupported action or on a task that is not a validated verification task is rejected.

Alternatives considered:

- Keeping `with.resourceProfile: full-verify` was rejected because the name hides the values and source from Definition validation and dispatch auditing.
- Defining a generic Stage-level resource pool was rejected because the issue concerns one process tree and Stage resources already mean locking; it would also require a new scheduling model.
- Partitioning `ci.verify` was rejected for this change because it changes ordering, workspace state, install reuse, and failure aggregation. It remains a future option only if one finite task budget cannot fit a supported Runner.

### 2. Propagate the declaration through the existing dispatch model

Extend the shared Workflow Definition record, YAML parser/renderer, Orleans surrogate, `WorkItem`, `WorkDispatch`, TypeScript dispatch types, and runtime follow-up/task reconstruction with the optional budget. The Server translator sends the raw declaration as immutable dispatch data alongside the existing task identity and variable snapshot. It is not placed in `with`, so Action input validation cannot remove or reinterpret it.

Runner resolution happens after dispatch context rendering has established the effective `run` command and before `core/script` writes or invokes its shell. The resolver receives the budget declaration, effective work identity (`workflowRunId`, `taskRunId`, `workId`, stage, and task title), normalized ordinary deployment settings, host capacity, and the current admission ledger. It returns either a fully resolved budget or a typed admission/configuration failure. The resolved record contains declared and effective values, source, host-capacity input, and the reservation decision so the same context is available to enforcement and diagnostics.

For a newly validated declared verification task, `resourceBudget.wallClockMs` is the sole task execution deadline passed to process-tree enforcement. Both built-in profiles remove their independent `with.timeout: 300000` Action input, and validation rejects a separate Action timeout on this validated task rather than choosing a hidden minimum or precedence rule. External work cancellation may still stop the command earlier. Ordinary tasks without `resourceBudget` retain the existing `with.timeout` and ordinary-work resource behavior.

The existing `resourceProfile` Action input is removed from the built-in profiles and the `core/script` manifest. The built-in `verify` task also removes its independent `with.timeout: 300000` input; `resourceBudget.wallClockMs` is the only declared verification deadline. The Runner keeps a compatibility reader only for the migration window, and it never converts a legacy profile into a new auditable budget for newly validated definitions. A legacy-only dispatch is reported as a budget-configuration outcome or held behind the capability gate described in the Migration Plan.

Alternatives considered:

- Reusing `with` was rejected because it would make budget data Action-specific, allow templates where values must be static, and prevent the Server from validating the workload contract before dispatch.
- Recomputing the declaration from the rendered command in Runner was rejected because command text is not a stable identity or source of resource requirements.

### 3. Use Runner-side finite admission with explicit capacity evidence

Introduce a small Runner resource admission component, separate from process enforcement. It owns a per-process reservation ledger keyed by work identity and releases a reservation in the same completion path that releases the command/process tree. Existing Runner slot capacity remains in force; a resource reservation is an additional pre-command check for declared verification work.

The host-capacity reader uses an enforceable limit, in this order:

1. A cgroup memory limit (`memory.max` or the cgroup v1 equivalent) when the Runner is inside a managed cgroup.
2. An explicit `WORK_RESOURCE_HOST_MEMORY_MB` deployment value when the environment is not exposing a usable cgroup limit.
3. No capacity value on platforms where neither source is available.

The third case is not guessed from total installed RAM. A verification task is rejected before its command starts with `resource-budget-unavailable`, naming the missing capacity source and the required deployment setting. The host value is also recorded in the resolved budget. The documentation must direct operators to keep this value consistent with the service/container memory limit, such as systemd `MemoryMax`.

For an explicit verification task, the declared memory and wall-clock values are required to be positive, finite, and within the validated host contract. The Runner does not silently clamp them to `WORK_RESOURCE_MEMORY_MB` or `WORK_RESOURCE_WALL_CLOCK_MS`; those variables continue to supply ordinary-work defaults. If the declared memory reservation plus active declared verification reservations exceeds available enforceable capacity, the Runner reports `resource-budget-insufficient` before starting the shell. It never starts the command with an unvalidated smaller budget or an unbounded fallback. The watchdog interval continues to come from the normalized deployment setting and remains independently validated.

Ordinary work keeps the existing default resource limits and does not inherit the verification declaration. Existing `slots` behavior, process-tree ownership, and Runner-level host protection remain the final safety boundaries. The admission ledger is deliberately limited to the declared verification capacity so this change does not turn ordinary task dispatch into a new global resource policy.

Alternatives considered:

- Making the Server scheduler own host-memory admission was rejected because host capacity and live reservations belong to the Runner process and are not authoritative in the Server's persisted dispatch state.
- Using `os.totalmem()` as the capacity contract was rejected because it is not an enforceable limit in containers or service managers.
- Silently taking `min(declared, configured)` was rejected because it can produce a budget lower than the validated workload requirement while hiding the reason for failure.

### 4. Separate enforcement observations from exit-code interpretation

Keep `prlimit`, aggregate RSS sampling, wall-clock watchdogs, process-group termination, and force-kill grace periods. Change the command result so containment is set only by an explicit observation from the containment controller, including the affected dimension and effective bound. The wall-clock timer and RSS watchdog record the observation before initiating termination. Where an OS-level limiter can terminate first, the implementation adds the platform-specific authoritative event hook available for that limiter; a generic signal, non-zero exit code, or resource-like stdout/stderr text is never sufficient by itself.

Remove the current generic `isResourceLimitExit` and `isResourceLimitOutput` classification behavior. If a process exits abnormally without an observed bound breach, `runCommand` returns the ordinary process result. If the host enforcement mechanism cannot provide an authoritative observation, the result remains a process/host failure rather than claiming that this work breached its bound. This favors diagnostic correctness over guessing and keeps the process group kill independent from result classification.

The command result carries an internal resource observation with `dimension` (`memory` or `wall-clock`) and `effectiveBound`. `core/script` maps an observed event to `resource-containment`; otherwise it preserves the existing `timeout` or `script-failed` mapping. Bounded stdout/stderr tails and exit information remain available through the existing result/log paths.

Alternatives considered:

- Treating SIGKILL, exit 137, or text such as `out of memory` as containment was rejected because those signals can come from unrelated process failures or host pressure and are the false-positive behavior in this issue.
- Removing `prlimit` and relying only on a JavaScript watchdog was rejected because the OS-level limit is still valuable as a last line of defense, especially between RSS samples.

### 5. Add structured diagnostics without changing the result protocol

Extend the optional Action/Execution error details with a JSON object while retaining the existing `code` and human-readable `message` fields. The public resource diagnostic contains:

```text
outcome: overrun | invalid | unavailable | insufficient
work: workflowRunId, taskRunId, workId, stage
source
resourceDimension
 declared: memoryMb, wallClockMs
 effective: memoryMb, wallClockMs
 hostCapacityMb (when known)
 requestedMemoryMb / reservedMemoryMb (when applicable)
 reason and correctiveAction
```

The TypeScript `ActionError`, wire `WorkResult`, C# `ExecutionError`, and normal WorkItem-to-TaskReport translation are extended with this optional details object. Existing consumers that read only `error.code` and `error.message` remain compatible. The Runner result journal persists and retries the complete result under the original work identity without special settlement logic.

Use these stable codes:

- `resource-containment`: an observed per-work memory or wall-clock breach caused termination.
- `resource-budget-invalid`: the declaration contains an invalid value, source, action, or task contract.
- `resource-budget-unavailable`: a required host/configuration input cannot be read or validated.
- `resource-budget-insufficient`: a valid declaration cannot be admitted with current capacity or reservations.

Normal non-zero commands remain `script-failed` or the action-specific command error, and command output remains bounded as before. Recovery matching is changed so the built-in verification repair handler explicitly matches `script-failed`; resource-budget outcomes are not misrouted to a test-failure repair task. Genuine `resource-containment` remains excluded from automatic recovery. Operators fix the configuration or capacity and manually rerun a failed budget-configuration task.

Alternatives considered:

- Encoding diagnostic context only in a message was rejected because operators and recovery code would have to parse prose and could lose requested-versus-effective values.
- Adding a second report endpoint was rejected because it would bypass the existing at-least-once acknowledgement and stale-report handling.

### 6. Validate and document the full verification contract as one built-in invariant

Both built-in profile definitions declare the same explicit `resourceBudget` on `build.verify`, retain the complete `${{ vars.ci.verify }}` command, and omit the independent Action `with.timeout`. For declared verification, the resolved `resourceBudget.wallClockMs` is the sole finite execution deadline; ordinary tasks keep their existing Action timeout policy. The profile validation/golden tests compare the task identity, source, dimensions, deadline rule, and resolution rule; command-input differences elsewhere in the profiles cannot make the budgets diverge.

Update the workflow definition reference, core Action reference, Runner deployment documentation, and self-hosting guidance. Documentation will explain that `WORK_RESOURCE_MEMORY_MB` and `WORK_RESOURCE_WALL_CLOCK_MS` remain ordinary-work defaults, the host-capacity setting/cgroup limit is an admission input, the effective budget is recorded in the result, and each of the three configuration outcomes has a concrete corrective action.

The focused evidence suite will cover declaration parsing, profile parity, resolution, host-capacity absence, insufficient reservations, successful bounded execution, actual memory/duration containment, false-positive exits, ordinary script failures, report settlement, sibling isolation, and the complete current `ci.verify` command. No evidence command may call a workflow retry or mutate the blocked runs.

## Risks / Trade-offs

- [A declared budget may still be too small for a changing `ci.verify` workload] -> Keep the value explicit and versioned in both profiles; require focused command evidence and update the declaration when the workload envelope changes rather than silently falling back to ordinary limits.
- [Host capacity cannot be determined on an unmanaged or unusual platform] -> Fail closed with `resource-budget-unavailable` before command start and document `WORK_RESOURCE_HOST_MEMORY_MB` as the explicit enforceable deployment input.
- [A concurrent verification reservation may be rejected even though the command would probably fit] -> Prefer a deterministic admission failure over starting an unvalidated workload; operators can increase the enforceable Runner capacity or serialize verification work.
- [OS-level memory termination can occur before a watchdog sample] -> Keep the OS limiter as protection, add the platform event hook where available, and never infer containment from a generic signal or exit code when no authoritative event exists.
- [Adding a first-class task field crosses Server, Orleans, and Runner serialization boundaries] -> Add the field with new serializer IDs, preserve null as the old shape, and cover parser, surrogate, translator, redelivery, and runtime follow-up round trips.
- [Older Runners may ignore the new dispatch field and apply ordinary limits] -> Gate dispatch of declared-budget work on a Runner capability/version and keep the old definition out of the built-in profile once the new contract is enabled.
- [Resource-budget failures could trigger existing broad recovery handlers] -> Make the built-in verification handler match ordinary `script-failed` explicitly and keep budget codes out of automatic repair/retry paths.
- [A richer error payload could expose implementation details to unrelated consumers] -> Keep details optional, bounded, and limited to identity, budget, capacity, dimension, and corrective context; retain the existing short message for general UI/log consumers.

## Migration Plan

1. Add the semantic `resourceBudget` field, wire fields, Runner resolver, admission ledger, diagnostics, and compatibility tests while the old profiles remain valid. New Runners advertise a `workflow-resource-budget-v1` capability.
2. Deploy the new Runner build and verify that its host-capacity source is readable. Runners without the capability are excluded from claiming declared-budget verification work; this prevents a new profile from silently running under an old ordinary-work limit.
3. Update both built-in profiles and the shared profile assertions to replace `with.resourceProfile: full-verify` with the explicit task declaration, remove `with.timeout: 300000` from each built-in `verify` task, and make `resourceBudget.wallClockMs` the sole deadline for that task. Remove the legacy resource-profile input from newly validated workflow definitions and update recovery handlers to match explicit ordinary failure codes.
4. Run Definition validation, fake resource/process suites, and focused `ci.verify` evidence on an isolated workspace. Record the resolved budget and diagnostic payload. Do not retry or replay the currently blocked issue 622 runs.
5. Enable the updated profiles for new or rerun workflow attempts after all target Runners report the capability. Existing attempts containing only the legacy profile are either completed under the compatibility window or fail with an actionable budget-configuration result and require a manual rerun.

Rollback is configuration-first: disable the new built-in profile revision and stop dispatching declared-budget work before reverting Runner binaries. Keep the compatibility reader and capability gate through the rollback window so an old profile cannot be interpreted as a new budget and a new profile cannot be claimed by an old Runner. Revert the profile definitions and documentation together if the focused verification evidence is not acceptable. Do not roll back by globally disabling containment or by restoring an unbounded/full-verify bypass.

## Open Questions

- What exact memory and wall-clock values does the isolated complete `ci.verify` evidence justify for the built-in declaration? The old 4096 MiB profile constant and one-hour ordinary default are only pre-evidence candidates. The design fixes the shape and resolution rules; implementation evidence must select final values that fit the supported Runner deployment and complete workload without relying on the old opaque profile or its five-minute Action timeout.
- Should the first release require `WORK_RESOURCE_HOST_MEMORY_MB` on every non-cgroup platform, or should a later platform adapter provide another enforceable capacity source? The fail-closed behavior is fixed either way.
- Does the existing Runner capability registration have a preferred field for `workflow-resource-budget-v1`, or should the capability be represented by the existing build/version metadata and dispatch compatibility check? The rollout must retain the same safety property regardless of the transport choice.
