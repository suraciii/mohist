## Context

The backend already stores a per-project default workflow template in `ProjectWorkflowProfile.DefaultTemplateId` and exposes it through three endpoints in `packages/server/src/Mohist.Server/Api/ProjectRoutes.cs`:

- `GET /api/projects/{projectRef}/workflow-profile` → `{ projectId, defaultTemplateId (string|null), variables }` (line 225)
- `PUT /api/projects/{projectRef}/workflow-profile/default-template` body `{ templateId }` → `{ projectId, defaultTemplateId }` (line 233)
- `DELETE /api/projects/{projectRef}/workflow-profile/default-template` → `{ projectId, defaultTemplateId: null }` (line 245)

These endpoints are **already implemented, have no authorization, and require no server change** for this issue (confirmed: no `RequireAuthorization` anywhere in the server). The `defaultTemplateId` wire field is camelCase and **nullable** — `null` means "no project default configured; inherit system default".

The Web UI, however, never reads or writes these endpoints. Settings → Workflows (`pages/settings/ui/WorkflowProfilesSection.tsx`) is a read-only system catalog built solely from `useWorkflowProfiles()` (the global `/workflow-templates/system` source). Two problems follow:

1. **No project default control.** Users cannot configure what new issues inherit; every non-default policy needs a per-issue override.
2. **Ambiguous "Default" badge.** The static `isDefault` flag on `mohist/default` (system-default *metadata*) is rendered as a green "Default" badge in both the card (`WorkflowProfilesSection.tsx:138`) and detail (`:86`) views, indistinguishable from a project's *configured* default.

The system-default resolution is also hardcoded in two consumer surfaces via `workflowProfiles.find((p) => p.isDefault)`:

- `features/create-issue/ui/CreateIssueDialog.tsx:225` (fallback for the workflow `<select>`)
- `widgets/issue-workflow/ui/WorkflowProfileControl.tsx:47` (per-issue profile selector)

Both ignore any configured project default.

**Stakeholders:** single-user local-first system; the active project is obtained from `useProject()` (`entities/project/model/ProjectContext.tsx`), where `projectId === projectRef`. Existing conventions for project-scoped TanStack Query hooks are established by `useOpencodeModel`/`useUpdateOpencodeModel` (`entities/settings/api/queries.ts:60-83`) and `useSetDefaultRepository` (`entities/project/api/queries.ts:52`).

## Goals / Non-Goals

**Goals:**
- Add a project default workflow control to Settings → Workflows that reads `GET .../workflow-profile`, writes `PUT .../default-template`, and clears via `DELETE .../default-template`, with readback after every write.
- Make "system default" (static `isDefault` metadata) visually distinct from "this project's configured default" (sourced from the project read model).
- Make create-issue and per-issue profile selection resolve the effective default from the **project configuration first**, falling back to the system default only when unset.
- Web tests covering readback, switching to `mohist/github-pr`, clearing, and the system-vs-project distinction.

**Non-Goals:**
- No backend changes (endpoints already exist).
- No change to workflow execution semantics or resolver precedence.
- No new workflow profile/template types; no edits to built-in YAML.
- No bulk re-targeting of existing issues when the project default changes (display concern only).

## Decisions

### Decision 1: Place new client + hooks in `entities/settings` (not `entities/project`)

**Choice:** Add `getProjectDefaultWorkflowProfile` / `setProjectDefaultWorkflowProfile` / `clearProjectDefaultWorkflowProfile` to `entities/settings/api/client.ts` and matching `useProjectDefaultWorkflowProfile` / `useSetProjectDefaultWorkflowProfile` / `useClearProjectDefaultWorkflowProfile` hooks to `entities/settings/api/queries.ts`, re-exported from `entities/settings/index.ts`.

**Rationale:** All existing project-scoped workflow settings (`useOpencodeModel`, `useStageModels`, `getProjectWorkflowVariables`) already live in `entities/settings`. Co-locating keeps the workflow-profile surface in one slice and matches the closest precedent (`useOpencodeModel`/`useUpdateOpencodeModel` is a near-exact structural twin: project-scoped GET + write mutation + toast).

