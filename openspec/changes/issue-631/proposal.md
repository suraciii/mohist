## Why

Provider rate-limit and quota-exhaustion failures are currently treated as ordinary failed Workflow tasks after the runtime's short in-turn retries finish. Recovery handlers such as `recover:fix-ci` and `recover:fix-review-findings` can then dispatch another Agent to fix a provider condition that code changes cannot resolve, wasting recovery budget, tokens, Runner capacity, and time instead of exposing the original provider message to the user.

## What Changes

- Classify provider failures that are already judged non-recoverable by the Runner, including rate-limit exhaustion and quota or usage-limit exhaustion, as ineligible for Agent recovery handlers.
- Make the recovery scheduler preserve the failed task result and skip both recovery-handler tasks and `retrySelf` when that provider classification is present.
- Preserve the provider's original failure text in the failed task message so users can identify the affected provider, change models, or wait for the usage window to reset.
- Keep ordinary task failures and explicitly recoverable failures eligible for their existing handlers and budgets.
- Keep short in-turn provider retries and SDK retry behavior unchanged; this change applies only after the runtime has produced a non-recoverable turn failure.
- Do not add a new terminal state or change Server, CLI, Web, persistence, or public workflow contracts.

## Capabilities

- `provider-failure-recovery-eligibility`: Runner classification of non-recoverable provider rate-limit/quota failures and the rule that those failures bypass Agent recovery while ordinary failures retain existing recovery behavior.

## Impact

- **Runner runtime:** Provider-error classification and diagnostics for the OpenCode and Pi paths, plus the Workflow executor/recovery boundary that currently matches failed results against recovery handlers.
- **Workflow behavior:** Provider-limit failures remain failed task results; recovery tasks are not inserted, and the existing failure message remains available for task status and user-facing diagnostics. Existing recovery declarations and normal failure matching remain unchanged.
- **Tests:** Runner coverage for quota and rate-limit examples, both runtime paths where applicable, the no-recovery result, preservation of the original provider message, and the regression case that ordinary failures still schedule recovery.
- **Server, CLI, Web, and dependencies:** No public contract or dependency changes are expected. The non-recoverable marker remains an internal Runner concern and must not introduce a new user-visible terminal state.
