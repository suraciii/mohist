# Review: issue-620

## Verdict

**FAIL** — one retryable follow-up failure category can be recorded while the
Turn is settled as completed, so Slack does not render the required Retry
action.

## Must-fix findings

### MF-1 — `generation-drain-timeout` follow-up failures are settled as completed

**Violates issue acceptance criterion 1** (a retryable failure must display a
Retry action) and criterion 7 (deterministic verification of the failure
category allowlist and Retry presentation). It also violates T-007's
requirement that a failed follow-up's runtime error kind be recorded so the
failed Turn is classifiable as retryable.

In `packages/runner/src/server/followup-handler.ts:367-383`, a failed runtime
result is given activity `unknown` only when
`isUncertainFollowupFailure` recognizes `unavailable-runtime` or
`deadline-exceeded`. `generation-drain-timeout` is not included, so the
terminal activity is passed as `idle`. The same function does pass the mapped
category through `readRuntimeErrorCategory`, so the resulting event contains
`failureCategory: "generation-drain-timeout"`, but
`recordFollowupActivity` at `:653-668` derives `status: "completed"` whenever
the terminal activity is `idle`.

The Server then treats that event as a completed Turn:
`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:2639-2650`
(`ResolveFollowupTurnTerminalStatus` maps a `completed` payload to
`AgentTurnStatus.Completed`). The follow-up Slack delivery consequently has
`Status: "completed"`, and
`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackTerminalDeliveryHandler.cs:130-135`
requires `delivery.Status == "failed"` before it can call
`SlackRetryActionService` and render Retry. The user therefore receives the
normal completion/reaction-only presentation even though the recorded
category is explicitly in `AgentSessionRetryPolicy`'s retryable allowlist.

The implementation must settle runtime-result failures such as
`generation-drain-timeout` as failed terminal follow-up activity (while
preserving the existing unknown/no-category behavior for failures without a
recoverable runtime kind), and add runner plus Server/Slack presentation
coverage for this category. The current runner tests cover
`unavailable-runtime`, Pi `deadline-exceeded` → `timeout`, and permanent
`missing-session`, but do not exercise `generation-drain-timeout`, which is why
this path was missed in the earlier review.

## Re-review of previous findings

- **Pending interim Unknown delivery suppressing final reconciliation delivery — fixed.** `AgentJobGrain.StageTerminalDeliveryEvent` now replaces a pending interim Unknown obligation when entering the authoritative final state, while preserving an already-final obligation. `AgentJobRunnerRecoverySpecs.ReconciliationFailure_ReplacesPendingUnknownDeliveryAfterInterimAppendFailure` forces the interim append to fail and verifies the final `failed` event carries `runner-lost`.
- **Shared event identity for interim Unknown and final Failed reconciliation — fixed.** The Unknown and final terminal-delivery events use distinct ids, and the existing reconciliation spec verifies both persisted events.
- **Root retry losing recorded execution facts — fixed.** The retry path uses the failed Session's recorded definition, Skills, startup snapshot rebased to the new Session, attachments, startup context, workspace label, Slack provenance, and pre-allocated identities. The changed-Agent integration spec verifies these facts and the failed Turn remains unchanged.
- **Missing deterministic terminal presentation coverage — fixed for the covered categories.** The real terminal handler has positive signed Retry coverage and reaction-only fallback coverage. It does not cover MF-1's runner `generation-drain-timeout` producer path.

The MF-1 above is a separate uncovered category path rather than a failure of
the pending-delivery disposition. Earlier positive presentation tests seeded
server reconciliation categories, and the runner tests omitted the
`generation-drain-timeout` category, which explains why the earlier
per-dimension checks did not expose it.

## Dimension checks

- **Acceptance-criteria coverage — FAIL:** the signed action, authorization, durable operation, root/thread execution, restart recovery, cleanup, and Stop compatibility are covered, but one allowlisted retryable follow-up failure cannot reach Retry presentation.
- **Correctness — FAIL:** a real OpenCode `generation-drain-timeout` runtime result is mapped to a retryable category but becomes a completed Turn and therefore cannot be retried from Slack.
- **Consistency — checked, no additional must-fix issue found:** the classifier is shared by presentation and acceptance; the Retry route retains the Stop route's lease/outbox envelope; root and targeted thread retry use durable identities and provenance.
- **Tests — FAIL for MF-1:** build and existing suites pass, but the required `generation-drain-timeout` producer-to-Turn-to-Slack path is not covered and currently behaves incorrectly.

## Verification

- Server build with `dotnet build packages/server/src/Mohist.Server/Mohist.Server.csproj --no-restore -p:SkipWebBuild=true`: passed.
- Server UnitTests: 3793 passed.
- Server SpecTests: 3064 passed.
- Runner `npm test`: 1690 passed across 156 files.
- TypeScript Slack adapter `npm test`: 91 passed.
- Go adapter tests could not run because the workspace has no `go` executable.
- `git diff --check`: clean.

## Observations

- Retry acceptance checks the signed Connection, team, and conversation context but does not separately compare the signed message/thread identity with the interaction's message/thread fields. This follows the existing Stop convention and is not a must-fix against the stated acceptance criteria.
- `agent_retry_operations` has globally unique idempotency and `(SessionId, TurnId)` indexes while reads are project-scoped. The issue does not define whether those namespaces are global or project-scoped, so this remains an observation.
- The reconciliation recovery spec verifies the final event payload, while the terminal-presentation specs seed retryable delivery facts directly rather than driving the complete reconciliation event through the Slack handler. The behavior is otherwise covered by the separate deterministic tests.
- Go adapter verification remains an environment limitation, not a code finding.

<promise>FAIL</promise>
