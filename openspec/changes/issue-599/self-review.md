# Self-Review

## Review Basis

This is a re-review. I re-read the live issue with `mo issue view 599 --project proj_f6c141d63b6243bfbb481737b2243b87` before reviewing the artifacts. The issue acceptance criteria are: configurable execution-plane concurrency for one Provider; 429/rate-limit backoff honoring `Retry-After` without consuming the ordinary consecutive-retry budget; bounded expiry classified as `provider-rate-limited` and distinct from genuine failure; and deterministic tests for concurrency, recovery, and expiry.

The previous review reported three must-fix findings. All three dispositions are verified against the current artifacts:

- **F1, production attempt boundary: fixed.** `design.md:62-70`, `tasks.json:T-002`, and `specs/provider-rate-limit-retry/spec.md:16-47` define one Mohist-owned `ProviderAttemptExecutor` for both adapters, require exactly one transport attempt, prohibit opaque replay and `promptAsync`, define Pi fail-closed behavior, and require the pinned OpenCode server/SDK capability plus compatibility tests.
- **F2, generic terminal close: fixed.** `design.md:86-90`, `tasks.json:T-003`, and `specs/workflow-rate-limit-outcomes/spec.md:32-52` cover both OpenCode and Pi close paths, typed `turn.rate_limited`, binding validation, duplicate/legacy `turn.failed` precedence, non-authoritative Session observation, and report/event ordering.
- **F3, structured-fact loss: fixed.** `design.md:110-118`, `tasks.json:T-004`, and `specs/workflow-rate-limit-outcomes/spec.md:15-30` name the complete OpenCode and Pi projection chains, the shared `ProviderRateLimitFacts` field, malformed-fact rejection, Server translation, persistence, and exact-fact tests.

No regression introduced by those fixes meets the must-fix threshold. The revised fail-closed runtime behavior is deliberate, and the revised Session observation remains non-authoritative until the durable work report.

## Verdict

PASS. No must-fix problems remain; the plan is ready to build.

## Must-Fix Findings

None.

## Dimension Review

### Coverage: checked, no issue

The backpressure spec covers canonical Provider-keyed admission, separate Provider queues, per-request lease lifetime, cancellation, and composition with existing Runner/Agent gates (`specs/provider-rate-limit-backpressure/spec.md:1-54`). The retry spec covers 429 recognition, `Retry-After`, fallback backoff, independent retry accounting, bounded expiry, cancellation, and genuine-failure preservation (`specs/provider-rate-limit-retry/spec.md:1-115`). The Workflow spec covers waiting, expiry, report translation, Session close behavior, persistence, retry, and CLI/Web projections (`specs/workflow-rate-limit-outcomes/spec.md:1-118`). These collectively address every issue acceptance criterion.

### Correctness: checked, no issue

The design admits immediately before each actual Provider attempt, releases before backoff, reacquires for retries, and bounds the absolute throttle wait (`design.md:52-70`). The adapter contract and fail-closed rules prevent hidden SDK replay from defeating the limiter. Typed Session close and report ordering prevent expiry from becoming `turn-failed` or `TaskFailed` (`design.md:86-90`). The report path preserves the exact category and structured Provider/timing facts, while ordinary failures and cancellation retain their existing classifications (`design.md:110-128`).

### Current-Code Consistency: checked, no issue

The plan changes the existing shared `RunnerHost` runtime lifetime, Pi `ModelRuntime` transport, OpenCode prompt path, Workflow Agent Session reporter/outbox, AgentSession observation boundary, Runner result projection, Server translator/lifecycle, and CLI/Web status consumers. It explicitly accounts for the current OpenCode SDK limitation rather than silently relying on it: the required server/SDK capability must be upgraded or patched and readiness fails closed when absent (`design.md:66`, `proposal.md:28`). Additive serialized fields, append-only enum values, binding validation, and a coordinated consumer release match the repository's existing contract conventions.

### Task Breakdown: checked, no issue

The ordering is buildable and verifiable: T-001 establishes shared admission; T-002 adds the production attempt boundary and bounded retry; T-003 adds durable waiting and typed close observations; T-004 adds authoritative report/state/retry handling; and T-005 updates consumers. Dependencies are explicit and acyclic (`tasks.json:T-001` through `T-005`). Each load-bearing boundary has deterministic acceptance coverage, including same-Provider concurrency, recovery, expiry, close ordering, structured fact preservation, cancellation, and unchanged genuine-failure behavior.

## Observations

- The checked-in OpenCode dependency is currently `@opencode-ai/sdk` 1.18.3, whose prompt type does not expose `retry`; the plan correctly treats the compatible server/SDK build as a prerequisite and fails readiness closed until its one-attempt behavior is verified. Deployment must not enable OpenCode before that prerequisite is met (`packages/runner/package.json:30`, `design.md:66`).
- Production defaults for Provider concurrency, maximum wait, and fallback backoff remain deployment-tuning questions (`design.md:151-152`). The plan requires validated values and per-Provider overrides, so this does not block the issue acceptance criteria.
- Admission is intentionally Runner-local; independent Runner processes can still exceed one external Provider quota. The design records this as an explicit non-goal (`design.md:8-9`, `design.md:31-33`).
- The new outcome is a coordinated breaking contract across Server, Runner, CLI, and Web. The migration and rollback sections account for that operational constraint (`design.md:134-154`), and it does not alter the verdict.

<promise>PASS</promise>
