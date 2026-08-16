# Self-Review — issue-558 plan artifacts

- **Issue**: #558 “Agent 历史与 Session 时间线：解释执行结果” (proj_f6c141d63b6243bfbb481737b2243b87)
- **Artifacts reviewed**: `proposal.md`, `design.md`, `tasks.json`, `specs/agent-history-results/spec.md`, `specs/session-result-presentation/spec.md`
- **Round**: re-review — verified dispositions of round-1 findings (MF-1, MF-2), regression check on the edits, and spot re-verification of every codebase claim the fixed sections rely on (all confirmed against `master` at `2681298d7`, the disposal commit).
- **Verdict**: **PASS** — both round-1 must-fix findings are properly fixed; no regression from the fixes meets the must-fix bar.

---

## Dispositions of round-1 must-fix findings

### MF-1 — Cost and duration uncovered (violated AC #2: 状态、结果摘要、开始和结束时间、耗时、模型和成本) — FIXED

The disposal commit adds cost and duration end-to-end, and every layer now agrees:

- **Read model (design D2, T-001)**: `Cost` (`CostAmount`/`CostCurrency` from `Status.UsageSummary`, absent when unrecorded — verified fields exist, `AgentSession.cs:384-392`) and `EndedAt` (latest non-null anchor among `LastDataAt`/`CurrentTurnEndedAt`/`IdleSince`/`BoundAt` — all four verified on `AgentSessionStatusSnapshot`, `AgentSession.cs:333-378`); start stays `CreatedAt`. `AgentSessionTurnOutcomeDto` carries the Turn's own `RecordedAt`/`UpdatedAt` (verified on `AgentTurnRecord`, `AgentSession.cs:621-622`), making the first execution's duration Turn-level — this also resolves round-1 observation O-7 (explicit end-time fact choice).
- **Row presentation (design D5, T-002)**: start/end/elapsed trailing signals; active sessions show elapsed-so-far with no fabricated end; cost omitted rather than zero — honest-handling criteria are explicit in both the task and the spec.
- **Spec**: new requirement “History rows surface duration and cost” (ended/active/cost scenarios) plus read-model scenarios “Session cost is carried when recorded” and “End time derives from recorded lifecycle anchors”.
- **AC #2 element map is now complete**: 状态 → Activity badge; 结果摘要 → outcome chips; 开始/结束 → `createdAt` + `endedAt`; 耗时 → elapsed / elapsed-so-far; 模型 → secondary line; 成本 → cost signal. The issue's non-goal is cost *budget policy* (which the plan still doesn't decide), not cost display — no scope creep.

### MF-2 — False premise that agent-connection Turns never carry `JobId` (violated AC #1 coverage for Slack-origin rows and the domain-model rule against suppressing authoritative facts) — FIXED

The premise was corrected, not patched around, and the correction is codebase-accurate (re-verified):

- `AgentLaunchCoordinatorGrain.cs:501-537`: for `plan.ConnectionOrigin != null` the coordinator stamps `Source = "agent-connection"` and passes `JobId: plan.JobKey` into `EnsureInitialLaunchAsync`.
- `AgentSessionGrain.cs:3082-3094`: `EnsureInitialLaunchAsync` **throws** on a missing JobId — every coordinator-created session (both classes) carries one on its first Turn; `AgentSession.Transitions.cs:475,501` stamps it.
- **Design D3** now states both launch classes (`agent-launch` and `agent-connection`) get a fact-based Launch envelope and no recorded fact is suppressed; the rule is positional-by-JobId, applied identically on both surfaces.
- **T-001**: the internal contradiction is gone — the derivation rule and the acceptance criterion now agree (agent-connection sessions *do* produce a Launch envelope); SpecTests expectations updated to test presence for both classes and absence only when no Turn carries a JobId.
- **T-003**: agent-connection sessions present the launch result; history row and Session header must agree for the same session — closing the surface-divergence risk.
- **Spec**: new scenario “Agent-connection sessions present their launch result”; the non-fabrication scenario's premise is corrected to “the session was not created by an AgentJob launch”, which is now true for exactly the sessions it excludes.

---

## Regression check on the fixes

Checked every section the disposal commit touched and every new claim it introduced; no new must-fix problem:

