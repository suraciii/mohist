## Why

The SPA has three routing bugs that degrade user experience: navigating to a nonexistent issue shows an infinite "Loading..." spinner (because `useIssue` errors are not handled), `IssueCard` uses native `<a>` tags instead of React Router `<Link>` causing full page reloads on every navigation, and there is no catch-all 404 route so invalid paths render a blank page.

## What Changes

- Handle 404/error states in `IssueDetailPage.tsx` — destruct `isError`/`error` from `useIssue`, show a "Not Found" message instead of infinite loading
- Replace `<a href>` in `IssueCard.tsx` with React Router `<Link>` to preserve SPA navigation
- Add a catch-all `*` route in `App.tsx` that renders a 404 page component

## Capabilities

### New Capabilities

- `not-found-page`: A reusable 404 page component shown for invalid routes and missing issues

### Modified Capabilities

- `web-ui`: Issue detail page error handling (404 from API), SPA-compliant navigation links, catch-all route

## Impact

- `packages/cli/web/src/components/IssueDetailPage.tsx` — error state handling
- `packages/cli/web/src/components/IssueCard.tsx` — `<a>` → `<Link>`
- `packages/cli/web/src/App.tsx` — catch-all route
- New component: `NotFoundPage.tsx`
