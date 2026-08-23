# Self-Review — Issue 634 plan (openspec/changes/issue-634)

Reviewer: pi (first review, full sweep). Reviewed against issue #634
(`mo issue view 634`, project `proj_f6c141d63b6243bfbb481737b2243b87`) and the
current codebase at `master`.

Artifacts reviewed: `proposal.md`, `design.md`, `tasks.json`,
`specs/slack-agent-selection-prompt/spec.md`,
`specs/slack-agent-selection-action/spec.md`,
`specs/slack-selection-execution-attribution/spec.md`.

Codebase claims were verified, not taken on faith: `SlackConnectionRoutes.ChannelIngress.cs`
(`HandleAmbiguousPromptAsync`, `HandleAmbiguousNonOwnerAsync`,
`LaunchChannelRootAsync`, `MentionedWorkspaceBots`), `SlackAmbiguousPromptStore`
/`SlackAmbiguousPromptRow` (once-only claim, `PromptDispatchRef`),
`SlackRetryActionService` / `SlackTurnControlService` (5-minute signed-action
lifetime, canonical form), `SlackInteractionRoutes` (operator auth, lease
validation, `ActionDispatchRef` reply coalescing), `SlackConnectionAccessDecider`
+ `SlackLeaseContext` + `SlackAdapterLeaseService` (per-target lease
semantics), `SlackOutboxStore` (unique `(OwnerKind, ConnectionId, DispatchRef,
Kind)` index; `MarkDeliveredAsync` persists `ProviderMessageIdentity`),
`PreMintSlackLaunchIds`, `RouteFollowupAsync`,
`SlackAgentAppBindingObligationWorker`, `SlackAdmissionService`, the Go
adapter's `NormalizeSlackInteraction` (`actions[0].value`, container
`message_ts`, lease/adapter ids on interactions), `SlackProviderOptions.SlackEventRetentionWindow`
(30 min), and the existing spec suites named in T-001 (all exist). The
design's factual grounding in the codebase is otherwise accurate and unusually
thorough.

## Verdict: FAIL

Three must-fix problems. Everything else checked out; observations below do
not affect the verdict.

## Must-fix findings

### MF-1 — The ">5 candidates" acceptance criterion is unaddressed, and the design contradicts it

Issue, Acceptance Criteria #1:

> 两到五个 eligible Bot 被同一消息提及时，Slack 显示签名选择控件和可读文本；
> **超过五个候选时不截断或自动选择，而是要求明确重新提及一个 Bot**

Issue, Product Shape:

> Server 发布一个签名交互选择，**最多展示五个候选；候选超过五个**或 Slack
> interaction 不可用时，显示可读文本回退并要求用户重新明确提及一个 Bot。

The plan renders one button per candidate with **no cap**:

- design.md Decision 2: "Render one button per candidate Bot … `SlackMultiAgentRoutingPolicy`
  is unchanged; only the `Prompt` disposition's side effect changes from text
  to blocks."
- design.md Risks: "[Large candidate sets render many buttons] -> Capped by
  the actions-block element limit" — i.e., buttons up to 25 elements, which is
  precisely the truncation-free interactive control the criterion forbids for
  >5.
- None of the three spec deltas contains a candidate-count requirement or a
  >5 text-fallback requirement; `slack-agent-selection-prompt`'s "Chooser
  candidates are exactly the mentioned workspace Bots" actively implies all
  candidates render as choices.
- No task in `tasks.json` mentions the cap or the fallback (T-002 bakes in
  "one per mentioned workspace Bot").

`MentionedWorkspaceBots` (ChannelIngress.cs) has no upper bound, so >5
eligible mentions is reachable, and a builder following the plan ships buttons
for 6–25 candidates — a direct violation of AC #1's second half and the
Product Shape's "最多展示五个候选" rule. The plan needs the boundary: 2–5
candidates → signed chooser (+ readable text); >5 → readable text fallback
requiring an explicit single-Bot re-mention, no interactive control, no
truncation, no auto-selection. The block-level fallback text should carry the
same re-mention instruction so the "Slack interaction 不可用" leg of the
Product Shape sentence is also covered.

### MF-2 — The selected Connection's current runtime lease is never resolved; the plan codifies reusing the prompt-owner's lease, which the codebase's lease semantics make incorrect

Issue, Acceptance Criteria #3 and #4:

> selected Connection 使用**自己的当前 lease** 和 access policy 完成 owner、
> allowlist、live-member 与 channel membership 检查，**prompt-owner lease 不被复用**
>
> selected Connection 缺失或 **lease 失效返回 unavailable**

Issue, Product Shape:

