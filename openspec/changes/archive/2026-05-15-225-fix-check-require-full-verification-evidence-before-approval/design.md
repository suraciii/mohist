## Context

The current default Check stage has one task, `ai-review`, followed by `review-passed`, `merge-ready`, and `user-approval`. `BuildTestCheck` and the newer parameterized `HealthGateCheck` exist, and workflow config already maps legacy `checks.buildTest` into `healthGates.check`, but the Check stage domain definition and default runner path do not include a Check-stage full verification check before review.

WorkflowRun is the authoritative runtime state. Therefore the fix must update both the executable runner ordering and the WorkflowRun stage definition; otherwise the runner can execute evidence that the domain does not model, or the domain can wait for checks the runner cannot execute. Existing Integrate `health:integrate` remains the post-merge safety net and does not satisfy approval-time evidence.

## Goals / Non-Goals

**Goals:**

- Run the configured Check full verification command before `ai-review` starts.
- Persist Check verification as a first-class Check-stage check, preferably `health:check`.
- Ensure failed verification stops Check before AI review, merge-ready, or approval request.
- Ensure Check approval output and approve-time guards prove verification, review, and merge-ready evidence are for the same candidate implementation.
- Keep legacy `checks.buildTest` usable through the existing `healthGates.check` compatibility mapping.
- Surface failed or missing Check verification evidence through existing stage/check state consumed by CLI, API, and Web UI.

**Non-Goals:**

- Do not remove or weaken Integrate `health:integrate`.
- Do not make `merge-ready` run build/test commands.
- Do not introduce a new standalone verification store if WorkflowRun check output is sufficient.
- Do not redesign the global health gate model from #147 or the Check ordering principle from #150.
- Do not require all stages to run the same heavy verification command.

## Decisions

### D1: Model Check verification as `health:check` in WorkflowRun

Add `health:check` to the Check stage definition before `review-passed` and `merge-ready`. It should use the existing `Check`/`CheckResult` path and output shape from `HealthGateCheck`: `kind`, `stage`, `command`, `timeout`, `duration`, `enabled`, `summary`, `logExcerpt`, and failure details.

This keeps Check verification in the same ordered check chain that already gates stage progression and approval. It also makes missing or failed verification naturally visible in stage state projection, workflow logs, check updates, and issue surfaces that read stage checks.

**Alternatives considered:** Keep using `build-test` as the canonical check name. This is compatible with older code but weaker as the long-term model because the project already has per-stage health gate naming (`health:build`, `health:integrate`) and config. `build-test` can remain a compatibility alias or fallback, but new default evidence should be `health:check`.

### D2: Execute full verification as a pre-task Check-stage check

Register `new HealthGateCheck({ stage: 'check', policy: loadHealthGatePolicies(workflow).check, worktreePath })` in `CheckStageRunner.getPreTaskChecks()` for the default Check runner. This places verification before `executeTasks()` generates `review.md`, so a failed command prevents new AI review artifacts, `review-passed`, `merge-ready`, and `user-approval` from running.

The runner should derive the policy from the issue worktree workflow config. If custom `checks` are injected in tests, keep that injection behavior explicit; production defaults should always include `health:check`.

**Alternatives considered:** Add `health:check` to `postTaskChecks` before `review-passed`. That would persist evidence before approval, but it would still allow AI review to be generated before machine verification and would violate the required ordering. Make `ai-review` internally run tests was also rejected because it couples machine verification to an AI task and makes evidence harder to surface as a stable check.

### D3: Use candidate snapshot SHA as the evidence binding

Treat the candidate implementation as the Check worktree `HEAD`/candidate head SHA at verification time. `health:check` output should include `candidateHeadSha` and, where practical, `baseSha` and `checkedAt`. `review-passed` already converges to an authoritative `snapshotSha`, and `merge-ready` already records `candidateHeadSha`; approval should require these values to match.

Approval output should include a `verificationEvidence` object copied from the passing `health:check` result. Approve-time validation should reject missing evidence, failed evidence, malformed evidence, or evidence whose `candidateHeadSha` no longer matches the current issue branch/worktree head or the review/merge-ready candidate SHA.

**Alternatives considered:** Use timestamps or check run order only. Ordering is necessary but not sufficient because the candidate may change after a check passes. A snapshot SHA is the smallest stable identity already used by review and merge-ready freshness checks.

### D4: Gate approval in both request-time and approve-time paths

