# Self-Review

## Review Basis

This is the first review of `openspec/changes/issue-599/`; no prior `self-review.md` exists, so this is a full sweep. I re-read issue 599 using the structured issue record before reviewing the artifacts. The issue requires: configurable same-Provider concurrency enforcement; 429/rate-limit backoff that honors `Retry-After` without consuming the ordinary consecutive-retry budget; bounded expiry classified as `provider-rate-limited` rather than a real task failure; and deterministic tests for concurrency, recovery, and expiry classification.

## Verdict

FAIL. The plan has must-fix problems in the execution retry boundary and in the terminal result propagation path. As written, implementation can still either hold a Provider permit across opaque SDK backoff or publish a generic `turn.failed`/failed Workflow outcome, so the issue's core acceptance criteria are not yet guaranteed.

## Must-Fix Findings

### F1. The per-attempt retry boundary is left as an unresolved either/or

**Violates:** issue Acceptance Criterion 1 (the Provider concurrency bound must be enforced by the execution plane) and Acceptance Criterion 2 (429 backoff must not be counted as ordinary consecutive retry exhaustion).

The current OpenCode path calls one opaque `client.session.prompt()` operation (`packages/runner/src/runtime/opencode/turn-prompt.ts:201-207`) and observes `session.status` retry events only through `RetryTracker` (`packages/runner/src/runtime/opencode/turn-prompt.ts:371-406`). The current Pi path likewise calls one opaque `session.prompt()` operation (`packages/runner/src/runtime/pi/runtime.ts:276-278`), while its Runner SDK interface exposes no individual Provider-attempt operation (`packages/runner/src/runtime/pi/sdk.ts:18-57`). A single OpenCode prompt can also use `promptAsync()` for a separate model request (`packages/runner/src/runtime/opencode/turn-prompt.ts:569-575`).

The design acknowledges the problem but does not resolve it: it says the adapters must "either disable opaque automatic replay or expose each replay as a coordinator attempt" (`design.md:62`). It does not identify a supported SDK/runtime switch for disabling replay, a replacement request API, or a concrete adapter contract that can expose each Provider attempt. T-002 repeats the required outcome, but its acceptance criteria do not add that missing mechanism (`tasks.json:30-37`). Without choosing and validating one mechanism, the implementation cannot prove that a permit is released during SDK backoff, that each retry reacquires admission, or that no hidden retry occurs after the absolute rate-limit deadline. A fake coordinator test can pass while the production SDK still owns the retry loop.

The plan must define the production attempt boundary for both runtimes, including the behavior when the installed SDK cannot expose or disable its retry loop, and make the adapter contract and integration test depend on that decision.

### F2. The existing terminal close path still converts Provider expiry into a generic turn failure

**Violates:** the issue Product Shape requirement to distinguish rate-limit waiting from real failure and Acceptance Criterion 3's required `provider-rate-limited` classification; it also contradicts the Domain Model requirement that transient Provider throttling not be absorbed into turn/task failure semantics.

The OpenCode Workflow Action currently calls `enqueueTerminalClose` for every non-success runtime result (`packages/runner/src/runtime/executor-capabilities.ts:515-515`). That helper registers a `failed` close for every non-OK result (`packages/runner/src/runtime/executor-capabilities.ts:664-681`), and `WorkflowAgentSessionReporter.registerClose` only accepts `completed`, `failed`, or `unknown` (`packages/runner/src/actions/workflow-agent-session-reporter.ts:198-226`). On the Server, `AgentSessionGrain.BuildWorkflowRuntimeObservations` maps `turn.failed` to `SessionWorkflowObservationKind.Failed` (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1854-1871`), which `WorkflowSessionWorkPort` forwards as `AgentExecutionObservationKind.Failed` (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowSessionWorkPort.cs:72-84`).

Therefore a new runtime error kind alone is insufficient: the durable AgentSession path can still publish and arbitrate a provider expiry as `turn.failed` before or alongside the authoritative work report. That breaks the plan's own requirement that waiting leave the settlement `AwaitingResult` (`design.md:100`, `tasks.json:58`) and that an expiry never become `turn-failed` or `TaskFailed` (`design.md:98,102`, `tasks.json:79-82`). The plan must specify a terminal runtime event/status and Session-to-Workflow observation mapping for provider expiry, or explicitly suppress the generic close for this outcome, including ordering and replay behavior. T-003/T-004 currently cover the waiting/report paths but do not cover this existing close path.

### F3. Structured Provider facts are dropped in the Workflow Action path before report translation