> 并分别使用 prompt-owner Connection 与 selected Connection **各自当前的
> runtime lease**、access policy、allowlist、owner/live-member/channel
> membership 重新授权。**prompt-owner 的 lease 不得用于 selected Connection。**

The plan does the opposite in three places:

- design.md Decision 4, step 7: "`SlackConnectionAccessDecider.EvaluateAsync`
  under the **chosen** Connection's current policy **with the interaction
  lease context, exactly as Retry does**."
- design.md Decision 8: the selection action id is dispatched "with lease
  context — the access re-evaluation needs it" (the route-bound context of the
  delivering adapter).
- specs/slack-agent-selection-action: "The evaluation SHALL include **the
  lease context of the delivering adapter**."

Why this is wrong in the codebase, not just in wording: the interaction
arrives over the **posting** (prompt-owner) Connection's socket, and the route
validates that Connection's lease. Leases are strictly per-target —
`SlackLeaseTargetRef.Connection.TargetKey` is
`connection:{ProjectId}:{ConnectionId}` and
`SlackAdapterLeaseService.ValidateRuntimeLeaseAsync` compares the presented
`leaseId` against the **active lease of that target key**. Retry gets away
with "the interaction's lease context" only because its evaluated Connection
*is* the delivering Connection. For a chooser, picking any candidate other
than the posting Connection is the **mainline case** (the chooser exists
precisely because several Bots were mentioned). Passing the interaction's
lease context while evaluating the chosen Connection makes the decider's
`ResolveVerifiedBotToken` resolve the chosen Connection's target with the
prompt-owner's `LeaseId`/`AdapterId` → validation fails → bot token null →
deny with `VerificationFailedReason`. Under `allowlist`/`anyone` policies
(non-owner clicker), **every cross-Connection selection click would be
wrongfully rejected as unauthorized**.

Additionally, nowhere in design/specs/tasks is there a check that the chosen
Connection currently holds a valid runtime lease, nor an `unavailable` outcome
for a missing/stale one (the taxonomy covers vanished → `no_longer_valid`,
disabled → `connection_disabled`, agent-not-ready → setup nudge — nothing for
"lease 失效"), so AC #4's first clause has no implementation path. AC #9's
"跨 Connection 授权拒绝…均有确定性验证" also has no corresponding spec
scenario, and as specified it would test the wrong mechanism.

The fix direction (for the disposal task): at click time, resolve the chosen
Connection's **own current** runtime lease (e.g., via the lease store's active
lease for its target) and build the re-authorization context from it; absent
or expired → distinct visible `unavailable` outcome with no resources; never
derive it from the interaction's delivering lease. Update design Decision 4/8,
the `slack-agent-selection-action` requirement text, T-003, and add the
cross-Connection authorization-rejection spec scenario.

### MF-3 — Action validity (24 h) and retention (7 d + 24 h grace) contradict the issue's pinned parameters

Issue, Product Shape:

> **动作沿用现有五分钟有效期。**
>
> 过期 prompt/operation **只保留到现有 Slack redelivery 与
> delivery-reconciliation retention window，之后按现有清理策略删除，不新增
> 长期审计存档**。

The plan deviates on both parameters, deliberately and without sanction from
the issue:

- design.md Decision 3: "unlike Stop/Retry's 5 minutes, a chooser must survive
  a user reading the thread later; **default 24 hours**". T-002 bakes "24h
  expiry" into the task description and acceptance pipeline; T-003 repeats it.
