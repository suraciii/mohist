## ADDED Requirements

### Requirement: Settings sections use the unified Card component

Every card-shaped container on the Settings surface (all 6 tabs) SHALL be rendered with the shared `CardSection` component. Settings section components SHALL NOT wrap content in hand-rolled `rounded-md`/`rounded-lg` + `border` + background divs that duplicate `CardSection`'s appearance. This applies to `RepositoriesSection` cards, `TemplatesSection`/`TemplateRow`, `WorkflowProfilesSection`, `AiSettingsSection`, `AgentSettingsSection`, and `SystemSettingsSection`.

#### Scenario: A section card is rendered

- **WHEN** any of the 6 Settings tabs renders a card-shaped block of content
- **THEN** the block SHALL be a `CardSection` instance
- **AND** it SHALL NOT use a local `rounded-md` or `rounded-lg` + `border` wrapper class

#### Scenario: Card radius is uniform across tabs

- **WHEN** the Settings surface is grepped for card wrapper classes
- **THEN** there SHALL be no mixing of `rounded-md` and `rounded-lg` on section card containers
- **AND** every section card SHALL share the single corner radius, border, and background defined by `CardSection`

### Requirement: Settings page titles use the SettingsSection wrapper

Each Settings tab's page-level title (and optional description) SHALL be rendered through a shared `<SettingsSection title="..." description="...">` wrapper. The wrapper SHALL render the title as a single consistent `<h3>` element. Settings section files SHALL NOT hand-write their own `<h3 className="text-sm font-medium">` page titles.

#### Scenario: Page title is consistent across the 6 tabs

- **WHEN** any of the 6 Settings tabs is rendered
- **THEN** the page title SHALL come from `<SettingsSection>`
- **AND** it SHALL be an `<h3>` with identical font size, weight, and color across all tabs

#### Scenario: A page title is not hand-written

- **WHEN** a Settings section file is inspected
- **THEN** it SHALL NOT contain a locally styled `<h3>` page title
- **AND** page title styling SHALL be owned solely by `SettingsSection`

### Requirement: Settings text colors use the converged token palette

Settings text color classes SHALL be limited to exactly three tiers: `text-foreground` (primary text), `text-muted-foreground` (secondary text), and `text-foreground/70` (weakest emphasis only). Settings SHALL NOT use hardcoded `text-gray-*` classes. Settings SHALL NOT use `text-foreground/85`, `text-foreground/80`, or `text-foreground/75` opacity variants.

#### Scenario: No hardcoded gray text colors

- **WHEN** `packages/web/src/pages/settings` is grepped for `text-gray-`
- **THEN** there SHALL be zero matches

#### Scenario: No ad-hoc foreground opacity variants

- **WHEN** `packages/web/src/pages/settings` is grepped for `text-foreground/8`, `text-foreground/75`, and `text-foreground/80`
- **THEN** there SHALL be zero matches

#### Scenario: Existing emphasis maps to an allowed tier

- **WHEN** a Settings element previously used `text-foreground/85` or `text-foreground/80`
- **THEN** it SHALL be remapped to `text-foreground`, `text-muted-foreground`, or `text-foreground/70`
- **AND** the chosen tier SHALL preserve the intended visual emphasis ordering

### Requirement: Settings icons come solely from lucide-react

All icons rendered on the Settings surface SHALL be sourced from `lucide-react`. Settings SHALL NOT define or render inline `<svg>` icon elements. `ModelSelect` SHALL replace its hand-rolled `SearchIcon`, `ChevronDownIcon`, and `XIcon` with the corresponding `lucide-react` icons (`Search`, `ChevronDown`, `X`). The only permitted `<svg>` occurrences under `packages/web/src/pages/settings` are those inside shadcn/ui primitive components.

#### Scenario: ModelSelect uses lucide icons

- **WHEN** `ModelSelect` renders its search, chevron, and clear affordances
- **THEN** each icon SHALL be a `lucide-react` component
- **AND** `ModelSelect` SHALL NOT define local `SearchIcon`, `ChevronDownIcon`, or `XIcon` SVG components

#### Scenario: No inline SVG icons in settings

- **WHEN** `packages/web/src/pages/settings` is grepped for `<svg`
- **THEN** there SHALL be zero matches outside of shadcn/ui primitive components

### Requirement: Settings heading hierarchy is standardized

Settings SHALL use a single heading hierarchy: page titles SHALL be `<h3>` rendered by `SettingsSection`; card titles SHALL be rendered by `CardSection` in its fixed title style (uppercase, tracked, with `titleAs` configurable). Settings SHALL NOT use an `<h2 className="uppercase tracking-wider">` as a card title. Card titles SHALL be visually consistent across all tabs, including the System tab.

#### Scenario: Page titles and card titles use distinct, fixed styles

- **WHEN** a Settings tab is rendered
- **THEN** the page title SHALL be an `<h3>` from `SettingsSection`
- **AND** each card title SHALL come from `CardSection`'s fixed title style
- **AND** the two SHALL NOT share the same element or styling

#### Scenario: System tab card titles match other tabs

- **WHEN** the System tab card titles are compared with card titles on the other 5 tabs
- **THEN** they SHALL share the same font size, weight, tracking, and color
- **AND** the System tab SHALL NOT render a larger or wider-tracked card title

### Requirement: Settings body text meets WCAG AA contrast

All Settings body text SHALL meet WCAG AA contrast thresholds against its rendered background (at least 4.5:1 for normal-size text). Converging to the three-tier token palette and removing ad-hoc opacity variants SHALL be the mechanism that satisfies this.

#### Scenario: Settings body text passes automated contrast audit

- **WHEN** an automated accessibility audit (e.g. axe-core) is run against the Settings surface
- **THEN** all Settings body text SHALL pass the WCAG AA contrast requirement
- **AND** no element SHALL rely on a forbidden opacity variant to meet contrast

### Requirement: Settings visual refactor preserves existing behavior

The visual consistency refactor SHALL NOT change Settings functional behavior, API contracts, or persisted data. Existing Settings tests (`SettingsPage.test.tsx`, `AiSettingsSection.test.tsx`, `ModelSelect.test.tsx`) SHALL continue to pass without behavioral modification.

#### Scenario: Existing Settings tests pass unchanged in intent

- **WHEN** the Settings test suite is run after the refactor
- **THEN** `SettingsPage.test.tsx`, `AiSettingsSection.test.tsx`, and `ModelSelect.test.tsx` SHALL pass
- **AND** no test SHALL have been weakened to accommodate a visual-only change

#### Scenario: No backend or data-model impact

- **WHEN** the refactor is reviewed
- **THEN** there SHALL be no changes to HTTP API routes, request/response shapes, or persisted Settings data models
- **AND** the change SHALL be confined to frontend presentation
