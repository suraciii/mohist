# Self-Review — issue 592 plan artifacts

Reviewer: plan reviewer (first review, full sweep) · Date: 2026-08-14
Artifacts: `proposal.md`, `design.md`, `tasks.json`, `specs/session-detail-data-source/spec.md`, `specs/session-transcript-timeline/spec.md`
Issue: #592 “web：删除无生产消费者的组件与投影，会话数据源接口改具体类型” — body enumerates **five** Change-Scope groups and a five-item Done-When checklist.

## Verdict

**FAIL.** The session-side work that the plan does cover is technically excellent (every dead-code and survivor claim I re-derived from the codebase held up), but the plan covers only two of the issue's five scope groups, omits an explicitly-motivated sub-requirement of the group it does cover, and carries acceptance criteria that are unsatisfiable without deleting live code the issue protects.

## Must-Fix Findings

### MF-1 — Plan omits issue groups 2, 4, and 5 entirely; three Done-When criteria cannot be met

The issue's Change Scope is “以删除为主、一处文档化，共五组”. The plan (proposal What Changes, design D1–D5, tasks T-001–T-004, both spec deltas) addresses only **group 1** (dead coder-session chain + SessionEvent view projections) and **group 3** (interface → inferred concrete type), plus data-layer deletions the issue never enumerates (see Obs-3). Nothing in any artifact mentions the following, all of which I verified exist in `packages/web` today exactly as the issue describes:

- **Group 2 — old markdown renderer + self-built toast system.** `src/shared/ui/components/markdown-content.tsx` has zero production references (only `MarkdownReader.test.tsx` mentions it); `src/shared/ui/toast/` (`RuntimeToastHost.tsx` + test + index) is mounted in `src/app/App.tsx` while every production toast call goes through sonner (`run-lifecycle-toast.ts`, `AppContent.tsx`, `agent-sessions.ts`).
- **Group 4 — viewport hooks + markdown-reader toggles.** Three hooks, one usage point each: `shared/lib/use-narrow-viewport.ts` (→ `IssueDetailPage.tsx`), `shared/lib/use-media-query.ts` (→ `SessionDetailShell.tsx`), `shared/hooks/use-mobile.ts` (→ `shared/ui/components/sidebar.tsx`). `MarkdownReader.tsx:338-340` ships three never-enabled toggles (`showToc`, `showHeadingAnchors`, `showCopyCode`, all default `false`) dragging `copy-code-button.tsx` / `heading-remap.tsx` and tests.
- **Group 5 — AGENTS.md convention.** `packages/web/AGENTS.md` contains no query options-factory/thin-hook documentation.

**Violated acceptance criteria (Done When):** #1 “第 1、2、4 组删除点全部归零，产品代码净减少约 2500 行以上” (groups 2 and 4 never reach zero); #3 “viewport 检测 hook 从 3 个收敛为 1 个，三个原使用点全部指向它”; #4 “packages/web/AGENTS.md 新增查询双写约定说明”. The omission is silent — no Non-Goal disposes of these groups — and no sibling issue covers them (open issues 593–595 are server/runner/cli).

**Fix direction:** extend proposal/design/tasks/specs to cover groups 2, 4, and 5 (or renegotiate the issue scope explicitly — as written the plan is simply incomplete).

### MF-2 — T-001/T-002 rg-zero acceptance criteria demand removal of symbols that stay live, contradicting the issue's Non-Goals

- T-001 criterion 3 requires zero hits for **`useSessionTimeline`** — but that exact symbol is the *live* hook at `widgets/session-transcript/model/useSessionTimeline.ts`, consumed by `SessionTranscriptLayout.tsx` and `pages/session/data/useUnifiedSessionDataSource.tsx`. The issue's Non-Goal says “不动正在使用的会话 transcript 时间线及其时间线派生逻辑”. (T-003 exempts session-transcript local names for `SessionEvent`; T-001 has no such exemption.)
- Same criterion requires zero hits for **`SessionTimeline`** (a plain substring of the live `useSessionTimeline`; at T-001 verification time `entities/session` still exports `SessionTimelineRound/ToolCall/Recovery/Compaction/View` — they die only in T-003) and **`WaitingCard`** (a live local interface at `entities/agent-ops/model/activity-cards.ts:16`, re-exported from `entities/agent-ops/index.ts`).
- T-002 criterion 5 requires zero hits for **`stopSession`** — but `stopSession` remains a live local callback in `useUnifiedSessionDataSource.tsx:203` in the plan's final state; no task renames it (T-004 only removes annotations).

These tasks are AFK-mode with these criteria as pass/fail gates: as written they either never pass, or invite an executor to delete/rename live code — violating the Behavior Contract's safety net (“保留下来的 hook、组件、页面的既有测试”) and Done When #5.

**Fix direction:** scope the sweeps precisely (word-boundary patterns, exclude `widgets/session-transcript` and `entities/agent-ops`, drop `SessionTimeline` as a bare token or qualify it, and either exempt the hook-internal `stopSession` callback or plan its rename).

### MF-3 — Group 3's three always-empty, still-rendered fields are not removed; issue requires them and their render branches to disappear

The issue's Motivation explicitly flags “三个由唯一实现恒返回空值、页面却仍在解构渲染的字段”. These are exactly `siblingNav` (always `null`), `siblingSidebar` (always `null`), and `issueTitle` (always `undefined`) in `useUnifiedSessionDataSource`'s return object; `SessionDetailShell.tsx` destructures them (lines 174–178) and renders them (461, 467, 535). Change Scope group 3 requires “接口删除后，无消费字段与**恒空字段及其页面空渲染分支**随之消失”.

