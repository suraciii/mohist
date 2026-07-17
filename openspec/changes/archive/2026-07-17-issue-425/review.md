# Review Report

## Result: PASS

The post-build candidate now contains a real, working product fix (commit `39c2ffa05`), unlike the prior review which found zero implementation. The SPA-fallback bug for dotted session names is fixed, real static assets are preserved, and the `/api` + `/otel/v1` 404 carve-outs are intact. All four issue acceptance criteria are met with concrete test evidence, and the full server suite is green.

Note on mechanism: the implemented fix uses `app.MapFallback("{*path:notstaticfile}", handler)` plus a custom `NotStaticFileConstraint`, not the plain `app.MapFallback("{*path}", handler)` described in `design.md` Decision 1. This deviation is sound — a blanket catch-all was verified (during the build stage) to regress static-asset serving in this hosting pipeline, because `UseStaticFiles` defers once the catch-all fallback endpoint is selected. The constraint approach achieves the design's stated goal ("every request that does not resolve to a real static file is served the entry page, regardless of dots") while preserving full `UseStaticFiles` behavior for assets. The deviation is documented in `self-review.md`.

## Repaired Items

None. The candidate is clean; no safe in-place repairs were required. (The candidate's prior blocking defects — missing implementation and missing tests — were already resolved by the build/fix-review stages and are verified below.)

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `openspec/changes/issue-425/design.md` (Decision 1)
  Evidence: Decision 1 still prescribes `app.MapFallback("{*path}", handler)` (a plain catch-all) as the fix. That mechanism was proven during build to break static assets and is NOT what is implemented (`{*path:notstaticfile}` + `NotStaticFileConstraint`). The traceability gap is mitigated — `self-review.md` documents the deviation and reason in detail — so it is not a workflow/merge blocker, but the design doc now describes a mechanism that was rejected, which could mislead a future reader.
  SuggestedAction: Amend Decision 1 (and the Migration Plan step 1) to record the actual decision: `{*path:notstaticfile}` with a constraint that consults `IWebContentProvider.Files`, plus a note on why the plain catch-all was rejected (static-files endpoint-defer).
  Status: follow-up

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Hosting/NotStaticFileConstraint.cs`
  Evidence: The new constraint is exercised end-to-end by the SPA-fallback spec facts, but it has no dedicated unit test. Its defensive branches (`httpContext` null, `IWebContentProvider` unresolved, non-string/empty route value, `UrlGeneration` direction) are not directly covered. These are unlikely paths in production, but the constraint is the single new unit of logic in this change.
  SuggestedAction: Add a focused unit test for `NotStaticFileConstraint.Match` covering the file-exists (exclude) and file-missing (match) outcomes plus the defensive fall-throughs. Not required for correctness — integration coverage is meaningful.
  Status: follow-up

- [ID: item-3]
  Severity: test-gap
  Scope: `openspec/changes/issue-425/specs/web-hosting-fallback/spec.md` (Requirement 1, Scenario 3)
  Evidence: Spec Scenario 3 ("a dot in a non-final segment falls back to the entry page") has no dedicated test. The implemented constraint checks the whole path (not just the last segment), so the behavior is covered by the constraint logic, but the specific scenario is not asserted. Low risk: this path shape is not produced by current frontend routes.
  SuggestedAction: Optionally add a case (e.g. `/a.b/session`) asserting the entry page is served, for full spec scenario coverage.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Hosting/NotStaticFileConstraint.cs:37` vs `UseStaticFiles`
  Evidence: The constraint treats "real static file" as `files.GetFileInfo(path).Exists`, while `UseStaticFiles` additionally requires a known content type (`ServeUnknownFileTypes` defaults to false). For a file that exists but has an unknown extension, the constraint would exclude it from the fallback (Exists=true) and `UseStaticFiles` would also decline to serve it (unknown type), yielding a 404. This is an extreme edge case (web bundles use known types: .js/.css/.woff2/.png/…) and matches the pre-existing static-files semantics; it is noted only for completeness, not as a regression introduced here.
  SuggestedAction: None.
  Status: pre-existing

## Acceptance Criteria Verification

1. Dotted session deep link renders — `SpaFallback_WhenDottedSessionDeepLink_ReturnsHtmlEntryPoint` + dotted case in `WebRoot_WhenConfigured_ServesIndexAndSpaFallback`: GET `/issues/12/workflow/sessions/T-001.1` → HTTP 200, `text/html`, entry-page body. The client router then renders the session page. ✅
2. Dot-free frontend routes unchanged — `WebRoot_WhenConfigured_ServesIndexAndSpaFallback`: `/`, `/issues/1`, `/issues/1/workflow/sessions/plan` → entry-page body. ✅
3. Real static assets unchanged — `SpaFallback_WhenRealStaticAsset_ServedAheadOfFallback`: `/assets/app.css` → HTTP 200, `text/css`, asset body `body{color:red}` (served by `UseStaticFiles`, not the fallback). ✅
4. API 404 semantics unchanged — `ApiFallback_WhenUnknownApiPath_ReturnsNotFound`: `/api/missing-route` → 404; `SpaFallback_WhenOtelV1Path_ReturnsNotFound`: `/otel/v1/traces` → 404 (carve-outs preserved in the unchanged handler body). ✅

## Verification

- Build: `dotnet build Mohist.sln -p:SkipWebBuild=true` → 0 warnings, 0 errors (C# lint via TreatWarningsAsErrors).
- Tests: `Mohist.Server.SpecTests` 2705 passed; `Mohist.Server.UnitTests` 1063 passed; `Mohist.Server.ArchTests` 28 passed. Targeted SPA/API/OTLP subset: 24 passed.

<promise>PASS</promise>
