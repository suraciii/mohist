## MODIFIED Requirements

### Requirement: Settings interactive elements meet minimum touch-target size

Every interactive element rendered on the Settings tabs (including the Preferences tab) and within the settings search dialog — buttons, links, tab triggers, clickable chips, the Preferences theme-selector options, and the search result `CommandItem` rows — SHALL provide a pointer hit area of at least 44×44 CSS pixels (WCAG 2.5.5), including padding. Where a control's visual size is smaller than 44px, the hit area SHALL be extended via padding so the total clickable region meets the minimum. Settings section files SHALL NOT apply fixed height classes that cap interactive controls below 44px (e.g. `h-7`, `h-8` on buttons).

#### Scenario: RepositoriesSection action buttons meet target size

- **WHEN** the Repositories tab renders the `Set default` and `Remove` buttons for a non-default repository
- **THEN** each button's clickable hit area SHALL be at least 44×44 CSS pixels including padding
- **AND** the buttons SHALL NOT use a `h-7` (or smaller) fixed height class

#### Scenario: No sub-minimum height classes on interactive controls

- **WHEN** `packages/web/src/pages/settings` is grepped for fixed height classes on interactive elements
- **THEN** no button, link, tab trigger, or clickable chip SHALL be capped below 44px height by a `h-7`, `h-8`, or equivalent fixed height utility

#### Scenario: Visually small control extends hit area via padding

- **WHEN** a Settings control is intentionally rendered smaller than 44px for visual density
- **THEN** the surrounding hit area SHALL be expanded via padding to at least 44×44 CSS pixels
- **AND** the visual rendering MAY remain compact

#### Scenario: Preferences theme options and search result rows meet target size

- **WHEN** the Preferences theme-selector options or the settings search `CommandItem` result rows are rendered
- **THEN** each option/row's clickable hit area SHALL be at least 44×44 CSS pixels including padding

### Requirement: Settings keyboard navigation reaches all interactive elements in DOM order

Across all Settings tabs (including the Preferences tab), sequential `Tab`/`Shift+Tab` traversal SHALL reach every interactive element in DOM order with no focus traps on the static surface (dialogs and popovers MAY trap focus while open). Every button SHALL be activatable via both `Enter` and `Space`, including the Stage Model Overrides disclosure control. `Escape` SHALL close any open dialog or popover. Arrow keys SHALL move the selected option inside `Select` and `ModelSelect` option lists. The settings search dialog SHALL open on ⌘K (macOS) / Ctrl+K (other) while the Settings page is active, SHALL move focus into its search input on open, SHALL trap focus while open, and SHALL close on `Escape` with focus restored to the element that held focus before the dialog opened.

#### Scenario: Tab traversal reaches all interactive elements

- **WHEN** a keyboard user traverses any Settings tab (including Preferences) with `Tab` and `Shift+Tab`
- **THEN** every interactive element SHALL be reachable in DOM order
- **AND** focus SHALL not be trapped on the static surface (no open dialog/popover)

#### Scenario: Enter and Space activate buttons

- **WHEN** focus is on any Settings button (including the Stage Model Overrides disclosure)
- **AND** the user presses `Enter` or `Space`
- **THEN** the button's action SHALL be triggered

#### Scenario: Escape closes dialogs and popovers

- **WHEN** a dialog (e.g. Template Editor, settings search) or popover (e.g. ModelSelect) is open on a Settings tab
- **AND** the user presses `Escape`
- **THEN** the dialog or popover SHALL close and focus SHALL return to the invoking control (or, for the settings search opened by keyboard shortcut, to the element that held focus before the dialog opened)

#### Scenario: Arrow keys operate Select and ModelSelect

- **WHEN** a `Select` or `ModelSelect` option list is open on a Settings tab
- **AND** the user presses the up/down arrow keys
- **THEN** the active option SHALL move within the list

#### Scenario: Settings search opens via keyboard and traps focus

