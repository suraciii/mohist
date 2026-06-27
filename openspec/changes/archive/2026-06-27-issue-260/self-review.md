# Self Review Report

## Result: PASS

The plan artifacts (`proposal.md`, `design.md`, `tasks.json`, `specs/approval-waiting-metrics/spec.md`, `specs/dashboard-attention/spec.md`) were reviewed against issue #260 and the live codebase. All referenced file paths and line ranges in `design.md` were verified to exist and match (see Verification below). No repairs were needed; no blocking items remain.

## Repaired Items

_None. No safe, localized fixes were required — the artifacts are internally consistent and trace cleanly to the issue._

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: feasibility
  Evidence: The aggregation deserializes every project workflow-run `State` row in memory (mirroring `GetCompletionBucketsAsync`). At very large project issue counts this could become noticeable. `design.md` already captures this under "Risks / Trade-offs" and "Open Questions" (history-growth profiling, deferred to the same D-OQ2 note at `IssueQuerier.cs:283`), so it is tracked — no change required here.
  SuggestedAction: If profiling later shows cost, consider a `json_extract`-based raw SQL pre-filter on `respondedAt`, or persisting approval transitions as `IssueEvents` rows (currently a Non-Goal, would be a separate change).
  Status: follow-up

## Verification Detail

### Alignment
- Issue AC "后端提供审批等待时长聚合（avg / median / max，trailing 7d）" → `approval-waiting-metrics` spec (5 requirements + scenarios) and `tasks.json` T-001.
- Issue AC "指标在 Attention 区域可见" → `dashboard-attention` spec (MODIFIED + ADDED requirements) and T-002.
- Issue AC "数据来自现有 approvalState 时间戳，无需新增采集" → spec scenario "Aggregation introduces no new data collection"; design D1/D2; T-001 AC #7.
- Issue AC "有测试覆盖聚合逻辑" → T-001 AC #8 (IssueQuerierSpecs) and #9 (endpoint contract); T-002 AC #8 (AttentionHero.test.tsx).
- Issue boundary "只算审批 gate 等待 (requestedAt → respondedAt)" → spec requirement #1.
- Issue boundary "分母只统计 respondedAt 已存在的审批" → spec + T-001 AC #2 (`approved`/`rejected`, non-null `RespondedAt`).
- Issue boundary "pending 不参与聚合" → spec scenario "Pending approval is excluded"; T-001 AC #2; T-002 AC #7.
- Issue boundary "前端不在本地全量 issue 列表上计算" → `dashboard-attention` spec "Hero SHALL NOT compute the metric client-side over the full issue list"; design D7 explicitly rejects client-side computation.
- Non-Goals (no other-stage breakdown, no reminders) → proposal & design Non-Goals sections.

### Completeness
- Two capabilities (one new `approval-waiting-metrics`, one modified `dashboard-attention`) map 1:1 to two tasks (T-001 backend slice, T-002 frontend slice).
- Edge cases covered: zero-sample window, single sample, genuine zero-duration wait, window aging past 7d, empty-vs-error, pending exclusion, even-`n` median (D5).
- Scenario math re-checked: samples `[1h,2h,2h,4h,16h]` → avg 5h (25/5), median 2h, max 16h ✓; single 3.2h sample → 3.2h for all three ✓.

### Consistency
- Naming is uniform across artifacts: endpoint `approval-wait`, hook `useApprovalWait`, query key `['issues','metrics','approval-wait',projectId]`, prop `approvalWait?`, DTO `ApprovalWaitMetricsResponse`, capability `approval-waiting-metrics`, testid `approval-wait-empty`.
- Tasks point to the correct spec files (T-001 → `specs/approval-waiting-metrics/spec.md`, T-002 → `specs/dashboard-attention/spec.md`).
- Design decisions map cleanly to spec requirements (D1↔projection reuse, D2↔in-memory aggregation, D4↔trailing-7d-on-respondedAt, D5↔zero-sample discriminator, D7↔Hero hook, D8↔narrow spec relaxation).
- Proposal "New Capabilities" / "Modified Capabilities" lists match the spec delta (ADDED in `approval-waiting-metrics`; MODIFIED + ADDED in `dashboard-attention`).