The plan handles the *unconsumed* drift fields (runtime-lineage family, `emptyStateKind` — verified accurate) but its design Non-Goal (“Renaming or reshaping the hook's returned fields beyond what inference and dead-field removal require”) leaves the always-empty trio and the shell's empty-render branches in place; neither design nor tasks ever mention them. Since these fields are hook-returned, the inferred contract preserves them, so Done When #2 (“页面外壳不再解构渲染不存在的值”) is at best partially met under the issue's own framing.

**Fix direction:** add removal of `siblingNav`/`siblingSidebar`/`issueTitle` from the hook's return and the shell's destructure/render branches (with the corresponding fixture/test pruning) to D4/T-004, or explicitly justify keeping them in the plan.

## First-Review Dimension Sweep

- **Issue goals vs plan framing — FAIL.** Re-read the issue body before the artifacts. The proposal's framing (“dead session surface”) is coherent but quietly re-scopes the issue from five groups to two-plus-extras; MF-1, MF-3.
- **Coverage — FAIL.** Groups 2/4/5 and their Done-When items unaddressed (MF-1); group 3's always-empty-field requirement unaddressed (MF-3). Everything else the issue requires of groups 1 and 3 is covered by T-001–T-004 and the two spec deltas.
- **Correctness — checked in depth; the covered approach is sound, with the MF-2/MF-3 exceptions.** I independently re-derived every load-bearing claim and they all hold: the widget chain has no production consumers outside `index.ts`/tests/one consistency leg; the six survivor families are production-consumed (ActivityPage, SessionDetailShell) and import nothing deleted; the SessionEvent family's only outside consumers are the dead chain and `AgentSessionEvent` (coder-session `model/types.ts:1,104`); `entities/session/model/types.ts` is 100% the SessionEvent family (12/12 exports); session-transcript imports only the timeline projection from `entities/session`; every T-002 symbol is dead in production (the `stopSession` hit in the unified hook is a local callback, not the client); the interface drift fields exist only in fixtures; `emptyStateKind` has zero production readers; the shell's presentation map keys are exactly `active/idle/unknown`, so `StatusKind`→`SessionStatusKind` compiles; `AgentTurnObservation.status: string` + the `!== 'queued' && !== 'executing'` guard narrows to `'queued' | 'executing'`, so the inferred stop handle keeps its shape; `createIdempotencyKey` in `client.ts` is used only by the deleted `postFollowup`/`stopSession`.
- **Consistency with codebase/conventions — checked, no issue.** FSD index pruning, `@x` re-export handling, and the `### Requirement:`/`#### Scenario:` delta format match `issue-589`'s accepted change; every gate named in tasks exists (`typecheck` = `tsc -b`, `test:ci` = `check:fsd && check:test-boundaries && vitest run`, `build`); the 1000-line ratchet is real (`scripts/check-file-sizes.ts`).
- **Task breakdown — FAIL via MF-2; otherwise sound.** The reordering (T-002 before T-003 so `AgentSessionEvent` dies before `@x/session-view.ts`) keeps every intermediate state compiling; per-task gates and delete-with-subject test handling are verifiable; T-004 carries the final sweep + build correctly. The defect is the sweep criteria naming live symbols (MF-2).

## Observations (non-blocking)

1. **Design/tasks wording mismatch on `AgentSessionTranscriptResponse`.** Design D3 says it leaves the `@x/agent-session` import list (“dies with the DTO”); T-002 says it “survives as a type because the unified transcript client uses it”. Both are true at different levels (the type survives in `entities/coder-session`; `agent-sessions.ts` stops importing it), but the task wording could be read as keeping a dead import. Harmonize.
2. **`stopGenericSession` is mischaracterized as a “zero-consumer alias… only its own test references it”.** `stopGenericSessionMutationOptions` (`agent-sessions.ts:360-363`) calls it and feeds the live `useGenericTurnControl` via `genericTurnControlMutationOptions` (line 384). Harmless here because the plan keeps it, but the Open Question's “delete in follow-up” recommendation would need rewiring — correct the rationale before anyone acts on it.
3. **T-002's data-layer deletions are beyond the issue's enumerated scope** (`useCoderSessions`, followup/stop mutations, issue-scoped clients, generic duplicates are in none of the five groups). I verified they are genuinely dead in production, and they fit the issue's title/spirit; keeping them is fine, but the proposal should acknowledge the scope addition.
4. **“全库仅 4 处类型标注” vs reality: 5 files import `SessionDataSourceResult`** (shell + 3 test files + the hook's own return annotation). Presumably the issue counted 4 external sites; T-004 correctly covers all five.
5. **The issue's “保留仍被使用的四个值类型” does not map onto the plan's dispositions** — all five `SessionDataSource.ts` types die; the stop-handle typing comes from indexed access (`UnifiedSessionDataSourceResult['stop']`) and `SessionStatusKind` lives in `entities/coder-session`. The outcome still satisfies the contract goal; reconcile the bookkeeping during implementation.
6. **Positive verification notes for the implementer:** survivor test files all exist (`ContextHealthBar.test.tsx`, composer/recovery/label tests, activity-events/usage-snapshot tests); `convertLegacyToAgentMetadata` appears only in `tests/session-page-test-utils.tsx`; the consistency spec's three legs are exactly as described (`CompactSessionCard` from `widgets/dashboard-pulse` stays); `AgentSessionEventSummary` is live (consumed by `entities/agent/model/types.ts`) and must survive the T-003 sweep — use word-boundary matching so it and `matchesSessionEvent` don't false-positive.

<promise>FAIL</promise>
