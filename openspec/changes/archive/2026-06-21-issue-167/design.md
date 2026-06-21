## Context

Issue #163 shipped the Dashboard as the default landing page with four zone mount-point slots (`Attention`, `Pulse`, `Productivity`, `Digest`), each rendered as an empty `DashboardZonePlaceholder` (`packages/web/src/pages/dashboard/ui/DashboardZonePlaceholder.tsx`). Issue #164 then promoted the attention judgment into a shared, pure derivation in the Issue entity:

- `deriveAttentionItems(issues, agentStatus): AttentionItem[]` — `packages/web/src/entities/issue/model/attention.ts`, exported from `entities/issue`. `AttentionItem = { issueNumber, issueId, label, detail? }`. Exactly four labels: `Approval needed`, `Integration failed`, `Interrupted`, `Needs action`.
- The Kanban widget already consumes it (`KanbanBoard.tsx:542`) via `useMemo(() => deriveAttentionItems(issues, agentStatus), [issues, agentStatus])` and renders an inline `NeedsAttentionSummary` (links to issue detail) plus a `RunnerUnavailableBanner`.

The building blocks the Hero needs already exist as public seams:

- Issues: `useIssues()` (`entities/issue`).
- Agent/runner status: `useAgentStatus()` (`entities/agent`); `AgentStatus.runnerAvailable?: boolean`, `runnerMessage?: string|null`, `runners?` list.
- Runner summary (richer): `useRunnerSummary()` / `hasConnectedCapacity` (`entities/runner`) — used by Kanban's banner.
- Mutations: `resumeIssue(number, projectId)` and `approveIssue(number, projectId)` (`entities/issue/api/client.ts`, both `POST`). Reference mutation pattern at `IssueCard.tsx:203` invalidates `['issues']` + `['agent-status']` on success.
- Navigation: `useProjectPath()` → `toProjectPath('/issues/:n')` and `toProjectPath('/activity')`. Issue detail route is `issues/:number` (`app/App.tsx:57`).

This issue is **pure consumption UI** in the Dashboard composition layer (web, not a domain context). It adds no server endpoints, no new attention categories, and mutates no domain state of its own accord.

## Goals / Non-Goals

**Goals:**
- Fill the Dashboard `Attention` slot with a self-adaptive two-state Hero driven entirely by the shared `deriveAttentionItems` + `useAgentStatus`.
- Make every attention item reachable in one action (navigate / Approve / Resume).
- Guarantee Hero output is identical to Kanban's attention items for the same input (by construction — same derivation).

