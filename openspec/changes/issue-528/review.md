# Review — Issue 528

Reviewer mode: reviewed the changed files (commits `72d777401`..`ca6a45416`) against the
issue body, the plan artifacts under `openspec/changes/issue-528/`, and the three spec files.
Acting as a reviewer only; no artifact other than this file was modified.

Build: `dotnet build Mohist.sln` — 0 warnings, 0 errors.
Tests: SpecTests 3733 passed, UnitTests 1751 passed, CLI 1521 passed, mohist-slack 14 passed,
web 5154 passed.

## Acceptance-criteria coverage

| Issue AC | Task | Status |
|---|---|---|
| AC1 — accepted inputs / terminal results not dropped under pressure | T-001 | PASS — `SlackOutboxBackpressureRecoverySpecs.Backpressure_recovery_does_not_drop_accepted_inbox_or_terminal_outbox_rows` + replaceable-merge regression. |
| AC2 — Degraded(Backpressured) + reject + visible reason | T-001 + T-002 | PASS — recovery sweep, `ConnectionDiagnosticState.Backpressured` branch, structured `kind:"backpressured"` IngressResult, CLI `StoredPrimaryState` backpressured branch. |
| AC3 — replaceable merges; terminal/failure/user-action neither merged nor dropped | T-001 | PASS — `Replaceable_progress_still_merges_across_backpressure…` spec. |
| AC4 — explicit failure retries safely, no duplicate | T-003 | PASS — adapter `response.ok === false → retry`, spec `Delivery_outcome_retry_reschedules_explicit_failure_without_marking_uncertain`. |
| AC5 — Delivery uncertain shown, no blind resend, execution result unchanged | T-003 | PASS — `GET /deliveries`, `POST /deliveries/{id}/resend`, `ResendUncertainAsync`, `SlackDeliveryOutcomesSpecs`, Web `UncertainDeliveriesSection`, CLI `deliveries`/`resend-delivery`. |
| AC6 — backlog drains → Connection self-recovers | T-001 | PASS — `RecoverBackpressureAsync` sweep in `SlackOutboxDispatcherService.DispatchAsync`, reason-guarded flip, `outbox-drains` + `inbox-drains` specs. |
| AC7 — long offline → possible-gap notice, ask user to resend | T-004 | **FAIL — see F1, F2, F3.** |

## Findings

### F1 (must-fix) — `OfflineGapAt` is never cleared

T-004 AC2 requires: "after the first accepted ingress post-gap (or operator acknowledge)
`OfflineGapAt` is cleared and the notice disappears." Design D6: "Clear `OfflineGapAt` when the
first new ingress is accepted after the gap (proven liveness) or on an explicit operator
acknowledge."

`AgentConnectionStore.ClearOfflineGapIfSetAsync` (`AgentConnectionStore.cs:279`) is defined
but **never called from anywhere** — not from the ingress path (`SlackConnectionRoutes.cs`
has zero references to `OfflineGapAt`), and there is no operator-acknowledge endpoint or
CLI/Web command. `UpdateAsync(..., clearOfflineGapAt: true)` is never passed. Once
`OfflineGapAt` is stamped by `RecordAdapterHeartbeatAsync` (`SlackSetupVerifier.cs:175`),
it persists forever. The possible-gap condition can never be dismissed.

**Fix context for the repair task:** wire `ClearOfflineGapIfSetAsync` (or
`UpdateAsync(..., clearOfflineGapAt: true)`) into the ingress acceptance path — after
`inbox.AcceptAsync` returns a non-already-existed result — in all three ingress handlers
(`HandleDmIngressAsync`, `LaunchChannelRootAsync`, `DispatchChannelFollowupAsync` in
`SlackConnectionRoutes.cs`). Add an operator-acknowledge `POST .../clear-gap` route (or
equivalent) that calls `ClearOfflineGapIfSetAsync`, plus a CLI/Web command to trigger it.

### F2 (must-fix) — possible-gap notice is not rendered on any surface

T-004 AC2 requires: "the Web Connection page, CLI, and Owner diagnostic surface show a
possible-gap notice asking the user to resend critical delegations." The spec
(`slack-offline-gap-notice/spec.md`) requires a visible notice on the Connection page, CLI,
and Owner diagnostic surface.

The server stamps `OfflineGapAt` and includes it in the C# `ConnectionDiagnosticFacts`
record (`ConnectionDiagnostic.cs:55,91`), but:

- **Web type** `ConnectionDiagnosticFacts` (`packages/web/src/entities/agent-connection/model/types.ts:11-21`)
  does **not** include `offlineGapAt`. The field is silently dropped by the JSON decoder.
- **Web page** (`ConnectionDiagnosticPage.tsx`) renders primary state, uncertain deliveries,
  and supporting facts — but **no gap notice** anywhere, and `offlineGapAt` is not in the
  facts table.
