# Self-Review — issue-558 plan artifacts

- **Issue**: #558 “Agent 历史与 Session 时间线：解释执行结果” (proj_f6c141d63b6243bfbb481737b2243b87)
- **Artifacts reviewed**: `proposal.md`, `design.md`, `tasks.json`, `specs/agent-history-results/spec.md`, `specs/session-result-presentation/spec.md`
- **Round**: first review — full sweep (coverage, correctness, codebase consistency, task breakdown). All codebase claims in the artifacts were verified against `master` at `33dda0f27`.
- **Verdict**: **FAIL** — two must-fix findings (MF-1, MF-2 below).

---

## Must-fix findings

### MF-1 — Acceptance criterion #2 is not covered: 成本 (cost) and 耗时 (duration) are absent from the plan entirely

**Violated criterion (issue AC #2)**: “历史记录展示状态、结果摘要、开始和结束时间、耗时、模型和成本。” — reinforced by the User Voice: “我需要从 Agent 历史中快速判断每次任务做了什么、结果如何、**花了多少时间和成本**”.

A grep across all five artifacts for `cost|usage|token|成本|耗时|duration|elapsed` returns **zero** matches. The plan's history-row contract (design D5, T-002 acceptance criteria) lists exactly: subject excerpt, origin, context references, model, created + last-activity timestamps, Activity badge, launch-result chip, latest-outcome chip. Status ✓, result summary ✓, model ✓, start/end ~ (created + last-activity as a proxy), but:

- **成本**: not presented anywhere, not even declared a non-goal. (The issue's non-goal is cost *budget policy* — “不决定 Agent 并发或成本预算策略” — not cost display.)
- **耗时**: not displayed. Two timestamps are shown, from which a user could mentally derive a duration, but the criterion enumerates 耗时 as a presented fact; and for the *first execution* (the launch Job) the two row timestamps are session-level, not Job-level.
- **结束时间**: last-activity is a proxy; whether an explicit ended-at fact should surface is undecided.

This is a pure coverage miss, not a technical constraint: `AgentSessionStatusSnapshot.UsageSummary` (`CostAmount`/`CostCurrency`, `AgentSession.cs:392-402`) and the duration anchors (`CreatedAt`, `LastDataAt`, `CurrentTurnEndedAt`, `IdleSince`) sit on the **already-deserialized `AgentSession` record the querier holds per row** — derivable at the read boundary with zero additional queries, exactly like the subject excerpt the plan does specify (design D1). The plan must either add cost/duration facts to the list read model and row presentation, or explicitly de-scope them with a justification the issue supports — currently it does neither, so the plan is incomplete against AC #2.

### MF-2 — D3's factual premise about `JobId` on agent-connection turns is false; T-001's launch-envelope acceptance criteria for agent-connection sessions are unsatisfiable as written

**Violated criteria**: AC #1 (“每条记录能区分任务、结果和上下文” — for Slack-origin rows, the first-execution result handling is mis-specified) and the issue's domain model (“投影只呈现权威生命周期事实，不自行推断或改写” — the plan demands suppressing or mis-expecting a recorded authoritative fact for a whole session class).

Design D3 states: “`AgentTurnRecord.JobId` is stamped only on launch-created turns …; follow-up and **agent-connection Turns never carry one**.” T-001 requires: “agent-connection / 无 Turn / 非 launch 会话不产出 Launch 信封（不编造）”. The spec scenario “Non-launch sessions do not fabricate a launch result” rests on the same premise.

The codebase says otherwise — **every agent-connection session is created by the launch coordinator with a real AgentJob, and its first Turn carries that JobId**:

- `AgentLaunchCoordinatorGrain.cs:505-540` — for `plan.ConnectionOrigin != null` (Slack connection sessions) the coordinator stamps `SourceKind = "agent-connection"` and calls `sessionGrain.EnsureInitialLaunchAsync(..., JobId: plan.JobKey, ...)` where `JobKey = "agent-job-launch-{guid}"` (a real `IAgentJobGrain`, `SubmitJob` path).
- `AgentSessionGrain.cs:3082-3135` — `EnsureInitialLaunchAsync` **throws if `JobId` is missing** and records the first turn via `EnsureInitialLaunch(jobId: ...)`; `AgentSession.Transitions.cs:424-503` stamps `JobId: jobId` on that turn. Only follow-up turns get `JobId: null` (`Transitions.cs:519,604,612`). The launch coordinator is the only stamper of the `agent-connection` source-kind label (grep across `packages/server/src`).

Consequences:

1. **T-001 is internally contradictory**: the specified derivation rule (“launch 信封由最低序列带 JobId 的 Turn 提供”) *will* produce a Launch envelope for agent-connection sessions, while the same task's acceptance criterion demands the opposite (“agent-connection … 不产出 Launch 信封”) and instructs SpecTests to verify absence for agent-connection. A builder cannot satisfy both; the demanded test encodes a false expectation.
2. **Surface divergence risk**: T-003's client-side rule (“取首个携带 jobId 的 Turn 的终态结果作为 launch 结果”) is a pure function over `summary.turns`, which carry no source-kind — the Session page will present a launch result for connection sessions no matter what the server does, so a server-side suppression special case would make history rows and the session header disagree for the same session.

Fix direction: correct the premise. Connection sessions' first executions *are* AgentJob launches with recorded results; the honest projection presents their launch result on both surfaces. Update D3's parenthetical, T-001's criterion and SpecTests expectations (agent-connection sessions *do* produce a Launch envelope — `ListUnifiedSessionsByAgentAsync` includes them), T-003, and the spec's non-fabrication scenario (premise “was not created by an AgentJob launch” is false for connection sessions). Alternatively define a source-kind gate applied consistently on both surfaces — but that suppresses a recorded authoritative fact and would need explicit justification against the issue's domain model.

---

## Dimension verdicts (first review, full sweep)

### Coverage — FAIL

- AC #1 (no duplicate records; task/result/context distinguishable): covered — T-002 removes the “Recent” duplicate slice, subject + refs + dual outcome chips. Checked.
- AC #2 (status, result summary, start/end, duration, model, cost): **MF-1** — cost and duration uncovered.
- AC #3 (timeline distinguishes input/reply/key actions/errors/Compact/Reset/unknown): covered — existing classes preserved; outcome entries add terminal-result distinction; nothing in the plan regresses the boundary/error/status classes. Checked, no issue.
- AC #4 (collapse low-value; never hide failures/key actions): covered — D8 keeps grouping restricted to `file-read`/`shell`/`tool` terminal items (verified `group.ts:3-6`), failed stays `error`/`critical`, outcome items never groupable. Checked, no issue.
- AC #5 (history/Session/export keep same execution context): all surfaces derive from the same recorded Turn facts by construction; no export surface exists today. Not explicitly acknowledged — observation O-3.
- AC #6 (history → Session navigation; refresh continuity): capability preserved implicitly (rows are links today; all new derivations are server-backed pure projections) but never pinned — observation O-2.
- **MF-2** additionally means a documented session class (agent-connection) has mis-specified result coverage.

### Correctness — FAIL (D3 premise; otherwise sound)

- D1 (derive from already-loaded status snapshot): verified — `AgentSessionQuerier.ListAgentSessionsAsync` / `ListUnifiedSessionsByAgentAsync` / `ListUnifiedSessionsByWorkspaceAsync` all hold the deserialized `AgentSession` per row (`AgentSessionQuerier.cs:109-262`); zero-extra-query claim holds.
- D2 (shared outcome envelope, optional appended DTO fields): verified — `AgentSessionListItemDto` (`AgentSessionReadModels.cs:212`) and `UnifiedSessionListItemDto` (`:464`) already use the `Origin`/`TargetId` optional-append precedent; `AgentTurnResultObservationDto` (`:325`) has the exact result shape; `AgentTurnStatus` enum (`AgentSession.cs:685-691`) matches the completed/failed/cancelled → unresolved mapping.
- D3: **false for agent-connection** (MF-2). The positional-by-JobId rule itself is sound; only the premise about which sessions carry JobId-bearing turns is wrong.
- D4/D5 (grouping by latest outcome, task-bearing rows): current behavior exactly as described in the Context section (verified `AgentDetailPage.tsx:355-377, 520-529`; row primary label is the redundant `agentName`, `:304-307`).
- D6/D7 (client-side header derivation; `jobId` on turn observation): verified — `UnifiedSessionSummaryDto.turns` reaches the page (`useUnifiedSessionDataSource.tsx:75`); the mapper does not map `JobId` today (`AgentSessionObservationMapper.cs:37-50`); the `?jobId=` fresh-launch flow exists (`useUnifiedSessionDataSource.tsx:97-107`). Selection-rule ambiguity — O-1.
- D8 (outcome entries): verified — `turnStateFacts` currently maps `completed` → muted `status` (`timeline-facts.ts:381-384`), failed/cancelled → `error` (`:374-379`); `salienceFor` maps `status` → `quiet` (`derive.ts:198-201`); the `<details>` pattern exists (`TimelineItemRow.tsx`). Sound.
- D9 (docs): both target documents and their gap statements exist (see O-4/O-5 for verification fragility).

### Consistency with the current codebase — one factual error (MF-2), otherwise verified

Every file path named in the artifacts exists (`AgentSessionReadModels.cs`, `AgentSessionQuerier.cs`, `AgentSessionObservationMapper.cs`, `AgentDetailPage.tsx`, `SessionDetailShell.tsx`, `entities/session/model/timeline`, `widgets/session-transcript/model/timeline-facts.ts`, `TimelineItemRow`, `docs/web-ui.md`, `design/session-timeline.md`). `CliFieldContractTests` covers both list DTOs (`CliFieldContractTests.cs:77,103`) and SpecTests infrastructure for the querier exists. Current-behavior claims in the Why/Context sections are accurate. Minor rationale nits — O-6.

### Task breakdown — sound structure, two criteria flawed

- Ordering/dependencies correct: server-first additive fields (T-001) → web history (T-002) ∥ session header (T-003) → timeline entries (T-004, shares T-003's helper) → docs (T-005, gated on all web tasks). Migration/rollback reasoning holds for a read-only change.
- Every spec requirement heading is anchored by ≥1 task; all anchors resolve to real headings in the spec files. Checked.
- Flawed criteria: T-001's agent-connection expectation (MF-2); T-005's grep verification is line-wrap fragile (O-4).

---

## Observations (do not affect the verdict)

1. **Mixed-state selection ambiguity (server D2 vs web D6/T-003).** The spec derives from “the session's **latest AgentTurn**” (latest by sequence; non-terminal → unresolved per the scenario enumeration), while D6/T-003 select “the highest-sequence Turn **with a terminal status**” (an earlier completed Turn wins while a follow-up executes). For a session with Turn1=completed, Turn2=executing, history could say `unresolved` while the Session header says `completed`. Both are honest, but the two surfaces should pin one selection rule; recommend “highest-sequence terminal Turn, else unresolved” on both sides, mirrored in T-001/T-003 tests.
2. **AC #6 not pinned.** No scenario requires the rewritten history row to remain a link to `/sessions/{id}`. Current rows are links (`AgentDetailPage.tsx:296-313`) and T-002 rewrites the row component wholesale; an explicit “row links to the session page” scenario would cheaply prevent regression of AC #6 during the rewrite.
3. **AC #5's “导出结果” is never mentioned.** No export surface exists in the Web today and the external `agent-api.md` projection is an explicit non-goal, so consistency holds by construction — but one sentence in the design acknowledging the criterion (all three surfaces project the same Turn facts) would close the loop.
4. **T-005's grep check is fragile.** The gap sentence in `docs/web-ui.md:131-132` is wrapped across lines (“AgentJob has\nno result view separate…”), so grepping the full sentence already returns empty today. Verify with a stable single-line fragment (e.g. `no result view separate`).
5. **Doc staleness beyond scope.** The `docs/web-ui.md` AgentSession gap footnote (`:200-206`) and `design/session-timeline.md`'s Status section both claim “no raw event view / turn state not in timeline”, which is already false. T-005 rewrites adjacent text; implementer should avoid leaving new contradictions, but full doc repair is out of scope.
6. **Minor rationale inaccuracies in design.md.** (a) D1 calls the agent-scoped session list “5s-polled”; `agentSessionsQueryOptions` has no `refetchInterval` today (other agent queries do) — the N+1 rejection still stands. (b) D2 cites the “cancelled alias” note as precedent; that normalization concerns runner-protocol *Activity* vocabulary, not Turn status — harmless as phrasing, but worth tightening when MF-2's edits land.
7. **End-time fact choice** (created + last-activity vs an explicit ended-at) should be decided together with MF-1 so AC #2's “开始和结束时间” is satisfied by an explicit mapping, not an implicit proxy.

---

## What a fix must touch

- MF-1: proposal (What Changes), design D2/D5, T-001 (list read model carries cost/duration facts) + T-002 (row presentation + tests); or an explicit, justified de-scope.
- MF-2: design D3 premise; T-001 criterion + SpecTests expectations; T-003 description; `session-result-presentation` spec “Non-launch sessions do not fabricate a launch result” scenario premise; `agent-history-results` D3-adjacent wording.

<promise>FAIL</promise>
