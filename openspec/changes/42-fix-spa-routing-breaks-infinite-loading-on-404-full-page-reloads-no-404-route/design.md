## Context

Three distinct SPA routing bugs in the mohist web UI (React + React Router v6):

1. `IssueDetailPage.tsx:279` — the loading guard `if (isLoading || !issue)` never exits when `useIssue` returns an error. React Query sets `isError=true` but the component only checks `isLoading` and `!issue`, resulting in an infinite "Loading..." spinner.
2. `IssueCard.tsx:103` — uses `<a href>` for navigation, causing full browser page reloads on every card click.
3. `App.tsx:94-103` — no catch-all route; unmatched paths render the layout shell with blank content.

All three are in `packages/cli/web/src/`.

## Goals / Non-Goals

**Goals:**
- Fix infinite loading on API errors in IssueDetailPage
- SPA-compliant navigation from IssueCard
- Catch-all 404 route for invalid paths

**Non-Goals:**
- Server-side rendering or SSR 404 handling
- Retry logic or specialized error messages per HTTP status code
- Changing any other components that use `<a>` navigation

## Decisions

### D1: Single reusable NotFoundPage component

Create one `NotFoundPage` component used both by the catch-all route and by `IssueDetailPage` on API error. It shows a centered message ("Page not found") and a React Router `<Link to="/">` back to the board.

**Alternatives considered:**
- Separate error page vs 404 page — unnecessary complexity for the same visual result.
- Inline 404 in IssueDetailPage without a component — duplicates UI if other pages need it later.

### D2: IssueDetailPage checks isError before loading guard

Destructure `isError` from `useIssue` and add `if (isError)` return block before the existing `if (isLoading || !issue)` guard. This preserves the loading state for legitimate slow loads while immediately surfacing errors.

```tsx
const { data: issue, isLoading, isError } = useIssue(issueNumber)
// ...
if (isError) {
  return <NotFoundPage />
}
if (isLoading || !issue) {
  return <LoadingSpinner />
}
```

**Alternatives considered:**
- Use React Query `error` object to distinguish 404 vs 500 — added complexity with no UX benefit per specs; both cases show NotFoundPage.

### D3: IssueCard swaps `<a>` for React Router `<Link>`

Replace `<a href={...}>` with `<Link to={...}>` in `IssueCard.tsx:103`. The `<Link>` renders an `<a>` tag internally but intercepts clicks for client-side navigation. All existing className/style props transfer directly.

**Alternatives considered:**
- `useNavigate` + `onClick` — loses native right-click/ctrl-click semantics that `<Link>` preserves.

### D4: Catch-all route inside ProjectGuard layout

Add `<Route path="*" element={<NotFoundPage />} />` as the last child of the `<Route element={<ProjectGuard />}>` wrapper. This keeps the Header visible on 404 pages for easy navigation.

## Risks / Trade-offs

- [IssueDetailPage shows NotFoundPage for any API error, not just 404] → Acceptable per specs; the user can navigate back and retry.
- [IssueCard `<Link>` may conflict with nested `onClick` handlers (e.g., "Resume" button)] → The existing buttons use `e.preventDefault()` + `e.stopPropagation()`, which correctly prevents Link navigation.

## Migration Plan

No migration needed — this is a bug fix. Deploy in a single commit.