- **CLI** (`MohistCliCommands.AgentConnection.cs`) has **no gap-notice rendering** and no
  `clear-gap` / `acknowledge-gap` command.

The user is never told that messages may have been missed — the entire product value of T-004
is absent from the UI surfaces.

**Fix context:** add `offlineGapAt` to the web `ConnectionDiagnosticFacts` type; render a
non-blocking notice card on `ConnectionDiagnosticPage.tsx` (alongside the primary state)
when `facts.offlineGapAt` is set; add a gap-notice line + clear command to the CLI connection
view; expose the fact through the existing diagnostic route (already present server-side).

### F3 (must-fix) — no spec test for `OfflineGapAt` stamping

T-004 AC1 requires: "A heartbeat arriving after a gap >= SlackEventRetentionWindow stamps
OfflineGapAt; a heartbeat after a shorter gap does not (**spec test, fake time**)."

There is **no test** for `RecordAdapterHeartbeatAsync`'s gap-detection logic.
`SlackSetupVerifierSpecs.cs` tests `VerifyAsync` and `VerifyRotationAsync` but never calls
`RecordAdapterHeartbeatAsync` with a stale `LastHeartbeatAt`. A `grep` for `OfflineGapAt`
across `packages/server/tests` returns only the schema-fix DDL in `GrainTestConfig.cs:92` —
no behavioral assertion.

**Fix context:** add spec tests in `SlackSetupVerifierSpecs.cs` (or a new spec file) that
seed a connection with a `LastHeartbeatAt`, advance `FakeTimeProvider` past / within
`SlackEventRetentionWindow`, call `RecordAdapterHeartbeatAsync`, and assert
`OfflineGapAt` is set / unset. Also add a test for the clear-on-liveness path once F1 is
wired.

---

### N1 (non-blocking) — inconsistent `IsBackpressured` matchers

`SlackConnectionRoutes.IsBackpressured` (`SlackConnectionRoutes.cs:1842`) uses substring
match: `HealthReason?.Contains("backpressured")`. `ConnectionDiagnostic.IsBackpressured`
(`ConnectionDiagnostic.cs:174`) uses exact constant match via
`SlackConnectionBackpressureReasons.IsBackpressureReason` (reason `is OutboxOverflow or
InboxOverflow`). The progress note explicitly said to "use the same matcher pattern for the
diagnostic branch to stay consistent with existing ingress gate," but the implementation
diverged. No functional impact today (only two reason strings exist, both containing
"backpressured"), but the divergence could cause a future backpressure reason to be
recognized by one path and not the other.

### N2 (non-blocking) — first-overflow inbox message gets HTTP 409, not the structured response

The `catch (SlackProviderInboxCapacityExceededException ex)` blocks
(`SlackConnectionRoutes.cs:1150-1153`, `1913-1916`, `2095-2098`) still return
`ApiResults.Conflict(ex.Message, "slack_inbox_backpressured")`. Only the pre-check
`IsBackpressured(connection)` paths were converted to the structured `kind:"backpressured"`
response. The first message that triggers inbox overflow causes `AcceptAsync` to flip the
connection to Degraded(Backpressured) and throw; the route returns 409; the adapter's
`transport.ingress` throws (`transport.ts:63`); `ack()` is not called; Slack redelivers; the
second delivery hits the now-true `IsBackpressured` pre-check and gets the structured
response. The rejection IS eventually surfaced (one redelivery interval later), but the
first response is a silent HTTP 409 — precisely the "transport-level error" the proposal
says should be eliminated. The spec scenario ("sends a message to a backpressured
Connection") technically covers only the already-backpressured case, so this is an
edge-case gap rather than a spec violation.

### N3 (non-blocking) — `ResendUncertainAsync` does not scope by `connectionId`

The resend route (`SlackConnectionRoutes.cs:367`) accepts `{connectionId}` but
`SlackOutboxStore.ResendUncertainAsync` (`SlackOutboxStore.cs:400`) filters by
`projectId` + `id` only — no `connectionId` in the `Where` clause. A delivery belonging to a
different connection in the same project can be resent if its ID is known. The list endpoint
(`ListAsync`) correctly scopes by `connectionId`. Low severity (project-scoped access,
unguessable IDs, non-destructive transition), but the route URL promises connection-scoped
behavior that the store does not enforce.

## Verdict

T-001, T-002, T-003 are well-implemented with thorough spec + unit + adapter + CLI + Web
coverage; all their acceptance criteria are met. T-004 is only partially implemented: the
server stamps `OfflineGapAt` and the migration is correct, but the notice is never rendered
on any surface (F2), the flag can never be cleared (F1), and the stamping behavior has no
spec test (F3). These three findings must be fixed before merge.

<promise>FAIL</promise>
