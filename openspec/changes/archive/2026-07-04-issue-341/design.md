# Design

## Context

The issue detail page (`packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`) currently renders every block — runtime decision surface, branch bar, workflow view, PR delivery, profile editor, diff banner, description, diff files, commits, comments, and a dozen right-rail cards — as one undifferentiated vertical stack inside a single centered `max-w-4xl` column, with a `lg:grid-cols-3` content grid only at the bottom. All blocks carry equal visual weight, so answering "where is this issue right now?" requires scrolling through many same-weight cards.

This change is a **layout reorganization only**. It builds on issue #340, which already converged:

- A single runtime reducer, `deriveRuntimeDecision` (`widgets/issue-workflow/model/derive-runtime-decision.ts:623`), producing one `{ summary, headline, currentTask, stage progress, actions, ... }` decision.
- A single action surface, `RuntimeDecisionSurface`, owning all workflow write controls (approve / send-back / stop / start / retry / resume / rerun), with `WorkflowView` mounted read-only.
- A unified visual substrate (`CardSection`, semantic tokens) and a badge-grouping rule (identity badges vs. exactly one runtime pill in `status-badges-runtime`).

No server, runner, CLI, API, DTO, or query changes are involved. The reorganization consumes the *existing* projections verbatim (`deriveRuntimeDecision`, `issue.workflowStageProgress`, the workflow timeline, diff/commits queries). Block components are **repositioned, not rewritten**.

**Constraints / stakeholders:**
- The page has many conditional paths (archived, backlog readiness, draft, drift, convergence, interrupted health, PR delivery, capacity gating) — all must remain correct under the new layout.
- jsdom cannot lay out, so stickiness cannot be tested via real scroll offsets; the established pattern (`SessionDetailShell.tsx:289`, `SessionPage.sticky.test.tsx:445`) is a `data-sticky="true"` attribute + class assertion.
- Issue #340's review closed with open items (see Risks). This issue does **not** redo #340's action-surface/color convergence; it inherits those items.

## Goals / Non-Goals

**Goals:**
- Reshape the flat stack into three attention-graded tiers: a **sticky status headline** (heaviest), a **reading flow** main column (medium), and a **reference rail** (lightest).
- Aggregate the current runtime situation, stage + progress, and current task title into one glanceable, pinned region.
- Anchor the runtime decision/action surface in the status-header tier (above the reading flow), never in the flow or the rail.
- Order the reading flow by attention: workflow progress & outputs → changes/diff → commits → description → comments.
- Limit the reference rail to metadata + low-frequency config, with drift and convergence collapsed by default.
- Enforce the single-runtime-badge invariant on the identity block.
- Preserve all existing block behavior and `data-testid` anchors (renaming only where a tier needs its own anchor).

**Non-Goals:**
- Mobile-only affordances (bottom action bar / drawer) — next issue.
- Changing any block's underlying data source or query.
- Introducing new blocks, content, charts, or panels.
- Redoing #340's action-surface convergence, stop semantics, or palette tokenization (its open review items are carried forward, not fixed here).
- Rewriting block internals (e.g. `WorkflowView`, `TaskProgressPanel`) — they move as-is.

## Decisions

### D1. Three tiers via layout, not new state

Introduce the tiers purely through layout position, width, and chrome — no new data, no new state semantics. This keeps the change in the Web tier-1 display-surface band (`design/conventions.md` placement table) and respects the execution-fact-vs-adjudication separation (`design/architecture.md`): the page consumes projections, it does not adjudicate.

**Alternatives considered:**
- *New "page mode" state driving layout.* Rejected — adds state for a pure presentation concern.
- *Server-side section ordering.* Rejected — ordering is a presentation concern; violates placement rules.

### D2. Layer assignment (the core decision)

Each existing block is assigned to exactly one tier. Identity/runtime-status badges stay in the header's identity block per #340.

