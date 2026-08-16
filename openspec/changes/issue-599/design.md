## Context

Issue 599 is caused by a capacity fact being handled as a task failure. The current Agent-level concurrency gate protects one Agent, while several Workflow runs can select different Agents that resolve to the same Provider. The shared Provider then receives a burst of model requests, returns 429s, and Pi/OpenCode eventually turns the exhausted retry sequence into `turn-failed`. An operator retry immediately recreates the burst.

The Runner already has one process-wide `RunnerHost` that owns the shared Pi and OpenCode runtimes. Workflow Agent execution can enter either runtime through `WorkExecutor`/`executor-capabilities.ts`, while standalone AgentJob execution enters through `AgentJobExecutor`/`agent-job-turn.ts`. Both paths eventually call a shared runtime `runTurn` boundary. Existing Runner slots and Agent concurrency are enforced elsewhere and must remain independent.

The current runtime policies observe provider retry events but abort at the ordinary consecutive-retry threshold. The `WorkResult` and `TaskReport` contracts then reduce every non-completed result to a generic failure. The Workflow already has a durable AgentSession-to-Workflow execution binding and observation path, but it has no Provider rate-limit waiting state. The design extends those boundaries rather than creating a second execution identity or a server polling loop.

Constraints are a Runner-local execution-plane limit, no new external Provider dependency, cancellation at every wait point, deterministic time in tests, additive durable state, and coordinated handling by Server, Runner, CLI, and Web consumers. A limiter is intentionally scoped to one Runner execution plane; a distributed limit across independent Runner processes is outside this change.

## Goals / Non-Goals

**Goals:**

- Enforce a configurable maximum number of in-flight model requests per canonical Provider identity, shared by Workflow runs, Agents, runtimes, and AgentJobs on one Runner.
- Admit immediately before each actual Provider request, release after that request, and never hold a permit during retry backoff.
- Classify 429 and equivalent rate-limit signals separately from ordinary Provider failures, honor `Retry-After`, use configured fallback backoff, and bound the total rate-limit wait.
- Expose durable `provider-rate-limit-waiting` and `provider-rate-limited` Workflow states with Provider identity and actionable timing facts.
- Preserve ordinary success, cancellation, runtime failure, and task failure semantics.
- Make an expired outcome explicitly retryable by an operator. A retry must re-enter normal Runner, Agent, and Provider admission.
- Cover shared admission, cancellation, retry recovery, expiry, report translation, persistence, and consumer rendering with deterministic tests.

**Non-Goals:**

- A distributed or cross-Runner Provider quota service.
- Token-rate limiting, request-cost accounting, or Provider-specific quota prediction.
- Automatic Workflow resubmission after a rate-limit wait expires.
- Changes to existing Runner-slot or Agent-level concurrency policy.
- Relabeling genuine quota, billing, invalid-input, cancellation, or runtime failures as rate limiting when they do not carry a recognized rate-limit signal.
- Reworking unrelated follow-up, workspace, artifact, or AgentSession recovery behavior beyond the shared model-turn boundary needed for this change.

## Decisions

### 1. Use one Runner-local, Provider-keyed admission controller

Add a Runner-owned `ProviderAdmissionController` created once by `RunnerHost` and injected into both `PiRuntime` and `OpenCodeRuntime`. The same instance is also used by the direct Workflow Agent and AgentJob paths because both invoke those runtimes. The controller keeps a lazily-created FIFO queue and permit count for each `ProviderKey`; a cancelled waiter is removed without consuming a permit.

`ProviderKey` is resolved before the request is sent. An explicit `provider/model` request uses the runtime's canonical Provider ID. When the request omits a model, the adapter resolves the effective model from the bound runtime Session or its normal catalog/default-model path before admission. If the adapter cannot prove a Provider identity, it returns the existing configuration/runtime error and does not send an unbounded request or put it into a shared `unknown` bucket. Display names and model IDs remain separate from the key.

Provider concurrency policy is loaded with Runner configuration alongside the existing Provider error policy. It has a validated default limit, optional per-Provider overrides, a rate-limit maximum wait, and fallback backoff initial/max values. The CLI passes the policy into `RunnerOptions`; invalid values prevent Runner admission and produce the same actionable startup diagnostic pattern used by the existing Provider policy. The default and override syntax are deployment configuration, not Workflow DSL.

The controller's lease is held only around one actual model request. Every normal attempt and every rate-limit retry acquires a fresh lease immediately before sending and releases it in `finally`. Admission queue time and rate-limit backoff are observable waits, but neither occupies Provider capacity. Different Provider keys have independent queues.