**Violates:** issue Acceptance Criterion 3's requirement for an actionable provider-rate-limited reason and the Workflow report requirement to preserve Provider identity and throttling facts.

The design's required `WorkItemResult` shape contains a structured `providerRateLimit` object (`design.md:84-92`), but the current Workflow Agent Action path cannot carry it. `runtimeActionFailure` creates an `ActionResult` containing only an error code and message (`packages/runner/src/runtime/executor-capabilities.ts:607-609`), and `projectTaskOutput` projects a failed Action to only `status`, `message`, `error`, and `exitCode` (`packages/runner/src/runtime/executor.ts:395-414`). The Pi Action has the same loss: `runtimeFailure` maps the runtime error to a code/message Action failure and discards the runtime diagnostics/facts (`packages/runner/src/actions/pi.ts:532-538`). On the Server, `TaskReport` currently has no Provider facts field and `TaskReportStatus` has only `Succeeded` and `Failed` (`packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkItem.cs:60-80`); `WorkflowItemTranslator.ResolveTaskReportStatus` reduces every non-success status to `TaskReportStatus.Failed` (`packages/server/src/Mohist.Server/Runner/Services/WorkflowItemTranslator.cs:627-635`).

T-002 and T-004 say to extend the runtime/report contracts, but they do not explicitly cover both Workflow Action projections (`executor-capabilities.ts` and `actions/pi.ts`) and their structured-fact transport into `WorkResult`/`TaskReport`. Following the task descriptions literally can produce a runtime `provider-rate-limited` error that arrives at the Server with no timing/provider data, or is reduced to the generic failed report. The plan must name and test the complete propagation chain for both Pi and OpenCode Workflow Actions, in addition to the direct AgentJob projection, before the Server can satisfy the exact category/fact criteria.

## Dimension Review

### Coverage: must-fix gaps found

The plan has explicit spec and task coverage for the four issue criteria at a declarative level (`proposal.md:8-11`, `specs/provider-rate-limit-retry/spec.md:53-69`, `specs/workflow-rate-limit-outcomes/spec.md:2-76`). The gaps above are missing coverage of the production SDK attempt boundary, the existing AgentSession terminal-close path, and the Workflow Action structured-fact propagation path. Those are required paths for the stated criteria, not optional implementation detail.

### Correctness: must-fix gaps found

The proposed ProviderAdmissionController and bounded deadline are directionally correct, and the plan correctly keeps permits out of explicit coordinator backoff (`design.md:40-48`). However, the current runtime interfaces make the stated per-attempt behavior unachievable without the F1 decision. Even with a working coordinator, F2 and F3 allow the result to become a generic failure or lose the actionable facts before reaching the Workflow projection.

### Current-code consistency: must-fix gaps found

The plan correctly identifies RunnerHost as the shared lifetime owner and the existing outbox/AgentSession route as the durable event path. It does not yet account for the current runtime close contract, which has no provider-specific terminal status, or the current ActionResult-to-WorkItemResult projection, which has no structured Provider fact channel. These are established boundaries in the current codebase and must be changed explicitly rather than assumed away.

### Task breakdown: must-fix gaps found

The dependency order T-001 through T-005 is acyclic and generally follows the execution-to-projection direction. The breakdown is incomplete at the load-bearing seams: T-002 needs a concrete production SDK-attempt decision; T-003/T-004 need a task for the AgentSession terminal close and observation mapping; and T-002/T-004 need explicit acceptance coverage for both Workflow Action projection paths and structured fact preservation. The existing broad wording and fake-provider tests do not make those requirements verifiable against the current runtime interfaces.

## Observations

- The production default Provider concurrency, maximum wait, and fallback backoff values remain open (`design.md:151-152`). This is a rollout-tuning concern rather than a must-fix issue criterion, provided validated nonzero defaults are chosen before deployment.
- The design intentionally scopes admission to one Runner process (`design.md:8`, `design.md:31-33`). Multiple independent Runners can still exceed an external Provider quota; this is an acknowledged non-goal, not a defect against the stated Runner-local plan.
- Provider alias handling is left as an open deployment question (`design.md:152`). The canonical runtime Provider ID plus the planned explicit tests are enough for the issue's stated `GLM/zai` case, but deployments with aliases need a confirmed mapping before rollout.
- The plan declares a coordinated breaking release and a rollback backup procedure (`design.md:118-120`, `design.md:147-154`). That is operationally significant, but it does not change the PASS/FAIL decision for the issue criteria.

<promise>FAIL</promise>
