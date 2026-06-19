## MODIFIED Requirements

### Requirement: Settings sections use the unified Card component

Every card-shaped container on the Settings surface (all Settings tabs, including the Preferences tab) SHALL be rendered with the shared `CardSection` component. Settings section components SHALL NOT wrap content in hand-rolled `rounded-md`/`rounded-lg` + `border` + background divs that duplicate `CardSection`'s appearance. This applies to `RepositoriesSection` cards, `TemplatesSection`/`TemplateRow`, `WorkflowProfilesSection`, `AiSettingsSection`, `AgentSettingsSection`, `SystemSettingsSection`, and `PreferencesSection`. The settings search dialog is a cmdk `CommandDialog`, not a card, and is therefore out of scope for this requirement.

#### Scenario: A section card is rendered

- **WHEN** any Settings tab (including Preferences) renders a card-shaped block of content
- **THEN** the block SHALL be a `CardSection` instance
- **AND** it SHALL NOT use a local `rounded-md` or `rounded-lg` + `border` wrapper class

#### Scenario: Card radius is uniform across tabs

- **WHEN** the Settings surface is grepped for card wrapper classes
- **THEN** there SHALL be no mixing of `rounded-md` and `rounded-lg` on section card containers
- **AND** every section card SHALL share the single corner radius, border, and background defined by `CardSection`

#### Scenario: Preferences cards use CardSection

- **WHEN** the Preferences tab renders a card-shaped block (e.g. around the theme selector or the keyboard-shortcut reference)
- **THEN** it SHALL be a `CardSection` instance
- **AND** it SHALL NOT use a hand-rolled `rounded-*` + `border` wrapper

### Requirement: Settings page titles use the SettingsSection wrapper

Each Settings tab's page-level title (and optional description) SHALL be rendered through a shared `<SettingsSection title="..." description="...">` wrapper. The wrapper SHALL render the title as a single consistent `<h3>` element. Settings section files SHALL NOT hand-write their own `<h3 className="text-sm font-medium">` page titles.

#### Scenario: Page title is consistent across the Settings tabs

- **WHEN** any Settings tab (including Preferences) is rendered
- **THEN** the page title SHALL come from `<SettingsSection>`
- **AND** it SHALL be an `<h3>` with identical font size, weight, and color across all tabs

#### Scenario: A page title is not hand-written

- **WHEN** a Settings section file is inspected
- **THEN** it SHALL NOT contain a locally styled `<h3>` page title
- **AND** page title styling SHALL be owned solely by `SettingsSection`

### Requirement: Settings heading hierarchy is standardized

Settings SHALL use a single heading hierarchy: page titles SHALL be `<h3>` rendered by `SettingsSection`; card titles SHALL be rendered by `CardSection` in its fixed title style (uppercase, tracked, with `titleAs` configurable). Settings SHALL NOT use an `<h2 className="uppercase tracking-wider">` as a card title. Card titles SHALL be visually consistent across all tabs, including the System tab and the Preferences tab.

#### Scenario: Page titles and card titles use distinct, fixed styles

- **WHEN** a Settings tab is rendered
- **THEN** the page title SHALL be an `<h3>` from `SettingsSection`
- **AND** each card title SHALL come from `CardSection`'s fixed title style
- **AND** the two SHALL NOT share the same element or styling

#### Scenario: System tab card titles match other tabs

- **WHEN** the System tab card titles are compared with card titles on the other tabs
- **THEN** they SHALL share the same font size, weight, tracking, and color
- **AND** the System tab SHALL NOT render a larger or wider-tracked card title

#### Scenario: Preferences tab card titles match other tabs

- **WHEN** the Preferences tab card titles are compared with card titles on the other tabs
- **THEN** they SHALL share the same font size, weight, tracking, and color
