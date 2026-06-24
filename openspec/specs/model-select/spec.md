### Requirement: ModelSelect renders inline variant chips on variant-capable rows

The shared `ModelSelect` component SHALL render a row of small tappable variant chips inline on every model row whose model exposes variants. Models that expose no variants SHALL render no chips and SHALL behave exactly as before. Variant availability SHALL be sourced from a `modelVariants: Record<string, string[]>` prop keyed by model id. Rows that survive a search filter SHALL continue to render their chips.

#### Scenario: Variant-capable model row shows chips

- **WHEN** `ModelSelect` renders a model row for a model whose id is present in `modelVariants`
- **THEN** the row SHALL render one chip per variant listed for that model
- **AND** the chips SHALL appear inline on the row alongside the model name and id

#### Scenario: Non-variant model row renders no chips

- **WHEN** `ModelSelect` renders a model row for a model whose id is not present in `modelVariants`
- **THEN** the row SHALL render no variant chips
- **AND** the row SHALL behave identically to the pre-change single-level `ModelSelect`

#### Scenario: Search preserves chips on surviving rows

- **WHEN** a user types a search query that filters the model list
- **AND** a variant-capable model row remains visible
- **THEN** that row SHALL still render its variant chips
- **AND** the chips SHALL remain individually selectable

### Requirement: Inline chips select model and variant in one action

Activating a variant chip SHALL select both the model and that specific variant in a single action and SHALL close the popover. Activating a model row's main body (model name/id area, not a chip) SHALL select the model with the default variant (no variant) and SHALL close the popover — preserving the pre-change behavior for body clicks.

#### Scenario: Chip click selects model and variant

- **WHEN** a user clicks a variant chip on a model row
- **THEN** `onChange` SHALL be invoked with the selected model id
- **AND** the variant selection SHALL be reported through the variant change callback
- **AND** the popover SHALL close

#### Scenario: Model body click selects default variant

- **WHEN** a user clicks the main body of a variant-capable model row (not a chip)
- **THEN** the model SHALL be selected with no variant (default)
- **AND** the popover SHALL close
- **AND** any previously selected variant for a different model SHALL be cleared

### Requirement: Active model and variant visual state is distinct

The currently selected model row SHALL be visually distinct from non-selected rows. Within the selected model row, the active variant chip SHALL be visually distinct (filled/highlighted) from inactive chips (outline). When the selected model has no variant selected, no chip within its row SHALL be marked active.

#### Scenario: Active variant chip is highlighted

- **WHEN** the selected model has a selected variant
- **THEN** the chip matching that variant within the selected model row SHALL render as filled/highlighted
- **AND** every other chip on that row SHALL render as outline

#### Scenario: No variant selected shows no active chip

- **WHEN** the selected model has no variant selected
- **THEN** no chip on the selected model row SHALL be marked active
- **AND** the selected model row itself SHALL still be visually distinct from other rows

### Requirement: Keyboard navigation reaches inline variant chips

The `ModelSelect` popover SHALL support keyboard navigation without leaving the popover. `ArrowUp`/`ArrowDown` SHALL move the highlighted model row. `ArrowRight` SHALL expand focus into the chip row of the highlighted model when that model has variants. `Enter` SHALL confirm either the highlighted model (default variant) or the focused chip. `Tab` SHALL also move focus into the chip row. `Escape` SHALL close the popover.

#### Scenario: Arrow keys move across model rows

- **WHEN** the popover is open and focus is in the model list
- **THEN** `ArrowDown` and `ArrowUp` SHALL move the highlighted model row without selecting it

#### Scenario: Arrow right enters the chip row

- **WHEN** the highlighted model row has variants
- **AND** the user presses `ArrowRight`
- **THEN** focus SHALL move into the chip row of that model
- **AND** the first chip SHALL be focusable

#### Scenario: Enter confirms the focused target

- **WHEN** focus is on a model row body and the user presses `Enter`
- **THEN** the model SHALL be selected with the default variant and the popover SHALL close
- **WHEN** focus is on a chip and the user presses `Enter`
- **THEN** the model and that chip's variant SHALL be selected and the popover SHALL close

#### Scenario: Escape closes the popover

- **WHEN** the user presses `Escape` while the popover is open
- **THEN** the popover SHALL close without changing the selection

### Requirement: Inline chips preserve compact and mobile tap-target sizing

Variant chips SHALL render at a reduced size when `ModelSelect` is used with `size="compact"` so per-stage override rows fit. At a 375px mobile viewport the popover SHALL fill the available width and chips SHALL wrap gracefully when a model exposes four variants. Every chip SHALL remain a tap target of at least 44x44 px (accounting for padding) on both default and compact sizes.

#### Scenario: Compact size renders smaller chips

- **WHEN** `ModelSelect` renders with `size="compact"`
- **THEN** variant chips SHALL use the compact chip size
- **AND** the per-stage override row SHALL not overflow its compact layout

#### Scenario: Mobile viewport wraps chips and keeps tap targets

- **WHEN** the popover renders at a 375px viewport width
- **AND** a model exposes four variants
- **THEN** the chips SHALL wrap onto additional lines rather than overflow horizontally
- **AND** each chip SHALL remain at least a 44x44 px tap target including padding

### Requirement: Standalone VariantPicker is removed for model selection

The standalone `VariantPicker` component SHALL no longer be used to select a model variant alongside `ModelSelect`. Variant selection for models SHALL happen exclusively through the inline chips of the unified `ModelSelect`. The `variantListFor` and `resolveVariantAgainstModel` helpers previously used by `VariantPicker` SHALL be relocated into `ModelSelect` or a small adjacent utils module so the behavior is reused by the unified control. `VariantPicker` MAY remain only for documented non-model contexts; if no such contexts exist it SHALL be deleted.

#### Scenario: No paired VariantPicker next to ModelSelect

- **WHEN** the issue detail, Settings AI defaults, or Create Issue surfaces render a model selector
- **THEN** they SHALL render a single `ModelSelect`
- **AND** no standalone `VariantPicker` dropdown SHALL appear next to it

#### Scenario: Variant helpers are reused by the unified control

- **WHEN** the `ModelSelect` source is inspected
- **THEN** variant list resolution and variant-against-model validation SHALL be provided by relocated helpers
- **AND** the behavior SHALL not be duplicated between a removed `VariantPicker` and `ModelSelect`