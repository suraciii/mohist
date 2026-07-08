### Requirement: The same domain state renders identically across covered surfaces

Each production domain state SHALL render with the same visual treatment on every covered surface — dashboard (including the Pulse zone, `CompactSessionCard`, and `StatusBar`), board (`IssueCard` and kanban column headers), issue detail, activity (issue-event-timeline), session (context-health indicator/bar), and runner (`RunnerList`, `RunnerSummary`, `AttentionHero`). Two surfaces that render the same state SHALL produce the same status color, dot color, border treatment, and (where applicable) icon family. Specifically, the existing within-state divergences SHALL be eliminated: runner `idle` SHALL use one hue family (not `emerald` on `RunnerList` and `green` on `RunnerSummary`); workflow `completed` and issue `done` SHALL use the `success` family (not `emerald` in `WorkflowRunStatusPill`, `green` in `StatusBar`, and `#22c55e` in `stage-colors.ts`); and context-health `green` SHALL render consistently between `ContextHealthIndicator` and `ContextHealthBar` (not `bg-gray-400` on one and `bg-green-500` on the other).

#### Scenario: Runner idle is consistent across runner surfaces

- **WHEN** a runner in the `idle` state is rendered by both `RunnerList` and `RunnerSummary` (and the `AttentionHero` all-clear state)
- **THEN** all three SHALL resolve the idle/available treatment to the `success` family
- **AND** SHALL NOT produce an `emerald` vs `green` divergence

#### Scenario: Completed/done is consistent across workflow and dashboard surfaces

- **WHEN** a completed workflow run, a completed stage, a `done` issue, and a `completed` count on the dashboard `StatusBar` are rendered
- **THEN** each SHALL resolve to the `success` family
- **AND** SHALL NOT mix `emerald`, `green`, and inline `#22c55e` across these surfaces

#### Scenario: Context-health green is consistent between indicator and bar

- **WHEN** `ContextHealthIndicator` and `ContextHealthBar` both render a `green`/healthy status at the same threshold
- **THEN** the dot/bar fill SHALL resolve to the same semantic treatment
- **AND** SHALL NOT diverge between `bg-gray-400` and `bg-green-500`

### Requirement: Dark mode has no light-only status combinations on covered surfaces

On every covered production surface, status treatments (pill, badge, dot, marker, banner, and toast) SHALL remain legible in dark theme: backgrounds, text, borders, dots, and icons SHALL all resolve to dark-theme-aware values. The dark theme SHALL NOT retain any light-only combination — including light backgrounds with light text, white panels paired with light-only borders, or amber/red status surfaces whose background/foreground pair is calibrated only for light theme. Blocking (`blocked`/`failed`) and approval (`awaiting-approval`) signals SHALL remain at least as visually prominent in dark mode as in light mode.

#### Scenario: Blocking and approval signals stay prominent in dark mode

- **WHEN** a `blocked`, `failed`, or `awaiting-approval` status is rendered in dark theme
- **THEN** its background, text, border, and dot SHALL all resolve to dark-theme token values
- **AND** the treatment SHALL NOT be weaker (lower contrast or smaller visual weight) than the same state in light theme

#### Scenario: No light-only panel or badge combinations remain

- **WHEN** any covered surface is rendered in dark theme
- **THEN** no status pill, badge, dot, marker, panel, or banner SHALL use a light-only class combination (e.g. `bg-white`, `bg-amber-50`, `bg-red-50`, `border-amber-200` without a dark counterpart, or `text-*` calibrated only for light backgrounds)
- **AND** attention/toast surfaces (`AttentionHero`, `RuntimeToastHost`) SHALL render legibly in dark theme

### Requirement: Status contrast is asserted against the rendered treatment

The status contrast test SHALL verify the treatment that is actually rendered, not a parallel hex map that diverges from the DOM. The existing `StatusPill.contrast.test.ts` SHALL assert contrast against the shared status-presentation layer's resolved treatment for each covered status indicator (at minimum `blocked`, `cancelled`, `approval`, `running`, `waiting`, `drift`), and SHALL NOT assert against a `STATUS_PILL_PAIRS` hex map that disagrees with the rendered classes. Every status background/text combination that the covered surfaces render SHALL meet WCAG AA contrast (≥ 4.5:1) in both light and dark theme.

#### Scenario: Contrast test covers the rendered treatment

- **WHEN** the status contrast test runs for each covered status indicator
- **THEN** it SHALL compute contrast from the treatment that the shared status-presentation layer resolves for that indicator (in both light and dark theme)
- **AND** SHALL NOT compute contrast from a hex map that diverges from what is rendered

#### Scenario: All covered status treatments meet WCAG AA

- **WHEN** any covered status indicator is rendered in light or dark theme
- **THEN** the background/text contrast SHALL be at least 4.5:1
- **AND** the contrast test SHALL fail if any covered combination drops below 4.5:1

### Requirement: Cross-surface status equivalence is asserted by spec

A spec test SHALL assert that the same domain state resolves to the same semantic treatment across the covered surfaces, so that a future per-widget color drift is caught at test time. This test SHALL cover, at minimum: workflow run status as rendered by `WorkflowRunStatusPill` and by the dashboard `StatusBar`; runner state as rendered by `RunnerList` and `RunnerSummary`; issue health as rendered by the issue-health badge helper and by `IssueCard`; and context health as rendered by `ContextHealthIndicator` and `ContextHealthBar`.

#### Scenario: Same status resolves to the same treatment across widgets

- **WHEN** two covered widgets render the same domain state (e.g. runner `idle`, workflow `completed`, health `green`)
- **THEN** the spec test SHALL assert they resolve to the same semantic family and treatment
- **AND** SHALL fail if one widget introduces a divergent hue family for that state

### Requirement: Existing page-level interactions and product terms are preserved

Routing status and action styling through the shared layer SHALL NOT change page-level interactions or product terminology on the covered surfaces. The product terms issue, workflow, stage, health, approval, runner, artifact, session, and epic SHALL remain unchanged in copy, labels, tooltips, and roles. Existing behavioral/ARIA contracts on status surfaces (e.g. `role="alert"` for red context-health, `role="status"` for yellow, `aria-live` regions on toasts and health banners) SHALL be preserved; only the color/visual treatment is unified.

#### Scenario: Product terms are unchanged

- **WHEN** any covered surface renders after the unification
- **THEN** the copy SHALL continue to use the existing product terms (issue, workflow, stage, health, approval, runner, artifact, session, epic)
- **AND** no product term SHALL be renamed or removed

#### Scenario: Status ARIA contracts are preserved

- **WHEN** a red/critical status surface and a yellow/warning status surface render after the unification
- **THEN** they SHALL continue to carry the existing `role`/`aria-live` semantics (e.g. `role="alert"` for critical, `role="status"` for warning)
- **AND** the unification SHALL NOT weaken or remove these contracts
