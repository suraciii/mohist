### Requirement: Frontend-owned routes SHALL fall back to the Web UI entry page

The Web UI is a single-page application. Any request whose path is owned by a frontend route — including paths whose final segment contains a dot (e.g. workflow task-session names like `T-001.1`) — SHALL be served the Web UI entry page (`index.html`) when the path does not resolve to a real static file. The fallback SHALL NOT exclude a path merely because its final segment resembles a file extension. The entry page body and a `text/html` content type SHALL be returned so the browser hands off to the client router.

#### Scenario: Direct open of a dotted session-name deep link serves the entry page
- **WHEN** a client requests `/issues/12/workflow/sessions/T-001.1` directly (paste into a new tab) or via a hard refresh
- **THEN** the server SHALL respond with the Web UI entry page body (HTTP 200, `text/html`), not a 404, so the client router renders the session page

#### Scenario: Dot-free frontend routes keep falling back to the entry page
- **WHEN** a client requests a dot-free frontend route such as `/`, `/Test%20Project/issues/12`, or `/issues/12/workflow/sessions/plan`
- **THEN** the server SHALL respond with the Web UI entry page body, preserving the behavior these routes had before this change

#### Scenario: A path with a dot in a non-final segment falls back to the entry page
- **WHEN** a client requests a frontend-owned route whose dot appears in a non-final segment or whose final segment contains a dot but resolves to no real static file
- **THEN** the server SHALL respond with the Web UI entry page body

### Requirement: API paths SHALL return 404 and never fall back to the entry page

Any request whose path starts with `/api` SHALL respond with HTTP 404. The fallback SHALL NOT serve the Web UI entry page for `/api` paths, preserving API 404 semantics so unknown API routes remain distinguishable from frontend routes.

#### Scenario: Unknown API route returns 404
- **WHEN** a client requests an API path that has no matching endpoint, e.g. `/api/missing-route`
- **THEN** the server SHALL respond with HTTP 404 and SHALL NOT serve the Web UI entry page body

#### Scenario: Existing API route is unaffected
- **WHEN** a client requests a known API path
- **THEN** the server SHALL dispatch it to its endpoint as before; the fallback change SHALL NOT alter API request handling

### Requirement: OTLP system paths SHALL return 404 and never fall back to the entry page

Any request whose path starts with `/otel/v1` SHALL respond with HTTP 404. The fallback SHALL NOT serve the Web UI entry page for `/otel/v1` paths, preserving the OTLP port-surface isolation enforced elsewhere so the main port never leaks that an OTLP listener exists.

#### Scenario: OTLP path on the main port returns 404
- **WHEN** a client requests a path under `/otel/v1` such as `/otel/v1/traces`
- **THEN** the server SHALL respond with HTTP 404 and SHALL NOT serve the Web UI entry page body

### Requirement: Real static assets SHALL be served unchanged ahead of the fallback

The static-files middleware, which runs before the fallback, SHALL continue to serve real static assets (scripts, stylesheets, icons, and other bundled files) with their correct content types and bytes. Asset loading behavior SHALL be unchanged by the fallback change.

#### Scenario: Real static asset is served with its content type
- **WHEN** a client requests a path that resolves to a real static asset (e.g. a bundled script or stylesheet file present in the web content root)
- **THEN** the static-files middleware SHALL serve that asset with its correct content type and body, and the request SHALL NOT reach the fallback

#### Scenario: A missing file-like path that is not a known asset falls back to the entry page
- **WHEN** a client requests a file-like path (a path with an extension-like final segment) that does not resolve to a real static asset
- **THEN** the server SHALL respond with the Web UI entry page body rather than 404
