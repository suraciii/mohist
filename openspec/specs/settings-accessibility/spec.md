### Requirement: Settings interactive elements meet minimum touch-target size

Every interactive element rendered on the 6 Settings tabs — buttons, links, tab triggers, and clickable chips — SHALL provide a pointer hit area of at least 44×44 CSS pixels (WCAG 2.5.5), including padding. Where a control's visual size is smaller than 44px, the hit area SHALL be extended via padding so the total clickable region meets the minimum. Settings section files SHALL NOT apply fixed height classes that cap interactive controls below 44px (e.g. `h-7`, `h-8` on buttons).

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

### Requirement: Settings Add Repository form stays legible on narrow viewports

The Repositories tab's Add Repository form SHALL keep its Name, Base Branch, and Git URL inputs and their associated labels fully visible and uncrowded at a 375px viewport width. Labels SHALL NOT be clipped and inputs SHALL NOT overflow or collapse in a way that prevents accurate data entry.

#### Scenario: 375px viewport form layout is usable

- **WHEN** the Add Repository form is rendered at a 375px CSS viewport width
- **THEN** the Name and Base Branch inputs SHALL both remain visible with their labels
- **AND** no label SHALL be truncated and no input SHALL overflow the viewport horizontally

### Requirement: Settings keyboard navigation reaches all interactive elements in DOM order

Across all 6 Settings tabs, sequential `Tab`/`Shift+Tab` traversal SHALL reach every interactive element in DOM order with no focus traps on the static surface (dialogs and popovers MAY trap focus while open). Every button SHALL be activatable via both `Enter` and `Space`, including the Stage Model Overrides disclosure control. `Escape` SHALL close any open dialog or popover. Arrow keys SHALL move the selected option inside `Select` and `ModelSelect` option lists.

#### Scenario: Tab traversal reaches all interactive elements

- **WHEN** a keyboard user traverses any of the 6 Settings tabs with `Tab` and `Shift+Tab`
- **THEN** every interactive element SHALL be reachable in DOM order
- **AND** focus SHALL not be trapped on the static surface (no open dialog/popover)

#### Scenario: Enter and Space activate buttons

- **WHEN** focus is on any Settings button (including the Stage Model Overrides disclosure)
- **AND** the user presses `Enter` or `Space`
- **THEN** the button's action SHALL be triggered

#### Scenario: Escape closes dialogs and popovers

- **WHEN** a dialog (e.g. Template Editor) or popover (e.g. ModelSelect) is open on a Settings tab
- **AND** the user presses `Escape`
- **THEN** the dialog or popover SHALL close and focus SHALL return to the invoking control

#### Scenario: Arrow keys operate Select and ModelSelect

- **WHEN** a `Select` or `ModelSelect` option list is open on a Settings tab
- **AND** the user presses the up/down arrow keys
- **THEN** the active option SHALL move within the list

### Requirement: Settings focus-visible ring is visible in light and dark themes

Every interactive element on the 6 Settings tabs SHALL render a visible `:focus-visible` focus indicator in both the light and dark themes. The indicator SHALL have sufficient contrast against its background to be perceivable.

#### Scenario: focus-visible ring visible in both themes

- **WHEN** any interactive element on a Settings tab receives keyboard focus
- **THEN** a `:focus-visible` ring SHALL be rendered
- **AND** it SHALL be visible in both the light theme and the dark theme

### Requirement: Settings surface passes WCAG AA contrast at runtime

All text rendered on the 6 Settings tabs SHALL meet WCAG AA contrast thresholds (at least 4.5:1 for normal-size text, 3:1 for large text) against its rendered background, verified via an axe-core runtime scan. This SHALL specifically cover `SettingsSection` descriptions rendered with `text-muted-foreground` on muted/tinted backgrounds and error-state text (e.g. `text-red-600` on `bg-red-50`).

#### Scenario: axe-core reports zero critical or serious violations

- **WHEN** an axe-core scan is run against each of the 6 Settings tabs
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

- **WHEN** any of the 6 Settings tabs is rendered
- **THEN** the page SHALL contain exactly one `<h1>` element
- **AND** that `<h1>` SHALL represent the Settings page landmark

#### Scenario: Heading levels do not skip

- **WHEN** the rendered heading sequence on a Settings tab is inspected in document order
- **THEN** each successive heading level SHALL increase by at most one
- **AND** the surface SHALL pass the axe-core `heading-order` rule

### Requirement: Settings form inputs are programmatically labeled

Every form input rendered on the 6 Settings tabs SHALL be programmatically associated with a text label via a `<label htmlFor>`/`id` pairing or an `aria-labelledby` reference, so that assistive technology announces the input's purpose.

#### Scenario: Every input has an associated label

