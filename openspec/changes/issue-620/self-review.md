# Self-Review: issue-620

First review (full sweep). Artifacts reviewed: `proposal.md`, `design.md`, `tasks.json`, `specs/slack-failure-retryability/spec.md`, `specs/slack-retry-action/spec.md`, `specs/slack-retry-operation/spec.md`, `specs/slack-retry-attempt-execution/spec.md`, judged against issue #620's User Voice, Product Shape, Domain Model, Acceptance Criteria, and Non-Goals. All code claims were verified against the current codebase.

## Verdict

FAIL. Two must-fix problems, both in the failure-category plumbing that the whole feature hangs on. The durable-operation, idempotency, recovery, presentation, and interaction-route design is otherwise sound and verified against real code.

## Must-Fix Findings

### MF-1: The allowlist tokens do not match the failure-category vocabulary the system actually records

Design Decision 1 (design.md) and the `slack-failure-retryability` spec fix the retryable set as exactly: `runner-unavailable`, `runner-lost`, `report-timeout`, `deadline`, `timeout`, `probe-timeout`, `runtime-transport-unavailable`, `rate-limited`, `retry-safe`, classified by **exact ordinal comparison** on `AgentTurnResult.FailureCategory` (T-001 notes: "exact ordinal comparison ... never text heuristics").

The categories the codebase actually records are different strings:

- Server-side reconciliation writes the `AgentJobFailureReasons` constants — `runner-unavailable`, `runner-lost`, `report-timeout`, `workspace-unavailable` (`packages/server/src/Mohist.Server/Agent/Grains/IAgentJobGrain.cs:409-418`). These three **do** match the allowlist.
- Every runner-reported turn failure flows through `FailureCategoryFromErrorCode(result.ErrorCode)` verbatim (`AgentJobGrain.cs:357-359, 1553`), and the ErrorCode is the runner's `RuntimeError` kind: `deadline-exceeded`, `generation-drain-timeout`, `unavailable-runtime`, `turn-failed`, `interrupted`, `permission-required`, `invalid-input`, `missing-session`, `incompatible-runtime`, `conflict` (`packages/runner/src/runtime/opencode/errors.ts:83,112,132,143,159,174,244`; `packages/runner/src/runtime/agent-job-turn.ts:698,782`). **None** of these equal `deadline`, `timeout`, or `runtime-transport-unavailable`.
- The codebase's own readiness logic confirms the live vocabulary by normalizing `_`↔`-` and matching both `runtime-unavailable` and `unavailable-runtime` (`AgentReadinessService.cs:193-197`) — `runtime-transport-unavailable` appears nowhere in the repo.
- `probe-timeout` appears nowhere; the only occurrences of a probe-timeout category are snake_case `probe_timeout` in test fixtures (`AgentSessionRuntimeEventSpecs.cs:498`, `ContextExhaustionClassifierTests.cs:219`).
- `rate-limited`/`rate_limited` exists only as a test fixture (`AgentSessionSummaryAssemblerSpecs.cs:461`) or as unrelated HTTP 429 error codes (`AuthDeviceRoutes.cs:271`); no producer records it as a turn category.
- `retry-safe` exists only in the CLI's issue-delivery guidance vocabulary (`packages/cli/Mohist.Cli/DeliveryFailureGuidance.cs:11`) — it is never a session/turn failure category. Keeping the token is fine as a forward-looking entry, but nothing produces it today.

Net effect: with exact-match classification, only `runner-unavailable`, `runner-lost`, and `report-timeout` can ever classify as retryable. The issue's Product Shape enumerates the allowlist as "runner unavailable/lost、report timeout、deadline/timeout、probe timeout、runtime transport unavailable、rate limited 和显式 retry-safe", and Acceptance Criterion 1 requires "retryable failure 显示一个五分钟有效的 Retry 动作". A classifier that cannot recognize deadline/timeout, probe-timeout, runtime-transport, or rate-limited failures leaves the majority of the issue's named retryable classes permanently without a Retry button — the plan is wrong against AC 1 as written. T-001's unit-test matrix ("every allowlist category is retryable") tests the wrong list against itself and will not catch this.

Fix direction (for the disposal task): align the allowlist tokens with the recorded vocabulary (`deadline-exceeded`, `generation-drain-timeout`, `unavailable-runtime`, …) or add an explicit producer-side normalization task that makes the recorded categories canonical; the chosen fix must be pinned by tests that use the real recorded strings, not the allowlist's own tokens.

