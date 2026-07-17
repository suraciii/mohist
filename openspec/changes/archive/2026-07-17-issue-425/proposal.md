## Why

The Web UI is a single-page app whose invariant is: any address owned by a frontend route must render the app on a hard refresh or direct paste, with the browser handing off to the client router. Today that invariant breaks for session names containing a dot. Workflow task sessions are named like `T-001.1`, so **every** task-session deep link (`/…/issues/<n>/workflow/sessions/T-001.1`) returns a server 404 on refresh or direct open — reachable only by in-app clicks. This affects the whole session-browsing experience (epic #49), not isolated links, and it's a hard break of the SPA contract.

The root cause is the server's fallback registration: `MapFallback(handler)` defaults to a `{*path:nonfile}` route constraint, which silently excludes any path whose final segment looks like it has a file extension. A dotted session name matches that "file-like" shape, so the request never reaches the fallback and the (missing) static-file lookup returns 404.

## What Changes

- Fix the SPA fallback so that, **except for API and system endpoints**, every request that does not resolve to a real static file is served the app entry page (`index.html`), regardless of dots in any path segment. This removes the accidental "file-extension" exclusion that dotted session names trip.
- Preserve the existing explicit 404 carve-outs: paths under `/api` and `/otel/v1` keep returning 404 and never fall back to the entry page (preserves API 404 semantics and the OTLP port-surface isolation the `OtelPortIsolationMiddleware` already enforces).
- Real static assets (scripts, styles, icons) keep being served by the static-files middleware, which runs ahead of the fallback, so asset loading is unaffected.
- No change to session naming, no change to the frontend route structure, no change to any API behavior or data.

## Capabilities

- `web-hosting-fallback`: The server's SPA static-hosting fallback — the rule deciding which requests receive the Web UI entry page (`index.html`) vs. a 404. Covers the invariant that all frontend-owned routes fall back to the entry page on direct open/refresh (including dot-containing path segments such as `T-001.1`), while API (`/api`) and system (`/otel/v1`) paths remain 404 and never serve the entry page, and real static assets are served unchanged by the static-files middleware ahead of the fallback.

## Impact

- **Server (`packages/server`)**:
  - `src/Mohist.Server/Infrastructure/Hosting/MohistWebRegistration.cs` — the `MapFallback` registration is the single change site; the default `nonfile`-constrained catch-all must become an explicit catch-all that serves the entry page for every non-`/api`/non-`/otel/v1` request. The in-handler `/api` and `/otel/v1` 404 carve-outs already exist and are kept.
- **Web / runner / CLI**: no changes. The frontend route `issues/:number/workflow/sessions/:sessionName` (`packages/web/src/app/App.tsx:68`) already handles dotted names; the bug is purely server-side fallback dispatch.
- **Dependencies**: none.
- **Tests**: extend the existing SPA-fallback spec (`packages/server/tests/Mohist.Server.SpecTests/Specs/SystemSpecs/RuntimeEntrySpecs.cs`, `WebRoot_WhenConfigured_ServesIndexAndSpaFallback`) — currently only asserts the dot-free `plan` session — to assert a dotted session name (`T-001.1`) also serves the entry page, and assert a real static-asset path and an unknown `/api` path keep their current behavior. The `/otel/v1` 404 isolation specs and `ApiFallback_WhenUnknownApiPath_ReturnsNotFound` must continue to pass.
