# Review: issue-620

## Verdict

**FAIL** — the reconciliation path for a real retryable failure still does not
produce a retryable Slack terminal notice.

## Must-fix findings

### MF-1 — Reconciliation failures become failed in the Job, but the final Slack delivery is deduplicated away

**Violates issue acceptance criterion 1** (retryable failures display Retry) and
criterion 7's deterministic presentation verification. It also leaves the
`slack-failure-retryability` and `slack-retry-action` contracts incomplete for
`runner-lost`/`report-timeout` reconciliation.

`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:108-129`
now records a canonical `report-timeout` (or `runner-lost`) category and arms a
recovery deadline. However, the initial transition to `Unknown` immediately
stages and emits a terminal-delivery event at lines 203-208. That event uses the
stable id `agent-job:{jobKey}:terminal-delivery`, created in
`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:1446-1452`.

When the reconciliation deadline expires, `FailRecoveringJobIfDueAsync` does
enter the Job's `Failed` state with the exact category at
`AgentJobGrain.Recovery.cs:215-237`, but the subsequent terminal-delivery event
has the same source and event id. `EventStore.AppendAsync` treats that pair as
already persisted and returns without storing the new failed payload at
`packages/server/src/Mohist.Server/Infrastructure/Data/Events/EventStore.cs:94-102`.
Consequently Slack only receives the earlier `status=unknown,
failureCategory=unknown` delivery, not the final `status=failed,
failureCategory=report-timeout`/`runner-lost` delivery.

The presentation handler would only render Retry for the final failed payload
(`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackTerminalDeliveryHandler.cs:127-135`),
and `SlackRetryActionService.CreateRetryActionAsync` independently re-reads
the target Turn and requires it to be `Failed` at
`packages/server/src/Mohist.Server/Slack/Services/SlackRetryActionService.cs:40-49`.
Thus the real unresolved report-timeout path remains reaction-only despite the
allowlist containing `report-timeout`. The new tests only assert that a recovery
deadline is present; they do not advance past that deadline, emit the final
reconciliation result, and run it through the actual Slack handler.

The fix must make the interim Unknown delivery and the final failed delivery
distinct or otherwise updateable while retaining idempotency, and must add a
deterministic end-to-end spec using the real report-timeout producer through
reconciliation and Slack presentation. The spec must assert the exact
`report-timeout` category, `failed` target state, and signed Retry action.

## Re-review of previous findings

- **Previous MF-1 (report-timeout reconciliation): not fixed.** The canonical
  category and deadline are now stored, but the stable terminal-delivery event
  id prevents the final failed presentation from reaching Slack. The prior
  disposition therefore does not hold.
- **Previous MF-2 (root retry execution facts): fixed for the reviewed scope.**
  Root retry now reads the failed Session's durable `Definition`, startup
  snapshot, input text, attachments, startup context, workspace label, and Slack
  provenance; it passes the definition/startup overrides and pre-allocated
  identities through the coordinator instead of resolving a new execution
  definition from current Agent settings. The changed-Agent integration spec
  passes, and the failed Turn remains unchanged.
- **Previous presentation-coverage finding: fixed.** The real terminal handler
  is exercised for a positive signed Retry notice and reaction-only fallback
  cases. That coverage does not, however, cover the missing real
  report-timeout-to-final-delivery transition described above.

## Dimension checks

- **Acceptance-criteria coverage — FAIL:** ordinary seeded retryable failures,
  signed acceptance, root/thread execution, operation persistence/recovery,
  cleanup, and Stop compatibility are covered; the actual reconciliation
  producer does not reach Retry presentation.
- **Correctness — FAIL:** the final Job facts contain the canonical category,
  but the event-store identity/idempotency behavior suppresses the final Slack
  projection.
- **Consistency — checked, no additional must-fix issue found:** the shared
  classifier is used by presentation and acceptance, the Retry route preserves
  the Stop route envelope, and root retry uses the recorded execution
  definition.
- **Tests — FAIL for coverage completeness:** the final build and test runs
  below pass, but no test exercises the real report-timeout deadline through
  final terminal delivery and Retry rendering, which is the path that currently
  fails.

## Verification

- Server build: passed.
- Server UnitTests: 3793 passed.
- Server SpecTests: 3060 passed after rebuilding the test project.
- Runner tests: 1690 passed.
- TypeScript Slack adapter typecheck/tests: 91 passed.
- Go adapter tests could not run because no `go` executable is installed.

## Observations

- `AgentRetryOperationStore` scopes reads by `ProjectId`, while the migration's
  unique indexes for idempotency key and `(SessionId, TurnId)` are global. A
  cross-project reuse of an idempotency key can therefore raise a uniqueness
  error without a project-scoped winner being readable. The issue does not
  define the idempotency-key namespace, so this remains an observation.
- Retry acceptance validates Connection, team, and conversation context but does
  not separately compare the signed message/thread identity with the
  interaction's message/thread fields. The signed target still binds the
  rendered notice and this follows the existing Stop convention.
- Root retry copies attachment descriptors but the reviewed test does not prove
  that attachment rows are rebound from the failed input owner to the new
  Session/Input owner. If accepted Slack attachments become available through
  this path, the new input-scoped content URL may not be able to read rows still
  owned by the old input; this is not used as a verdict driver because the
  current Slack binder accepts no file content.
- Go adapter verification remains unavailable in this environment.

<promise>FAIL</promise>
