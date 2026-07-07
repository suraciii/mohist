## Context

The Web UI exposes the same production state (issue health, workflow run/stage
status, approval, runner state, severity) through **several parallel color
systems that disagree and ignore dark mode**. This issue unifies them onto the
existing semantic-token palette so the rest of the epic (dashboard, board,
issue detail, activity, session) has one visual baseline to build on.

### Current state (verified by code reading)

**Token infrastructure exists and works.** `app/styles/index.css` defines four
semantic families — `success` / `warning` / `info` / `danger` — each exposing
`-subtle` / `-foreground` / `-border` (root values lines 84–99, `@theme inline`
map lines 29–44, dark overrides lines 135–150). Tailwind v4 CSS-first config
turns these into utilities (`bg-success-subtle`, `text-danger`, etc.) with no
JS `tailwind.config.*`. ~100 call sites already consume them
(`StageStatusIcons`, `failure-panels`, `InlineApproval`, `WorkflowConvergencePanel`,
`TaskItem`, `FeedbackHistory`, `StageBar`, …). **The gap is adoption and missing
primitive variants, not the token system.**

**Three structural defects block unification:**

1. **Divergent per-widget color maps** author status color independently and
   disagree on hue for the *same* state:
   - `entities/issue/lib/status-badge.ts` — raw palette (`green/amber/red/orange/gray`).
   - `widgets/issue-workflow/ui/WorkflowRunStatusPill.tsx:19-119` — 10 per-status
     constants across 5 hue families (violet/cyan/blue/emerald/slate); the `dot`
     field is dead (render uses `currentColor`).
   - `widgets/kanban-board/ui/IssueCard.tsx` — **two parallel systems**: the
     rendered `StatusPill` (lines 98–185, raw palette per indicator) and the
     exported `STATUS_PILL_PAIRS` hex map (lines 76–83, consumed *only* by the
     contrast test, never by the rendered pill).
   - `widgets/runner-status/ui/RunnerList.tsx:11-16` (`STATUS_CONFIG`, `emerald`)
     vs `RunnerSummary.tsx:76-86` (`green`) — idle renders as two different hues.
   - `widgets/session-health/ui/ContextHealthIndicator.tsx` (green dot is
     `bg-gray-400`) vs `ContextHealthBar.tsx` (green is `bg-green-500`).
   - `shared/ui/StatusBar.tsx:11-16`, `shared/lib/log-levels.ts`,
     `widgets/dashboard-pulse/ui/CompactSessionCard.tsx:7-13` (its own local
     `STAGE_COLORS`), `widgets/attention-hero/ui/AttentionHero.tsx`,
     `widgets/issue-workflow/ui/{TaskProgressPanel,ReviewReportModal,ReviewSummary}.tsx`,
     `widgets/kanban-board/model/stage-colors.ts` (inline hex `accent` + raw palette
     `labelClass`/`activeBg`/`activeBorder`), `widgets/issue-event-timeline/`
     (`bg-red-500`/`bg-amber-500` markers).

2. **Primitives lack semantic variants.** `Badge` (`shared/ui/components/badge.tsx`)
   and `Button` (`shared/ui/components/button.tsx`) expose only a soft
   `destructive` variant; no `success`/`warning`/`info`/`danger`. `--destructive`
   is a **bare token** (line 83) with no `-subtle`/`-foreground`/`-border`
   siblings, unlike the four families. `AlertDialog` (lines 49–51) sets
   `variant="destructive"` *and* overrides it with `bg-red-600 text-white
   hover:bg-red-700`. `FieldError` hardcodes `text-red-700`.

3. **Token hue drift.** `--warning` light-theme values span three hues: base 70,
   `-subtle` 80, `-border` 75 (`index.css:88-91`). Dark theme is already
   consistent at 75.

4. **Registries hold inline hex.** `shared/lib/label-colors.ts` — `PRIORITY_COLORS`,
   `PRIORITY_STRIP_COLORS`, `RISK_COLORS`, `TYPE_LABEL_COLORS`, `TYPE_STRIP_COLORS`,
   `AREA_LABEL_COLORS`, `URGENCY_LABEL_COLORS` — all inline `#rrggbb`, consumed via
   inline `style`, invisible to dark theme.

### Constraints / stakeholders

- **Web-only** (`packages/web/src`). No server / runner / CLI / API / domain
  behavior change. No new design-system dependency.
