## Why

Operators finishing a batch of issues want to know "how am I doing lately", but every analysis number on the Dashboard is an isolated scalar (cycle time 5.2h, FTR 73%, $182 this week) with no baseline, no trend direction, and no verdict — the user must recall last week's value, do the arithmetic, and judge whether the number is good. The Insights epic (epic #30) introduces a dedicated `/insights` page whose first screen is a **Signal Summary**: four complete verdict sentences (产出节奏 / 交付效率 / 质量信号 / 投入信号), each carrying current value + trend direction + change magnitude. M1 is the epic's highest-value slice and the only milestone that needs new backend capability: to produce a trend arrow the four metrics queriers must return a **current window and the adjacent previous window** in a single read, so the frontend can derive the delta. This is needed now because the conclusion-first summary is the core differentiator of Insights and unblocks M2 (chart migration) by establishing the route + nav + summary skeleton.

## What Changes

- Add a new `/insights` route and an "Insights" entry in the sidebar nav.
- First screen renders a **Signal Summary**: four verdict sentences, one per dimension, conclusions-first:
  - **产出节奏** — issues completed in the current window vs the previous window (e.g. "本周完成 5 个，比上周多 2 个").
  - **交付效率** — average cycle time for the current window vs the previous window, plus the slowest stage named (e.g. "交付在加快：5.2h，快了 18%；最慢是 Build 阶段").
  - **质量信号** — first-time-right rate for the current window vs the previous window, as a percentage-point delta (e.g. "质量需关注：首次正确率 73%，下降 8 个百分点").
  - **投入信号** — spend in the current window + per-issue cost, each vs the previous window (e.g. "本周 $182，单 issue $36，持平").
- Each verdict renders a trend direction (↑ / ↓ / 持平) and a change magnitude derived strictly from the current-vs-previous window comparison.
- Each verdict degrades gracefully when data is insufficient (new project, no previous window, empty result): it shows the current value and hides the trend or marks "数据不足", never rendering a misleading arrow and never erroring.
- Below the summary, render a chart placeholder zone marked "图表将在后续迁移" (M2 deliverable).
- Server: the four metrics data sources — completion, delivery-time, quality, agent-cost — each additionally return the **previous adjacent window** alongside the current window in a single read, so the frontend can compute the delta and direction. This is additive only: existing response fields and their shapes are preserved (no field removed, no existing field re-shaped); only previous-window fields are added.
- Scope lock: the four dimensions are fixed (no fifth dimension); the retrospective target is the project as a whole (no epic/label drill-down); no time-range selector (M3); no Dashboard content changes (#320 owns Dashboard cleanup); no chart migration (M2).

## Capabilities

### New Capabilities

- `insights-signal-summary`: The `/insights` route, the sidebar "Insights" nav entry, and the Signal Summary component that renders the four verdict sentences (产出 / 交付 / 质量 / 投入), each with current value + trend direction + change magnitude derived from the four metrics surfaces' current-vs-previous-window returns, with graceful degradation on insufficient data, plus the M2 chart-placeholder zone.
- `issue-completion-metrics`: The project-scoped completion-count surface (completed / failed issue counts windowed by completion-event time). Introduces the dedicated capability for the existing `issues/metrics/completion` data source (currently unspecced) and adds the current-window + previous-adjacent-window return so the 产出节奏 verdict can derive its delta.

### Modified Capabilities

- `issue-delivery-time-metrics`: The delivery-time surface gains a previous-adjacent-window return alongside the existing fixed trailing window so the 交付效率 verdict can derive the cycle-time delta and name the slowest stage.
- `ai-quality-metrics`: The quality surface gains a previous-adjacent-window first-time-right return alongside the existing windows so the 质量信号 verdict can derive the percentage-point delta.
- `agent-cost-metrics`: The agent-cost surface gains a windowed current-vs-previous return for spend and per-issue cost alongside the existing cumulative rollup, so the 投入信号 verdict can derive its deltas.

## Impact

- **Server** (`packages/server`):
  - `Api/IssueRoutes.Metrics.cs` + `Issue/Services/IssueQuerier.cs` (`GetCompletionBucketsAsync`) — completion surface returns previous window.
  - `Api/IssueRoutes.DeliveryTimeMetrics.cs` — delivery-time surface returns previous window.
  - `Api/IssueRoutes.QualityMetrics.cs` — quality surface returns previous window.
  - `Api/AgentRoutes.cs` (`/agent/cost` + `AgentSessionQuerier`) — cost surface returns windowed current + previous spend / per-issue cost.
  - All four changes are additive to the response DTOs (new previous-window fields only); no existing field removed or re-shaped.
- **Web** (`packages/web`):
  - New `insights` page/widget (route component + Signal Summary), new `/insights` `<Route>` in `app/App.tsx`, new nav entry in `widgets/app-shell/ui/AppSidebar.tsx`.
  - New hooks deriving deltas from the four dual-window surfaces; new query keys.
  - Chart placeholder component (M2 marker).
- **No schema migration, no API contract break** (only additive fields), per the non-goals.
- **Tests**: web unit + a11y for the Signal Summary (four verdicts, trend arrows, graceful degradation, empty-state); server spec + unit for each of the four queriers' previous-window return and zero-sample/empty behavior. typecheck + test must pass for both web and server.
- **Risk** (medium, carried from issue): spans server (querier dual-window) and web (new route + new component), but no schema migration and no API contract break.