- **Cost/EndedAt design coherence**: server always derives `EndedAt` from recorded anchors; the web spec's active-session branch (elapsed-so-far, no end) is a presentation choice keyed on Activity, not a contradiction — the read-model scenario (“latest recorded anchor”) and the row scenario (“active → elapsed so far”) compose cleanly.
- **Open questions adopted without drift**: excerpt bound (~200 chars, T-001 notes), workspace-variant population (shares `UnifiedSessionListItemDto` — verified both variants exist, `AgentSessionQuerier.cs:162,213`), group ordering (T-002 notes). All consistent with design D1/D4.
- **DTO append precedent**: both list records still end with optional `Origin`/`TargetId` (`AgentSessionReadModels.cs:221-222, 478-479`) — the “append five optional fields” plan compiles as claimed; `CliFieldContractTests` covers both DTOs (`CliFieldContractTests.cs:77,103`).
- **T-003's “含别名” test requirement** is grounded: the web turn-state vocabulary does carry aliases (`TERMINAL_TURN_STATES` includes `succeeded`/`success`/`done`/`canceled`/`stopped`/`timeout`, `timeline-facts.ts:46`).
- **D8 premises still accurate**: `turnStateFacts` maps completed → muted `status` and failed/cancelled → `error` (`timeline-facts.ts:372-384`); grouping restricted to `file-read`/`shell`/`tool` (`group.ts:3-6`); `status` → `quiet` salience (`derive.ts:198-202`).
- **Current-behavior Context claims** re-verified: `?jobId=` fresh-launch flow exists (`useUnifiedSessionDataSource.tsx:97-107`), "Recent" duplicate slice and `agentName` row label exist (`AgentDetailPage.tsx:306,529`), the observation mapper does not map `JobId` today (`AgentSessionObservationMapper.cs:33-50`).
- **Task/spec anchors**: every `spec` anchor and every “本任务同时满足” heading resolves to a real requirement in the spec files; ordering and dependencies unchanged and correct; T-005 still gated on all web tasks.

## Dimension re-verification (edited regions)

- **Coverage**: all six ACs re-checked after the edits — AC #2 now fully mapped (MF-1 fix); AC #1/#3/#4 unchanged and covered; AC #5/#6 remain covered-by-construction (observations 2–3 below). Checked.
- **Correctness**: D2/D3/D5/D7 re-derived against the verified facts above; the launch-envelope rule is now internally consistent across D3, T-001, T-003, and both specs. Checked.
- **Codebase consistency**: every path, field, enum value, and behavior claim in the edited sections verified against `2681298d7`. Checked.
- **Task breakdown**: T-001/T-002 criteria updated in lockstep with the design and spec; test expectations match the corrected premise. Checked.

---

## Observations (do not affect the verdict; carried from round 1 unless noted)

1. **Mixed-state selection ambiguity persists (server vs web).** The read-model spec derives from “the session's **latest AgentTurn**” (non-terminal → unresolved), while D6/T-003 select “the highest-sequence Turn **with a terminal status**”. For Turn1=completed + Turn2=executing, history could say `unresolved` while the Session header says `completed`. Both rules present recorded facts honestly, so this stays below the must-fix bar; recommend pinning one rule (e.g. highest-sequence terminal Turn, else unresolved) on both sides during implementation.
2. **AC #6 still not pinned as a scenario.** No spec scenario requires the rewritten history row to remain a link to `/sessions/{id}`. Current rows are links and nothing instructs dropping them, so this is regression-prevention hygiene, not a coverage hole.
3. **AC #5's “导出结果” remains unacknowledged** in the design (no export surface exists; external `agent-api.md` projection is an explicit non-goal, so consistency holds by construction). One sentence would close the loop.
4. **T-005's grep verification is vacuous as written.** The gap sentence in `docs/web-ui.md:130-131` is line-wrapped (“AgentJob has\nno result view separate…”), so grepping the full sentence already returns empty today. Verify deletion with a single-line fragment (e.g. `no result view separate`).
5. **Adjacent doc staleness** (AgentSession gap footnote's already-false “no raw event view” claim; `design/session-timeline.md` Status section) is out of scope; T-005 implementers should avoid leaving new contradictions.
6. **Round-1 rationale nits resolved**: the “5s-polled” claim is gone, and D2's cancelled-alias citation now explicitly says “same boundary, a different vocabulary”.
7. **New, minor (from this round's regression sweep)**: (a) the read-model requirement's phrase “sourced from the existing AgentJob read surface and launch observation rather than a duplicated read path” could be misread as mandating a launch-observation query per row; D1's rejected alternative and D3's reconciliation make the intent unambiguous (derive from the first JobId-bearing Turn; build no new Job read path). (b) The history spec's “Launch result resolves from the first Turn” scenario doesn't itself condition on JobId presence, but its requirement subject (“the session's first AgentJob”) presupposes one, and T-001/T-002/the session-page non-fabrication scenario pin the JobId guard — no actual conflict.

---

## Summary

Round-1 MF-1 and MF-2 are both properly disposed: the fixes are complete across proposal, design, tasks, and both specs; every factual premise the corrected plan rests on was re-verified against the codebase; and the edits introduced no regression that meets the must-fix bar. The plan is ready to build.

<promise>PASS</promise>