- **Risk: medium.** Touches shared presentation across many surfaces; visual
  regressions can cascade. Mitigated by routing through one shared layer and by
  a cross-surface equivalence spec.
- **In scope:** diff/added-removed line coloring (green/red) is explicitly
  **out** of scope (conventional line-level convention, not state).
- Product terms (issue, workflow, stage, health, approval, runner, artifact,
  session, epic) and existing ARIA contracts (`role="alert"` / `role="status"`
  / `aria-live`) are preserved — only color/visual treatment changes.

## Goals / Non-Goals

**Goals:**

- **G1:** One shared status-presentation layer maps every covered domain state
  to exactly one semantic-token treatment; every status pill/badge/dot/marker
  on the covered surfaces resolves through it.
- **G2:** `Badge` and `Button` expose token-backed `success`/`warning`/`info`/
  `danger` variants; destructive is dark-mode-aware and not shadowed by raw-red
  overrides.
- **G3:** Status colors are reserved strictly for state meaning; the `success`
  hue family is the *only* hue family for completed/done.
- **G4:** Priority/risk/label/log-level/stage-accent registries are dark-mode-
  aware and free of inline hex.
- **G5:** Covered panels express action color through `Button` variants only —
  no bespoke `border-amber-300`/`border-slate-300` color overlays.
- **G6:** The contrast test asserts against the *rendered* treatment (not a
  divergent hex map), and a cross-surface equivalence spec catches future drift.

**Non-Goals:**

- No full-page redesign of dashboard/board/issue-detail/activity/session/files/diff.
- No workflow/issue/runner/epic/approval semantic change.
- No decorative skin or new design-system dependency.
- No recoloring of diff added/removed lines or session-transcript line coloring.

## Decisions

### D1 — One shared status-presentation layer at `shared/status-presentation/`

**Decision.** Add `packages/web/src/shared/status-presentation/` exposing:

- `statusTreatment(kind, state)` → `StatusTreatment` — a frozen record of class
  strings (`{ container, text, border, dot }`) drawn entirely from token
  utilities. `kind` ∈ `{ issue-health, workflow-run, workflow-stage, approval,
  runner, severity }`; `state` is the domain value for that kind.
- Internally composed of two pure functions:
  - `familyFor(kind, state)` → `'success' | 'warning' | 'info' | 'danger' | 'muted'`
    — the single semantic-family source of truth.
  - `TREATMENT_BY_FAMILY` — a fixed map family → class set (the only place that
    names token utilities for status). This is what makes "dot tracks pill
    family" structural rather than per-call-site.
- A thin `<StatusPill>` component (Badge + optional dot, optional icon via
  `StageStatusIcons`) for the most common pill shape; widgets that need bespoke
  layout consume `statusTreatment()` directly.

**Rationale.** The layer spans multiple domains (issue/workflow/runner/severity),
so it cannot live in one entity folder. `shared/` is the natural home. Splitting
`familyFor` from `TREATMENT_BY_FAMILY` gives the cross-surface equivalence spec
(D8) a single value to assert on (`familyFor(...)`) while letting the visual
treatment evolve in one place. Keeping `StageStatusIcons` as the icon resolver
avoids duplicating icon logic in the layer.

**Alternatives considered.**

- *A1 — Extend `entities/issue/lib/status-badge.ts` to cover all kinds.*
  Rejected: it is named for one entity and already carries the wrong abstraction
  (a flat class string). Promoting it would entrench an entity-scoped name for a
  cross-domain concern.
- *A2 — Return only the family and let each consumer build classes.* Rejected:
  the spec requires "the treatment (background, foreground/text, border, dot, and
  icon class set)" to come from the layer; pushing class authoring back into
  widgets reopens the drift this issue closes.
- *A3 (chosen) — Layer returns the treatment record, internally derived from one
  family map.* Single source for both family and treatment.

### D2 — Fixed semantic-family reservation per state

