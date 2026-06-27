# Review Report

## Result: FAIL

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: `packages/web/src/widgets/app-shell/ui/Header.tsx` ended without a trailing newline after the header change. Added the missing newline only; no product behavior changed.
  Verification: `npm run typecheck -w packages/web`; `npm run test:run -w packages/web -- Header.test.tsx EpicDetailPage.test.tsx App.test.tsx`
  Status: resolved

## Blocking Items

- [ID: item-2]
  Severity: blocking
  Scope: `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx`
  Evidence: The issue/spec require long English titles to wrap or break at 320px and not cause horizontal overflow (`openspec/changes/issue-277/specs/epic-detail-responsive-layout/spec.md:41`). The candidate stacks the header (`EpicDetailPage.tsx:518`) but the title remains `<h1 className="mt-2 text-2xl font-bold text-foreground">` (`EpicDetailPage.tsx:535`) with no `break-words`, `[overflow-wrap:anywhere]`, or equivalent. A long unbroken English token has a min-content width equal to the full token and can still force `documentElement.scrollWidth > clientWidth`. The same issue exists for plain description text: the description wrapper has no break rule (`EpicDetailPage.tsx:537`), and `MarkdownReader` paragraphs are only `my-3 leading-relaxed` (`packages/web/src/shared/ui/markdown-reader/MarkdownReader.tsx:208`). This is directly in scope because the issue says long English title/description must not be compressed or overflow. [disallowed:product-behavior-change]
  SuggestedAction: Add an explicit break/wrap rule to the epic title and description content path, for example `break-words` or `[overflow-wrap:anywhere]` on the title and description reader/container, then add tests that assert the class contract for both a long unbroken English title and a long plain English description.
  Verification: In a real browser, open running, idle, done, and closed Epic detail pages at 320px, 390px, and 430px with a long unbroken English title/description and verify `document.documentElement.scrollWidth <= document.documentElement.clientWidth`. Also run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx`
  Evidence: The new mobile layout tests define `LONG_ENGLISH_TITLE`, but the value contains normal spaces (`EpicDetailPage.test.tsx:2171`) and the tests only assert DOM order and container classes. They do not assert any break/wrap class on the title or description, nor do they cover an unbroken English token required by the spec (`spec.md:41`). This allowed item-2 to pass CI.
  SuggestedAction: Change the long-English fixture to include an unbroken token and assert the title/description elements carry a break rule. If browser-based tests are added later, include the actual `scrollWidth <= clientWidth` assertion there.
  Verification: Run `npm run test:run -w packages/web -- EpicDetailPage.test.tsx`; for full confidence run `npm run test:run -w packages/web`.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: mobile overflow verification
  Evidence: The acceptance criteria use a pixel-level browser condition, `documentElement.scrollWidth <= clientWidth`, across 320px, 390px, and 430px for running, idle, done, and closed Epics. The candidate only adds jsdom structural tests, which cannot compute CSS layout or real scroll widths. This limitation is documented in `openspec/changes/issue-277/design.md:72`, but it leaves the central overflow guarantee dependent on manual browser verification.
  SuggestedAction: Consider adding a small Playwright/browser layout test for this page once the project is ready to support pixel-level frontend checks.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: warning
  Scope: `packages/web/src/widgets/epic-dependency-graph/ui/DependencyGraphCanvas.tsx`
  Evidence: The spec explicitly names the dependency graph as content that must not force the page wider than mobile viewports (`spec.md:5`). The graph wrapper uses `w-full` (`DependencyGraphCanvas.tsx:86`), while internal React Flow nodes have 180px minimum widths (`MemberFlowNode.tsx:29`, `GhostFlowNode.tsx:24`). I did not find candidate changes or browser evidence proving the React Flow internals cannot contribute to mobile scroll width. This appears adjacent/pre-existing because the current change did not modify the graph.
  SuggestedAction: Include the graph-selected linked issues view in any browser overflow verification, especially with multiple linked issues and external prerequisites.
  Status: pre-existing

## Verification Summary

- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 168 files, 2434 passed, 1 skipped.
- Post-repair focused verification passed: `npm run typecheck -w packages/web`; `npm run test:run -w packages/web -- Header.test.tsx EpicDetailPage.test.tsx App.test.tsx` passed with 4 files and 111 tests.

<promise>FAIL</promise>