**Alternative considered: extend the existing Agent concurrency grain.** Rejected because that gate is intentionally Agent-scoped and lives in the Server control plane; it cannot see runtime Provider identity for different Agents without coupling scheduling to model execution and it would not protect direct runtime calls.

**Alternative considered: one semaphore for the whole Runner.** Rejected because unrelated Providers must not contend and one Provider's throttling would unnecessarily reduce capacity for all others.

**Alternative considered: a Server-side distributed Provider grain.** Rejected for this change because the required scope is the Runner execution plane, and a network admission round trip would complicate cancellation, Runner loss, and retry ownership without solving independent multi-Runner quota coordination.

### 2. Put rate-limit retry policy at the runtime request boundary

Introduce one normalized internal `ProviderRateLimitSignal` containing the canonical Provider, HTTP/status or runtime reason, message, attempt, and parsed `Retry-After` duration when available. Pi and OpenCode adapters map their native signals into this type. Recognition accepts HTTP 429 and documented equivalent rate-limit status/reason fields; a message-only match is used only for the configured equivalent patterns. Quota, billing, and balance failures remain on the existing non-recoverable Provider policy unless the signal is explicitly rate limiting.

The runtime request coordinator performs this sequence:

1. Resolve the effective Provider and acquire its admission lease.
2. Execute exactly one Provider request attempt and release the lease when it completes, fails, or is cancelled.
3. If the result is a rate-limit signal, record the latest facts, emit a waiting update, and wait for `max(valid Retry-After, configured fallback backoff)`.
4. Re-enter Provider admission for the next attempt. Rate-limit attempts and their delays use a separate budget and do not increment the ordinary consecutive Provider retry counter.
5. Stop at the absolute rate-limit deadline, return `provider-rate-limited`, and include the latest signal, Provider, deadline, next-attempt calculation, and accumulated wait/attempt facts.

The production attempt boundary is a fixed adapter contract, not an implementation-time choice. Both adapters expose a Mohist-owned `ProviderAttemptExecutor` whose `executeOne` method performs exactly one Provider model request and returns its structured response or `ProviderRateLimitSignal`; the bounded coordinator is the only owner of retry and backoff.

Pi is initialized with SDK auto-retry disabled through the installed `AgentSession.setAutoRetryEnabled(false)` capability. The Pi SDK boundary is extended with a Provider transport hook around the `ModelRuntime` stream/streamSimple calls, so every actual model request inside the agent loop acquires and releases Provider admission independently; `session.prompt()` remains orchestration and is never treated as the request boundary. The SDK contract test must assert that a rate-limited stream produces one transport call and no SDK replay.

OpenCode uses a pinned server/SDK single-attempt capability that the runtime adapter owns. The concrete boundary is `OpenCodeProviderAttemptExecutor.executeOne`, implemented by a typed `client.session.prompt` request whose body includes `retry: false`; the corresponding pinned OpenCode server build MUST enforce that field by returning after one Provider request, without server-owned replay or delay. The generated SDK type used by Runner must expose the field, and a compatibility test must verify that two fake 429 responses produce two requests only when the Mohist coordinator schedules them. The current `@opencode-ai/sdk` 1.18.3 `session.prompt()` surface has no such option and is not an acceptable production implementation. The OpenCode server build and generated SDK must therefore be upgraded or patched before this change is enabled. The runtime startup health check must verify the capability before the runtime becomes ready. If it is absent or the server ignores `retry: false`, `OpenCodeRuntime` returns `provider-attempt-boundary-unsupported` and RunnerHost does not admit OpenCode work; there is no fallback to opaque `session.prompt()` replay.

The existing deadline-warning `session.promptAsync()` injection is removed from this coordinated path because it is a separate model request that cannot be admitted after it has been scheduled. Deadline enforcement and cancellation remain unchanged; the runtime emits the warning as a non-Provider session/runtime event instead. Tests must assert that `promptAsync()` is never called by a Provider-coordinated turn. Reattach/recovery paths still watch the already-submitted physical turn and never submit a new Provider request, so they do not acquire an admission lease. The normal OpenCode/Pi cleanup and generation-quarantine behavior remains in force for transport, deadline, and cancellation failures.

`Retry-After` supports the existing Provider response forms exposed by each adapter, including a seconds value and an HTTP-date where available. An unusable or absent value uses fallback backoff. A delay that reaches past the remaining deadline ends as `provider-rate-limited` without another request. Cancellation interrupts both admission and delay and returns the existing interrupted/cancelled classification.

**Alternative considered: keep the SDK retry loop and only increase its threshold.** Rejected because it cannot reliably release a Provider permit during hidden backoff, honor a bounded total wait, or keep rate-limit retries independent from ordinary retry accounting.