| Block | Current location | Target tier | Notes |
|---|---|---|---|
| Identity block (#number, priority, draft/archived, title, labels, epic, timestamps, edit/activity) | header | **Status header — identity row** | keeps at most one `RuntimeSummaryPill` |
| `RuntimeSummaryPill` | `status-badges-runtime` | **Identity row** (the single badge) | see D5 |
| New `StatusHeadline` (situation + stage + progress + current task) | — | **Status header — sticky region** | heaviest tier; see D3 |
| `RuntimeDecisionSurface` (all 7 runtime actions) | own frame | **Status header tier** (below headline, not sticky) | see D4 |
| `BranchBar` | between surface & workflow | **Reading flow** (leads workflow block) | stage/rebase = progress context |
| `WorkflowView` (read-only) | own frame | **Reading flow** | workflow progress |
| `PrDeliverySummary` | own frame | **Reading flow** | delivery output |
| `TaskProgressPanel` | rail "Runtime/Sessions" | **Reading flow** | task progress (dissolve the wrapper) |
| `WorkflowSessionsPanel` | rail "Runtime/Sessions" | **Reading flow** | runtime evidence; preserves #340's "supporting evidence beneath the surface" invariant |
| `LatestArtifactsPanel` | rail | **Reading flow** | outputs |
| `WorkflowYamlDialog` trigger | main column | **Reading flow** (near workflow) | unchanged trigger |
| diff-summary-banner | above content grid | **Reading flow** (above diff files) | change context |
| `IssueDiffFilesSection`, `IssueCommitsSection`, `IssueDescriptionSection`, `IssueCommentsSection` | main column | **Reading flow** (in spec order) | |
| `IssueDetailsCard` | rail | **Reference rail** | details metadata |
| `IssueConfigurationCard` (model + prerequisites edit) | rail | **Reference rail** | model config |
| `WorkflowProfileControl` | rail | **Reference rail** | workflow-profile control |
| `IssuePrerequisitesCard`, `IssueReadinessCard` | rail | **Reference rail** | prerequisites / readiness metadata |
| `IssueDriftCard` | rail | **Reference rail — collapsed** | low-frequency |
| `WorkflowConvergencePanel` | rail | **Reference rail — collapsed** | low-frequency |
| `IssueActionsCard` (Mark ready / Close / Ask Agent / archived note / draft readiness) | rail | **Reference rail** | non-runtime-flow actions; does not overlap the 7 runtime actions, so it is not bound to the header tier |

**Alternatives considered:**
- *Move `IssueActionsCard` into the header tier.* Rejected — its contents (Mark ready, Close, Ask Agent) are not in the runtime-action set the spec binds to the header; keeping it in the rail avoids a tall sticky region and preserves #340's "right-rail Actions card is limited to non-overlapping actions."
- *Keep `WorkflowConvergencePanel` in the reading flow.* Rejected — the reference-rail spec explicitly classifies convergence as low-frequency and collapsed by default.
- *Fold "Workflow Interrupted" into the rail.* See Open Questions; the headline's `blockedReason` already surfaces interrupted health, so the standalone card is redundant and is removed (its text is not a data source, just a static note duplicating the reducer's blocked rationale).

### D3. Sticky `StatusHeadline` — compact and pinned

Introduce a new presentation component (`StatusHeadline`) that renders, in one cohesive region: the adjudicated `decision.summary` (with icon + tone), the current stage name and `completed/total` progress from `workflowStageProgress`, and `decision.currentTask.title`. It is the **only** sticky element, pinned with `sticky top-0 z-20` plus a `data-sticky="true"` attribute, mirroring `SessionDetailShell.tsx:289-290`.

When no stage/progress exists (e.g. backlog), the headline shows the situation alone without fabricating a stage figure (per spec). When no runtime decision applies (archived done), it reflects `done` and offers no active controls.

Heaviest visual weight is achieved through: stickiness (constant presence), a semantic fill keyed off the summary tone (`bg-info-subtle` / `bg-warning-subtle` / `bg-danger-subtle` / `bg-success-subtle`, reusing the existing `runtimeSummaryPresentation` token map in `pills.tsx:18-49`), an icon, and a bottom border. No other tier uses a fill + sticky + border combination.

**Alternatives considered:**
- *Pin the entire `RuntimeDecisionSurface` too.* Rejected — the surface carries forms (send-back body, stop confirmation) that would make the pinned region too tall, defeating "1-second glanceable."
- *Sticky with a JS scroll listener.* Rejected — CSS `sticky` needs no listener and avoids re-render/scroll-jank; also avoids time/async test concerns.

### D4. Action surface anchored in the header tier, but not itself sticky

