## Why

The dashboard is Mohist's first screen and the highest-frequency entry point, but it does not answer the question an owner opens it for: *what needs my attention right now?* Today it renders a stat strip, an attention hero, and two equal-weight dashed zones (active sessions + recent history) side by side, regardless of state. Needs-attention signals are present but incomplete (runner *unavailable* shows; runner *capacity-limited* does not), running *issues* are shown only when an agent session is active (so in-progress issues paused between stages vanish from the first screen), and empty zones hold fixed 160px boxes that dominate the page whenever nothing is running. The result is an owner who must open the issue board to learn what is blocked, what is waiting on approval, whether the runner is saturated, and what is actually moving. With the shared status-presentation baseline from issue 398 now in place, the dashboard can be reworked into a true attention-first production overview.

## What Changes

- Re-establish the dashboard as a single attention-first production overview with a strict priority order: **needs-attention → active production → capacity → recent history**. Lower-priority zones yield visual prominence when higher-priority zones have content.
- Grow the needs-attention model to include **runner capacity-limited** (active slots at or above max) alongside the existing runner-unavailable, approval-needed, blocked, interrupted, and integration-failed states, so capacity constraints surface without opening the runner board.
- Make the active-production zone show **running issues** (in-progress issues) with their current **workflow stage** and an **owner-action-needed** cue — not only active agent sessions — so work paused between stages stays visible on the first screen.
- Make empty dashboard zones **collapse** (not occupy fixed-height boxes) when there are active, blocked, interrupted, or approval-waiting issues, so low-value/empty areas stop dominating the first screen.
- Replace the current "headline + all-clear hero + two empty 160px zones" layout with a **concise ready state** when nothing needs attention and nothing is active, instead of a large empty layout.
- Keep all product terms intact: issue, workflow, stage, health, approval, runner, session, epic.
- **No backend/workflow/runner change** — this rework only reshapes the dashboard's information hierarchy and visible content model; how issues, workflow runs, approval state, and runner capacity are computed is unchanged.

Non-goals (per issue): no push notifications, event subscriptions, or inbox mechanism; no Settings or runner-configuration redesign; no replacement of the issue board; no change to how issues, workflow runs, or runner capacity are computed.

## Capabilities

- `dashboard`: The dashboard as an attention-first production overview — its zone priority order (needs-attention → active production → capacity → recent history); the needs-attention states it surfaces (approval gates, blocked, interrupted, integration-failed, runner-unavailable, runner capacity-limited); the context active/running issues show (workflow stage + owner-action-needed cue); how empty zones behave when higher-priority content exists; and the concise ready state when nothing needs attention or is active.

## Impact

- **Affected code (Web only, `packages/web/src`):**
  - Page composition: `pages/dashboard/ui/DashboardPage.tsx` (zone order, conditional zone rendering, ready-state branch); `pages/dashboard/ui/DashboardZone.tsx` (zone identity/collapse behavior).
  - Attention model: `entities/issue/model/attention.ts` (`deriveAttentionItems` grows to include runner capacity-limited) and `entities/issue/lib/attention-treatment.ts`.
  - Attention surface: `widgets/attention-hero/ui/AttentionHero.tsx` (surface capacity-limited alongside runner-unavailable).
  - Active-work zone: `widgets/dashboard-pulse/` (`PulseZone.tsx`, `CompactSessionCard.tsx`) to present running issues with stage + owner-action cue, and `widgets/coder-session/model/activity-cards.ts` (`useActivityCards`) where the issue/session projection is sourced.
  - Recent-history zone: `widgets/dashboard-digest/ui/DashboardDigestWidget.tsx` (collapse when empty/deprioritized).
  - Headline: `widgets/factory-status/` (remains the compact status strip atop the overview; verify it stays subordinate to the attention hero).
- **Tests:** `pages/dashboard/ui/DashboardPage.test.tsx`, `widgets/attention-hero/ui/AttentionHero.test.tsx`, `widgets/dashboard-pulse/ui/PulseZone.test.tsx`, `entities/issue/model/attention.test.ts` — add/adjust spec tests for zone priority, capacity-limited attention, running-issue stage context, empty-zone collapse, and the concise ready state.
- **APIs / dependencies / systems:** none changed. No server, runner, or CLI impact; no new dependency. All data is sourced from existing queries (`useIssues`, `useAgentStatus`, `useAgentActivity`, `useRecentDigest`, `useApprovalWait`).
- **Risk:** touches a high-frequency entry point and several dashboard widgets, but no backend workflow behavior. Regressions are confined to dashboard presentation/hierarchy; mitigated by preserving existing test-ids (`dashboard-zone-attention`/`-pulse`/`-digest`, `factory-status-headline`) and by routing every state through the shared status-presentation layer landed in issue 398.