- design.md Decision 7 / T-004: a **new** retention regime — finished records
  reaped after **7 days**, pending rows settled after expiry **+ 24 h grace**.
  The existing window the issue points at is
  `SlackProviderOptions.SlackEventRetentionWindow` = **30 minutes** (and the
  current `SlackAmbiguousPromptRow` docs contemplate reaping "older than the
  Slack redelivery window"). 7 days is a new, materially longer operational
  retention than "现有清理策略/窗口".
- The design's Open Questions defer these bounds to "confirm against expected
  Slack delayed-events and interaction-redelivery windows" at implementation
  time — i.e., the contradiction is never reconciled with the issue, and a
  builder executing the tasks as written ships 24 h/7 d behavior that violates
  the stated product shape.

The engineering rationale (users read threads later) may be sound, but the
issue text is unambiguous ("沿用现有五分钟有效期"); conforming — or getting
the issue amended first — is required. Note the deviations compound: with the
issue's 5-minute validity, retention naturally aligns with the existing
window; the 7 d/24 h scheme exists only to support the unsanctioned 24 h
expiry.

## Dimension verdicts (first review, full sweep)

- **Coverage** — FAIL. AC #1's >5-candidate leg unaddressed (MF-1); AC #3's
  own-lease requirement and AC #4's lease-invalid→`unavailable` leg unaddressed
  (MF-2); Product Shape validity/retention parameters contradicted (MF-3).
  All other goals and criteria are covered: single chooser claim across
  fan-out/redelivery/failover (Decision 1 + existing claim store), durable
  fact snapshot at first admission (non-nullable columns on the claim), signed
  payload with Stop/Retry signing material, actor binding to the original
  sender, distinct visible rejections creating no resources, CAS single-winner
  arbitration, pre-allocated execution identity persisted before dispatch,
  restart recovery via obligation worker, root/thread routing by original
  provenance, bounded cleanup, non-goals respected (no Retry, no auto-select,
  no pagination, no modal/slash/shortcut, no long-term archive).
- **Correctness** — FAIL. The acceptance pipeline's lease-context reuse is
  incorrect against per-target lease semantics for the mainline cross-Connection
  case (MF-2). Otherwise the mechanisms check out against the codebase: the
  CAS fence key `(WorkspaceTeamId, ConversationId, MessageTs)` is
  DB-unique; the chooser-message-identity check via the acked outbox
  `ProviderMessageIdentity` is real (`MarkDeliveredAsync` persists it) and
  fails closed pre-ack; dispatch idempotency holds (thread launch reservation
  + inbox route + `PreMintSlackLaunchIds` determinism over the original
  message identity; `RouteFollowupAsync` idempotent by message identity);
  reply coalescing holds (`ActionDispatchRef` + unique dispatch-ref index);
  the adapter genuinely needs no contract change for `block_actions` values
  and container `message_ts`.
- **Consistency with codebase** — checked, one issue (counted under MF-2):
  every named file/service/pattern exists as described (verified list above);
  the one inconsistency is that "exactly as Retry does" lease-context reuse
  does not transfer to a cross-Connection evaluation. Conventions (outbox
  UserAction deliveries, spec-test suites, obligation-worker shape, EF
  additive migration) are followed.
- **Task breakdown** — checked, no structural issue beyond the must-fixes.
  T-001→T-002→T-003→T-004 ordering is sound (behavior-preserving extraction
  guarded by existing suites first), each task is independently verifiable
  with concrete outputs, and spec anchors resolve to real requirements. The
  gaps are omissions inside otherwise well-formed tasks: no task covers the
  >5 fallback (MF-1) or selected-Connection lease resolution (MF-2), and
  T-002/T-004 hard-code the deviating 24 h/7 d bounds (MF-3).

## Observations (do not affect the verdict)

1. **Outcome taxonomy vs the issue's domain model.** The issue names
   `unavailable` / `unauthorized` / `stale`; the plan uses `connection_disabled`,
   `no_longer_valid`, `stale_action`, `expired`, `invalid_action`,
   `unauthorized`. The visible user-facing states would be easier to verify
   against AC #4 if mapped explicitly (a deleted chosen Connection returns
   `no_longer_valid` where AC #4's wording suggests `unavailable`). Beyond the
   lease leg (MF-2), this is naming, not behavior.
2. **Spec/design inconsistency on the signed payload's bound message
   identity.** `slack-agent-selection-action` says the payload binds "the
   workspace, conversation, and **message identity of the chooser**", but
   design Decision 3 signs the **original** message identity and enforces the
   chooser-message binding durably via the acked outbox row (with a reasoned
   rejection of sign-at-delivery-time). The design's mechanism is the
   implementable one — Slack assigns the chooser ts only at delivery — and the
   scenario outcomes match; the spec requirement text should be reworded to
   match so a literal implementation isn't asked to do the impossible.
3. **Concurrency scenario framing.** "Two users click different candidates at
   the same time" reduces to one authorized click plus one `unauthorized`
   rejection, because actor binding admits only the original sender; the CAS
   fence's real concurrency load is same-actor double-clicks, interaction
   redelivery, and failover replays. Harmless, but the scenario could say so.
4. **Pre-migration rows.** Non-nullable fact columns need defaults for
   existing `SlackAmbiguousPrompts` rows; the design's "old claims can never
   start an execution (enforced structurally)" relies on empty-sentinel
   semantics that the implementation must actually enforce (and old `Pending`
   rows must become sweepable). T-002's "no backfill" criterion covers the
   migration itself; watch the sentinel check.
5. **Open questions are genuinely open.** Button label source and the
   decision-view privacy note are appropriately deferred and do not affect any
   acceptance criterion.
6. **Rollback note.** Choosers rendered before a rollback leave buttons whose
   clicks 404 on the old route; the design accepts this within the rollback
   window with the re-mention fallback — reasonable, and consistent with the
   Product Shape's text-fallback posture.

<promise>FAIL</promise>
