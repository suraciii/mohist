## Why

The Web UI provides no user feedback for async operations — mutations succeed or fail silently, and users have no way to notice background events (agent completing, approval needed, errors) without keeping the page visible. Toast notifications and dynamic page titles are standard UX patterns that close this gap.

## What Changes

- Add a toast notification system (using sonner) to surface mutation results and SSE-driven events (success, error, info)
- Add dynamic `document.title` that reflects the current page context and active background activity (e.g., "● Issue #47 — Mohist" when an agent is running)
- Wire toast triggers into existing mutations (`useMutation` calls in `useQueries.ts`) and key SSE events (`agent_paused`, `agent_error`, `rebase_conflict`, pipeline stage transitions)

## Capabilities

### New Capabilities

- `toast-notifications`: Global toast notification system — toast provider component, toast trigger API, integration with mutations and SSE events
- `dynamic-page-title`: Dynamic `document.title` management — hook to set/update title based on route context and live agent activity

### Modified Capabilities

- `web-ui`: Mutation hooks in `useQueries.ts` will call toast triggers on success/error; SSE handler will emit toasts for key background events

## Impact

- **Frontend**: New dependency `sonner`; new components/hooks in `web/src/`; modifications to `App.tsx` (add toast provider), `useQueries.ts` (toast on mutation results), `useSSE.tsx` (toast on background events)
- **No backend changes**: All data needed is already available via existing APIs and SSE events
