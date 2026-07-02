## Why

The Dashboard 首页 should answer "is the system healthy, and is there anything that needs me?" at a glance. Today it forces the user to scroll and self-filter through stale placeholders and duplicated counts: the All-clear block advertises a "Productivity preview will appear here once it ships." while Productivity is already rendered directly beneath it; an "Ask Agent" button sits beside Attention answering a different question (attention is for human approval, not agent dispatch); Pulse re-lists the four lifecycle counts that the Factory status headline already carries as In flight / Awaiting approval; and SnapshotRow's weekly Completed/Failed/New overlaps the Digest's recent completion list. This change is a pure deletion pass to restore the 首页 to its single职责 of situational awareness + action.

## What Changes

- Remove the stale "Productivity preview will appear here once it ships." placeholder block from Attention's All-clear state. All-clear keeps the all-clear message and the `ApprovalWaitSummary` only.
- Remove the "Ask Agent" button (and its navigation to `/agent-sessions/new`) from the dashboard hero. The header's New Issue entry, the Attention Hero's inline Approve/Resume actions, and the Pulse session cards remain the legitimate action entry points.
- Remove Pulse's four lifecycle status pills (`Active`/`Waiting`/`Completed`/`Failed`); keep the runner `slots used` indicator, the compact session card list, and the overflow link to Activity.
- Remove SnapshotRow (the "This week — Completed/Failed/New" block) from the Productivity zone; the Digest zone already covers recent completed/failed issues with titles and links at higher density.
- Update affected web unit tests/specs to assert the post-cleanup structure, with no residual assertions on the removed elements.

No new components, routes, charts, data contracts, query keys, or API endpoints are introduced. All changes are deletions/relocations within the single dashboard subsystem. **BREAKING** only at the DOM/testid level: the `productivity-placeholder`, `ask-agent-project`, `pulse-status-pills`, `pulse-pill-*`, `productivity-snapshot-row` (and children) testids disappear from the rendered Dashboard. No public API or data-contract break.

## Capabilities

### New Capabilities

(none — this is pure subtraction; no new capability is introduced.)

### Modified Capabilities

- `dashboard-pulse`: The requirement mandating that the zone render status pills for the four lifecycle states (`active`/`waiting`/`completed`/`failed`) is **removed**. This directly contradicts the current spec text, so the requirement is rewritten to scope the zone to the runner `slots used` indicator + compact session cards + overflow link only.
- `dashboard-attention`: The All-clear state contract is tightened: the All-clear state SHALL render only the all-clear message and the `ApprovalWaitSummary`, and SHALL NOT render a productivity-preview placeholder block. (The current spec is silent on the placeholder; this adds an explicit anti-regression requirement.)
- `dashboard-shell`: The hero / first-screen composition is tightened: the dashboard hero SHALL NOT render an "Ask Agent" entry. The `agent-workbench` "Ask Agent" quick entry continues to apply to issue/epic/project pages only; the dashboard is explicitly excluded.

Note: the SnapshotRow removal is implementation-level — no existing spec mandates a weekly-completion-snapshot block in the Productivity zone (`dashboard-shell` only pins the `QualityPanel` there), so it requires no spec delta. Its removal is enforced by the web unit-test updates.

## Impact

- **packages/web (deletions only)**:
  - `widgets/attention-hero/ui/AttentionHero.tsx` — delete the `productivity-placeholder` block inside `AllClearState`; the now-unused surrounding markup/imports are cleaned up.
  - `pages/dashboard/ui/DashboardPage.tsx` — remove the "Ask Agent" `<Button>` from `dashboard-hero` and its `useNavigate`/`BotIcon`/`toProjectPath('/agent-sessions/new')` usage if these become unused.
  - `widgets/dashboard-pulse/ui/PulseZone.tsx` — delete the `pulse-status-pills` block and the `PILL_STYLE`/`PILL_LABEL` maps; keep `pulse-capacity-header` / `pulse-slots`.
  - `pages/dashboard/productivity/ProductivityZone.tsx` and `SnapshotRow.tsx` (+ `SnapshotRow.test.tsx`) — drop `<SnapshotRow />` from the zone and delete the component and its test.
  - Tests: `AttentionHero.test.tsx`, `DashboardPage.test.tsx`, `PulseZone.test.tsx`, `ProductivityZone.test.tsx` updated to reflect the converged structure.
- **No server / runner / CLI / API / database / schema changes.** No new dependencies.
- Verification: `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` must pass.