`BaseStageRunner.buildApprovalOutput()` and `prepareApproval()` should require a passing latest `health:check` result before requesting Check approval. The approval output should include verification evidence next to `reviewReport`, `snapshotSha`, and `mergeReadySnapshot`.

The API approval path should independently validate the same evidence before accepting approval, mirroring the existing merge-ready snapshot freshness checks. This protects against stale approval state, older check suites, or manual/API callers that bypass the normal runner request path.

**Alternatives considered:** Rely only on ordered WorkflowRun checks. Ordered checks prevent the normal workflow from reaching approval incorrectly, but do not protect existing approval state, direct approve APIs, or stale projections. Duplicating a small guard at approve-time is intentional defense in depth.

### D5: Keep failure visibility in existing stage/check surfaces

Do not add a new UI-specific failure channel. Persist `health:check` as a normal stage check and ensure projections do not filter it out. `workflow-run-projection` should include `health:check` in Check check-suite projection alongside `review-passed`, `merge-ready`, and `user-approval` if that surface is used by CLI/API/Web UI.

`mo issue show` and Web UI should read the existing stage check state and render failed approval-blocking checks with command, summary, duration, and log excerpt from the check output. Passing health gates may stay compact; failed or missing approval-blocking evidence must be visible.

**Alternatives considered:** Only write detailed failure information to logs. That caused the user-facing trust problem: approval disappeared or appeared without clear machine evidence. The check output is the correct durable evidence boundary.

### D6: Reuse health gate configuration and failure policy

Use `loadHealthGatePolicies(workflow).check` as the source of Check full verification behavior. Existing `checks.buildTest` remains supported through the loader’s compatibility mapping when `healthGates.check` is absent.

If auto-fix policy is enabled for `health:check`, map it to the existing build health repair path only if the current workflow policy already supports an equivalent explicit fix task. If no safe Check-specific repair task exists, failure should remain a failed approval-blocking check rather than silently escalating after generating review artifacts.

**Alternatives considered:** Keep separate `BuildTestCheck` behavior for Check. This duplicates command execution, log formatting, and config mapping. `HealthGateCheck` is already the deeper module for per-stage command gates.

## Risks / Trade-offs

- [Risk] Existing WorkflowRun rows may have Check stages without `health:check` in their seeded definitions. → Mitigation: treat newly started/rerun Check stages as requiring the new definition; avoid silently approving old awaiting approvals unless approve-time evidence exists.
- [Risk] Disabled `healthGates.check.enabled=false` could conflict with the product requirement for full verification before approval. → Mitigation: persist disabled evidence clearly but do not allow disabled verification to satisfy Check approval evidence. Default remains enabled.
- [Risk] Candidate SHA collection may differ between worktree HEAD and branch head when convergence commits are created. → Mitigation: collect `candidateHeadSha` immediately when `health:check` runs, require review convergence and merge-ready evidence to match the final approval snapshot, and invalidate/rerun verification if later tasks change HEAD.
- [Risk] Adding `health:check` to projections may make older UI panels noisier. → Mitigation: render passing health gates compactly and emphasize only failed, missing, or approval-blocking evidence.
- [Risk] Legacy `build-test` check-suite consumers may not recognize `health:check`. → Mitigation: use `health:check` as the canonical WorkflowRun check and optionally mirror/update `build-test` only for compatibility if existing consumers require it.

## Migration Plan

1. Add `health:check` to default Check stage definitions before `review-passed` and `merge-ready`.
2. Register `HealthGateCheck` as the default Check pre-task check using `loadHealthGatePolicies(...).check`.
3. Extend `HealthGateCheck` output for Check with candidate snapshot metadata (`candidateHeadSha`, `checkedAt`, and base metadata where available).
4. Update Check approval output and request-time validation to require passing, fresh verification evidence.
5. Update approve API validation to reject missing, failed, malformed, or stale verification evidence before accepting Check approval.
6. Update projection and user-facing rendering so `health:check` failures are visible in CLI/API/Web UI stage check surfaces.
7. Add regression tests for failed verification blocking AI review/merge-ready/approval and passing verification evidence preceding review, merge-ready, and approval.

Rollback is straightforward: remove `health:check` from Check definitions and runner defaults. Existing persisted check rows can remain as inert historical evidence because consumers should tolerate unknown check names.

## Open Questions

- Should compatibility mirror `health:check` results into legacy `build-test` check-suite entries, or is accepting `health:check` in all consumers sufficient?