**Non-Goals:**
- Do not re-implement, extend, or override the four attention rules (owned by #164 / `issue-attention-derivation`).
- Do not implement Productivity content (issue G) — only a placeholder affordance.
- Do not add notifications, pushes, or toasts from mere surfacing.
- Do not change server/API/domain layers.

## Decisions

### D1. Placement: a dedicated widget composed into the Dashboard page
The Hero composes multiple entities (`issue`, `agent`), mutations, and routing — that is widget-tier in this codebase's layering (cf. `widgets/kanban-board`, `widgets/issue-workflow`). It will live at `packages/web/src/widgets/attention-hero/` with a public `AttentionHero` component. `DashboardPage` imports it and renders `<AttentionHero />` in place of `DashboardZonePlaceholder` for the `attention` slot only; the other three slots keep their placeholders.

- *Alternative considered:* colocate the component under `pages/dashboard/ui/`. Rejected because the Hero is a self-contained composition unit (data + actions + navigation) and widgets is the established home for that shape; colocating would also make the dashboard page directory hold non-page logic.

### D2. Data + state derivation — reuse, do not reinvent
```
const { data: agentStatus } = useAgentStatus()
const { data: issues } = useIssues()
const items = useMemo(() => deriveAttentionItems(issues ?? [], agentStatus), [issues, agentStatus])
const runnerDown = agentStatus?.runnerAvailable === false
const hasAttention = items.length > 0 || runnerDown
```
The Hero branches on `hasAttention`. `agentStatus === undefined` (loading) is treated as "runner available" (`runnerAvailable !== false`), matching the spec — no flappy Runner-down flash during initial load.

### D3. Per-item direct actions reuse existing mutations
Each entry always offers navigation to `issues/:number` via `useProjectPath`. Additionally:
- `label === 'Approval needed'` → `Approve` button wired to `useMutation({ mutationFn: () => approveIssue(item.issueNumber, projectId) })`.
- Other labels (`Interrupted` / `Needs action` / `Integration failed`) → `Resume` button wired to `resumeIssue(item.issueNumber, projectId)`.
- On success, invalidate `['issues']` and `['agent-status']` (the IssueCard pattern), so the Hero list and Kanban re-derive in lockstep.

- *Alternative considered:* delegate all actions to the issue detail page (links only). Rejected — the issue's user voice explicitly wants one-click Resume/Approve from the dashboard.

### D4. Runner-down entry is visually distinct and links to diagnostics
When `runnerDown`, the Hero renders a Runner-down entry separate from per-issue rows, showing `agentStatus.runnerMessage ?? 'No runner is connected.'` with a link to `toProjectPath('/activity')` — mirroring Kanban's `RunnerUnavailableBanner`. It renders even when `items` is empty (so a downed runner alone triggers the has-attention state).

### D5. All-clear state is copy-only
When `!hasAttention`, render an `All clear` message plus a short placeholder string pointing at the Productivity preview. No live Productivity content, no fetches.

### D6. Passive surface
No `useEffect` side effects on render; mutations fire only on explicit user action. Surfacing an item never mutates workflow state.

## Risks / Trade-offs

- **[Runner-down signal divergence]** Kanban's banner keys off `useRunnerSummary().hasConnectedCapacity`, while the Hero (per spec) keys off `agentStatus.runnerAvailable === false`. The two can momentarily disagree. -> *Mitigation:* follow the spec contract (`runnerAvailable === false`) for the Hero; record reconciling the two signals project-wide as an open question. Both ultimately derive from server-reported runner state, so divergence is transient, not contradictory.
- **[Hero Resume bypasses detail-page context]** Resume/Approve from the Hero skips the richer issue-detail surface (e.g. no Reject, no feedback prompt). -> *Mitigation:* acceptable — the Hero is a shortcut; the full decision flow remains on the detail page, reachable via the entry's navigation affordance.
- **[DashboardPage test churn]** The current `DashboardPage.test.tsx` asserts four placeholders including `dashboard-zone-attention`. Wiring in the Hero will break that assertion. -> *Mitigation:* update the test to assert the Hero renders in the attention slot and the other three placeholders remain; preserve the `data-testid="dashboard-zone-attention"` / `data-zone="attention"` hook on the Hero's root so zone-identity assertions stay meaningful.
- **[All-clear placeholder rot]** If issue G slips, the Productivity placeholder copy may linger. -> *Mitigation:* keep it to one short, clearly-placeholder sentence; no separate component to rot.

## Migration Plan

- Additive change behind the existing Dashboard route; no feature flag needed.
- Implementation order: (1) add `widgets/attention-hero` with unit tests (two-state rendering, per-item actions, runner-down); (2) wire `<AttentionHero />` into `DashboardPage` for the `attention` slot; (3) update `DashboardPage.test.tsx`.
- Validate: `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`; confirm the `dashboard-shell` + `dashboard-attention-hero` specs via `npx openspec validate issue-167 --strict`.
- Rollback: revert the `DashboardPage` wiring so the `attention` slot renders `DashboardZonePlaceholder` again. The widget can remain in-tree unused (no runtime cost unless imported).

## Open Questions

- Should the runner-down signal be unified across the Hero and Kanban banner (one hook, one truth) in a follow-up, or is the current `runnerAvailable` vs `hasConnectedCapacity` split acceptable?
- Should the Hero cap the visible item count (Kanban caps `NeedsAttentionSummary` at 6 with "+N more"), or show all items given the Hero is the primary attention surface?
- For `Approval needed` items, do we also want a secondary "open issue" affordance alongside Approve (to review before approving)? Default: yes — navigation is the primary affordance, Approve is the inline action.
