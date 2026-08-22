# Review: issue-620

## Verdict

**FAIL** — two must-fix gaps leave the retry behavior incomplete for a real
retryable failure category and for faithful root-attempt recovery.

## Must-fix findings

### MF-1 — The real `report-timeout` reconciliation path never becomes a retryable terminal failure

**Violates issue acceptance criterion 1** (retryable failures display Retry) and
criterion 7's deterministic failure-category allowlist verification.

The authoritative policy in
`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionRetryPolicy.cs:12-29`
accepts the exact category `report-timeout`, but the real AgentJob recovery path
does not record that category on a retryable terminal Turn. In
`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:113-124`,
`OnJobTimeoutAsync` builds the reason
`report-timeout: report timeout after <duration>` and calls
`EnterUnknownStateAsync`. That method stages an `unknown` terminal-delivery
envelope with `failureCategory: "unknown"` at lines 179-194, while leaving the
AgentJob in `Unknown`. The report-timeout branch also sets no recovery deadline
(line 117-119), so `FailRecoveringJobIfDueAsync` does not later convert it to a
failed Turn. The only path that reaches that conversion is the runner-loss path;
if it did receive the suffixed reason, lines 210-215 would also pass the whole
reason as `failureCategory`, which would not equal the exact allowlisted token.

As a result, a genuine report-timeout reconciliation failure gets no Retry
button: presentation requires `status == "failed"` and an exact allowlisted
category in `SlackTerminalDeliveryHandler.ShouldRenderRetry` at
`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackTerminalDeliveryHandler.cs:130-135`.
The fix must define the intended terminal/retryable state for report-timeout,
record the canonical category token `report-timeout` separately from the
human-readable reason, and add deterministic coverage using the real
report-timeout producer through presentation and acceptance.

### MF-2 — Root retry does not preserve the original launch's durable execution facts

**Violates the `slack-retry-attempt-execution` plan requirement** that a root
retry create a new Session from the original durable Slack provenance **and the
recorded execution facts of the original launch**; this makes the accepted
root retry an incomplete reproduction of the failed request.

`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionRetryService.cs:236-264`
rebuilds only a `ConnectionLaunchOrigin`, the input text, and a workspace-name
label. It then calls `LaunchConnectionAsync`, which resolves the *current*
Agent definition in `packages/server/src/Mohist.Server/Agent/Services/AgentLauncher.cs:451-457`
instead of using the failed Session's persisted
`Session.Settings.Definition`. The retry also does not pass the original input's
`Attachments` or `StartupContext`, nor the persisted
`Session.Settings.AgentSessionStartup`.

If the Agent configuration/defaults change after the failure, the new Session
can run with a different runtime, model, variant, instructions, or skills. If
the original Slack request carried accepted attachments or startup context,
the retry silently drops them. The new Session therefore has the right Slack
identity but not the recorded execution request/facts that the plan requires.
The retry path must source these values from the failed Session/Input snapshot,
carry them through the launch pipeline, and add coverage for changed current
configuration plus attachments/startup context so a root retry remains the
same durable request with fresh execution identities.

## Re-review of previous finding

The previous review's MF-1 (missing deterministic handler-level presentation
coverage) is fixed properly. The new
`SlackTerminalDeliveryPresentationSpecs` invokes the real
`SlackTerminalDeliveryHandler`, inspects the explicit-failure outbox payload
and signed action value, and covers reaction-only fallback for missing durable
facts and missing signing material. The handler also now catches unavailable
durable retry facts and preserves the reaction-only liveness fallback. No
regression was found in the existing Stop path: the signing extraction is
internal, Stop canonicalization remains unchanged, and the Stop interaction
specs pass.

## Dimension checks

- **Coverage — FAIL:** the allowlist includes `report-timeout`, but the real
  producer records a suffixed value, and root retries omit recorded execution
  facts described by the attempt-execution contract.
- **Correctness — FAIL for MF-1 and MF-2:** the remaining retryable categories,
  signed presentation, acceptance revalidation, durable receipt, targeted
  thread dispatch, recovery worker, and failed-Turn immutability were checked;
  these two paths do not satisfy their contracts.
- **Consistency — checked, no additional must-fix issue found:** Retry uses the
  shared classifier and signing helper, Stop remains on its existing route,
  and adapter source contracts remain unchanged. The project-scoped lookup vs
  global unique-index concern is recorded below as an observation.
- **Tests — checked with the gaps above:** Server build succeeds; UnitTests
  pass (3793), SpecTests pass (3059), runner tests pass (1690), and TypeScript
  Slack adapter tests pass (91). The existing suites do not exercise the real
  report-timeout category producer or root retries after execution facts/config
  changes. Go adapter tests could not run because `go` is not installed.

## Observations

- `AgentRetryOperationStore` scopes reads by `ProjectId`, while the migration
  creates global unique indexes for `IdempotencyKey` and `(SessionId, TurnId)`.
  A caller reusing an idempotency key in a different project could hit a
  uniqueness exception and fail to read the winner. The issue does not define
  a cross-project idempotency-key namespace, so this is an implementation
  consistency risk rather than a must-fix finding here.
- Retry acceptance validates Connection, team, and conversation context, as
  required, but does not separately compare the signed message/thread
  identity with the interaction's message/thread fields. This matches the
  existing Stop route's context convention; the signed fields still bind the
  rendered target, so this remains an observation rather than a verdict driver.
- Go adapter verification remains unavailable in this environment because the
  workspace has no `go` executable.

<promise>FAIL</promise>