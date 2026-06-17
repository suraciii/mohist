## Why

The Settings page's 6 tabs suffer from 4 user-visible UX inconsistencies that erode trust in one of the app's primary configuration surfaces: users can see the Workflows profile list but clicking a card 404s; the main content area duplicates the global sidebar header (title + New Issue) creating visual redundancy; save feedback is fragmented (some tabs show banners, others fail silently); and the Coder Agent tab shows a 3-column Runtime/Command/Models summary that duplicates System Install. These are low-risk, frontend-only cleanups bundled into one PR. The backend is correct and untouched — the workflow 404 is a client-side URL-encoding mistake.

## What Changes

- **Workflows detail 404 fix**: Remove `encodeURIComponent(id)` from `getWorkflowProfile` so `mohist/default` resolves to the literal path `/workflow-templates/system/mohist/default`, which the backend `{*id}` catch-all route already matches. One-line change, backend unchanged.
- **Remove duplicate main content header**: Delete the `<h1>Settings</h1>` title and the `+ New Issue` button from the Settings main content area header. The left sidebar already renders both (global navigation). Keep only the tab navigation in the main area.
- **Unified mutation feedback via sonner toast**: Replace per-section `saveSuccess` / `saveError` inline banners with `toast.success(...)` / `toast.error(...)` across Settings tabs. Add toast calls to mutations that were previously silent on failure (`useSetLogLevel`, repository add/remove/set-default, template override/reset/delete, coder model updates). Preserve inline field-level validation errors (red text under each field) — those are field validation, not mutation feedback.
- **Remove Coder Agent runtime summary**: Delete the Runtime / Command / Models 3-column block at the top of `AiSettingsSection` (redundant with System Install). Keep ModelSelect and Stage Model Overrides. Optionally show a lightweight "N models available" hint near ModelSelect to replace the Models count.
- **Preserve existing mechanisms**: The Runtime form's `unsupportedFields` backward-compat mechanism and the `useRuntimeConsistency` / `useUpdateConfig` hooks (#19 scope) remain untouched — no regression.

## Capabilities

### New Capabilities

_None._ All affected behavior already lives in the `web-ui` capability; no new capability is warranted.

### Modified Capabilities

- `web-ui`: Add requirements governing the Settings page: (1) workflow profile detail navigation SHALL resolve via the literal `/` path and not be URL-encoded; (2) the Settings main content area SHALL render only tab navigation (no duplicate title / New Issue header — that lives in the global sidebar); (3) Settings mutation feedback SHALL use sonner toasts (success/error) consistently across Repositories, Templates, System Log Level, and Coder Agent Model, while field-level validation errors SHALL remain inline; (4) the Coder Agent section SHALL NOT show the Runtime/Command/Models summary block; (5) the Runtime form `unsupportedFields` backward-compat mechanism SHALL keep working (regression guard).

## Impact

- **Scope**: Frontend only. No backend API endpoint, route, or response-shape changes.
- **Code**:
  - `packages/web/src/entities/settings/api/client.ts` — `getWorkflowProfile` URL encoding removed.
  - `packages/web/src/widgets/app-shell/ui/Header.tsx` — suppress the duplicate title `<h1>` and desktop `New Issue` button on `/settings/*` routes (the global app-shell header is where these elements physically render; `SettingsPage.tsx` is unchanged). Keep `SidebarTrigger` and the sidebar's own New Issue.
  - `packages/web/src/pages/settings/ui/AiSettingsSection.tsx` — remove Runtime/Command/Models 3-column block (lines ~68-86).
  - `packages/web/src/pages/settings/ui/AgentSettingsSection.tsx` — remove `saveSuccess`/`saveError` inline banners; keep field-level `validationErrors`.
  - `packages/web/src/entities/settings/api/queries.ts` — **verify** (not add) that repository, template, log-level, and coder-model mutation hooks already call `toast.success`/`toast.error`; the audit confirmed they do, so this is a consistency check, with the actual edit confined to `AgentSettingsSection.tsx`.
- **Tests**: `SettingsPage.test.tsx` must keep passing (incl. `useRepositoriesMock('proj-selected')` project-routing assertion). New regression coverage for toast feedback and `unsupportedFields`.
- **Dependencies**: Weak prereqs #113 (Popover) and #121 (agent.model resolution) affect whether the Coder Agent model toast can be fully runtime-verified; #19 affects Runtime mutation feedback verification but does not block the other three changes.
- **Out of scope** (explicit): backend endpoints, #19 Runtime/System API fixes, Card visual redesign (#116), global toast config, a `useToastMutation` abstraction, and any change to the `unsupportedFields` mechanism.
