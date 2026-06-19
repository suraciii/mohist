## ADDED Requirements

### Requirement: Preferences tab holds only real user preferences and read-only reference information

The Preferences tab SHALL contain only genuinely controllable user preferences and read-only reference information that has real backing. It SHALL NOT render controls for nonexistent subsystems (e.g. a notification toggle while no notification subsystem exists), and it SHALL NOT surface system-fact items (e.g. timezone, CLI executable path) that belong on the System tab.

#### Scenario: Preferences contents are limited to real preferences and reference info

- **WHEN** the Preferences tab is rendered
- **THEN** it SHALL contain the theme selector and the read-only keyboard-shortcut reference
- **AND** it SHALL NOT contain a notification-preference control
- **AND** it SHALL NOT contain timezone or CLI executable-path fields (those belong on the System tab)

### Requirement: Theme selector offers light, dark, and system options with immediate effect

The Preferences tab SHALL render a theme selector with exactly three options: light, dark, and system. Selecting an option SHALL apply the theme immediately (without a page reload). The selected choice SHALL be persisted to `localStorage`. When no stored preference exists, the theme SHALL default to system, tracking the OS `prefers-color-scheme` media query.

#### Scenario: Three theme options are present

- **WHEN** the Preferences tab is rendered
- **THEN** the theme selector SHALL offer exactly light, dark, and system options

#### Scenario: Selecting light or dark applies immediately

- **WHEN** the user selects light (or dark)
- **THEN** the theme SHALL be applied immediately without a page reload
- **AND** the choice SHALL be persisted to `localStorage`

#### Scenario: System option tracks prefers-color-scheme

- **WHEN** the user selects system
- **THEN** the applied theme SHALL follow the OS `prefers-color-scheme`
- **AND** the choice SHALL be persisted to `localStorage`

#### Scenario: Default is system when no stored preference exists

- **WHEN** the Preferences tab is loaded with no theme stored in `localStorage`
- **THEN** the selector SHALL reflect system
- **AND** the applied theme SHALL follow `prefers-color-scheme`

### Requirement: Theme selection loads without a flash of unstyled content (no FOUC)

The persisted or system theme SHALL be applied before first paint so that users with a stored dark or system-dark preference do not observe a light-theme flash on load.

#### Scenario: No light flash on load with stored dark preference

- **WHEN** the app loads with a dark theme stored in `localStorage`
- **THEN** the first paint SHALL already be in the dark theme
- **AND** no light-theme flash SHALL be observable before the dark theme applies

#### Scenario: No light flash on load with system-dark preference

- **WHEN** the app loads with the system theme stored (or no stored preference) and the OS `prefers-color-scheme` is dark
- **THEN** the first paint SHALL already be in the dark theme
- **AND** no light-theme flash SHALL be observable

### Requirement: Theme switching activates the existing dark-mode styles

Selecting dark (or system while the OS is in dark) SHALL activate the dark-mode styles already authored in the component layer (the currently inert `dark:` variants) by setting the appropriate attribute/class on the root element. Selecting light SHALL deactivate them.

#### Scenario: Dark theme activates dark-mode styles

- **WHEN** the dark theme is active
- **THEN** the root element SHALL carry the dark-mode class/attribute
- **AND** the existing `dark:` styles SHALL take effect across the UI

#### Scenario: Light theme deactivates dark-mode styles

- **WHEN** the light theme is active
- **THEN** the root element SHALL NOT carry the dark-mode class/attribute
- **AND** `dark:` styles SHALL NOT apply

### Requirement: Keyboard-shortcut reference is read-only and lists only currently-real shortcuts

The Preferences tab SHALL render a read-only keyboard-shortcut reference. It SHALL list only shortcuts that actually exist in the application at render time (e.g. sidebar toggle ⌘B, settings search ⌘K). It SHALL NOT list shortcuts that have no implementation, and it SHALL NOT impose a minimum count of shortcuts. The reference SHALL be non-interactive (it SHALL NOT toggle or rebind shortcuts).

#### Scenario: Real shortcuts are listed

- **WHEN** the keyboard-shortcut reference is rendered
- **THEN** it SHALL list the shortcuts that exist in the application (e.g. sidebar toggle ⌘B and settings search ⌘K)

#### Scenario: Nonexistent shortcuts are not listed

- **WHEN** the reference is rendered
- **THEN** it SHALL NOT include any shortcut that has no current implementation (e.g. a notification or system shortcut whose handler does not exist)

#### Scenario: Reference is read-only

- **WHEN** the user interacts with the reference entries
- **THEN** the entries SHALL NOT toggle, rebind, or otherwise mutate any shortcut binding
