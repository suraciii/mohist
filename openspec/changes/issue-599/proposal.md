## Why

When several Workflow runs use different Agents backed by the same model Provider, the current Agent-level concurrency gate does not protect the Provider's shared request limit. A burst of requests can therefore produce repeated 429 responses; after Pi or OpenCode exhausts its turn retry threshold, the Workflow records a generic task failure, and an operator retry immediately recreates the burst. Provider throttling is an execution-plane capacity fact and needs backpressure, not a false task outcome.

## What Changes

- Add a configurable Provider-scoped concurrency limit for Workflow Agent executions. Requests using the same canonical Provider identity share one admission bound even when they originate from different Agents or Workflow runs.
- Apply Provider admission before sending an in-flight model request. Work that reaches the bound waits in the execution plane instead of creating an avoidable 429 burst; existing Agent and Runner capacity limits remain separate constraints.
- Treat 429 and equivalent Provider rate-limit responses as retryable capacity signals. Honor `Retry-After` when supplied and otherwise use the configured backoff policy; rate-limit backoff does not consume the normal consecutive Provider retry budget.
- Bound the total rate-limit waiting period. If the Provider remains throttled beyond that window, return a distinct `provider-rate-limited` outcome with the Provider and throttling facts needed for an actionable retry, rather than reporting generic `turn-failed`.
- Preserve the distinction between rate-limit waiting, rate-limit expiry, and genuine runtime or task failure in Workflow task reports and status projections. **BREAKING**: consumers of Workflow task/run outcomes must handle the new rate-limit state/category instead of treating every exhausted 429 sequence as an ordinary task failure.
- Make the Pi and OpenCode production adapters expose one Provider attempt at a time. Pi disables SDK auto-retry and wraps each ModelRuntime transport call; OpenCode requires a pinned single-attempt server/SDK capability and fails closed when it is unavailable. No opaque SDK replay or raw `promptAsync()` request may bypass Provider admission.
- Keep Provider expiry out of the generic AgentSession close path: emit a typed `turn.rate_limited` event, map it to a non-failing Workflow observation, and let the durable Runner work-result report apply the authoritative outcome.
- Preserve structured Provider timing and identity facts through both OpenCode and Pi Workflow Action projections, Runner `WorkItemResult`/`WorkResult`, Server `TaskReport`, and CLI/Web status views.
- Add deterministic coverage for shared-Provider concurrency, backoff recovery, `Retry-After`, bounded-wait expiry, production SDK attempt boundaries, terminal-close ordering, structured fact preservation, and separation from genuine failures.

## Capabilities

- `provider-rate-limit-backpressure`: Configurable Provider-keyed execution admission and bounded waiting for concurrent Workflow Agent requests, including the shared scope across Agents/runs and interaction with existing Runner and Agent capacity gates.
- `provider-rate-limit-retry`: Recognition and handling of Provider throttling responses, including `Retry-After`, fallback backoff, retry-budget accounting, cancellation, and the bounded transition from waiting to a `provider-rate-limited` outcome.
- `workflow-rate-limit-outcomes`: The Workflow task/report/status contract for distinguishing Provider rate-limit waiting and expiry from genuine task or runtime failure, including the data exposed to CLI and Web consumers and the available recovery action.

## Impact

- **Runner execution plane:** Provider identity resolution and request admission in `packages/runner/src/runtime/agent-job-turn.ts`, the Pi runtime, and the OpenCode runtime/prompt path; Provider error policies, backoff timing, and runner configuration become part of the execution boundary. Existing runtime adapters remain responsible for reporting facts, while the shared limiter owns Provider capacity behavior.
- **Server Workflow and Runner boundary:** Dispatch/result translation and reporting under `packages/server/src/Mohist.Server/Runner/Services/`, `Workflow/Grains/`, and `Workflow/Domain/Run/` must carry the rate-limit waiting/expiry classification without converting it to an ordinary `TaskFailed` reason. Existing Agent-level concurrency and Runner slot gates remain in place and must compose with Provider admission.
- **CLI and Web projections:** Workflow task/run status DTOs, available actions, and rendering need to expose a throttled/waiting state and a distinct expired rate-limit reason so users do not immediately retry into the same Provider limit.
- **Dependencies and persistence:** No new external Provider dependency is required. The change may extend runner configuration and execution/report contracts; any durable Workflow status fields or serialized report values must remain compatible with in-flight work and be covered by migration-free or explicitly versioned contract tests.
- **Tests:** Runner unit tests use fake Provider responses and injected/fake time; Server Workflow tests use deterministic dispatch/report fakes. Tests must cover concurrent requests from separate Workflow runs sharing one Provider, recovery after backoff, bounded expiry, and unchanged classification of non-rate-limit failures.