### Feasibility (codebase-verified)
- `StageApproval.cs:7-8` — `RequestedAt` (non-null `DateTime`) / `RespondedAt` (nullable `DateTime?`) exist as claimed. ✓
- `IssueQuerier.cs:215-336` — `GetCompletionBucketsAsync(projectId, bucket, now)` precedent confirmed; `:278-294` documents the EF Core SQLite / TEXT / `DateTimeOffset` limitation that justifies in-memory aggregation; `:283` carries the D-OQ2 profiling note; `:495-571` is exactly `LoadWorkflowStatesAsync` + `ApplyWorkflowProjections` (the path D1 reuses). ✓
- `IssueRoutes.Metrics.cs:10-11` — the literal `metrics/` route-prefix collision comment exists; `IssueRoutes.cs` registers partials via `MapIssueMetrics()` next to which `MapIssueApprovalMetrics()` will sit. `IssueRoutes.Dtos.cs:227+` holds `CompletionMetricsResponse` / `CompletionMetricsWindowDto`, confirming the DTO placement convention. ✓
- `MohistServiceRegistration.cs:77` — `TimeProvider.System` is registered; design D3 deliberately chooses passed-in `now` for parity with the sibling metric, a justified divergence. ✓
- `completion-trend.ts` — exact structural twin (same folder, `staleTime: 60_000`, query key `['issues','metrics','completion','week',projectId]`, inline DTO typing); `entities/issue/index.ts:3-4` confirms the export pattern. ✓
- `AttentionHero.tsx:27-30` (`AttentionHeroProps` with optional `issues?`/`agentStatus?` slots), `:40-47` (prop-override resolution + `deriveAttentionItems`), `:52-66` (approve/resume `onSuccess` invalidation) — all match D7's claims; `AttentionHero.test.tsx:20` uses `vi.hoisted` mock pattern as claimed. ✓
- `DashboardPage.tsx:66-71` — the `dashboard-hero` slot exists (#258 merged), confirming "no sequencing blocker". ✓
- `relative-time.ts` / `format-time.ts` — neither formats elapsed durations; the new `format-duration.ts` is genuinely needed. ✓
- `IssueQuerierSpecs.cs:774+` — `GetCompletionBucketsAsync_*` tests exist with a literal `now`, confirming the proposed test pattern. ✓
- Existing `openspec/specs/dashboard-attention/spec.md:124` forbids new backend endpoints; the proposal/design correctly flags the narrow relaxation as a spec-level BREAKING change (D8). ✓
- New files (`IssueRoutes.ApprovalMetrics.cs`, `approval-wait.ts`, `format-duration.ts`) do not yet exist — correctly scoped as task outputs, not pre-existing dependencies.

### Dependency completeness
- T-001: `priority: 1`, `dependsOn: []` — first task, no missing prerequisites. ✓
- T-002: `priority: 2`, `dependsOn: ["T-001"]` — references an existing lower-priority task; the frontend hook consumes T-001's endpoint contract. ✓
- No cycles; ordering is acyclic. ✓

### Task granularity
- T-001 is one cohesive backend feature slice (query method + result records + route partial + DTOs + specs), not fragmented into "define interface / register DI / add tests" sub-tasks; tests are bundled as acceptance criteria inside the slice.
- T-002 is one cohesive frontend feature slice (hook + formatter + Hero prop/render + invalidation + tests); the formatter is co-located because the Hero needs it, and tests live inside the task rather than as a standalone "add tests" task.
- No over-fragmentation; each task is a complete, independently-verifiable slice.

<promise>PASS</promise>