### MF-2: Thread follow-up failures record no failure category at all, so thread-retry presentation is unreachable in production

Design Decision 3 acknowledges "Follow-up terminal events currently carry null failure facts" and mitigates by making `AgentSessionGrain.TryEmitFollowupDeliveryAsync` populate `failureReason`/`failureCategory` from `turn.Result` (T-003). But the root cause is one layer deeper, and the mitigation does not reach it:

- A follow-up turn's `Result.FailureCategory` comes verbatim from the terminal `session.activity` runtime-event payload (`AgentSessionGrain.cs:2672-2675`, `ResolveFollowupTurnResult`). Today that payload carries only `failureReason` (the masked error message) — the runner's follow-up terminal event never sets `failureCategory` for ordinary failures (`packages/runner/src/server/followup-handler.ts:643-653`, `recordFollowupActivity`; the only category ever set is `unknown` for expired manager credentials).
- Follow-up turns do not go through `AgentJobGrain` (the follow-up dispatch path selects turns *without* a JobId, `AgentSessionGrain.FollowupDispatch.cs:15-16`), so the server-side `AgentJobFailureReasons` categories never apply to them either.
- The Non-Goal "不根据错误文本猜测 retryability" forbids inferring a category from `failureReason`.

So even after T-003, `turn.Result.FailureCategory` for a failed thread follow-up is null in production → "absent facts degrade to no button" → **no thread failure can ever display a Retry button**. This violates AC 1 for thread turns and leaves AC 6's entire thread-retry deliverable ("thread retry 在原 Session 中创建并 dispatch 指定的新 follow-up") with no production entry point — the T-002 machinery would only be reachable through tests that inject categories by hand. The task list contains no `packages/runner` change (the only adapter/runner task, T-006, is test-only).

Fix direction: add an explicit task (runner-side) to report the failure category/error kind on terminal follow-up runtime events, or an equivalent authoritative fact source on the Server, so that transient thread failures actually carry a retryable category; the "absent ⇒ no button" degradation may remain as the safety net, not as the permanent behavior.

## Review Dimensions

### Issue Basis

