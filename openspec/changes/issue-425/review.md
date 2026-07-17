# Review Report

## Result: FAIL

The post-build candidate snapshot contains **zero product code changes**. `git diff master --name-only` lists only five files, all under `openspec/changes/issue-425/` (proposal.md, design.md, tasks.json, self-review.md, specs/web-hosting-fallback/spec.md). The single implementation site the design calls for — `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistWebRegistration.cs` — is byte-identical to master, and the spec/test extensions the task graph requires were never added. The primary acceptance criterion is demonstrably unmet: a direct GET to a dotted session-name deep link still returns HTTP 404.

## Repaired Items

None. No safe in-place repair is possible because the entire defect is a missing product-behavior change; implementing the fallback fix and its tests is the change itself, which the repair policy excludes.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistWebRegistration.cs:26`
  Evidence: The fix was never applied. The registration is still `app.MapFallback(async context =>` (the `MapFallback(RequestDelegate)` overload), which uses the default `{*path:nonfile}` route constraint — the exact root cause the design (`design.md` Decision 1) and proposal identify. There is only one `MapFallback` call in the whole server codebase (`rg -n "MapFallback" --type cs`), and it is unchanged from master. Concrete reproduction: a temporary spec test requesting `/issues/12/workflow/sessions/T-001.1` against the candidate returned `NotFound` (404) —
  ```
  Assert.Equal() Failure: Values differ
  Expected: OK
  Actual:   NotFound
  ```
  This directly violates the issue's first acceptance criterion ("直接打开或刷新含点号 session 名的页面 URL，正常渲染对应 session 页面") and `specs/web-hosting-fallback/spec.md` Scenario 1. Note: I verified the design's proposed remedy is correct by temporarily applying `app.MapFallback("{*path}", async context => …)` and re-running the repro — the dotted path then returned HTTP 200 with the `index.html` body, while `/api/missing-route` and `/otel/v1/traces` still returned 404, and the existing dot-free fallback test still passed. The plan is sound; it was simply never built. Both temporary edits were reverted; `git status` is clean. [disallowed:reason — implementing the fix is a product behavior change, excluded by repair policy]
  SuggestedAction: Apply the design's Decision 1: change `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistWebRegistration.cs:26` from `app.MapFallback(async context =>` to `app.MapFallback("{*path}", async context =>`, leaving the handler body (the `/api` + `/otel/v1` 404 carve-outs and `SendIndexAsync` dispatch) untouched.
  Verification: `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj` with an assertion that GET `/issues/12/workflow/sessions/T-001.1` returns 200 + `text/html`; I confirmed this passes with the one-line fix in place.
  Status: unresolved

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/SystemSpecs/RuntimeEntrySpecs.cs:34-43`
  Evidence: None of the acceptance-criteria tests defined in `tasks.json` T-001 exist. `WebRoot_WhenConfigured_ServesIndexAndSpaFallback` (lines 34-43) is unchanged from master and still asserts only dot-free paths (`/`, `/issues/1`, `/issues/1/workflow/sessions/plan`). There is no assertion for a dotted session name, no `/otel/v1` fallback assertion, and no real-static-asset assertion. `InMemoryWebContentProvider` (`Support/InMemoryWebContentProvider.cs`) still seeds only `index.html`; the sample asset file the task notes say to add was never added. The T-001 acceptance criteria ("A direct GET to /issues/12/workflow/sessions/T-001.1 returns HTTP 200…", "A path under /otel/v1 … returns HTTP 404", "A real static asset … is served with its correct content type", "A file-like path … is served the entry page body") are all unimplemented and unverifiable. [disallowed:reason — adding these tests is the test portion of the product change, excluded by repair policy]
  SuggestedAction: Extend `WebRoot_WhenConfigured_ServesIndexAndSpaFallback` (or add sibling facts) to cover the dotted deep link (200 + entry-page body), a `/otel/v1/traces` 404, a real static asset served with its content type (seed a sample asset in `InMemoryWebContentProvider`), and a missing file-like path falling back to the entry page, per `tasks.json` T-001 acceptance criteria.
  Verification: `npm test` (server) is green and the new assertions pass.
  Status: unresolved

- [ID: item-3]
  Severity: blocking
  Scope: `openspec/changes/issue-425/self-review.md`
  Evidence: `self-review.md` records a PASS verdict but it reviewed plan artifacts, not built code. Its own Repaired Items section states "All artifacts were reviewed against the issue and each other" — artifacts, not the implementation. Its Notes even claim "the change site (`MohistWebRegistration.cs`) matches the design's single-line claim," conflating "the file described in the design exists" with "the change was applied." Because the self-review is a workflow gate, a PASS here lets the change advance to Integrate with no implementation and no tests. This is a concrete workflow and traceability risk: the verdict is contradicted by the candidate snapshot (item-1, item-2). [disallowed:reason — correcting the verdict is a workflow-judgment change, not a local repair]
  SuggestedAction: After the build actually applies the code fix and tests (item-1, item-2), re-run self-review against the built candidate and only then record PASS/FAIL consistent with the code state. The current self-review's PASS must not stand while the implementation is absent.
  Verification: Re-read `MohistWebRegistration.cs:26` and `RuntimeEntrySpecs.cs`; confirm the self-review verdict matches the post-build code, not the pre-build plan.
  Status: unresolved

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: `openspec/changes/issue-425/design.md`, `proposal.md`, `specs/web-hosting-fallback/spec.md`, `tasks.json`
  Evidence: The plan itself is high quality and verified correct. The root-cause analysis (`:nonfile` constraint on the default `MapFallback(RequestDelegate)` overload) is accurate, and the proposed remedy (`MapFallback("{*path}", handler)`) is confirmed working — including preservation of the `/api` and `/otel/v1` carve-outs and unchanged real-asset serving via `UseStaticFiles` ahead of the fallback. The spec's four requirements map cleanly to the issue's acceptance criteria.
  SuggestedAction: When the build stage runs against this plan, no redesign is needed — execute T-001 as written. No changes to the plan artifacts are required for correctness.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistWebRegistration.cs`
  Evidence: The fallback handler relies on `path.StartsWithSegments("/api" …)` and `path.StartsWithSegments("/otel/v1" …)` for the 404 carve-outs. This is the documented, intended design and is unchanged by this issue. It is noted only because any future API/system endpoint not under those prefixes would be swallowed by the SPA fallback — a pre-existing condition the design's Risk section already calls out, not introduced here.
  SuggestedAction: None for this change.
  Status: pre-existing

<promise>FAIL</promise>