- **WHEN** a Settings tab renders a form input (text input, select, checkbox, textarea)
- **THEN** the input SHALL have an associated `<label>` via `htmlFor`/`id` or an `aria-labelledby` reference
- **AND** no input SHALL rely on placeholder-only labeling

### Requirement: Settings mutation feedback is announced to assistive technology

Success and failure feedback from Settings mutations SHALL be announced to assistive technology via an `aria-live` region. This SHALL be satisfied either by the toast component providing `role="status"`/`aria-live` on its announcements, or by an `aria-live` (polite) region within the active Section that surfaces mutation outcomes. The mechanism SHALL NOT require modifying the shared toast/Dialog/Select component internals beyond a minimal attribute patch; a deeper shared-component refactor is out of scope.

#### Scenario: Successful mutation is announced

- **WHEN** a Settings mutation succeeds (e.g. a model update, a repository add, a log level change)
- **THEN** the success feedback SHALL be announced through a `role="status"`/`aria-live` toast or a Section-level `aria-live` region
- **AND** a screen reader user SHALL receive notice that the action succeeded

#### Scenario: Failed mutation is announced

- **WHEN** a Settings mutation fails (e.g. a backend rejection or network error)
- **THEN** the failure feedback SHALL be announced through an `aria-live` region or assertive live region appropriate for errors
- **AND** a screen reader user SHALL receive notice that the action failed

### Requirement: Settings folding and dialog state is exposed to assistive technology

Folding/disclosure controls on the 6 Settings tabs (e.g. the Stage Model Overrides disclosure) SHALL expose their expanded/collapsed state via `aria-expanded`. Any modal dialog rendered on the Settings surface SHALL expose `aria-modal="true"` while open, SHALL be labelled via `aria-labelledby` referencing a visible dialog title, and SHALL trap keyboard focus within the dialog while open. The ModelSelect popover SHALL move focus into its search input when opened. Note: the Template Editor is currently an inline `CardSection` panel, not a modal dialog; the audit confirms which (if any) modal dialogs exist on the Settings surface, and this requirement applies to those modals rather than to the inline Template Editor.

#### Scenario: Disclosure controls expose aria-expanded

- **WHEN** a Settings folding control (e.g. Stage Model Overrides) is rendered
- **THEN** it SHALL expose `aria-expanded` reflecting its collapsed/expanded state
- **AND** toggling the control SHALL update `aria-expanded`

#### Scenario: Any modal dialog traps focus and is labelled

- **WHEN** a modal dialog opens on a Settings tab
- **THEN** it SHALL expose `aria-modal="true"`
- **AND** it SHALL reference its title via `aria-labelledby`
- **AND** keyboard focus SHALL be trapped within the dialog until it is closed

#### Scenario: ModelSelect popover focuses its search input on open

- **WHEN** the ModelSelect popover opens
- **THEN** focus SHALL move into the popover's search input

### Requirement: Settings accessibility regression coverage

The Settings surface SHALL include automated accessibility regression coverage using axe-core across all 6 Settings tabs, integrated into the existing frontend test suite (vitest) or Playwright. The regression suite SHALL fail on any new critical or serious axe-core violation on the Settings tabs. Existing Settings tests (`SettingsPage.test.tsx` and the section unit tests) SHALL continue to pass without behavioral weakening.

#### Scenario: axe-core regression tests cover all 6 tabs

- **WHEN** the Settings accessibility regression suite runs
- **THEN** it SHALL execute an axe-core scan against each of the 6 Settings tabs (ai, agent, repositories, workflows, templates, system)
- **AND** it SHALL fail on any critical or serious violation

#### Scenario: Existing Settings tests pass unchanged in intent

- **WHEN** the existing Settings test suite runs after the accessibility pass
- **THEN** `SettingsPage.test.tsx` and the section unit tests SHALL pass
- **AND** no test SHALL have been weakened to accommodate an a11y-only change

### Requirement: Settings accessibility pass preserves functional behavior and API contracts

The accessibility and responsiveness pass SHALL NOT change Settings functional behavior, persisted data, or backend API contracts. Changes SHALL be confined to presentation attributes (classNames, ARIA attributes, heading elements) and the addition of regression tests. The HTTP API routes (`/api/config`, `/api/agent-runtime`, `/api/system/info`, repository and workflow profile endpoints) SHALL remain unchanged in request/response shape.

#### Scenario: No backend or data-model impact

- **WHEN** the change is reviewed
- **THEN** there SHALL be no changes to HTTP API routes, request/response shapes, or persisted Settings data models
- **AND** the change SHALL be confined to frontend presentation and tests

#### Scenario: No new component library or shared-component refactor

- **WHEN** the change is reviewed
- **THEN** no component library SHALL be replaced (shadcn/Radix/Base UI retained)
- **AND** the shared toast, Dialog, and Select components SHALL NOT be refactored beyond a minimal attribute patch