**Alternative considered: retry in `AgentJobExecutor` only.** Rejected because Workflow Agent Actions and AgentJobs use different entry paths. The runtime request boundary is the shared deep module that can protect both without duplicating policy.

### 3. Publish waiting facts through the existing durable AgentSession event path

Extend the runtime observer contract with a Provider rate-limit waiting event, emitted for both Provider admission queueing and rate-limit backoff. The event payload contains `phase` (`admission` or `backoff`), Provider key, latest status/message, `retryAfterMs` when present, `nextAttemptAt`, `waitDeadlineAt` when a throttle window exists, remaining wait, and attempt count.

`WorkflowAgentSessionReporter` writes the event through the existing Runner-owned runtime-event outbox. Add the event to the Session transcript allowlist and make `AgentSessionGrain` validate it against the frozen `SessionWorkflowExecutionBinding`. After durable transcript persistence, the existing `ISessionWorkPort` path sends a new `ProviderRateLimitWaiting` observation to the Workflow grain. The observation carries typed rate-limit facts; it is not encoded into an error string.

The Workflow domain records the waiting facts on the current Agent execution's rate-limit state while leaving the task unfinished and its Agent result settlement in `AwaitingResult`. Repeated updates replace only the latest timing/signal facts and never extend the absolute Provider wait deadline. Admission waiting has no throttle expiry until a Provider actually returns a rate-limit signal; it remains cancellable and visible.

This event path is also useful for Pi/OpenCode session history, but it is not the authority for the terminal expiry. The Runner's durable work-result journal remains authoritative for the final `provider-rate-limited` report and retries that report until the Server acknowledges it.

Terminal expiry has a distinct runtime close contract. `enqueueTerminalClose` recognizes the `provider-rate-limited` runtime error and calls `WorkflowAgentSessionReporter.registerClose` with status `provider-rate-limited`; `registerClose` emits `turn.rate_limited` followed by the normal idle activity event and never emits `turn.failed` for this outcome. `turn.rate_limited` is added to the transcript/event allowlists and has a typed payload containing the same Provider facts as the work result. `AgentSessionGrain.BuildWorkflowRuntimeObservations` examines `turn.rate_limited` before `turn.failed`, validates the binding exactly as for other Workflow events, and maps it to `SessionWorkflowObservationKind.ProviderRateLimited`. `WorkflowSessionWorkPort` maps that value to `AgentExecutionObservationKind.ProviderRateLimited` and forwards the typed facts.

The Session observation is projection-only: it records/updates `ProviderRateLimitState` and leaves the Agent result settlement in `AwaitingResult`; it cannot call `FailTask`, emit `TaskFailed`, or close the Workflow attempt. The Runner work-result journal then submits the authoritative report. If the event arrives after the report, the binding-scoped observation is an idempotent no-op against the already rate-limited settlement; if the report arrives after the event, the same facts are retained and the dedicated report transition wins. Outbox record IDs, transcript replay, and repeated observations must preserve this ordering and avoid a generic failure event.

**Alternative considered: add a new Workflow polling endpoint for Runner progress.** Rejected because it would introduce a second delivery identity and a liveness race. The existing outbox already provides ordered, replayable, binding-validated runtime facts.

### 4. Add a first-class rate-limit outcome to the Runner and Workflow contracts

Extend `WorkItemResult`/`WorkResult` with the status and structured facts needed for bounded expiry:

```text
status: provider-rate-limited
error.code: provider-rate-limited
providerRateLimit: {
  providerId, latestStatus, message, retryAfterMs,
  firstThrottledAt, waitDeadlineAt, lastAttemptAt,
  nextAttemptAt, attempts, totalWaitMs
}
```

The field is optional and appended to existing serialized contracts. Existing successful and genuine failure results remain unchanged. AgentJob terminal projection uses the same error/category so standalone Agent consumers can distinguish it from `turn-failed`; it does not change AgentJob's unrelated lifecycle rules.

The structured field is preserved through both Workflow Action paths. `runtimeActionFailure` in `runtime/executor-capabilities.ts` copies `RuntimeError.providerRateLimit` into the internal `ActionResult`; `projectTaskOutput` copies it to `WorkItemResult.providerRateLimit` and selects `status: provider-rate-limited` instead of the normal failure status. The Pi Action's `runtimeFailure` in `actions/pi.ts` performs the same copy before the shared executor projection. The direct AgentJob projection uses the same helper and is covered separately. No path is allowed to reduce these facts to diagnostics or an error message only.