`RuntimeDecisionSurface` stays in the status-header **tier** (rendered directly beneath `StatusHeadline`, above the reading flow) so the spec's "anchored within the status-header tier" holds, but it is **not** given `sticky`. It scrolls away naturally once the user has seen the actions. This bounds pinned height to the compact headline while keeping all 7 runtime actions above the reading flow and out of the rail. Mutations continue to be created once via `useIssueDetailMutations` and shared; `WorkflowView` stays read-only.

**Alternatives considered:**
- *Move the surface into the reading flow.* Rejected — violates the spec's tier anchoring.
- *Inline only the primary action into the sticky headline, full surface below.* Considered; deferred (see Open Questions) to keep the first cut simple and avoid duplicating action wiring.

### D5. Resolve the duplicate runtime state (#340 item-7)

Today the runtime situation is rendered in three places: the header `RuntimeSummaryPill`, the surface's `runtime-summary-label`, and (after D3) the headline. To honor "single glanceable status region" + "at most one runtime badge":

- The **identity row keeps exactly one** `RuntimeSummaryPill` (the lightweight badge permitted by the invariant).
- The **`StatusHeadline` is the single heavy status region** (situation + stage + task).
- `RuntimeDecisionSurface` **drops its own summary label/icon row** and defers situation display to the headline directly above it; it keeps rationale, next-action, drift-note, wait-reason, and the action buttons.

This removes the duplicate same-weight indicator without touching the reducer.

**Alternatives considered:**
- *Drop the identity-row pill entirely (zero badges).* Rejected — the spec permits "at most one," and the pill gives identity context when the headline is scrolled out of view on very tall pages; keeping one is the more conservative reading.

### D6. Reading flow — widest column, lightest chrome

Desktop uses a two-column layout: the reading flow takes the larger share (keep the existing `lg:grid-cols-3` with flow at `lg:col-span-2`) and the rail the narrower `lg:col-span-1`. Reading-flow blocks use content-forward chrome — no `CardSection` borders, no heavy fills — so attention rests on content. Long blocks (`IssueDescriptionSection`, `IssueDiffFilesSection`) stay collapsible and preserve a key signal when collapsed (description presence + leading text; file/addition/deletion counts), per spec.

The weight delta between tiers is expressed through **saliency**, not literal border thickness: headline (sticky + fill + icon) > reading flow (widest, content-dense, calm chrome) > rail (narrow, muted, low-frequency, partly collapsed).

**Alternatives considered:**
- *A wider custom flex split (e.g. 3:1).* Rejected — `lg:grid-cols-3` is already established, tested, and keeps `min-w-0` overflow handling intact (relied on by the repository-metadata containment tests).
- *Strip `bg-card` fills from all flow blocks.* Partial — drop heavy fills/borders where the block is purely content (description, comments); keep minimal treatment for structured summaries. Final per-block chrome is settled in tasks against the "lighter than rail" assertion.

### D7. Reference rail — narrow column desktop, collapsed sections narrow screen

On desktop the rail is the right `lg:col-span-1` column. On narrow screens it renders as stacked collapsible sections beneath the reading flow (no right column). Drift and convergence are collapsed by default (`data-collapsed="true"`, expand on click). Rail cards keep the `CardSection` substrate and muted tokens so they recede. `IssueActionsCard` remains in the rail; its runtime-non-overlapping actions (Mark ready, Close, Ask Agent) stay reachable on narrow screens via the collapsed-section form.

**Alternatives considered:**
- *Collapse the whole rail into a drawer on narrow screens.* Rejected — drawer is a mobile affordance explicitly scoped to the next issue.

### D8. Test strategy — update in place, sticky via attributes

- Update the existing `IssueDetailPage.{main,readiness,capacity-gating,archived}.test.tsx` **in place** to the new anchors/ordering (no old+new duplication, per `design/testing.md`). Stable `data-testid`s are preserved; new anchors added: a status-headline anchor (e.g. `status-headline`), `data-sticky="true"`, and per-tier container anchors.
- **Stickiness** is asserted via `data-sticky="true"` + `className` contains `sticky` + the headline being the first child of the scroll container (the `SessionPage.sticky.test.tsx:445` pattern). No `getBoundingClientRect`-scroll assertions (jsdom does not lay out; existing `rect.top` ordering assertions only work because they compare zeros and really assert document order — those remain valid for *order*, not for *pinning*).
- **Reading-flow ordering** is asserted with `compareDocumentPosition` between blocks (workflow < diff < commits < description < comments).
- **Rail default-collapse** is asserted by drift/convergence bodies being absent (or `data-collapsed="true"`) until toggled.
- **Single-badge invariant** is asserted: exactly one `runtime-status-pill` in the identity row; the surface no longer renders a duplicate summary label.
- No real external deps (keep mocking `entities/issue`, `entities/agent`, `entities/settings`); no real time.

