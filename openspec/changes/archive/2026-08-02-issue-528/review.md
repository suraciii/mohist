# Review — Issue 528

Reviewer mode: reviewed the full change (commits `659029329`..`2315e30d6`) against the
issue body, the three spec files, `design.md`, and `tasks.json`. Acting as a reviewer only;
no artifact other than this file was modified.

Build: `dotnet build Mohist.sln` — 0 warnings, 0 errors.
Tests: SpecTests 3739 passed, UnitTests 1751 passed, CLI 1521 passed, mohist-slack 14 passed,
web 5154 passed. Web typecheck clean. Working tree clean.

## Acceptance-criteria coverage

| Issue AC | Task | Status |
|---|---|---|
| AC1 — accepted inputs and terminal results not dropped under pressure | T-001 | PASS — `Backpressure_recovery_does_not_drop_accepted_inbox_or_terminal_outbox_rows` + `Replaceable_progress_still_merges_across_backpressure…` spec tests. |
| AC2 — Degraded(Backpressured) + reject + visible reason | T-001 + T-002 | PASS — reason-guarded `FlipBackpressuredAsync`, structured `kind:"backpressured"` response at all ingress gates (pre-check + inbox-overflow catch), `ConnectionDiagnosticState.Backpressured` branch, adapter `renderUserFacingRejection`, Web/CLI state rendering. |
| AC3 — replaceable merges; terminal/failure/user-action neither merged nor dropped | T-001 | PASS — `Replaceable_progress_still_merges_across_backpressure_and_terminal_rows_are_neither_merged_nor_dropped` spec test. |
| AC4 — explicit failure retries safely, no duplicate | T-003 | PASS — adapter `response.ok === false → retry`, spec `Delivery_outcome_retry_reschedules_explicit_failure_without_marking_uncertain`, adapter test `acks_explicit_Slack_rejections_as_retry`. |
| AC5 — Delivery uncertain shown, no blind resend, execution result unchanged | T-003 | PASS — `GET /deliveries`, `POST /deliveries/{id}/resend` (connectionId-scoped), `ResendUncertainAsync` uncertain→pending only, `Resend_endpoint_transitions_uncertain_to_pending_without_touching_execution_result`, Web `UncertainDeliveriesSection`, CLI `deliveries`/`resend-delivery`. |
| AC6 — backlog drains → Connection self-recovers | T-001 | PASS — `RecoverBackpressureAsync` 4th sweep in `DispatchAsync`, 3 recovery spec tests (outbox drain, inbox drain, either-side-full persists). |
| AC7 — long offline → possible-gap notice, ask user to resend | T-004 | PASS — `OfflineGapAt` stamped in `RecordAdapterHeartbeatAsync`, 3 heartbeat-gap spec tests, gap notice on Web page + CLI, `clear-gap` endpoint/CLI command, clear-on-liveness at all 4 ingress sites, `Accepted_ingress_clears_offline_gap_after_reconnect` integration test. |

## Spec coverage

All three spec files have at least one scenario test per requirement:

- **slack-capacity-backpressure**: reversibility (3 scenarios), no-drop (2 scenarios), diagnostic
  distinctness (2 scenarios), sender-visible rejection (2 scenarios) — all covered.
- **slack-delivery-outcomes**: safe retry (2 scenarios), uncertain visibility (2 scenarios),
  no-reclassify (2 scenarios) — all covered.
- **slack-offline-gap-notice**: detection (2 scenarios), notice + resend ask (2 scenarios),
  no auto-replay (1 scenario) — all covered.

## Design compliance

- **D1**: recovery sweep is the 4th sweep in `DispatchAsync`, runs under the dispatch gate,
  uses `AgentConnectionStore.ListBackpressuredAsync`, checks both pending counts against
  capacities with strictly-below threshold. ✅
- **D2**: `RecoverBackpressuredAsync` is reason-guarded (`InboxOverflow` or `OutboxOverflow`
  only). ✅
- **D3**: `Backpressured` branch placed after service-offline, before owner-unavailable;
  names inbox-vs-outbox in the reason; single wait/retry next action. ✅
- **D4**: structured `IngressResult{backpressured}` success response (not HTTP 409);
  adapter renders via `runtime.web`; kind discriminator (`"backpressured"` only) prevents
  double rendering with server-enqueued rejections. ✅
- **D5**: `GET /deliveries` + `POST /deliveries/{id}/resend`; `ResendUncertainAsync`
  transitions uncertain→pending with bumped `AttemptCount` (still subject to
  `OutboxMaxAttempts`); duplicate warning in CLI prompt + Web inline confirmation. ✅
- **D6**: `OfflineGapAt` nullable column + EF migration (additive); captured at heartbeat
  when `now - previous >= SlackEventRetentionWindow`; surfaced in diagnostic facts;
  cleared on first accepted ingress (`!accepted.AlreadyExisted`) or operator acknowledge. ✅

## Non-goals compliance

- No persistent event caching in `mohist-slack` (adapter renders via bot-token client only). ✅
- No exactly-once claim. ✅
- No auto-replay of gap events. ✅
- No credential rotation / owner transfer / disable-enable-delete work. ✅

## Test constraints

All tests use `FakeTimeProvider` / `TestSqliteDatabase` / fake Slack API client / fake
transport — no real external dependencies, no wall-clock time. ✅

## Findings

No must-fix or non-blocking problems found. The change is internally consistent, covers all
seven acceptance criteria, all three spec files have scenario coverage, and the design
decisions (D1–D6) are faithfully implemented. The previous review's F1–F3 and N1–N3 findings
have all been resolved: `OfflineGapAt` is cleared on first accepted ingress at all four
ingress sites and via an operator-acknowledge endpoint; the gap notice is rendered on both
Web and CLI; heartbeat gap detection has spec tests; `IsBackpressured` matchers are aligned;
inbox-overflow catch blocks return the structured response; `ResendUncertainAsync` is scoped
by `connectionId`.

<promise>PASS</promise>