On the Server, append a typed `ProviderRateLimitFacts` field to `WorkResult` and `TaskReport` with new serializer IDs. `WorkflowItemTranslator` validates the required Provider/timing fields, maps the exact status to `TaskReportStatus.ProviderRateLimited`, and forwards the object unchanged to the lifecycle. The lifecycle's dedicated branch does not bind Action output, artifacts, follow-up tasks, or `FailureDetails`, and never invokes the generic failure-only translation.

Add `ProviderRateLimited` to the authoritative Workflow task report path rather than mapping it to `TaskReportStatus.Failed`. `WorkflowItemTranslator` validates and forwards the facts, and `WorkflowWorkLifecycle` applies a dedicated transition. A successful or genuine failed report continues through the current path. A rate-limited report has no Action output, artifact binding, follow-up task projection, or `TaskFailed` event.

Persist the facts on the Workflow `TaskRun` as `ProviderRateLimitState` with `Waiting` and `Expired` phases. Waiting is set by the Session observation and cleared on successful recovery or a normal failure. Expiry keeps the latest facts after the Agent execution settlement is closed so the retry view and API retain the Provider evidence.

Use explicit `ProviderRateLimited` task, stage, and run outcome states for the expired attempt. They are recoverable like the existing `Failed` state and therefore are not permanent `WorkflowRunStatus.IsTerminal` values. They do not populate `FailureDetails` and are never accepted by generic failure-only subscribers. The current task/run status mappers expose the exact wire category `provider-rate-limited`.

**Alternative considered: store Provider facts only in `ExecutionError.Message` or Action output.** Rejected because it loses structured timing data, makes CLI/Web behavior string-dependent, and risks persisting rate-limit evidence as normal task output.

**Alternative considered: retain `Failed` plus a special failure reason.** Rejected because existing retry, notification, and consumer paths would continue to treat the result as `TaskFailed`, which is the defect this change must remove.

### 5. Keep waiting nonterminal and make expiry explicitly retryable

While the rate-limit state is `Waiting`, the task remains `Running`, the stage does not advance, and the Workflow run remains nonterminal. `WorkflowStatusMapper` overrides the task, stage, and run wire status to `provider-rate-limit-waiting` and includes `ProviderRateLimitStatusView` with the Provider and timing facts. No retry action is offered while the current turn is still waiting, and no second dispatch is created.

On a bounded expiry, the dedicated report transitions the task, stage, and run to `ProviderRateLimited`, preserves the facts, and exposes the existing run-scoped retry operation with a rate-limit-specific label and target. `WorkflowRun.RetryTarget` and control guards recognize this outcome. Retry clears the expired Provider state, reopens the same logical task as a new normal attempt, and returns the run to the usual Ready/Runner/Agent admission path. It never bypasses the Provider limiter and never automatically resubmits during waiting or after expiry.

Update `WorkflowStatusView`, `TaskStatusView`, CLI JSON/table models, Web workflow unions, status pills, task rows, and available-action derivation. Waiting is not completed, failed, or immediately retryable. Expiry shows Provider, latest response facts, wait bound, and the explicit retry recovery action. Existing `turn-failed` and other genuine failures retain their current presentation.

### 6. Preserve contracts across persistence and verify every boundary

The implementation is migration-free at the database schema level: Workflow state is persisted in the existing run document and the new fields are additive. New Orleans/domain fields use append-only serializer IDs. Existing persisted runs without Provider state continue to deserialize with no change in meaning. New status/category values are a coordinated breaking contract for old CLI/Web/Server consumers, so all consumers are updated in the same release.

The main implementation seams are:

- Runner: new provider policy/limiter module, `RunnerHost` construction and injection, `PiRuntime`, OpenCode `turn`/`turn-prompt`, runtime error/result types, AgentJob and Workflow Action result projection.
- Session boundary: runtime event types (`turn.rate_limited`), transcript allowlist, typed Workflow observation, terminal-close ordering, and reporter/outbox payload validation.
- Server: Runner report DTOs, `WorkflowItemTranslator`, `TaskReport`/`WorkResult` and `ProviderRateLimitFacts`, Workflow run state transitions, AgentSession observation mapping, status mapper/views, retry control, event catalog/serialization, and report/persistence tests.
- Workflow Action projection: `runtimeActionFailure` and `projectTaskOutput` in `executor-capabilities.ts`/`executor.ts`, the Pi `runtimeFailure` path in `actions/pi.ts`, and direct AgentJob projection tests must preserve structured Provider facts.
- CLI/Web: new status unions, JSON fields, rendering, retry action handling, and no-generic-failure tests.

