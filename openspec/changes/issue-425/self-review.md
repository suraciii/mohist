# Self Review Report

## Result: PASS

## Repaired Items

None. The built candidate was reviewed against the issue, the spec, the design, and the task graph; no safe in-place repairs were required.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: info
  Scope: completeness
  Evidence: Verified traceability between the four spec requirements in `specs/web-hosting-fallback/spec.md` and the single task `T-001`. The task's `spec` field points at the primary requirement (`#requirement-frontend-owned-routes-shall-fall-back-to-the-web-ui-entry-page`, confirmed to resolve to the actual heading), while its acceptance criteria enumerate explicit coverage of all four requirements: dotted deep link (Req 1), dot-free routes (Req 1), unknown `/api` 404 (Req 2), `/otel/v1` 404 (Req 3), real-asset serving + missing file-like path (Req 4). No gap found.
  SuggestedAction: None — acceptable as-is. (If desired later, the task `spec` field could list multiple requirement anchors, but single-primary-anchor with full AC coverage is sufficient.)
  Status: follow-up

- [ID: item-2]
  Severity: info
  Scope: feasibility
  Evidence: Verified task granularity. The change is a single tightly-coupled functional slice (the SPA fallback dispatch rule plus its in-task tests). It is not over-split — there is no standalone "test task", no "define interface / register DI" micro-task, and the title is outcome-focused ("Serve Web UI entry page for dotted-path SPA fallback requests"). Splitting this into smaller tasks would violate the "no separate test tasks" and "merge tightly-coupled changes" principles.
  SuggestedAction: None.
  Status: follow-up

- [ID: item-3]
  Severity: info
  Scope: dependencies
  Evidence: Verified the dependency graph. With a single task and empty `dependsOn`, the graph is trivially acyclic; the `dependsOn` rule ("point only to existing IDs with strictly lower priority") holds vacuously. No cycles.
  SuggestedAction: None.
  Status: follow-up

## Notes

- Implementation verified against the built candidate, not just the plan artifacts. `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistWebRegistration.cs:26` registers `app.MapFallback("{*path:notstaticfile}", async context => …)`. This drops the implicit `:nonfile` constraint (so dotted session names reach the handler) but adds a `notstaticfile` constraint so paths that resolve to a real static file never match the fallback — letting `UseStaticFiles` keep serving real assets unchanged. The handler body is unchanged: the `/api` and `/otel/v1` 404 carve-outs and the `index.html` dispatch are intact.
- Deviation from design Decision 1, with reason. The design proposed a plain `app.MapFallback("{*path}", handler)`. Verified empirically (pipeline probing in the integration fixture) that a blanket catch-all is insufficient: in this hosting pipeline `UseStaticFiles` defers when the fallback endpoint is selected, so a catch-all would swallow real static assets (e.g. `/assets/app.css` returns `index.html` instead of `text/css`), breaking the issue's "real static assets unchanged" criterion. The `:nonfile` default is what currently lets assets reach `UseStaticFiles`. The constraint approach achieves the design's stated goal — "every request that does not resolve to a real static file is served the entry page, regardless of dots" — generally (any dotted frontend route, not just the session route the design's rejected per-route mapping would cover) while preserving full `UseStaticFiles` behavior (content types, range, etag) for assets. The constraint (`NotStaticFileConstraint`, `Infrastructure/Hosting/NotStaticFileConstraint.cs`) consults the same `IWebContentProvider.Files` used by `UseStaticFiles`, so the two agree on what is a real file; it is registered via `AddRouting` in `ConfigureMohistServices`.
- Spec coverage verified. `WebRoot_WhenConfigured_ServesIndexAndSpaFallback` now also asserts the dotted session name `/issues/12/workflow/sessions/T-001.1` serves the entry page. Sibling facts assert `/otel/v1/traces` 404s, a real static asset (`/assets/app.css`) is served with `text/css` ahead of the fallback, and a missing file-like path (`/assets/missing.js`) falls back to the entry page. The unknown-`/api` 404 stays covered by `ApiFallback_WhenUnknownApiPath_ReturnsNotFound`. `InMemoryWebContentProvider` seeds the `assets/app.css` sample asset.
- Cross-document consistency confirmed: the capability name `web-hosting-fallback` is identical across `proposal.md` (Capabilities), `specs/web-hosting-fallback/spec.md`, `design.md`, and `tasks.json`. The spec's requirements are satisfied by the built behavior; the design's mechanism (Decision 1) is superseded by the constraint mechanism described above, which the design's own goal ("not resolve to a real static file") implies.

<promise>PASS</promise>