- **WHEN** the user presses ⌘K (macOS) or Ctrl+K (other) while on the Settings page
- **THEN** the settings search dialog SHALL open
- **AND** focus SHALL move into the search input
- **AND** keyboard focus SHALL remain trapped within the dialog until it is closed

### Requirement: Settings focus-visible ring is visible in light and dark themes

Every interactive element on the Settings tabs (including the Preferences tab) and in the settings search dialog SHALL render a visible `:focus-visible` focus indicator in both the light and dark themes. The indicator SHALL have sufficient contrast against its background to be perceivable. The dark theme is a real, user-selectable state (via Preferences), so focus indicators SHALL be verifiable in dark mode, not only in the default light mode.

#### Scenario: focus-visible ring visible in both themes

- **WHEN** any interactive element on a Settings tab (including Preferences) or in the search dialog receives keyboard focus
- **THEN** a `:focus-visible` ring SHALL be rendered
- **AND** it SHALL be visible in both the light theme and the dark theme

#### Scenario: Focus ring is verifiable in user-selected dark theme

- **WHEN** the user selects the dark theme on the Preferences tab
- **AND** an interactive element receives keyboard focus
- **THEN** a perceivable `:focus-visible` ring SHALL be rendered against the dark background

### Requirement: Settings surface passes WCAG AA contrast at runtime

All text rendered on the Settings tabs (including the Preferences tab) and in the settings search dialog SHALL meet WCAG AA contrast thresholds (at least 4.5:1 for normal-size text, 3:1 for large text) against its rendered background, verified via an axe-core runtime scan. This SHALL specifically cover `SettingsSection` descriptions rendered with `text-muted-foreground` on muted/tinted backgrounds and error-state text (e.g. `text-red-600` on `bg-red-50`). Because the dark theme is now a reachable user-selectable state, the contrast scan SHALL be run in both light and dark themes.

#### Scenario: axe-core reports zero critical or serious violations

- **WHEN** an axe-core scan is run against each Settings tab (including Preferences) in the light theme and in the dark theme
- **THEN** the count of critical and serious level violations SHALL be zero
- **AND** any color-contrast findings SHALL be classified below serious

#### Scenario: SettingsSection description meets contrast on muted background

- **WHEN** a `SettingsSection` description rendered with `text-muted-foreground` appears on a muted or tinted background (e.g. `bg-muted/40`)
- **THEN** the description text SHALL meet at least 4.5:1 contrast against its rendered background
- **AND** if the contrast is achieved by adjusting the token usage, the before/after values SHALL be recorded

#### Scenario: Error-state text meets contrast on tinted background

- **WHEN** error-state text (e.g. `text-red-600`) is rendered on a tinted error background (e.g. `bg-red-50`)
- **THEN** the error text SHALL meet at least 4.5:1 contrast against the tinted background

### Requirement: Settings heading hierarchy is monotone

The Settings surface SHALL render exactly one page-level `<h1>` landmark. Heading levels SHALL descend monotonically in document order, increasing by at most one level between successive headings, so that the surface passes the axe-core `heading-order` rule. Section, card, and subtitle headings SHALL nest beneath the single page `<h1>` without skipping levels.

#### Scenario: Single page-level h1 landmark

- **WHEN** any Settings tab (including Preferences) is rendered
- **THEN** the page SHALL contain exactly one `<h1>` element
- **AND** that `<h1>` SHALL represent the Settings page landmark

#### Scenario: Heading levels do not skip

- **WHEN** the rendered heading sequence on a Settings tab (including Preferences) is inspected in document order
- **THEN** each successive heading level SHALL increase by at most one
- **AND** the surface SHALL pass the axe-core `heading-order` rule

### Requirement: Settings form inputs are programmatically labeled

Every form input rendered on the Settings tabs (including the Preferences tab) SHALL be programmatically associated with a text label via a `<label htmlFor>`/`id` pairing or an `aria-labelledby` reference, so that assistive technology announces the input's purpose.

#### Scenario: Every input has an associated label