Checked. The issue was re-read in full before the artifacts: one-click retry of a transiently-failed Slack Agent execution with signed action, revalidation at click, durable at-most-once operation, root retry → new Session / thread retry → targeted follow-up, category allowlist from recorded facts only, and the multi-Bot exclusion (#634). The review below judges the artifacts against those goals, not the plan's own framing.

### Coverage

Checked, with the two must-fix findings above. AC-by-AC mapping: AC1 → T-003 (broken by MF-1/MF-2 as described); AC2 → T-004 + T-001/T-002 (failed-Turn immutability explicitly tested); AC3 → T-004 covers all seven rejection classes (expiry, tamper, context, actor, permission/policy, disabled, no-longer-retryable) with no-resource-created assertions; AC4 → T-001 store unique indexes + T-004 replay specs; AC5 → T-005 recovery worker without click lease; AC6 → T-001 root path + T-002 targeted dispatch; AC7 → presentation/Stop-compat/allowlist/cleanup determinism spread across T-003/T-004/T-006/T-005 with injectable `TimeProvider`. Every AC has a task and testable acceptance criteria; the gaps are the category-plumbing ones in MF-1/MF-2, not missing work items.

### Correctness

Checked, with the must-fix findings above; the rest of the approach holds up adversarially:

- Persist-before-dispatch with claim-or-create on two unique indexes (idempotency key + `(session, turn)`) correctly yields at-most-once across concurrent clicks, fresh-nonce re-renders, redelivery, and restart; the `(session, turn)` index is what makes a later differently-nonced button resolve to the one recorded operation.
- Root-retry key override is real and necessary: `LaunchConnectionAsync` hardcodes `slack:{team}:{conversation}:{messageTs}` (`AgentLauncher.cs:451`), which would resolve back to the failed Session; `agent-retry:{operationId}` plus pre-minted ids mints a genuinely new Session, and coordinator replay makes worker re-dispatch idempotent.
- Thread retry via `AcceptFollowupAsync` (idempotency key + `PreMintedInputId`/`PreMintedTurnId`, verified present in `AgentSessionGrain.cs:575-655`) plus a targeted `BeginFollowupDispatchForTurnAsync` sharing `BeginNextFollowupDispatchAsync`'s selection body preserves the executing/JobId guards and never promotes unrelated queued turns — consistent with the grain's actual invariants (`AgentSessionGrain.FollowupDispatch.cs:13-16`).
- One nuance recorded as Observation O4: a concurrent click that loses the claim while the winner is still Pending reads a Pending record, so "both report the same recorded result" can transiently mean accepted-pending vs. finished; the acceptance feedback should be pinned as "accepted" in both cases.

### Consistency with Current Codebase

Checked, no issue beyond MF-1/MF-2. Verified against real code: the Stop action pattern (`SlackTurnControlService` — HMAC-SHA256 over a canonical payload keyed by the bot token, `FixedTimeEquals`, five-minute lifetime, actor/initiator binding, provider-inbox dedup), `SlackInteractionRoutes` envelope and `ActionDispatchRef` hash identity, `SlackStatusProjection.EnqueueTerminalAsync` supporting blocks and in-place progress promotion, `SlackTerminalDelivery` genuinely lacking `sessionId`/`turnId` today, `TryEmitFollowupDeliveryAsync` genuinely nulling failure facts, `SlackAgentAppBindingObligationWorker` + `MohistServiceRegistration` hosting pattern, EF migration infrastructure under `Infrastructure/Data`, `SlackConnectionAccessDecider` as the ingress policy read the design reuses, and the Slack adapters' `block_actions` normalization being generic over action ids (TS `adapter-events.ts:56-83`; Go `serverapi.go` forwards `actionId` generically) so T-006's test-only scope is correct. All six tasks' spec anchors resolve to real headings in the four spec files.

### Task Breakdown

Checked, no issue. T-001 (service core + store + root path) before T-002 (thread path) and T-003 (presentation); T-004 (route) correctly waits on T-001–T-003; T-005 (worker) needs only T-001/T-002; T-006 (adapter tests) needs only T-003's action id. Ordering, dependencies, priorities, and AFK/test modes are coherent; every task has verifiable acceptance criteria naming concrete specs. One bookkeeping defect recorded as Observation O1.

## Observations (do not affect the verdict)

- **O1 — tasks.json schema deviation:** only T-001 carries `"passes": false`; T-002–T-006 omit the field, unlike every other change's `tasks.json` in this repo (issue-505/560/589/627/631 all set it on every task). If the workflow tooling reads `passes` per task, the missing keys may break stage processing; add the field for consistency.
- **O2 — attachments/startup context dropped on retry:** `AgentSessionInputRecord` durably records `Attachments` and `StartupContext` (`AgentSession.cs:592-612`) and Slack ingress binds file attachments (`SlackAttachmentInputBinder`), but Decision 7 / T-001 / T-002 rebuild only prompt text + provenance. A retry of an attachment-bearing request silently executes a different request. The issue does not call attachments out explicitly, so this is recorded as a fidelity gap to close during implementation or a deliberate documented exclusion.
- **O3 — execution definition on retry:** the spec says the new attempt is "built from ... the recorded execution facts of the original launch", but the design/tasks never pin whether model/variant/runtime come from the recorded original or the Agent's *current* definition (`ResolveExecutionDefinition` re-resolves at launch). Ambiguity worth resolving before build.
- **O4 — pending-vs-finished result wording:** see Correctness nuance; pin in T-001/T-004 tests that a loser click during the winner's Pending window reports an accepted/pending result, and that later redelivery reports the finished one, so "same recorded result" has one testable meaning.
- **O5 — open questions:** Manager DM boundary, 24 h retention window, and Owner-as-alternate-operator binding are open questions in the design; none block this issue's ACs, but they should be answered before CLI/Web retry routes land.
- **O6 — thread mapping after root retry:** the failed Session keeps its thread bindings by design (non-goal), so subsequent thread replies keep routing to the failed Session while the retried Session runs elsewhere; acknowledged in the design, flagged as a product/UX consequence to watch.
- **O7 — transient accept failures:** `AcceptFollowupAsync` can throw (`FollowupOperationInProgressException`, activity-unknown, 16-queued-turn capacity). The Pending operation + worker retry makes these recoverable, but no acceptance criterion covers them; add a spec test for at least the capacity and in-progress cases.
- **O8 — pre-T-004 click safety verified:** today the route funnels every action id to `SlackTurnControlService.HandleAsync`, which rejects non-Stop ids with `unsupported_action`; T-003's note that rendered buttons are harmlessly rejected before T-004 lands is accurate.

<promise>FAIL</promise>
