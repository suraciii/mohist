# Review: issue-620

## Verdict

**FAIL** — the reconciliation delivery fix handles the normal append path, but a
failed interim Unknown delivery can still suppress the final retryable Failed
delivery permanently.

## Must-fix findings

### MF-1 — A pending interim Unknown delivery prevents the final Failed delivery from being staged

**Violates acceptance criterion 1** (a retryable failure must display Retry) and
criterion 7's deterministic presentation/reconciliation verification. It also
leaves the retryability and Retry-action contracts incomplete for a real
`runner-lost` or `report-timeout` reconciliation when terminal event delivery
has a transient failure.

`AgentJobGrain.StageTerminalDeliveryEvent` refuses to stage anything when
`State.PendingTerminalDeliveryEvent` is already populated
(`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:1446-1448`).
The interim Unknown transition stores an Unknown pending event and attempts to
append it (`AgentJobGrain.Recovery.cs:203-208`). If that append fails, the
exception is swallowed and the Unknown event remains pending
(`AgentJobGrain.cs:1470-1483`).

When the recovery deadline later expires, `FailRecoveringJobIfDueAsync` correctly
enters the Job's terminal Failed state with the retryable category
(`AgentJobGrain.Recovery.cs:215-237`), but `EnterTerminalStateAsync` calls
`StageTerminalDeliveryEvent` while the Unknown event is still pending
(`AgentJobGrain.cs:1219-1274`). The guard returns without creating the final
Failed event. The terminal path then retries/emits only the existing Unknown
pending event (`AgentJobGrain.cs:1229-1235`), after which it can be cleared;
there is no durable obligation left for the final Failed payload.

Therefore the Session/Turn can be Failed with `runner-lost` or `report-timeout`,
while Slack receives only the earlier `status=unknown,
failureCategory=unknown` projection. `SlackTerminalDeliveryHandler` quite
correctly renders Retry only for the missing final `status=failed` payload, so
the user remains reaction-only and cannot recover the turn.

The new distinct Unknown event id fixes the earlier event-store deduplication
case only when the Unknown append has already succeeded. The fix must also
handle a pending Unknown obligation during finalization: replace or otherwise
chain it to a distinct final Failed obligation, preserving idempotency and
ensuring the final payload is eventually appended. Add deterministic coverage
that forces the interim terminal-delivery append (or its pending-state
persistence) to fail, advances the recovery deadline, and asserts that the
final `failed` payload contains the exact retryable category and reaches Slack
with a signed Retry action.

## Re-review of previous findings

- **Previous MF-1 (shared event id for Unknown and final reconciliation):
  partially fixed.** The Unknown delivery now uses
  `agent-job:{jobKey}:terminal-delivery:unknown`, while the final delivery uses
the original id, so successful Unknown append followed by reconciliation now
  produces two persisted events. The pending-obligation case above means the
  reconciliation path is not fully fixed and still meets the must-fix bar.
- **Previous MF-2 (root retry execution facts): fixed for the reviewed scope.**
  Root retry uses the failed Session's recorded definition, startup snapshot,
  input text, attachments, startup context, workspace label, Slack provenance,
  and pre-allocated identities; the changed-Agent integration coverage passes
  and the failed Turn remains unchanged.
- **Previous presentation-coverage finding: fixed for the reviewed scope.**
  The real terminal handler has positive coverage for the three server-recorded
  retryable categories and reaction-only fallback coverage. It does not cover
  the pending-obligation failure in MF-1.

## Dimension checks

- **Acceptance-criteria coverage — FAIL:** the signed action, authorization,
  idempotent operation, root/thread execution, restart recovery, cleanup, and
  Stop compatibility are implemented. The final Slack presentation is still
  incomplete when reconciliation's interim delivery remains pending.
- **Correctness — FAIL:** the Job's authoritative facts can reach Failed with a
  retryable category while the corresponding terminal delivery remains the
  Unknown payload.
- **Consistency — checked, no additional must-fix issue found:** the shared
  retryability policy is used by presentation and acceptance; the Retry route
  preserves the Stop route envelope; root retry uses recorded execution facts.
- **Tests — FAIL for the uncovered failure mode:** the added reconciliation
  spec verifies distinct ids after a successful Unknown append, and the
  presentation spec seeds retryable categories directly. No test exercises a
  failed interim append followed by deadline reconciliation, which is the path
  that still loses the final Retry notice.

## Verification

- Server build with `-p:SkipWebBuild=true`: passed.
- Runner follow-up-handler tests: 10 passed.
- Full SpecTests invocation was started but exceeded the 300-second environment
  timeout; the latest change commit reports 3063 passing tests, but that result
  was not independently reproduced in this review.
- Go adapter tests remain unavailable because the workspace has no `go`
  executable.
- `git diff --check`: clean.

## Observations

- The current presentation tests parameterize `report-timeout` as a seeded
  terminal fact, but do not drive the real report-timeout producer through its
  deadline and Slack projection. This is useful coverage but weaker than an
  end-to-end producer-to-button test; it does not add a second verdict driver
  beyond MF-1.
- `AgentRetryOperationStore` scopes reads by `ProjectId`, while the migration's
  unique indexes for idempotency key and `(SessionId, TurnId)` are global. The
  issue does not define whether those keys are globally or project-scoped, so
  this remains an observation rather than a must-fix.
- Retry acceptance validates Connection/team/conversation context but does not
  separately compare the signed message/thread identity with the interaction's
  message/thread fields. The signed target remains bound to the rendered notice,
  and this follows the existing Stop convention.
- Root retry copies attachment descriptors, but the reviewed tests do not prove
  that any attachment rows are rebound from the failed input owner to the new
  Session/Input owner. The current Slack binder does not supply file content,
  so this remains out of scope for the verdict.

<promise>FAIL</promise>
