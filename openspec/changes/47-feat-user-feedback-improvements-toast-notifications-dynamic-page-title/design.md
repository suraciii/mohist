## Context

The WebUI (`packages/cli/web/`) is a React 19 + Vite + Tailwind v4 SPA. It uses `@tanstack/react-query` for data fetching and a custom SSE hook (`useSSE.tsx`) for real-time events. Currently mutations succeed/fail silently, and the page title is hardcoded as "mohist" in `index.html`. There is no toast library installed. The web frontend lives in `packages/cli/web/` with its own `package.json` separate from the backend.

## Goals / Non-Goals

**Goals:**
- Add `sonner` as the toast library — zero-config, headless-compatible, small bundle
- Show success/error toasts on all user-initiated mutations
- Show info/error toasts for key SSE background events (only when not viewing that issue)
- Dynamic `document.title` based on route + agent activity indicator

**Non-Goals:**
- Custom toast styling/theming beyond sonner defaults
- Toast for SSE events that already have inline UI (e.g., approval panel on current issue)
- Backend API changes
- Notification sound or browser Notification API

## Decisions

### D1: Use sonner directly (re-export `toast`)

Use sonner's exported `toast` function directly rather than wrapping it in a context/hook. Sonner's `toast` is a module-level function that works without context. The `<Toaster />` component just needs to be mounted once.

**Why:** Simplest integration — no context provider, no hook wrapper overhead. Just `import { toast } from 'sonner'` and call it from anywhere (mutations, SSE handler).

**Alternatives considered:**
- *Custom hook wrapper (`useToast`)*: Adds indirection with no benefit since sonner already provides a stable imperative API
- *react-hot-toast*: Older API, slightly larger, no significant advantage
- *react-toastify*: Heavier, more opinionated styling, overkill for our needs

### D2: Mount `<Toaster />` in `App.tsx` inside `BrowserRouter`

Place `<Toaster />` in `App.tsx` within the `<BrowserRouter>` tree but at the top level of `AppContent`, alongside `<Header>`.

**Why:** Toasts need to work from any route. Mounting inside `BrowserRouter` ensures the router context is available (not strictly needed by sonner, but keeps the component tree flat). Mounting at `AppContent` level avoids needing to touch `main.tsx`.

### D3: Add toast calls directly in mutation hooks' `onSuccess`/`onError`

Each mutation hook in `useQueries.ts` already has `onSuccess` callbacks. Add `toast.success(message)` to existing `onSuccess` and add `onError` with `toast.error(message)` where missing.

**Why:** Keeps toast logic co-located with the mutation definition. No wrapper abstraction needed — each hook is a 3-10 line function already.

**Alternatives considered:**
- *Global QueryCache meta pattern*: React Query supports `meta.toast` on mutations via the `QueryClient`. This is elegant but adds indirection and requires a custom `QueryClient` config. Direct calls are more explicit and debuggable.

### D4: SSE toast integration via ref to current route in `useSSE.tsx`

The SSE handler in `useSSE.tsx` needs to know the current issue being viewed to suppress duplicate toasts. Use a `useRef` to track the current issue number from the URL (parsed from `window.location.pathname`), checked inside `handleEvent`.

**Why:** `useSSE` is inside `LiveTaskProvider` which is outside the `Routes` tree, so `useParams()` is unavailable. Reading from `window.location` via ref is simple and avoids restructuring the component tree.

**Alternatives considered:**
- *React context for current issue*: Would require lifting state up or restructuring providers
- *useParams via router*: Not available outside `<Routes>` — would require moving `LiveTaskProvider` inside routes

### D5: `useDocumentTitle` hook with route + agent status

Create a `useDocumentTitle(title: string, active?: boolean)` hook that:
1. Sets `document.title = (active ? '● ' : '') + title`
2. Restores previous title on unmount via a `useRef`

Call this hook from each page component (KanbanView, IssueDetailPage, SessionPage, etc.) with the appropriate title derived from route params. Each component reads `useAgentStatus()` and passes `active` when an agent is running.

**Why:** Co-locating title logic with the page component is simpler than a centralized route-to-title mapper. Each component already knows its context (issue number, session id, etc.).

**Alternatives considered:**
- *Centralized title manager in AppContent*: A single `useEffect` in `AppContent` that maps `location.pathname` to titles. Simpler for pure route mapping but harder to inject page-specific data (issue title, session id). Fragile regex routing.

## Risks / Trade-offs

- **[Toast spam on rapid SSE events]** → Mitigate with sonner's built-in dedup/throttle. For events like `merge_completed`, each event has a unique issue number so duplicates are unlikely.
- **[Stale `window.location` ref in SSE handler]** → The ref is read synchronously in the SSE callback, so it always reflects the latest navigation. No race condition.
- **[Bundle size increase]** → sonner is ~3KB gzipped. Negligible.

## Migration Plan

1. `cd packages/cli/web && npm install sonner`
2. Add `<Toaster />` to `AppContent` in `App.tsx`
3. Create `web/src/hooks/useDocumentTitle.ts`
4. Add `toast.success()` / `toast.error()` calls to each mutation hook in `useQueries.ts`
5. Add toast calls in `useSSE.tsx` for the 5 SSE event types, with current-issue suppression
6. Add `useDocumentTitle` calls to each page component
7. Build + test: `cd packages/cli && npm run build && npm test`

No rollback complexity — this is purely additive frontend code. Reverting the commit removes all changes cleanly.
