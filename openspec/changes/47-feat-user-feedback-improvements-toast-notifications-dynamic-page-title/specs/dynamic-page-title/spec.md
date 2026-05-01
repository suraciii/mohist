## ADDED Requirements

### Requirement: Dynamic page title reflects current route

WebUI SHALL dynamically update `document.title` based on the current route. The base title SHALL be "Mohist".

| Route Pattern | Title |
|---|---|
| `/` | "Mohist" |
| `/issue/:number` | "Issue #N — Mohist" |
| `/issue/:number/session/:id` | "Session — Issue #N — Mohist" |
| `/explore` | "Explore — Mohist" |
| `/explore/:id` | "Explore — Mohist" |
| `/settings/:section` | "Settings — Mohist" |
| `/logs` | "Logs — Mohist" |
| `/archived` | "Archived — Mohist" |

#### Scenario: Navigate to issue detail page
- **WHEN** user navigates to `/issue/47`
- **THEN** `document.title` is set to "Issue #47 — Mohist"

#### Scenario: Navigate to kanban board
- **WHEN** user navigates to `/`
- **THEN** `document.title` is set to "Mohist"

#### Scenario: Navigate to settings
- **WHEN** user navigates to `/settings/ai`
- **THEN** `document.title` is set to "Settings — Mohist"

### Requirement: Page title indicates live agent activity

When an agent is actively running for an issue, `document.title` SHALL be prefixed with a visual indicator. The title SHALL use a Unicode dot prefix "●" to signal activity, and revert to the base title when no agents are running.

#### Scenario: Agent running for an issue
- **WHEN** user is on `/issue/47`
- **AND** agent status shows a running agent for issue #47
- **THEN** `document.title` is set to "● Issue #47 — Mohist"

#### Scenario: Agent running for a different issue (viewing kanban)
- **WHEN** user is on `/`
- **AND** agent status shows a running agent for issue #12
- **THEN** `document.title` is set to "● Mohist"

#### Scenario: Agent completes
- **WHEN** agent status transitions from running to not running
- **THEN** the "●" prefix is removed from `document.title`
- **AND** title reverts to the route-based title

### Requirement: Page title hook implementation

WebUI SHALL provide a `useDocumentTitle` hook (or equivalent) that accepts a title string and an optional `active` boolean. The hook SHALL set `document.title` as a side effect and restore the previous title on unmount.

#### Scenario: Hook sets title
- **WHEN** a component calls `useDocumentTitle("Issue #47 — Mohist")`
- **THEN** `document.title` is set to "Issue #47 — Mohist"

#### Scenario: Hook restores title on unmount
- **WHEN** a component that called `useDocumentTitle(...)` unmounts
- **THEN** `document.title` is restored to the value it had before the component mounted
