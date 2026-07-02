## ADDED Requirements

### Requirement: Dashboard hero excludes the Ask Agent quick entry

The Dashboard hero / first-screen composition SHALL NOT render an "Ask Agent" entry or any control that navigates to the new-session composer (`/agent-sessions/new`). The "Ask Agent" quick entry defined by the `agent-workbench` capability applies to the issue detail page, epic detail page, and project context only; the Dashboard is explicitly excluded from that scope. Legitimate action entry points on the Dashboard SHALL remain the header's New Issue entry, the Attention Hero's inline Approve/Resume actions, and the Pulse session cards.

#### Scenario: Dashboard hero renders no Ask Agent entry

- **WHEN** the Dashboard page renders
- **THEN** the dashboard hero SHALL NOT render an "Ask Agent" button or link
- **AND** the hero SHALL NOT navigate to `/agent-sessions/new`

#### Scenario: Ask Agent quick entry remains scoped to entity pages

- **WHEN** a user views the issue detail page, epic detail page, or project context
- **THEN** those surfaces MAY continue to expose the "Ask Agent" quick entry per the `agent-workbench` capability
- **AND** the Dashboard SHALL NOT inherit that quick entry
