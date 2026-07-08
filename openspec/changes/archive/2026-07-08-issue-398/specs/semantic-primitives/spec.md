### Requirement: Badge primitive exposes token-backed semantic variants

The `Badge` primitive SHALL expose `success`, `warning`, `info`, and `danger` variants in addition to the existing `default`, `secondary`, `destructive`, `outline`, `ghost`, and `link` variants. Each semantic variant SHALL be soft-tinted and token-backed: it SHALL render using the corresponding `-subtle` background, `-foreground`/`-border` text, and `-border` border tokens, so the variant switches correctly between light and dark theme. The existing `destructive` variant SHALL continue to be backed by the `--destructive` token (or be aliased to the `danger` token) so destructive badges are dark-mode-aware.

#### Scenario: Semantic badge variants render with token classes

- **WHEN** a `Badge` is rendered with `variant="success"`, `variant="warning"`, `variant="info"`, or `variant="danger"`
- **THEN** its class set SHALL reference the corresponding semantic tokens (e.g. `bg-success-subtle`, `text-success-foreground`-family, `border-success-border`)
- **AND** SHALL NOT reference raw Tailwind palette classes such as `bg-emerald-100`, `bg-amber-100`, `bg-blue-100`, or `bg-red-100`

#### Scenario: Semantic badge variants are dark-mode-aware

- **WHEN** a semantic `Badge` variant is rendered in dark theme
- **THEN** its background, text, and border SHALL resolve to the dark-theme token values
- **AND** SHALL NOT produce a light-only combination

### Requirement: Button primitive exposes token-backed semantic variants

The `Button` primitive SHALL expose `success`, `warning`, `info`, and `danger` variants in addition to the existing variants. The `destructive` action treatment SHALL be token-backed and dark-mode-aware, and SHALL NOT be shadowed by an inline raw-palette override. The `default` (primary), `outline` (secondary), `ghost`, `secondary`, and `link` variants SHALL remain the consistent treatment for primary, secondary/outline, and tertiary actions, and a disabled button SHALL render with the primitive's standard disabled treatment regardless of variant.

#### Scenario: Semantic button variants render with token classes

- **WHEN** a `Button` is rendered with `variant="success"`, `variant="warning"`, `variant="info"`, or `variant="danger"`
- **THEN** its class set SHALL reference the corresponding semantic tokens
- **AND** SHALL NOT reference raw Tailwind palette classes such as `bg-green-600`, `bg-amber-600`, `bg-red-600`, or `bg-blue-600`

#### Scenario: Destructive button is not overridden by raw red

- **WHEN** a destructive action is rendered via the `Button` primitive (including inside `AlertDialog` confirmation)
- **THEN** the rendered class set SHALL come from the `destructive` variant backed by the destructive/danger token
- **AND** SHALL NOT include a raw `bg-red-600`/`bg-red-700` (or similar) override layered on top of the variant

#### Scenario: Disabled action treatment is uniform

- **WHEN** any action button is disabled
- **THEN** it SHALL render the primitive's standard disabled treatment (pointer-events disabled, reduced opacity)
- **AND** SHALL NOT gain a state-specific bespoke disabled class per call site

### Requirement: Status and action styling is expressed through primitives, not ad-hoc classes

Components that render status surfaces and actions SHALL obtain their colors from the `Badge`/`Button` primitive variants or from the shared status-presentation layer, not from hand-rolled Tailwind class strings. The `AlertDialog` destructive confirmation and the `FieldError` text SHALL stop hardcoding raw red (e.g. `bg-red-600`, `text-red-700`) and SHALL route through the destructive/danger token or primitive.

#### Scenario: AlertDialog destructive confirmation uses the destructive variant

- **WHEN** an `AlertDialog` renders a destructive confirmation button
- **THEN** the button SHALL be styled by the destructive `Button` variant alone
- **AND** SHALL NOT carry an additional `bg-red-600 text-white hover:bg-red-700` className override

#### Scenario: FieldError text uses the danger token

- **WHEN** a `FieldError` renders an inline form error
- **THEN** its text color SHALL be backed by the danger/destructive token
- **AND** SHALL NOT hardcode `text-red-700`

### Requirement: Bespoke action-button styling is removed from covered panels

Bespoke per-state action-button class overrides in `WorkspacePanel`, `TaskLogPanel`, and `BranchBar` SHALL be replaced by `Button` variant selections, so these panels no longer author their own `border-amber-300 bg-amber-50`, `border-slate-300 bg-white`, `border-gray-300 bg-white`, or `border-amber-300` action styles. The panels MAY still express layout (size, width, padding) through `className`, but SHALL NOT express color or border-color through `className`.

#### Scenario: WorkspacePanel action buttons use Button variants

- **WHEN** `WorkspacePanel` renders a rebase action (both the `behind` and not-`behind` states) and any secondary action
- **THEN** each button SHALL select its color treatment through a `Button` variant
- **AND** SHALL NOT carry a bespoke `border-amber-300 bg-amber-50 text-amber-800` or `border-gray-300 bg-white text-gray-700` className for color

#### Scenario: BranchBar action buttons use Button variants

- **WHEN** `BranchBar` renders a rebase action in any of its states (rebasing, behind, upstream-unknown, default)
- **THEN** the action button SHALL select its color treatment through a `Button` variant
- **AND** SHALL NOT carry a bespoke `border-amber-300`/`border-gray-300` color override

#### Scenario: TaskLogPanel action affordances use primitives or the documented dark-terminal palette

- **WHEN** `TaskLogPanel` renders its download button and source chips
- **THEN** they SHALL use `Button`/`Badge` variants (or the documented dark-terminal palette for the terminal body itself)
- **AND** SHALL NOT hand-roll `border-slate-300 bg-white text-slate-700` action styling that diverges from the rest of the app