Deterministic Runner tests use fake Providers, fake clocks, queued requests, and injected cancellation. Server tests use fake dispatch/report paths and injected `TimeProvider`; they verify two separate Workflow runs sharing one Provider, separate Providers proceeding concurrently, waiting projection without stage advancement, recovery after backoff, bounded expiry, exact report translation, retry re-admission, cancellation, and unchanged non-rate-limit classification.

## Risks / Trade-offs

- [A runtime SDK may hide automatic retry attempts and prevent exact permit release or Retry-After control] -> Pi disables auto-retry and wraps every ModelRuntime transport call; OpenCode requires a pinned single-attempt server/SDK capability. The runtime health check fails closed with `provider-attempt-boundary-unsupported` when either capability is absent, and contract tests reject opaque replay before deployment.
- [The limiter is only Runner-local, so two Runners can still burst the same external Provider] -> Document the scope, configure compatible limits on all Runners, and leave distributed quota coordination as a separate design requiring an explicit shared service.
- [Provider aliases or case differences could create separate queues for one external Provider] -> Canonicalize the runtime Provider ID before keying, preserve it in facts for display, and add an explicit alias-map configuration only if deployment evidence requires it.
- [A durable waiting event can be delayed while the final result report succeeds] -> Waiting is a projection aid, not execution authority; the work-result journal durably retries the terminal report, and the Workflow report remains the sole expiry transition.
- [A Runner restart can lose in-memory admission waiters] -> The existing work journal and Runner-loss reconciliation handle the abandoned execution; no new request is replayed automatically, and a later operator retry enters admission again.
- [New status/category values break exhaustive consumers and old binaries] -> Update Server, Runner, CLI, Web, event catalog, and contract tests together; take a durable backup before deployment and do not use mixed-version rolling deployment after the first new outcome is persisted.
- [A long Provider wait can occupy a Workflow assignment and workspace longer than expected] -> Bound throttled waiting, display the deadline and next-attempt facts, keep cancellation available, and retain the existing explicit stop/retry controls.
- [A malformed or hostile Retry-After value could cause an excessive sleep] -> Validate and cap it by the configured absolute wait deadline; never issue a request after the deadline and never convert cancellation into rate-limit expiry.

## Migration Plan

1. Add and unit-test the Runner policy parser, shared Provider admission controller, canonical Provider resolution, and cancellation-aware fake clock behavior.
2. Verify and pin the production attempt capabilities before integrating retry: Pi `setAutoRetryEnabled(false)` plus the ModelRuntime transport hook, and an OpenCode server/SDK single-attempt prompt capability. Add startup fail-closed tests for a missing capability; do not ship an opaque-SDK fallback.
3. Integrate the controller into both runtime adapters and replace threshold-only 429 handling with the independent bounded retry coordinator. Add Pi/OpenCode adapter tests for Retry-After, fallback backoff, permit release, ordinary retry-budget separation, recovery, expiry, per-model-call admission, and the absence of `promptAsync()` replay.
4. Extend the runtime event/outbox and Session-to-Workflow observation path with typed waiting facts and the `turn.rate_limited` terminal event. Add tests proving terminal expiry never emits `turn.failed`, remains non-authoritative until the report, is durable/nonterminal while waiting, does not advance the stage, and does not duplicate work.
5. Extend Runner Action/AgentJob projections, reports, and Workflow translation with `provider-rate-limited` and structured facts. Add the dedicated Workflow task/stage/run transition, persistence reload coverage, explicit retry action, normal re-admission tests, and exact fact-preservation tests for OpenCode and Pi Workflow Actions.
6. Update CLI/Web status models and renderers, then run the full contract and UI test suites. Verify existing success, genuine failure, cancellation, and unknown-result settlement behavior is unchanged.
7. Deploy Server, Runner, CLI, and Web as one compatible release. No database migration is required. Back up the Workflow state store before deployment and monitor Provider wait/expiry counts and retry outcomes.

**Rollback:** before any new rate-limit outcome is persisted, revert the release normally. After new status/category values or event types are persisted, rollback requires restoring the pre-deployment state backup or deploying a reader that understands and safely preserves those values; an older binary must not be pointed at the changed state because it may classify or deserialize the new outcome incorrectly.

## Open Questions

- What default Provider concurrency and bounded-wait values should production use for GLM/zai after observing Runner capacity and Provider guidance? The implementation will support validated defaults and per-Provider overrides, but deployment tuning should be confirmed before rollout.
- Does the deployment require an explicit Provider alias map, or is the runtime's canonical Provider ID sufficient for all configured models? The first version assumes the runtime ID is authoritative and will not invent aliases without evidence.
