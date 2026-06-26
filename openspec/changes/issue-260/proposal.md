## Why

Users have no signal on whether they are the bottleneck: there is no metric showing how long their approvals keep issues waiting. Without knowing the average approval wait, a user cannot decide whether to batch approvals, lower the approval threshold, or enable reminders — the human is the real factory bottleneck, and right now that bottleneck is invisible.

## What Changes

- Add a project-level **approval waiting time aggregation** backend endpoint returning `avg` / `median` / `max` over a trailing 7-day window, computed from the existing `approvalState.requestedAt → respondedAt` timestamps. No new data collection is introduced.
- Aggregate only over **completed** approvals (`approvalState.status` is `approved` or `rejected`, i.e. `respondedAt` is present); currently-pending (`awaiting`) approvals are excluded from the aggregate denominator — they already surface separately as attention items.
- Surface a **summary approval-wait metric on the Attention Hero** (e.g. "your approvals averaged 3.2h") so the "is the human a bottleneck" signal lives where the user is about to act, rather than forcing them to compute it from the full issue list.
- **BREAKING** (spec-level, not API): relaxes the existing `dashboard-attention` requirement that the Hero derives content exclusively from existing frontend read-only sources and introduces no backend endpoint — the Hero now consumes one new aggregation endpoint for this single summary value.

## Capabilities

### New Capabilities

- `approval-waiting-metrics`: The project-level approval waiting time aggregation — what it measures (elapsed time from `approvalState.requestedAt` to `respondedAt`), the trailing 7-day window, the `avg` / `median` / `max` statistics, the exclusion of pending (`awaiting`) approvals from the aggregate denominator, empty/zero-sample handling, and the backend endpoint that exposes it.

### Modified Capabilities

- `dashboard-attention`: The Attention Hero gains a summary approval-wait metric display. This relaxes the existing "derives content exclusively from existing read-only sources / introduces no new backend API endpoint" requirement so the Hero may consume the new `approval-waiting-metrics` endpoint for this single summary value, while every other Hero behavior (attention types, inline actions, contexts) stays read-only over existing sources.

## Impact

- **Backend**: New project-scoped aggregation endpoint alongside the existing `IssueRoutes.Metrics` completion endpoint; new aggregation query in `IssueQuerier` computing avg / median / max over `IssueReadModel.StageApproval.RequestedAt` / `RespondedAt` for `approved` / `rejected` approvals within the trailing 7d. Data source is already populated — no new event or state collection.
- **Web**: The `attention-hero` widget (`packages/web/src/widgets/attention-hero/`) consumes the new aggregation query and renders the summary metric. Display landing depends on the Attention Hero first-screen refactor (#258).
- **Dependencies**: Blocked by #258 (Attention Hero) for the display landing point.
- **Tests**: Aggregation logic (trailing-7d windowing, statistics, pending-approval exclusion, zero-sample behavior) and the endpoint contract; Attention Hero rendering of the metric.
- **Non-Goals**: No breakdown of other stage durations; no approval reminder/notification mechanism — only the metric is exposed.
