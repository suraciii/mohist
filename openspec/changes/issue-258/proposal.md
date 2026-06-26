## Why

Opening Mohist shows a row of empty placeholder boxes, while the single most important question — "what needs me right now?" (approvals / blocked / interrupted) — is buried inside an equal-weight 2×2 zone grid, forcing users to dig through the issues list to find what awaits their action. The dashboard should let a user judge factory health at a glance and push "what needs me" to the most prominent position where it can be handled inline, because the human (not the runners) is the real bottleneck and must be surfaced to the top.

## What Changes

- Add a full-width **factory status headline** pinned to the top of the dashboard, surfacing at a glance: runner online state, in-flight issue count, awaiting-approval count, and issues shipped today. The headline lets a user decide "can I walk away / do I need to step in now" in one look.
- Mount the already-built `AttentionHero` widget as a **full-width Hero** directly under the headline, so attention (待审批 / 卡住 / 中断 + runner-down) is no longer an equal-weight zone but the dominant first-screen element, with inline Approve/Resume and a one-line context (issue title) per item so decisions can be made without leaving the page.
- **BREAKING**: Change the dashboard layout contract. Attention is no longer a peer 2×2 zone; it becomes a full-width Hero and a new full-width headline slot is introduced above the remaining zones (`Pulse`, `Productivity`, `Digest`).
- Reserve a "today cost" field slot in the headline; it stays empty until the cost rollup endpoint (epic issue #262) is ready and does not block this change.

## Capabilities

### New Capabilities

- `dashboard-factory-status`: The full-width factory status headline pinned atop the dashboard — which status fields it shows (runner online, in-flight count, awaiting-approval count, today shipped), how each is derived from existing read-only sources, and the reserved (initially empty) today-cost slot.
- `dashboard-attention`: The Attention Hero — surfacing rules for the three issue attention types (awaiting approval / blocked / interrupted) plus the runner-down alert, the inline Approve/Resume actions, the one-line per-item context (issue title), and its full-width Hero placement on the first screen.

### Modified Capabilities

- `dashboard-shell`: The zone/layout contract changes. The four equal-weight 2×2 zones are replaced by a full-width factory-status headline on top, a full-width Attention Hero below it, and the remaining zones (`Pulse`, `Productivity`, `Digest`) underneath. `Attention` ceases to be an equal-weight zone slot.

## Impact

- **Code**: `packages/web/src/pages/dashboard/ui/DashboardPage.tsx` (zone wiring + grid layout), `packages/web/src/pages/dashboard/ui/DashboardZone.tsx` (zone shell/type). New factory-status widget under `packages/web/src/widgets/` (consumes `useIssues` + `useAgentStatus`, read-only). Mount the existing `packages/web/src/widgets/attention-hero/` widget into the dashboard.
- **No backend/API changes**: all data derives from existing read-only sources (`useIssues`, `useAgentStatus`); the cost rollup endpoint is a separate downstream dependency (#262), not introduced here.
- **Dependencies**: Blocks epic issues #259 / #260 / #262 from landing into the dashboard. Depends on #262 for the reserved today-cost field only (non-blocking).
- **Tests**: Existing `AttentionHero` and `DashboardPage` tests must pass; new coverage required for the factory-status headline and the revised first-screen layout.