## Risks / Trade-offs

- **[Layout reshuffle regresses a conditional path (archived / backlog / drift / convergence / interrupted / PR delivery / capacity)]** → Mitigation: reuse every block component unchanged; keep all existing `data-testid` anchors stable; re-assert every conditional path against its new tier in the updated specs. The `IssueDetailPage.archived.test.tsx` and `capacity-gating.test.tsx` suites are the regression sentinels.
- **[Tall sticky header eats the viewport on small desktops]** → Mitigation: only the compact `StatusHeadline` is pinned (D3/D4); the action surface scrolls. Cap headline height; the pinned region never contains forms.
- **[Narrow-screen rail collapse buries Mark-ready / Close / Ask Agent]** → Mitigation: the 7 runtime actions live in the always-visible header tier, not in the collapsed rail; the remaining `IssueActionsCard` actions are non-blocking. Flag for the mobile-affordance issue.
- **[#340 open items inherited — `readOnly` blocks evidence expansion (item-5); raw palette in `BranchBar`/`TaskProgressPanel` (item-6)]** → Out of scope (Non-Goal). Carried forward; documented so the next issue can pick them up. The reading-flow relocation does not worsen them.
- **[Duplicate runtime state (#340 item-7) could regress to triplication]** → Resolved by D5 (headline is the single heavy region; surface drops its label; identity keeps one pill).
- **[Removing the standalone "Workflow Interrupted" card loses a user signal]** → Mitigation: `deriveRuntimeDecision` already classifies interrupted health into `blocked` with a rationale shown in the headline/surface; the removed card was static text duplicating that. If the reducer's text proves insufficient, restore as a rail card (Open Questions).
- **[Stickiness depends on the app shell's top offset]** → Mitigation: confirm the scroll container is `issue-detail-page-container` (the page scrolls inside it, not the window); set `top-0` relative to that container. Verify against the shell in tasks.

## Migration Plan

This is a web-only, single-PR change with **no data, API, or persistence migration**.

1. Introduce `StatusHeadline` (presentation component) consuming `decision` + `workflowStageProgress`.
2. Restructure `IssueDetailPage.tsx` into the three tiers per the D2 table; move blocks without rewriting them; apply D5 (drop the surface summary label).
3. Apply chrome/weight treatment per D3/D6/D7; add `data-sticky`, tier anchors, and collapse state.
4. Update the four `IssueDetailPage.*.test.tsx` files in place; add the new spec assertions (ordering, stickiness, default-collapse, single-badge).
5. Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`; ensure `TreatWarningsAsErrors`-equivalent lint is clean.

**Rollback:** revert the PR. No persistent state is written by this change, so revert is clean with no data remediency. No feature flag is required given the single-PR, self-contained scope.

## Open Questions

- **Inline primary action in the sticky headline?** D4 keeps the full surface non-sticky for simplicity. A future iteration could inline the single primary action (e.g. Approve / Start) into the pinned headline for one-tap access; deferred to avoid duplicating action wiring in the first cut.
- **`WorkflowSessionsPanel` tier.** Placed in the reading flow as runtime evidence (preserving #340's "supporting evidence beneath the surface"). If it proves too noisy in the flow, it can move to the rail — decision revisitable in review.
- **"Workflow Interrupted" standalone card.** Proposed for removal (subsumed by the reducer's blocked rationale in the headline). Confirm during implementation that the reducer's wording is sufficient; if not, keep it as a collapsed rail item.
- **Exact per-block chrome in the reading flow.** D6 settles the principle (lightest, content-forward); the precise fill/border per block is finalized against the "lighter than rail" spec assertion during tasks.
- **Reading-flow width split.** `lg:col-span-2` of 3 is the default; confirm it gives the flow enough width for the (large) read-only `WorkflowView` without horizontal crowding.
