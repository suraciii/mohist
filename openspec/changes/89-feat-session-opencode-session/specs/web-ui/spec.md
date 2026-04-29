## ADDED Requirements

### Requirement: Router includes session detail route
The `App.tsx` router SHALL include a route at `/issue/:number/session/:sessionId` that renders a `SessionPage` component. The route SHALL be nested under `ProjectGuard` alongside the existing `/issue/:number` route.

#### Scenario: Session page route renders SessionPage
- **WHEN** the router matches `/issue/86/session/abc-123`
- **THEN** the `SessionPage` component is rendered with `number=86` and `sessionId=abc-123` as URL params
- **AND** the `ProjectGuard` wrapper ensures a project is selected

#### Scenario: Session page route coexists with issue detail route
- **WHEN** the router matches `/issue/86` (without session segment)
- **THEN** the `IssueDetailPage` component is rendered as before
- **AND** the new session route does not interfere with the existing issue route
