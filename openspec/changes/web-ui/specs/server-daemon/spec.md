## MODIFIED Requirements

### Requirement: Server startup serves both API and Web UI
The server SHALL serve the embedded Web UI static files alongside the REST API. API routes at `/api/*` take priority over static file serving.

#### Scenario: Server starts with embedded UI
- **WHEN** `mo server start` is executed
- **THEN** server starts Hono with both API routes and static file serving

#### Scenario: API route priority
- **WHEN** a request matches both an API route and a static file path
- **THEN** the API route is served

#### Scenario: SPA fallback
- **WHEN** a request does not match any API route or static file
- **THEN** server returns `index.html` for client-side routing