**Alternatives considered:**
- `entities/project` — rejected: would split workflow-profile knowledge across two slices and duplicate the `projectApiPath` plumbing already present in `entities/settings/api/client.ts`.

### Decision 2: URL + payload mapping, minimal client model

**Choice:** Use `projectApiPath(projectId, '/workflow-profile')` for GET and `projectApiPath(projectId, '/workflow-profile/default-template')` for PUT/DELETE (helper from `shared/api/client.ts`). The GET client projects the response down to a minimal model `{ projectId: string; defaultTemplateId: string | null }` — dropping `variables`, which this feature does not need (keeps the model minimal per project guidance). PUT body is `{ templateId: string }`; DELETE has no body. These mirror the `SetDefaultTemplateRequest(string TemplateId)` DTO serialized as `templateId` (`ProjectRoutes.cs:425`, `JsonSerializerDefaults.Web`).

**Rationale:** The API is stable and already returns exactly what we need. Per AGENTS.md, the data model stays minimal.

**Alternatives considered:**
- Reuse the existing `/workflow-profile/variables` client — rejected: different sub-resource; conflating default-template with variables would couple unrelated writes.

### Decision 3: Query key + invalidation strategy

**Choice:**
- Query key for the read: `['project-workflow-profile', projectId]`, `enabled: !!projectId`.
- `useSetProjectDefaultWorkflowProfile` / `useClearProjectDefaultWorkflowProfile` `onSuccess` invalidates **only** `['project-workflow-profile', projectId]` (root form covers prefixed consumers) and emits `toast.success(...)`. `onError` → `toast.error(err.message || 'Request failed')`.

**Rationale:** The only data that changes is the project default itself. The system catalog (`['workflow-templates', 'system']`) is unaffected by project writes, so it is **not** invalidated. Consumer surfaces (create-issue, per-issue control) react automatically because their effective-default resolution (Decision 4) reads the invalidated project-default query through TanStack's cache.

**Alternatives considered:**
- Optimistic update with `setQueryData` (like `useUpdateConfig`) — rejected: the write is cheap and a server readback after invalidation is the spec requirement ("After any write or delete, the control SHALL read back"). Optimism adds rollback complexity for no UX gain here.

### Decision 4: Centralize effective-default resolution in one hook

**Choice:** Add `useEffectiveDefaultWorkflowProfile()` in `entities/settings/api/queries.ts` that combines:
1. `useProjectDefaultWorkflowProfile()` → configured `defaultTemplateId` (nullable), and
2. `useWorkflowProfiles()` → system catalog, to derive the system default as `profiles.find((p) => p.isDefault)?.id ?? 'mohist/default'`.

It returns `{ effectiveTemplateId: string; source: 'project' | 'system' | 'none'; configuredTemplateId: string | null }`. Resolution order: **project configured → system default**.

Both `CreateIssueDialog.tsx:225` and `WorkflowProfileControl.tsx:47` are rewired to consume `effectiveTemplateId` instead of the hardcoded `find((p) => p.isDefault)` lookup. This removes the duplication and makes both surfaces honor the project configuration.

**Rationale:** The fallback rule is identical in both consumers and stated in the spec; a single hook is DRY and guarantees consistency. Keeping `source` lets the UI label whether a value is "project-configured" vs "inherited".

**Alternatives considered:**
- Inline the fallback at each call site — rejected: duplicates the precedence rule and risks drift.
- Compute a `resolvedDefaultTemplateId` server-side — rejected as a non-goal (no backend change) and unnecessary; the system-default set is already available client-side.

### Decision 5: UI placement — project default control above the catalog

