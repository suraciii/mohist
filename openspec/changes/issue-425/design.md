## Context

The Web UI is a single-page application served by the ASP.NET Core server. Its invariant: any address owned by a frontend route must render the app on a hard refresh or direct paste, with the browser handing off to the client router. Today that breaks for every workflow task-session deep link (`/…/issues/<n>/workflow/sessions/T-001.1`), because workflow task sessions are named with a dot (`T-001.1`) and the server returns 404 on refresh/direct open. The pages are reachable only via in-app clicks.

The single hosting registration lives in `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistWebRegistration.cs`. The current pipeline is:

1. `UseStaticFiles` with a `FileExtensionContentTypeProvider` — serves real static assets from the web content root.
2. `app.MapFallback(async context => …)` — the SPA fallback. Inside the handler, paths starting with `/api` or `/otel/v1` return 404; everything else is served `index.html`.

The bug is in step 2's *registration*, not its handler. `MapFallback(RequestDelegate)` defaults to the route pattern `{*path:nonfile}`. Per ASP.NET Core's documented behavior, the `nonfile` constraint "ensures that a string parameter does not have a dot in its last path segment followed by one or more non-dot characters" — it excludes file-like paths. A dotted session name matches that shape, so the request never reaches the fallback handler at all: the static-files middleware finds no such file and calls next, endpoint routing finds no controller, and the fallback endpoint is the only candidate — but `nonfile` rejects the dotted path, so nothing matches and the default is a 404.

Constraints: low-risk, no API changes, no data migration, no frontend route changes. The existing `/api` and `/otel/v1` 404 carve-outs (and the `OtelPortIsolationMiddleware`, which independently 404s `/otel/v1` on the main port) must keep working. See the proposal for motivation and `specs/web-hosting-fallback/spec.md` for the required behavior.

## Goals / Non-Goals

**Goals:**
- Make the SPA fallback serve `index.html` for every non-`/api`/non-`/otel/v1` request that doesn't resolve to a real static file, **regardless of dots** in any path segment — so dotted session deep links work on refresh/direct open.
- Preserve the `/api` and `/otel/v1` 404 carve-outs and real static-asset serving exactly.
- Keep the change to one registration site with minimal blast radius.

**Non-Goals:**
- No change to the session naming scheme (dotted names like `T-001.1` stay).
- No frontend route restructure, and no URL encoding of session names.
- No static-asset caching strategy changes.

## Decisions

### Decision 1: Replace the default `nonfile`-constrained fallback with an explicit catch-all pattern

Change `app.MapFallback(async context => { … })` to `app.MapFallback("{*path}", async context => { … })`, keeping the handler body byte-for-byte the same.

**Rationale.** This is the documented ASP.NET Core remedy for "dots in route parameters": supply an explicit pattern so the implicit `:nonfile` constraint is dropped. The handler already implements the `/api` + `/otel/v1` 404 carve-outs and the `index.html` dispatch, so the only behavior change is *which* paths reach the handler — now all unmatched paths, including dotted ones.

**Alternatives considered:**
- *`MapFallbackToFile("{*path}", "index.html")`.* Built-in and terse, but it drops the in-handler carve-outs: `/api` and `/otel/v1` would need to be re-introduced as a preceding 404-producing step (separate mapped endpoints or an earlier middleware branch), adding churn and a second place that encodes the API/system boundary. The custom handler is also explicit about `text/html; charset=utf-8` and `ContentLength`. Rejected as more disruptive than the one-token pattern fix.
- *Per-route fallback mappings* (e.g. `app.MapFallbackToFile("/issues/{n}/workflow/sessions/{name}", "index.html")` ahead of the catch-all). Route-specific and brittle — every future dotted frontend route needs its own mapping. Rejected.
- *Encode session names in the frontend router* to remove the dot. Explicitly excluded by the issue's Non-Goals; it changes user-visible URLs and requires encode/decode round-trips everywhere a session name appears. Rejected.

### Decision 2: Keep the `/api` and `/otel/v1` carve-outs inside the fallback handler

Leave the `path.StartsWithSegments("/api")` / `path.StartsWithSegments("/otel/v1")` 404 branch exactly where it is. It already satisfies the spec's "API paths return 404" and "OTLP paths return 404" requirements, it's already covered by `ApiFallback_WhenUnknownApiPath_ReturnsNotFound` and the OTLP isolation specs, and it stays belt-and-suspenders alongside `OtelPortIsolationMiddleware`. Moving it would be gratuitous churn.

### Decision 3: Rely on pipeline ordering — static files still run first

`UseStaticFiles` is registered before the fallback, so a request for a real asset is served and never reaches the fallback. No change to that ordering is needed; the fallback only ever sees requests the static-files middleware already passed through.

## Risks / Trade-offs

- `[A missing/typo'd file-like path (e.g. /assets/nope.js) now returns index.html instead of 404]` -> Accept and document. This is standard SPA fallback behavior; the client router renders the appropriate page (or a not-found view). The alternative (re-introducing a file-extension allowlist) reintroduces exactly the class of bug we're fixing, so we deliberately do not exclude file-like paths.
- `[Removing nonfile widens the fallback's match set]` -> Mitigated by the explicit `/api` and `/otel/v1` carve-outs, which keep API and OTLP 404 semantics, and by `UseStaticFiles` running first so real assets are unaffected. New spec assertions cover the dotted case, the unknown-`/api` case, and a real-asset case.
- `[A future API/system endpoint not under /api or /otel/v1 could be accidentally swallowed by the fallback]` -> Pre-existing condition, unchanged by this fix; any such endpoint is matched by its own route ahead of the fallback and never reaches it. No new exposure introduced.

## Migration Plan

1. Implement the single registration change (Decision 1); no other code changes.
2. Extend the existing SPA-fallback spec in `packages/server/tests/Mohist.Server.SpecTests/Specs/SystemSpecs/RuntimeEntrySpecs.cs` (`WebRoot_WhenConfigured_ServesIndexAndSpaFallback`) — which currently only asserts the dot-free `plan` session name — to also assert a dotted session name (`T-001.1`) serves the entry page. Add (or keep passing) assertions that an unknown `/api` path 404s and that a real static-asset path is served. The `InMemoryWebContentProvider` already seeds `index.html`, and can seed a sample asset file for the asset scenario.
3. Run `npm test` (server); confirm `OtlpRoutesIntegrationSpecs`, `OtelPortIsolationMiddlewareSpecs`, and `ApiFallback_WhenUnknownApiPath_ReturnsNotFound` still pass.
4. Deploy is a normal server restart; no data or config migration. Rollback is reverting the one line (the fallback pattern) — behavior returns to today's, where dotted deep links 404 (the pre-fix state).

## Open Questions

None. The root cause, the fix site, and the test surface are all confirmed against the code and the ASP.NET Core docs.
