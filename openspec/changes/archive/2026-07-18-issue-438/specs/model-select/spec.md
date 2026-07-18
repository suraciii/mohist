### Requirement: Selection happens on confirmation, not on press

The model selector SHALL select an option only when the pointer press is resolved into a click on that same option (pointerdown and pointerup occur on the same option), or when the keyboard `Enter` confirms the highlighted option. A press that begins on an option and is released after moving off that option SHALL NOT select it. A touch or pen contact that initiates a scroll gesture SHALL NOT select any option it traverses. A successful selection SHALL invoke the model change callback with the selected model id and SHALL close the popover.

#### Scenario: Mouse click on an option selects it and closes

- **WHEN** a user presses the mouse button down on an option and releases it on the same option
- **THEN** the model change callback SHALL be invoked with that option's model id
- **AND** the popover SHALL close

#### Scenario: Press, drag away, release does not select

- **WHEN** a user presses the mouse button down on an option
- **AND** moves the pointer off that option before releasing
- **THEN** the model change callback SHALL NOT be invoked
- **AND** the popover SHALL remain open

#### Scenario: Touch tap selects, touch drag does not

- **WHEN** a touch or pen contact begins on an option and is lifted on the same option without an intervening scroll
- **THEN** that option SHALL be selected and the popover SHALL close
- **WHEN** a touch or pen contact begins on an option and moves beyond the tap threshold as a scroll gesture
- **THEN** no option traversed by the gesture SHALL be selected
- **AND** the popover SHALL remain open so the user can continue scrolling

#### Scenario: Keyboard Enter selects the highlighted option

- **WHEN** a model row is highlighted and the user presses `Enter`
- **THEN** the highlighted model SHALL be selected and the popover SHALL close

### Requirement: Pointer handling does not suppress native scrolling

The selector SHALL NOT register selection logic on `pointerdown`, `touchstart`, or any other event whose handler calling `preventDefault()` would disable the browser's native scroll initiation. Selection logic SHALL be bound to confirmation events (`click` for pointer, `Enter` for keyboard) only. The selector SHALL NOT `preventDefault()` or `stopPropagation()` on pointer events in a way that blocks scroll initiation over the option list.

#### Scenario: Dragging over the list scrolls it

- **WHEN** a touch or pen drag moves across the option list
- **THEN** the list SHALL scroll natively following the gesture
- **AND** the gesture SHALL NOT be cancelled by selection handling

#### Scenario: Mouse wheel over the list scrolls it

- **WHEN** the pointer is over the option list and the user rolls the mouse wheel
- **THEN** the option list SHALL scroll
- **AND** the page behind the popover SHALL not move as a side effect

### Requirement: Option list scroll is contained within the popover

The option list SHALL scroll independently from the page behind the popover. Scrolling the list to its top or bottom boundary SHALL NOT chain to, scroll, or otherwise move the underlying page. The scroll container SHALL apply scroll containment (`overscroll-behavior: contain` or an equivalent mechanism that prevents scroll chaining).

#### Scenario: Reaching the bottom boundary does not scroll the page

- **WHEN** the option list is scrolled to its last item and the user continues to scroll downward
- **THEN** the option list SHALL not scroll past its last item
- **AND** the page behind the popover SHALL NOT scroll

#### Scenario: Reaching the top boundary does not scroll the page

- **WHEN** the option list is scrolled to its first item and the user continues to scroll upward
- **THEN** the option list SHALL not scroll past its first item
- **AND** the page behind the popover SHALL NOT scroll

### Requirement: Provider group headers remain visible while their group is in view

The option list groups models by provider. While any model of a provider group is visible in the scroll viewport, that provider group's header SHALL remain visible at the top of the list area (sticky). When the last model of a group scrolls out of view, the next group's header SHALL take its place. The header SHALL persist across scroll within its group and SHALL NOT scroll out of view while the group is still partially visible.

#### Scenario: Scrolling within a long group keeps its header visible

- **WHEN** a provider group contains more models than fit in the visible list area
- **AND** the user scrolls down through that group's models
- **THEN** that group's header SHALL remain pinned at the top of the visible list area

#### Scenario: Crossing into the next group swaps the header

- **WHEN** the user scrolls from the last model of one provider group into the first model of the next group
- **THEN** the visible header SHALL transition to the next group's header

### Requirement: Options expose click affordance and selected-state indication

