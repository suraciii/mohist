## ADDED Requirements

### Requirement: Vite build produces embeddable static assets
The system SHALL use Vite to build the React SPA into a `dist/` directory under the web source root. The build output MUST contain `index.html`, JS bundles, CSS bundles, and any static assets.

#### Scenario: Production build
- **WHEN** `npm run build:web` is executed
- **THEN** Vite produces optimized static files in `packages/cli/web/dist/`

### Requirement: Server serves embedded static files
The system SHALL serve the Vite build output as static files from the Hono server. All non-API routes MUST fall back to `index.html` for client-side routing.

#### Scenario: Access root URL
- **WHEN** user navigates to `http://localhost:{port}/`
- **THEN** server returns the embedded `index.html`

#### Scenario: Client-side routing
- **WHEN** user navigates to `http://localhost:{port}/issue/1`
- **THEN** server returns `index.html` (SPA fallback)

#### Scenario: Static asset request
- **WHEN** browser requests `/assets/main-abc123.js`
- **THEN** server returns the corresponding built file

### Requirement: Development mode uses Vite dev server with API proxy
The system SHALL support a development mode where Vite dev server runs on a separate port and proxies API requests to the Hono backend.

#### Scenario: Dev server proxy
- **WHEN** developer runs `npm run dev:web`
- **THEN** Vite dev server starts on port 5173 and proxies `/api/*` requests to the Hono backend port