- **WHEN** a Settings tab renders a form input (text input, select, checkbox, textarea, radio group, or segmented/theme selector)
- **THEN** the input SHALL have an associated `<label>` via `htmlFor`/`id` or an `aria-labelledby` reference
- **AND** no input SHALL rely on placeholder-only labeling

#### Scenario: Preferences theme selector is labeled

- **WHEN** the Preferences theme selector is rendered
- **THEN** it SHALL be programmatically associated with a text label (e.g. "Theme") via `<label htmlFor>`/`id` or `aria-labelledby`
- **AND** assistive technology SHALL announce it as the theme control

### Requirement: Settings folding and dialog state is exposed to assistive technology

Folding/disclosure controls on the Settings tabs (including the Preferences tab) (e.g. the Stage Model Overrides disclosure) SHALL expose their expanded/collapsed state via `aria-expanded`. Any modal dialog rendered on the Settings surface — including the settings search dialog — SHALL expose `aria-modal="true"` while open, SHALL be labelled via `aria-labelledby` referencing a visible dialog title, and SHALL trap keyboard focus within the dialog while open. The ModelSelect popover SHALL move focus into its search input when opened. Note: the Template Editor is currently an inline `CardSection` panel, not a modal dialog; the settings search dialog IS a modal dialog and the modal-dialog requirement applies to it.

#### Scenario: Disclosure controls expose aria-expanded

- **WHEN** a Settings folding control (e.g. Stage Model Overrides) is rendered
- **THEN** it SHALL expose `aria-expanded` reflecting its collapsed/expanded state
- **AND** toggling the control SHALL update `aria-expanded`

#### Scenario: Any modal dialog traps focus and is labelled

- **WHEN** a modal dialog (including the settings search dialog) opens on a Settings tab
- **THEN** it SHALL expose `aria-modal="true"`
- **AND** it SHALL reference its title via `aria-labelledby`
- **AND** keyboard focus SHALL be trapped within the dialog until it is closed

#### Scenario: Settings search dialog is an accessible modal

- **WHEN** the settings search dialog opens
- **THEN** it SHALL expose `aria-modal="true"` and reference a visible title via `aria-labelledby`
- **AND** keyboard focus SHALL be trapped within it
- **AND** it SHALL close on `Escape`

#### Scenario: ModelSelect popover focuses its search input on open

- **WHEN** the ModelSelect popover opens
- **THEN** focus SHALL move into the popover's search input

### Requirement: Settings accessibility regression coverage

The Settings surface SHALL include automated accessibility regression coverage using axe-core across all Settings tabs (including the Preferences tab) and the settings search dialog, integrated into the existing frontend test suite (vitest) or Playwright. The regression suite SHALL fail on any new critical or serious axe-core violation on the Settings tabs or the search dialog. Existing Settings tests (`SettingsPage.test.tsx` and the section unit tests) SHALL continue to pass without behavioral weakening. Contrast and focus scans SHALL be executed in both the light and dark themes.

#### Scenario: axe-core regression tests cover all Settings tabs

- **WHEN** the Settings accessibility regression suite runs
- **THEN** it SHALL execute an axe-core scan against each Settings tab (ai, agent, repositories, workflows, templates, system, preferences)
- **AND** it SHALL fail on any critical or serious violation

#### Scenario: axe-core regression covers the settings search dialog

- **WHEN** the settings search dialog is opened within the regression suite
- **THEN** an axe-core scan SHALL be run against the open dialog
- **AND** it SHALL fail on any critical or serious violation

#### Scenario: axe-core scans run in both light and dark themes

- **WHEN** the regression suite runs contrast/focus checks
- **THEN** scans SHALL be executed with the light theme active and with the dark theme active

#### Scenario: Existing Settings tests pass unchanged in intent

- **WHEN** the existing Settings test suite runs after the accessibility pass
- **THEN** `SettingsPage.test.tsx` and the section unit tests SHALL pass
- **AND** no test SHALL have been weakened to accommodate an a11y-only change
