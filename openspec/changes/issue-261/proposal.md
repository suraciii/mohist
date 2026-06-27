## Why

There is no feedback loop on AI quality: a user cannot tell whether issues are getting done right the first time or whether a particular stage is repeatedly sending issues into repair. Without that signal, the user cannot decide whether to switch model, change a prompt, adjust the workflow, or simplify an issue — quality drift stays invisible until it becomes a pile of failed runs.

## What Changes

- Add a project-level **AI quality aggregation** backend endpoint returning, over trailing 7-day and 30-day windows: (1) the **first-time-right rate** — the share of shipped issues (`Done`) that reached `Done` with no check triggering repair across their entire lifecycle; and (2) the **per-stage rework rate** — for each stage, the share of issues that entered that stage where at least one check triggered repair. The endpoint is computed from the existing per-check repair counts already recorded on workflow runs; no new event or state collection is introduced.
- Surface a **`QualityPanel`** in the Dashboard `Productivity` zone (alongside `InvestmentPanel` and `CompletionTrend`) that renders the first-time-right rate and the per-stage rework rates from the new aggregation endpoint. The frontend does not compute these rates over the local full-set of workflow runs.
- **BREAKING** (spec-level, not API): the `dashboard-shell` requirement that the `Productivity` slot renders as an empty placeholder is relaxed — the slot now mounts the `QualityPanel` zone content.

## Capabilities

### New Capabilities

- `ai-quality-metrics`: The project-level AI quality aggregation — what "first-time-right" means (shipped to `Done` with no check having triggered repair across the whole lifecycle), what "per-stage rework" means (a stage counts as reworked when at least one of its checks triggered repair, over the set of issues that entered that stage), the trailing 7d / 30d windows, the ship / stage-entry denominators, zero-sample handling, and the backend endpoint that exposes it. Data is sourced from existing per-check repair records; no new collection.

### Modified Capabilities

- `dashboard-shell`: The `Productivity` slot gains the `QualityPanel` zone content. This relaxes the existing "only the `Productivity` slot SHALL render as an empty placeholder" requirement so the slot mounts the `QualityPanel`, while the slot identity (`Productivity`) and its peer relationship to `Pulse` and `Digest` stay unchanged.

## Impact

- **Backend** (`packages/server/`): New project-scoped aggregation endpoint alongside the existing `IssueRoutes.Metrics` completion endpoint and the `IssueRoutes.ApprovalMetrics` approval-wait endpoint; a new aggregation method in `IssueQuerier` following the `GetApprovalWaitAsync` pattern (load the project's issues, load each issue's workflow-run state, project repair counts per stage check, compute the two rates over the window). The per-check repair count already lives on `StageCheck.RepairCount`; it is not currently carried on `CheckStatusView`, so the aggregation either projects it onto the view or reads it from the deserialized workflow state — the exact path is a design decision, not new data.
- **Web** (`packages/web/src/pages/dashboard/productivity/`): New `QualityPanel` component mounted inside `ProductivityZone`, plus an `entities/issue` API hook (mirroring `useCompletionTrend` / the approval-wait hook) consuming the new endpoint. Display landing is the existing `ProductivityZone`.
- **Dependencies**: None blocking. Belongs to epic #23 (Dashboard control room).
- **Tests**: Aggregation logic (first-time-right vs. reworked classification, trailing-7d/30d windowing, per-stage denominator, zero-sample behavior) and the endpoint contract; `QualityPanel` rendering of the rates and empty state.
- **Non-Goals**: No per-model or per-prompt drill-down (total trend only); no quality alert thresholds; no per-session granularity.
