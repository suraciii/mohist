# Review: issue-620

## Verdict

**PASS** — no must-fix problems remain; the change is ready to merge.

## Re-review of previous findings

- **`generation-drain-timeout` follow-up settlement — fixed.**
  `packages/runner/src/server/followup-handler.ts` now treats this runtime
  failure as a failed terminal activity while preserving the mapped
  `failureCategory`. The runner test asserts `activity: "unknown"`,
  `status: "failed"`, the retryable category, and the failure reason. Server
  follow-up grain coverage verifies the Turn becomes `Failed` and retains the
  category, while Slack terminal-presentation coverage includes this category
  in the signed Retry notice matrix.
- **Pending interim Unknown reconciliation delivery — fixed.**
  `AgentJobGrain.StageTerminalDeliveryEvent` replaces a pending interim
  Unknown obligation when the authoritative terminal state is reached, and
  uses distinct event identities for interim Unknown and final delivery. The
  recovery spec forces the interim append to fail and verifies the final
  `failed` delivery carries `runner-lost`.
- **Root retry losing recorded execution facts — fixed.**
  `AgentSessionRetryService` now sources the retry from the failed Session's
  durable definition, startup snapshot, input attachments, startup context,
  workspace label, prompt, Slack provenance, and pre-allocated identities. It
  forwards those snapshots through the launch coordinator and skips current
  launchability re-resolution for this accepted replay. The changed-Agent
  integration spec verifies these facts and that the failed Turn remains
  unchanged.
- **Missing deterministic terminal-presentation coverage — fixed.**
  `SlackTerminalDeliveryPresentationSpecs` invokes the real terminal handler,
  checks the explicit-failure outbox payload and signed Retry action, and
  covers reaction-only fallback for missing durable facts and unavailable
  signing material.

## Dimension checks

- **Acceptance-criteria coverage — checked, no issue.** Retry is rendered only
  for failed Turns whose recorded category is in the shared allowlist, with a
  five-minute signed action; non-retryable, absent-category, non-failure, and
  Manager presentations remain reaction-only. Root retries create a distinct
  Session and thread retries create a fresh targeted follow-up without
  changing the failed Turn. The route revalidates signature, expiry, Slack
  context, actor, current access policy, and target facts before dispatch.
  Durable operation receipts, pre-allocated identities, unique idempotency
  boundaries, the pending recovery worker, and bounded cleanup cover the
  at-most-once and restart requirements. Stop remains on its existing route
  and uses the extracted signer without an observable behavior change.
- **Correctness — checked, no issue.** The authoritative exact-ordinal
  `AgentSessionRetryPolicy` is consumed by both presentation and acceptance.
  Runner follow-up errors use the same extracted runtime-kind mapping as the
  AgentJob path. The generation-drain-timeout path now reaches the required
  failed Turn state instead of being interpreted as a completion.
- **Consistency with the surrounding codebase — checked, no issue.** Retry
  reuses the existing launch coordinator, Session follow-up pipeline, Slack
  lease validation, access decider, outbox reply path, and obligation-worker
  pattern. The provider inbox remains raw ingress storage; operation
  idempotency is owned by the new receipt store as required by the design.
- **Tests — checked, no issue.** The focused changed-behavior coverage passes:
  runner follow-up tests (11), Server terminal-presentation specs (6), Agent
  reconciliation specs (9), follow-up grain specs (44), Retry interaction
  specs (5), root/thread/recovery specs (7), Stop interaction specs (5), and
  the full Server UnitTests suite (3793). TypeScript Slack adapter tests pass
  (91). The relevant runner typecheck and Server build also pass.

## Observations

- The full SpecTests assembly invocation exceeded the 300-second environment
  timeout, although all issue-620-related SpecTest classes pass independently.
  This is an environment/runtime verification limitation, not a product
  defect.
- Go adapter tests could not be executed because the workspace has no `go`
  executable. The changed Go tests are test-only forwarding coverage; this is
  an environment limitation rather than a must-fix finding.
- `AgentRetryOperationStore` reads are project-scoped while the migration's
  unique idempotency and `(SessionId, TurnId)` indexes are global. The issue
  does not define whether those namespaces are global or project-scoped, so
  this remains an implementation-policy observation rather than an acceptance
  failure.
- The reconciliation specs verify the real final delivery event and the
  terminal-presentation specs verify the real Slack handler, but they do not
  combine the report-timeout deadline, event dispatch, and Slack outbox into a
  single end-to-end test. The shared production path and each relevant
  boundary are deterministically covered, so this does not meet the must-fix
  bar.
- Retry acceptance follows the existing Stop convention by validating signed
  Connection/team/conversation context without separately comparing the signed
  message/thread identity to the interaction envelope. The signed target
  still binds the rendered action, and the issue's explicit context criterion
  does not require a separate message/thread check.

<promise>PASS</promise>
