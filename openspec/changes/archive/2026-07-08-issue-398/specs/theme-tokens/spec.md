### Requirement: Success/warning/info/danger tokens are the single source of truth for status color

The `success`, `warning`, `info`, and `danger` token families (each exposing `-subtle`, `-foreground`, and `-border` variants) SHALL remain the single source of truth for status color across the production surfaces covered by this milestone. The token families SHALL be defined once in the theme stylesheet and SHALL be consumed via the corresponding Tailwind utilities (e.g. `bg-success-subtle`, `text-warning-foreground`-family, `border-danger-border`). No production surface in this milestone SHALL introduce a new parallel status color system.

#### Scenario: Token families remain the only status color source

- **WHEN** any covered production surface needs a status color
- **THEN** it SHALL consume the `success`/`warning`/`info`/`danger` token utilities
- **AND** SHALL NOT define a new parallel semantic color system or import a new design-system dependency to provide one

### Requirement: Token hues are internally consistent

Each semantic token family SHALL keep a consistent hue across its `-subtle`, `-foreground`, and `-border` variants and across light and dark theme, so a single status does not shift hue between its background, border, and foreground. In particular the `--warning*` family SHALL use one hue across `--warning`, `--warning-subtle`, and `--warning-border`, eliminating the existing hue drift between these tokens.

#### Scenario: Warning token family uses a single hue

- **WHEN** the `--warning`, `--warning-subtle`, and `--warning-border` token values are inspected in light theme
- **THEN** they SHALL share the same hue (chroma/lightness may vary)
- **AND** the same SHALL hold for the dark-theme `--warning*` values

### Requirement: Priority, risk, and label color registries are dark-mode-aware and free of inline hex

The shared color registries for priority (`PRIORITY_COLORS`, `PRIORITY_STRIP_COLORS`), risk (`RISK_COLORS`), and label/type (`TYPE_LABEL_COLORS`, `AREA_LABEL_COLORS`, `URGENCY_LABEL_COLORS`, `TYPE_STRIP_COLORS`) SHALL be expressed as token-backed class strings or as a documented light/dark-aware palette, not as inline hex literals. Inline hex values (e.g. `#fee2e2`, `#ef4444`, `#22c55e`, `#dc2626`) SHALL be removed from these registries. Risk semantics SHALL align with the status families: `low`→`success`, `medium`→`warning`, `high`→`danger`.

#### Scenario: Risk registry maps to the status families

- **WHEN** `RISK_COLORS` resolves a risk level
- **THEN** `low` SHALL render with the `success` family, `medium` with the `warning` family, and `high` with the `danger` family
- **AND** none SHALL render with an inline hex literal

#### Scenario: Priority and label registries survive dark mode

- **WHEN** any priority or label chip renders in dark theme
- **THEN** its background, text, and (where present) strip color SHALL resolve to dark-theme-aware values
- **AND** SHALL NOT be a light-only hex combination that ignores dark theme

### Requirement: Log level and severity colors route through tokens

The shared log-level color maps (`LEVEL_COLORS`, `LEVEL_CHIP_COLORS`) SHALL route through the semantic tokens: `ERROR`→`danger`, `WARN`→`warning`, `INFO`→`info`, `DEBUG`→muted. Event-severity coloring used by the issue-event-timeline (the failure/attention marker overrides) SHALL likewise route through the `danger`/`warning` tokens rather than raw `bg-red-500`/`bg-amber-500`.

#### Scenario: Log level map uses tokens

- **WHEN** a log line is colored by `LEVEL_COLORS` or `LEVEL_CHIP_COLORS`
- **THEN** `ERROR` SHALL use the `danger` family, `WARN` the `warning` family, `INFO` the `info` family, and `DEBUG` a muted treatment
- **AND** SHALL NOT use raw `text-red-*`/`text-yellow-*`/`text-blue-*` palette classes

#### Scenario: Timeline failure and attention markers use tokens

- **WHEN** the issue-event-timeline renders a failure marker or an attention marker
- **THEN** the marker accent SHALL come from the `danger` (failure) or `warning` (attention) token family
- **AND** SHALL NOT use raw `bg-red-500`/`bg-amber-500`

### Requirement: Stage accent colors are dark-mode-aware

The kanban stage color scheme (`STAGE_COLORS` in `widgets/kanban-board/model/stage-colors.ts`) SHALL be dark-mode-aware: the `accent` value SHALL no longer be an inline hex literal consumed by inline `style` attributes, and the `labelClass`/`activeBg`/`activeBorder` values SHALL use tokens or a documented light/dark-aware palette rather than raw `text-amber-700`/`bg-amber-50/60`/`text-green-700` palette classes. Column accent colors SHALL remain distinguishable per stage while remaining legible in dark mode.

#### Scenario: Stage accent does not use inline hex

- **WHEN** a kanban column renders its accent dot / accent bar
- **THEN** the accent color SHALL come from a token or a light/dark-aware registry
- **AND** SHALL NOT be set via an inline hex literal in a `style` attribute

#### Scenario: Stage active states survive dark mode

- **WHEN** a kanban column is the active column in dark theme
- **THEN** its active background and border SHALL resolve to dark-theme-aware values
- **AND** SHALL NOT be a light-only `bg-amber-50/60`/`bg-green-50/40` combination
