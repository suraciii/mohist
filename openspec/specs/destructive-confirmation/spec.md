### Requirement: A shared accessible AlertDialog primitive is provided

The web app SHALL provide a single shared `AlertDialog` primitive, built on top of the existing base-ui `dialog.tsx`, that renders a destructive-operation confirmation. The primitive SHALL trap keyboard focus within the dialog while open, SHALL restore focus to the invoking element on close, and SHALL dismiss on Escape.

#### Scenario: AlertDialog traps focus while open

- **WHEN** an `AlertDialog` is opened
- **THEN** keyboard focus SHALL be moved into the dialog
- **AND** tab/shift-tab navigation SHALL cycle within the dialog and SHALL NOT escape to the underlying page

#### Scenario: AlertDialog restores focus on close

- **WHEN** an open `AlertDialog` is dismissed (confirm, cancel, overlay click, or Escape)
- **THEN** keyboard focus SHALL be returned to the element that invoked the dialog

#### Scenario: AlertDialog dismisses on Escape

- **WHEN** an `AlertDialog` is open
- **AND** the user presses Escape
- **THEN** the dialog SHALL dismiss as a cancellation (no destructive action performed)

### Requirement: All destructive operations confirm through the shared AlertDialog

Every destructive operation surfaced in the UI SHALL require a second confirmation via the shared `AlertDialog` primitive before executing. This SHALL cover, at minimum: Agent settings reset, label-definition delete, repository remove, template delete, and issue comment delete. The app SHALL NOT use hand-written confirm modals (e.g. a `fixed inset-0` overlay) or the browser `window.confirm`/`window.alert` functions for any destructive operation.

#### Scenario: Agent reset confirms through AlertDialog

- **WHEN** the user triggers Agent settings reset
- **THEN** the shared `AlertDialog` SHALL open requesting confirmation
- **AND** the reset SHALL NOT execute until the user confirms
- **AND** the previous hand-written `fixed inset-0` reset modal SHALL no longer be rendered

#### Scenario: Label-definition delete confirms through AlertDialog

- **WHEN** the user triggers deletion of a label definition in the Label catalog
- **THEN** the shared `AlertDialog` SHALL open requesting confirmation
- **AND** the deletion SHALL NOT execute until the user confirms

#### Scenario: Repository remove confirms through AlertDialog

- **WHEN** the user triggers removal of a repository in Repositories settings
- **THEN** the shared `AlertDialog` SHALL open requesting confirmation
- **AND** the removal SHALL NOT execute until the user confirms

#### Scenario: Template delete confirms through AlertDialog

- **WHEN** the user triggers deletion of a template in Templates settings
- **THEN** the shared `AlertDialog` SHALL open requesting confirmation
- **AND** the deletion SHALL NOT execute until the user confirms

#### Scenario: Issue comment delete confirms through AlertDialog instead of window.confirm

- **WHEN** the user triggers deletion of a comment on the issue detail page (`IssueDetailPage.tsx`)
- **THEN** the shared `AlertDialog` SHALL open requesting confirmation
- **AND** the browser `window.confirm` (or `window.alert`) SHALL NOT be invoked
- **AND** the comment SHALL NOT be deleted until the user confirms

#### Scenario: No hand-written confirm modal or window.confirm remains

- **WHEN** the repository is inspected for confirmation primitives
- **THEN** no hand-written `fixed inset-0` confirm overlay SHALL remain for destructive operations
- **AND** no `window.confirm` or `window.alert` call SHALL remain for destructive operations
