### Requirement: Settings search is invoked by a settings-scoped keyboard shortcut

The Settings page SHALL open the settings search dialog when the user presses ⌘K (macOS) or Ctrl+K (Windows/other) while the Settings page is the active route. The shortcut SHALL be registered only while the Settings page is mounted and SHALL NOT register a global application-wide ⌘K/Ctrl+K handler, so the global command-palette slot remains free for future use. The shortcut SHALL be available regardless of which Settings tab is currently active.

#### Scenario: ⌘K opens search on macOS Settings page

- **WHEN** the user is on the Settings page and presses ⌘K on macOS
- **THEN** the settings search dialog SHALL open

#### Scenario: Ctrl+K opens search on non-macOS Settings page

- **WHEN** the user is on the Settings page and presses Ctrl+K on a non-macOS platform
- **THEN** the settings search dialog SHALL open

#### Scenario: Shortcut does not fire outside the Settings page

- **WHEN** the user is on any non-Settings route and presses ⌘K or Ctrl+K
- **THEN** the settings search dialog SHALL NOT open
- **AND** no global command-palette handler registered by this feature SHALL consume the keystroke

#### Scenario: Shortcut works from any Settings tab

- **WHEN** the user is on any Settings tab (Coder Agent, Runtime, Repositories, Workflows, Templates, System, Preferences) and presses ⌘K or Ctrl+K
- **THEN** the settings search dialog SHALL open

### Requirement: Settings search is built from the existing cmdk primitives

The settings search dialog SHALL be composed from the codebase's existing cmdk primitives (`Command`, `CommandDialog`, `CommandInput`, `CommandList`, `CommandEmpty`, `CommandGroup`, `CommandItem`). The feature SHALL NOT introduce a new command-palette infrastructure, a new keyboard-shortcut dispatch framework, or a parallel dialog component.

#### Scenario: Search dialog reuses cmdk primitives

- **WHEN** the settings search dialog is rendered
- **THEN** it SHALL be composed of the existing `CommandDialog`, `CommandInput`, `CommandList`, `CommandEmpty`, `CommandGroup`, and `CommandItem` components
- **AND** it SHALL NOT introduce a new command-palette or dialog infrastructure

### Requirement: Every searchable settings field is registered in a central settings registry

The Settings feature SHALL maintain a single central registry of searchable settings descriptors. Each registered field SHALL expose: its owning tab, a human-readable label, a description, its placeholder text (where applicable), and a stable focus-target id that resolves to the field's focusable element on that tab. Every configurable field across the Settings tabs SHALL be registered. Fields that currently lack a stable focusable id SHALL be backfilled with one so registry navigation can target them.

#### Scenario: Registry entry shape

- **WHEN** a settings field is registered
- **THEN** its registry entry SHALL include the owning tab, label, description, placeholder (where applicable), and a stable focus-target id

#### Scenario: Every configurable field is registered

- **WHEN** the central settings registry is inspected
- **THEN** every configurable field across the Settings tabs SHALL have an entry
- **AND** every entry's focus-target id SHALL resolve to a focusable element on its owning tab

#### Scenario: Previously id-less fields gain a stable focus target

- **WHEN** a field that previously had no stable focusable id is rendered
- **THEN** it SHALL expose a stable id
- **AND** that id SHALL be referenced by its registry entry for focus navigation

### Requirement: Settings search matches on label, description, and placeholder but excludes current values

Search SHALL filter registry entries by matching the query (case-insensitive) against each entry's label, description, and placeholder text only. Search SHALL NOT match against a field's current value. This prevents numeric-value noise (e.g. querying "30" would otherwise match every timeout field whose value happens to be 30).

#### Scenario: Match on label

- **WHEN** the user types a term that appears in a field's label
- **THEN** that field SHALL appear in the results

#### Scenario: Match on description or placeholder

- **WHEN** the user types a term that appears in a field's description or placeholder but not its label
- **THEN** that field SHALL appear in the results

#### Scenario: Current values are not searched

- **WHEN** the user types a term that matches only a field's current value (e.g. "30" matching a timeout whose value is 30)
- **THEN** that field SHALL NOT appear in the results unless the term also matches its label, description, or placeholder

#### Scenario: Case-insensitive matching

- **WHEN** the user types a query whose casing differs from the stored text
- **THEN** matching SHALL still be case-insensitive

### Requirement: Activating a search result navigates to its tab and focuses the field

When a result is highlighted and the user presses Enter (or otherwise activates it), the dialog SHALL close, the Settings surface SHALL switch to the result's owning tab, and the field's focus-target element SHALL receive keyboard focus with a visible focus indicator.

#### Scenario: Enter navigates and focuses

- **WHEN** a result is highlighted and the user presses Enter
- **THEN** the search dialog SHALL close
- **AND** the owning tab SHALL become active
- **AND** the field's focus-target element SHALL receive keyboard focus

#### Scenario: Focused field shows a visible focus indicator

- **WHEN** search navigation focuses a field
- **THEN** a visible `:focus-visible` indicator SHALL be rendered on that field

### Requirement: Settings search empty state and dismissal

When the query produces no matches, the dialog SHALL display "No matching settings" via the `CommandEmpty` primitive. The dialog SHALL close on Escape and on outside dismissal without navigating.

#### Scenario: Empty state copy

- **WHEN** the user's query matches no registry entry
- **THEN** the dialog SHALL display "No matching settings"

#### Scenario: Escape closes without navigation

- **WHEN** the user presses Escape while the search dialog is open
- **THEN** the dialog SHALL close
- **AND** no tab switch or field focus SHALL occur

#### Scenario: Outside dismissal closes without navigation

- **WHEN** the user dismisses the dialog by clicking the overlay outside the dialog content
- **THEN** the dialog SHALL close
- **AND** no tab switch or field focus SHALL occur