**Choice:** Add a `ProjectDefaultWorkflowControl` sub-component rendered at the top of `WorkflowProfilesSection` (above the catalog list/detail), guarded by `currentProject` (mirror the `repositories`/`label-catalog` guard at `SettingsPage.tsx:68-83` — render a "No project selected" hint when `projectId` is null). The control:
- Shows the current `defaultTemplateId` (or "Not set — inheriting system default (`mohist/default`)" when null).
- Renders a `<select>` of system templates + a "Clear" button.
- On select → `useSetProjectDefaultWorkflowProfile`; on clear → `useClearProjectDefaultWorkflowProfile`.

**Rationale:** The proposal explicitly wants the page to "answer what will new issues inherit for this project?" first, so the control leads the section.

### Decision 6: Visually separate the two "default" concepts

**Choice:**
- Relabel the static catalog badge (currently green "Default" at `WorkflowProfilesSection.tsx:86,138`) to **"System default"** and restyle to a neutral slate/gray badge so it reads as catalog metadata, not project state.
- The new `ProjectDefaultWorkflowControl` uses a distinct accent (blue/green) and explicit copy ("Project default"), so the two cannot be visually conflated.

**Rationale:** The spec requires the system-default badge to "NOT be presented in a way that could be mistaken for the project default". Renaming + recoloring is the smallest change that satisfies this without removing the useful catalog metadata.

**Alternatives considered:**
- Remove the `isDefault` badge entirely — rejected: it is still useful metadata to know which template is the system fallback; demoting the styling preserves the signal without the ambiguity.

## Risks / Trade-offs

- **[Stale effective default in open dialogs]** -> `CreateIssueDialog`/`WorkflowProfileControl` snapshot `effectiveTemplateId` at open; if the project default changes in another tab, an already-open dialog won't refresh until remount. Acceptable for a single-user local-first system; TanStack refetch/focus-stale defaults will reconcile on refocus.
- **[Orphaned `defaultTemplateId` referencing a removed template]** -> GET returns the stored id even if the template no longer exists in the catalog. Mitigation: the control detects "configured id not in catalog", shows a warning with the raw id, and offers Clear. (PUT is already guarded server-side — unknown ids throw.)
- **[Two queries for effective default]** -> `useEffectiveDefaultWorkflowProfile` fans out to the project-default query + system catalog query. Both are already cached/cheap; acceptable. Trade-off: simplicity over a single combined endpoint.
- **[PUT unknown-template surfaces as a 500, not 400]** -> `ProjectWorkflowProfileManager.SetDefaultTemplateAsync` throws `InvalidOperationException` on unknown id, caught by global middleware as a generic error. Mitigation: the control only offers ids sourced from the system catalog, so an unknown id is not user-reachable; the toast reports `err.message`. (Hardening the server status code is out of scope / non-goal.)

## Migration Plan

This is a pure Web (`packages/web`) change with **no database or server migration** — the `DefaultTemplateId` column and all three endpoints already exist.

**Deploy steps:**
1. Add client functions + hooks + effective-default hook in `entities/settings`.
2. Add `ProjectDefaultWorkflowControl` and restyle the catalog badge in `WorkflowProfilesSection.tsx`.
3. Rewire `CreateIssueDialog.tsx` and `WorkflowProfileControl.tsx` to `useEffectiveDefaultWorkflowProfile`.
4. Add Web tests (readback, switch to `mohist/github-pr`, clear, system-vs-project distinction).
5. Verify: `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` pass.

**Rollback:** Revert the Web commit(s). Existing issues and the persisted `defaultTemplateId` are untouched; the backend remains on its current behavior. No data cleanup required.

## Open Questions

- **Naming tension:** the UI consistently says "workflow profile" while the API says "template" (`default-template`, `templateId`). This design keeps the UI-facing hook names in "profile" terms and maps to the "template" endpoints. Confirm this is acceptable vs. aligning UI copy to "template".
- **Per-issue control behavior:** when a project default is configured, should the per-issue `WorkflowProfileControl` actively *suggest* switching an issue that currently resolves to the system default back to the project default, or only affect new selections? Current design: affects the default *suggestion* only (non-destructive); confirm this matches intent.