**Decision.** Pin one family per state. The reservation (states not listed in
the spec reservation are resolved here and fed back into D1's `familyFor`):

| Kind | State | Family |
|------|-------|--------|
| workflow-run / workflow-stage | `running` | `info` |
| workflow-run | `ready` | `info` |
| workflow-run | `pending`, `created`, `stopped`, `unknown` | `muted` |
| workflow-run / workflow-stage | `awaiting-approval` | `warning` |
| workflow-stage | not-started | `muted` |
| issue-health | `active` | `info` |
| issue-health | `paused` | `muted` |
| issue-health | `blocked` | `danger` |
| issue-health | `interrupted` | `warning` |
| issue-health | `cancelled`, `done`(*) | see below |
| workflow-run / issue-health | `drift` | `warning` |
| workflow-run / workflow-stage / issue-health | `completed` / `done` | `success` |
| workflow-run / workflow-stage | `failed` | `danger` |
| approval | `awaiting` | `warning` |
| approval | `approved` | `success` |
| approval | `rejected` | `danger` |
| runner | `idle` | `success` |
| runner | `busy` | `info` |
| runner | `stale` | `warning` |
| runner | `offline` | `muted` |
| severity / log-level | `ERROR` | `danger` |
| severity / log-level | `WARN` | `warning` |
| severity / log-level | `INFO` | `info` |
| severity / log-level | `DEBUG` | `muted` |
| any | `cancelled` | `muted` |

`done` (issue-health) → `success`; `cancelled` (issue-health/workflow) → `muted`.

Unspecified-by-spec resolutions (called out for review in Open Questions):
`active` → `info` (shares meaning with `running`: in-progress, healthy — success
is reserved for terminal completion); `ready` → `info`; `pending`/`created`/
`stopped`/`paused` → `muted`.

**Rationale.** The spec reserves `success` exclusively for completed/done and
forbids the `info` family for any non-running state. `active` cannot be `success`
(would collide with `done`) and cannot be `warning`/`danger` (not blocking) —
`info` is the only family whose meaning ("in-progress, healthy") fits.
`idle`→`success` is mandated by the spec (healthy/available).

**Alternatives considered.**

- *A1 — `active` → `success`.* Rejected: violates the success-only-for-done
  reservation; an active issue is not terminal.
- *A2 — `active` → `muted`.* Rejected: visually demotes healthy in-progress
  issues to the same treatment as unknown/cancelled, weakening the primary
  "everything is fine" signal.

### D3 — `Badge` and `Button` gain token-backed semantic variants; `destructive` is aliased

**Decision.** Extend `badgeVariants` and `buttonVariants` cva with `success`,
`warning`, `info`, `danger`. Each is soft-tinted and token-backed, mirroring the
existing `destructive` shape: `bg-<family>-subtle text-<family>-foreground
border-<family>-border` (dark-theme tokens already exist). The `destructive`
variant is redefined to the `danger` treatment (alias) so destructive and danger
never diverge; `--destructive` stays as a CSS var for any legacy reference but
primitives no longer read it. Remove the `bg-red-600 text-white hover:bg-red-700`
override in `AlertDialog` (lines 49–51) — destructive confirmation uses the
`destructive` variant alone. `FieldError` routes through the `danger` token.

**Rationale.** Soft-tinted variants match the existing `destructive` aesthetic
(production-tool, not marketing), are dark-mode-aware by construction, and keep
the four families as the single color source. Aliasing destructive→danger avoids
a sixth parallel family (`--destructive`) that already lacks `-subtle`/`-border`
siblings.

**Alternatives considered.**

- *A1 — Solid semantic variants (`bg-success text-success-foreground`).*
  Rejected: inconsistent with the established soft `destructive`/`secondary`
  aesthetic; visually loud for a dense operational UI.
- *A2 — Promote `--destructive` to a full family.* Rejected: adds a parallel
  red family alongside `danger` for no semantic gain; aliasing is strictly
  simpler.

### D4 — Collapse divergent helpers into the shared layer

**Decision.** Remove or rewire every per-widget status-color author:

- `entities/issue/lib/status-badge.ts` — `statusBadge()` becomes a **thin
  delegate** to `statusTreatment('issue-health', health)` (keeps the entry point
  for minimal call-site churn; new code calls the layer directly). `statusLabel()`
  is unchanged.
- `widgets/issue-workflow/ui/WorkflowRunStatusPill.tsx` — delete the 10 per-status
  constants and `PRESENTATION_BY_STATUS`; the component reads
  `statusTreatment('workflow-run', status)` and keeps only label/icon mapping.
- `widgets/kanban-board/ui/IssueCard.tsx` — delete `STATUS_PILL_PAIRS` (hex map)
  **and** the branch-per-indicator styling inside `StatusPill`; `StatusPill`
  consumes `statusTreatment(...)` for the indicator it renders. The contrast test
  (D8) follows.
- `widgets/runner-status/ui/RunnerList.tsx` — delete `STATUS_CONFIG`; dot and
  badge resolve via `statusTreatment('runner', state)`. `RunnerSummary.tsx`
  consumes the same layer → `emerald`/`green` divergence disappears.
- `widgets/session-health/ui/ContextHealthIndicator.tsx` and `ContextHealthBar.tsx`
  — green/yellow/red maps replaced by `statusTreatment(...)`; the (deliberate)
  "healthy is quiet" intent is preserved by mapping healthy→`success` with the
  *soft* treatment rather than reintroducing `bg-gray-400`.
- `shared/ui/StatusBar.tsx`, `shared/lib/log-levels.ts`,
  `widgets/attention-hero/ui/AttentionHero.tsx`,
  `widgets/issue-workflow/ui/{TaskProgressPanel,ReviewReportModal,ReviewSummary}.tsx`,
  `widgets/issue-event-timeline/` (failure/attention markers),
  `widgets/dashboard-pulse/ui/CompactSessionCard.tsx` (status-bearing surfaces
  only) — each resolves through the layer.
- `widgets/issue-workflow/ui/StageStatusIcons.tsx` — **unchanged** (already
  token-backed reference component); the layer references it for icon resolution.

Product terms and ARIA contracts on all rewritten surfaces are preserved
verbatim.

**Rationale.** The spec requires that no widget "author status color decisions
of its own." Delegating (rather than deleting) `statusBadge()` minimizes churn
for its call sites while still removing its color authority; the divergent maps
that have no external callers (`STATUS_PILL_PAIRS`, `STATUS_CONFIG`,
`PRESENTATION_BY_STATUS`) are deleted outright since they are the drift source.

**Alternatives considered.**

- *A1 — Delete `statusBadge()` and update all call sites.* Rejected as primary:
  larger blast radius for no spec gain (the spec allows delegation). Revisit if
  call sites are few.
- *A2 — Keep `STATUS_PILL_PAIRS` as the contrast fixture.* Rejected: it already
  disagrees with the rendered pill (the bug D8 fixes).

### D5 — Fix the `--warning` hue drift

**Decision.** In `app/styles/index.css`, normalize the light-theme `--warning`
family to one hue (75, matching the existing `-border` and the dark-theme
values): align `--warning` (currently hue 70) and `--warning-subtle` (currently
hue 80) to hue 75, keeping their respective lightness/chroma. This is the only
token-value change in the issue; the other three families are already
hue-consistent.

**Rationale.** The spec's "token hues are internally consistent" requirement is
structural — a single state must not shift hue between background, border, and
foreground. Lightness/chroma may still vary within a family.

### D6 — Registries off inline hex; risk and type colors route through families

**Decision.** Convert `shared/lib/label-colors.ts` registries:

- **Risk** (`RISK_COLORS`): route through semantic families — `low`→`success`,
  `medium`→`warning`, `high`→`danger` (spec-mandated). Return class strings, not hex.
- **Type** (`TYPE_LABEL_COLORS`, `TYPE_STRIP_COLORS`): the five type hues already
  align with semantic families — `bug`→`danger`, `feature`→`success`,
  `enhancement`→`info`, `tech-debt`→`muted`, `performance`→`warning`. Route through
  families; strips reuse the same family foreground.
- **Priority** (`PRIORITY_COLORS`, `PRIORITY_STRIP_COLORS`): priority is *ordinal*,
  not state-meaningful — forcing five priority steps onto four semantic families
  would either collide or demote the meaning reservation. Keep a **documented
  light/dark-aware palette** (class strings with `dark:` variants, no inline hex),
  preserving the current ordinal hues (red/orange/yellow/green/gray) in both themes.
- **Area / urgency** (`AREA_LABEL_COLORS`, `URGENCY_LABEL_COLORS`): documented
  dark-aware palette (these are categorical labels, not state).
- **Kanban stage accent** (`widgets/kanban-board/model/stage-colors.ts`,
  `STAGE_COLORS` keyed by `IssueStatus`): this *is* status-bearing — route through
  semantic families (Backlog→`muted`, InProgress→`warning`, Done→`success`,
  Cancelled→`danger`). Drop the inline-hex `accent` consumed by inline `style`;
  render the accent via a token class instead. `labelClass`/`activeBg`/`activeBorder`
  become the family's soft treatment.
- **CompactSessionCard stage identity** (`CompactSessionCard.tsx:7-13`, keyed by
  stage *name* build/plan/review/check/integrate): categorical identity, not state
  — keep a documented dark-aware palette (no inline hex), not semantic families.

All inline `style={{ backgroundColor: '#…' }}` / `style={{ color: '#…' }}`
consumption in the covered surfaces is replaced by class-based treatment.

**Rationale.** The spec's "reserved strictly for state meaning" rule means
semantic families must not absorb non-state concepts. Priority and categorical
stage identity stay on a separate (still dark-aware, still hex-free) palette so
the status families keep their meaning contract. Risk and type *do* carry state
meaning, so they join the families.

**Alternatives considered.**

- *A1 — Collapse priority onto semantic families (p0→danger, p1→warning, …).*
  Rejected: overloads the meaning reservation (priority p3 is not "healthy", yet
  would render `success`).
- *A2 — Introduce a fifth "stage-identity" semantic family.* Rejected: adds a new
  family the spec doesn't ask for and complicates the meaning contract.

### D7 — Standardize action buttons through `Button` variants

**Decision.** Remove bespoke color/border-color className overlays from:

- `widgets/workspace/ui/WorkspacePanel.tsx` (rebase-behind currently
  `border-amber-300 bg-amber-50 text-amber-800`, default/secondary currently
  `border-gray-300 bg-white text-gray-700`) — rebase-behind → `variant="warning"`,
  default/secondary → `variant="outline"`. Result banners reuse the
  success/danger/info families via `Badge`/token classes.
- `widgets/issue-workflow/ui/BranchBar.tsx` (same overlay pattern, amber/gray) —
  same mapping.
- `widgets/issue-workflow/ui/TaskLogPanel.tsx` — the `slate` + `sky` dialect
  (used nowhere else) is replaced by `outline`/`secondary` variants and the
  standard `--ring` focus ring; only the documented dark-terminal palette for
  the terminal body itself stays.

Panels may still express layout (size, width, padding, gap) through `className`
but not color or border-color.

**Rationale.** The spec mandates action color be expressed through variants
alone. `warning`/`outline`/`secondary` cover the three states (needs-attention,
secondary, tertiary) without bespoke classes; the `slate`+`sky` dialect is an
accidental local divergence from the rest of the app.

**Alternatives considered.**

- *A1 — Keep rebase-behind as `outline` + a warning icon.* Rejected: the spec
  wants the *color* treatment expressed through the variant; an icon-only cue is
  weaker than the current amber frame and would regress scannability.

### D8 — Tests assert the rendered treatment and cross-surface equivalence

**Decision.** Two test tracks:

1. **Contrast (updated).** `widgets/kanban-board/ui/StatusPill.contrast.test.ts`
   is rewritten to compute contrast from the treatment `statusTreatment(...)`
   actually resolves, for each covered indicator (`blocked`, `cancelled`,
   `approval`, `running`, `waiting`, `drift`, plus runner/severity states), in
   both light and dark theme, asserting WCAG AA ≥ 4.5:1. `STATUS_PILL_PAIRS` is
   deleted. Contrast is computed from a **JS token fixture**
   (`shared/status-presentation/tokens.ts`) that mirrors the `index.css` values;
   a guard unit test asserts the fixture stays in sync with `index.css` (parse
   the CSS for the four families' `-subtle`/`-foreground`/`-border` values and
   compare), so a token edit that breaks the fixture fails a test rather than
   silently breaking contrast.
2. **Cross-surface equivalence (new spec).** A colocated spec asserts that
   `familyFor(kind, state)` returns the same family regardless of which widget
   renders it, covering at minimum: workflow-run status (rendered by
   `WorkflowRunStatusPill` and `StatusBar`), runner state (`RunnerList` and
   `RunnerSummary`), issue health (`statusBadge()` delegate and `IssueCard`),
   and context health (`ContextHealthIndicator` and `ContextHealthBar`). The test
   asserts on `familyFor` (the single source), not on class strings, so it is
   robust to treatment-class evolution.

Both are colocated unit/spec tests per `design/testing.md` (web track); no
e2e/a11y pipeline is added.

**Rationale.** The current contrast test passes against a hex map that diverges
from the rendered DOM — the exact drift this issue removes. Asserting on
`statusTreatment()`'s output closes the gap; the fixture+guard keeps the test
deterministic and fast (no jsdom oklch→rgb computation) while still bound to the
real token values. Asserting equivalence on `familyFor` (not on classes) makes
the spec resilient to future treatment edits while still catching any widget
that reintroduces a divergent hue.

**Alternatives considered.**

- *A1 — Compute contrast by rendering in jsdom and reading computed style.*
  Rejected: jsdom does not reliably resolve `oklch()` CSS vars to RGB; the test
  becomes flaky and platform-dependent.
- *A2 — Assert equivalence on full class strings.* Rejected: brittle to
  treatment evolution; the spec's intent is "same family", not "same string".

## Risks / Trade-offs

- **[Large touch surface]** The issue rewrites color across ~15 widgets.
  -> Mitigated by routing through one layer (D1) and by the cross-surface
  equivalence spec (D8) that catches per-widget drift at test time.
- **[`active`→`info` reads as "running"]** An active issue health badge and a
  running workflow pill will look the same. -> Accepted: their meanings overlap
  (in-progress, healthy) and the spec explicitly allows sharing when meanings
  match. `success` is unavailable (reserved for terminal done). Flagged in Open
  Questions for design review.
- **[Priority stays a separate palette]** A second dark-aware palette coexists
  with the semantic families. -> Accepted (D6): priority is ordinal, not state;
  forcing it onto semantic families would overload the meaning reservation. The
  palette is documented and hex-free.
- **[`idle`→`success` may conflict with done] on runner surfaces** A healthy idle
  runner and a completed workflow both render `success`. -> Mandated by the spec
  (idle = healthy/available). The runner pill label ("Idle"/"Ready") disambiguates.
- **[Contrast fixture drift]** The JS token fixture could silently diverge from
  `index.css`. -> The D8 guard unit test parses `index.css` and fails on drift.
- **[`destructive` aliasing]** Any call site depending on `destructive`≠`danger`
  visuals shifts. -> Both are already soft-red (`bg-destructive/10`); aliasing
  produces no visible change.
- **[Dark-mode blocking-signal prominence]** Recoloring `blocked`/`failed`/
  `awaiting-approval` onto soft-tinted variants could weaken them vs the current
  solid red. -> The soft `danger` treatment already meets WCAG AA contrast in
  both themes (asserted in D8); visual weight is comparable to the current
  `bg-red-100 text-red-800` pills. `AttentionHero` runner-down glyph keeps a
  solid `danger` fill for at-a-glance prominence.

## Migration Plan

- **No API/domain/schema migration.** Pure presentation change, web-only, no new
  dependency.
- **Order (each step independently reviewable):**
  1. **Tokens** (D5): normalize `--warning` hue in `index.css`.
  2. **Primitives** (D3): add `Badge`/`Button` semantic variants; alias
     `destructive`; remove `AlertDialog` override and `FieldError` hardcode.
  3. **Shared layer** (D1/D2): land `shared/status-presentation/` with the full
     family reservation.
  4. **Delegate/diverge cleanup** (D4): rewire `statusBadge()`, delete
     `STATUS_PILL_PAIRS`/`STATUS_CONFIG`/`PRESENTATION_BY_STATUS`, rewrite
     `WorkflowRunStatusPill`, `StatusPill`, runner/session/status-bar/log-level/
     timeline/attention-hero/task-progress/review surfaces onto the layer.
  5. **Registries** (D6): convert `label-colors.ts` and `stage-colors.ts`; drop
     inline-hex `style` consumption.
  6. **Action buttons** (D7): `WorkspacePanel`/`BranchBar`/`TaskLogPanel`.
  7. **Tests** (D8): rewrite contrast test, add equivalence spec, add fixture
     guard.
- **Verification.** Manual dark-theme sweep of every covered surface after step 4
  and again after step 6; `npm run typecheck -w packages/web` and
  `npm run test:run -w packages/web` green throughout.
- **Rollback.** Revert the commit(s); no persistent state change, no migration
  to undo.

## Open Questions

- **`statusBadge()` — delegate or delete?** Design chooses delegate (minimal
  churn). If call sites are few, deletion is cleaner; decide at implementation
  time.
- **`active` issue health family.** Chosen `info` (shares meaning with running;
  `success` reserved for terminal). Confirm with design review — the alternative
  is `muted`, which visually demotes healthy in-progress issues.
- **`ready` workflow status.** Chosen `info` (queued, about to run).
  Alternative: `muted` (not yet active). Confirm.
- **Priority palette shape.** Keep the current 5-step ordinal hues (dark-aware),
  or collapse to fewer steps? Design keeps 5; open to simplification.
- **CompactSessionCard stage-identity palette.** Share a single documented
  stage-identity registry with any future workflow-stage-accent consumer, or
  keep it local? Design keeps it local pending a second consumer.