Each option SHALL render with a pointer cursor and SHALL expose a hover visual state and an active/pressed visual state, so the option is recognizably clickable. The currently selected model (the model matching the selector's current value) SHALL be visually distinct from non-selected options anywhere in the list. Variant chip rows SHALL NOT compress the option's main row below its standard hit height.

#### Scenario: Options show pointer cursor and hover state

- **WHEN** the pointer is over an option
- **THEN** the option SHALL display a pointer cursor
- **AND** the option SHALL apply a visible hover state distinct from the resting state

#### Scenario: The selected model is visually marked

- **WHEN** the selector's current value matches a model present in the rendered list
- **THEN** that model's option SHALL render with a visual indication that distinguishes it from every non-selected option

#### Scenario: Variant chips do not squeeze the option row

- **WHEN** a model exposes one or more variants and the row renders inline variant chips
- **THEN** the option's main row hit area SHALL remain at least as tall as a non-variant option row

### Requirement: Complete keyboard navigation across the whole list

The selector SHALL support full keyboard operation while the popover is open. `ArrowDown` and `ArrowUp` SHALL move the highlighted option one step at a time and SHALL scroll the highlighted option into view when it would otherwise be off-screen. `Home` and `End` SHALL move the highlight to the first or last option in the filtered list respectively. `Enter` SHALL select the highlighted option (default variant when focused on a model row) or the focused variant chip. `Escape` SHALL close the popover without changing the selection. Typing into the search input SHALL filter the option list. Variant chips SHALL remain reachable: `ArrowRight` or `Tab` from a highlighted variant-capable model row SHALL move focus into that row's chip set, and `ArrowLeft` at the first chip or `Escape` SHALL return focus to the list.

#### Scenario: Arrow keys move highlight and keep it visible

- **WHEN** the popover is open and `ArrowDown` is pressed repeatedly past the bottom of the visible area
- **THEN** the highlight SHALL advance one option per keypress
- **AND** the highlighted option SHALL be scrolled into view as needed

#### Scenario: Home and End jump to the boundaries

- **WHEN** the popover is open and `Home` is pressed
- **THEN** the first option in the filtered list SHALL be highlighted
- **WHEN** `End` is pressed
- **THEN** the last option in the filtered list SHALL be highlighted

#### Scenario: Enter confirms the focused target

- **WHEN** focus is on a model row and `Enter` is pressed
- **THEN** that model SHALL be selected with the default (no) variant and the popover SHALL close
- **WHEN** focus is on a variant chip and `Enter` is pressed
- **THEN** that model and that variant SHALL be selected together and the popover SHALL close

#### Scenario: Escape closes without changing selection

- **WHEN** the popover is open and `Escape` is pressed
- **THEN** the popover SHALL close
- **AND** the selection SHALL remain unchanged

#### Scenario: Variant chips are reachable and escapable by keyboard

- **WHEN** a variant-capable model row is highlighted and `ArrowRight` (or `Tab`) is pressed
- **THEN** focus SHALL move into that row's chip set
- **WHEN** `ArrowLeft` is pressed at the first chip
- **THEN** focus SHALL return to the model list

#### Scenario: Typing filters the list

- **WHEN** the user types a query into the search input
- **THEN** the option list SHALL show only the options whose displayed text or id matches the query

### Requirement: Standard combobox and listbox semantics

The selector SHALL expose a standard combobox + listbox structure to assistive technology. The trigger control SHALL advertise `aria-haspopup` and reflect open state via `aria-expanded`. The search input SHALL be exposed as a combobox that controls the option list (`aria-controls`) and reports the currently highlighted option via `aria-activedescendant`. The option list container SHALL have `role="listbox"`. Each option SHALL have `role="option"` and SHALL reflect its selected state via `aria-selected`. Provider groups SHALL be labeled so a screen reader can announce the group context alongside the options it contains.

#### Scenario: Screen reader announces options as a list

- **WHEN** the popover is open and the option list is inspected by an assistive technology
- **THEN** the list container SHALL expose `role="listbox"`
- **AND** each option SHALL expose `role="option"`

#### Scenario: Selected option is announced as selected

- **WHEN** the selector's current value matches an option present in the list
- **THEN** that option SHALL expose `aria-selected="true"`
- **AND** every other option SHALL expose `aria-selected="false"`

#### Scenario: Combobox reports the highlighted option

- **WHEN** the user moves the highlight with `ArrowDown` or `ArrowUp`
- **THEN** the combobox element SHALL update `aria-activedescendant` to reference the newly highlighted option

#### Scenario: Provider groups are labeled

- **WHEN** the option list contains models from more than one provider
- **THEN** each provider group SHALL expose an accessible label
- **AND** assistive technology SHALL be able to announce the group alongside its options

### Requirement: Selection result and interaction are identical across all model-selection surfaces

The selector's selection semantics and interaction behavior SHALL be identical on every surface that uses it: Settings → AI Settings default model and per-stage overrides; the Issue detail configuration card default model and per-stage overrides; the Create Issue dialog; and the Agent Profile editor. Activating a model row's main body on any surface SHALL select that model with the default (no) variant and SHALL clear any previously selected variant for a different model. Activating a variant chip on any surface SHALL select that model together with that variant in a single action. The change callbacks SHALL fire with the same model id and variant values on every surface.

#### Scenario: Model body select clears a prior variant

- **WHEN** a different model with a selected variant is currently active
- **AND** the user selects a model by activating its row body on any of the four surfaces
- **THEN** the model change callback SHALL fire with the selected model id
- **AND** the variant callback SHALL report no variant (the prior variant SHALL be cleared)

#### Scenario: Chip select reports model and variant together

- **WHEN** the user activates a variant chip on any of the four surfaces
- **THEN** the model change callback SHALL fire with that chip's model id
- **AND** the variant callback SHALL report that chip's variant

#### Scenario: Interaction behavior is uniform across surfaces

- **WHEN** the selector is opened on any of Settings default, Settings per-stage, Issue config card default, Issue config card per-stage, Create Issue dialog, or Agent Profile editor
- **THEN** click-to-select, touch scrolling, scroll containment, sticky headers, keyboard navigation, and combobox semantics SHALL each behave identically across all surfaces
