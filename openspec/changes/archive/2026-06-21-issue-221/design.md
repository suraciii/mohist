## Context

Issue #221 closes the last gap in the labels epic (#8): the label catalog already exists as server data with a full CRUD API (`LabelsRoutes.cs`, including `PATCH /catalog/{key}` at lines 53–96) and is reachable from the CLI via `mo label list/add/remove`, but users have no visual way to curate it and no CLI verb to edit an entry. The `LabelDefinition` model (`{ key, description, supportedValues?, origin: system|user }`) and the `label-catalog` capability spec (storage, validation, system-seed immutability, advisory non-enforcement) are already in place and unchanged by this issue.

This change is purely a consumer of the existing API on two surfaces:

- **CLI** (`packages/cli/Mohist.Cli/MohistCliCommands.Label.cs`): the `label` command group has `list`/`add`/`remove`; `update` is missing. A generic `PrintPatchAsync(path, body)` helper already exists in `MohistCliApi.cs:102`, so the new verb mirrors `BuildAdd`/`BuildRemove` with no new infrastructure.
- **Web** (`packages/web/src/`): only the issue-level `LabelEditor` exists today — it edits an issue's free key-value labels, a separate concern. There is no project-scoped catalog surface. The web app uses a layered, FSD-like structure (`entities/<x>/api/{client,queries}.ts`, `pages/...`) with a shared `request()` + `projectApiPath()` HTTP layer (`shared/api/client.ts`) and a tabbed Settings page that already hosts project-scoped CRUD sections (`RepositoriesSection`).

Constraints: no server or domain change; no breaking API change; the catalog stays advisory (must not constrain issue labels). The project is pre-release, so there is no compatibility/migration burden.

## Goals / Non-Goals

**Goals:**
- Add `mo label update <key>` with partial-field update semantics, reusing the existing PATCH API and `PrintPatchAsync` helper.
- Add a project-scoped Web surface to list/add/edit/delete catalog entries, with client-side validation matching the `label-catalog` rules and system-entry protection.
- Keep both surfaces thin clients over the existing API — no business logic duplicated client-side beyond input validation.

**Non-Goals:**
- No server, domain, or persistence change (the PATCH endpoint and `LabelCatalogService.UpdateAsync` already exist).
- No change to the issue-level `LabelEditor` or the issue label primitive.
- No label colors, ordering, required-value constraints, bulk import/export, or auto-creation of catalog entries from issue labels (all confirmed non-goals in the issue).
- No enforcement of catalog membership on issue labels.

## Decisions

### D1 — CLI: `mo label update` mirrors `BuildAdd`, reuses `PrintPatchAsync`
Add `BuildUpdate(api)` to `MohistCliCommands.Label.cs` and register it in the `label` group alongside `list`/`add`/`remove`. Signature: `mo label update <key> [--description <text>] [--supported-values <a,b,c>] [--project <name>|--project-id <id>]`, identical option/argument shape to `add` (no `-o` flag; the response is printed directly via `PrintPatchAsync`, like `add`).

- Validate `key` with the existing `LabelDelta.ValidateKey` (`^[a-z0-9]([-a-z0-9]*[a-z0-9])?$`) before any request.
- Require at least one of `--description` / `--supported-values`; reject a no-op invocation client-side with a clear error.
- When `--description` is supplied, require non-empty/non-whitespace; when `--supported-values` is supplied, split on comma and reject empty entries (same rule as `add`).
- Build the PATCH body from **only the provided fields** and call `api.PrintPatchAsync(path, body)`; omit absent fields rather than sending `null`.
- Surface server errors (404 unknown key, 400 validation, 409 conflict / system-entry protection) via the existing `PrintPatchAsync` error path; exit non-zero.

**Alternatives considered:** (a) Fetch-then-merge on the client (read current entry, overlay, PUT whole object) — rejected; the server already merges absent fields (`LabelsRoutes.cs:73-83`), so a partial PATCH is simpler and avoids a race. (b) Add a generic `--key` rename — rejected as out of scope (key is immutable).

### D2 — Web placement: a new Settings tab, mirroring `RepositoriesSection`
Add a new `LabelCatalogSection` component under `pages/settings/ui/` and register it as a new tab in `SettingsPage.tsx` (`VALID_SECTIONS`, `SECTION_META`, and the `SectionContent` switch). The Settings route is already project-scoped (`/settings/:section`), so this needs no new top-level route.

**Alternatives considered:** (a) A dedicated `/labels` page — rejected; heavier routing/nav cost and less discoverable than a Settings tab. (b) Inline management inside the issue `LabelEditor` — rejected; conflates the project catalog (advice) with issue labels (free key-value pairs), the exact confusion the issue wants to avoid. (c) A panel on a project detail page — deferred; no project detail page is established today, whereas the Settings tab pattern is.

### D3 — Web data layer: a new `entities/label-catalog` slice
Following the existing slice pattern (`entities/project`, `entities/epic`, `entities/issue-templates`):

- `entities/label-catalog/model/types.ts` — `LabelDefinition { key; description; supportedValues?: string[]; origin: 'system' | 'user' }`.
- `entities/label-catalog/api/client.ts` — `getLabelCatalog(projectId)`, `createLabelDefinition`, `updateLabelDefinition` (PATCH, partial body), `deleteLabelDefinition`, all built on `request()` + `projectApiPath()`.
- `entities/label-catalog/api/queries.ts` — `useLabelCatalog()` (TanStack Query) plus `useCreate/Update/DeleteLabelDefinition` mutations that invalidate the catalog query on success.

**Alternatives considered:** fold catalog calls into `entities/issue` — rejected; the catalog is project-scoped, not issue-scoped, and `issue` already owns the issue-level label primitive. A dedicated slice keeps the two concerns separable and matches how `label-catalog` is modeled as its own capability.

### D4 — Partial-update contract: omit absent fields, never send `null`
Both CLI and Web send only the fields being changed. The server already treats an absent field as "keep current" and an explicit `null` as "clear" for `supportedValues` (`LabelsRoutes.cs:68-83`). To stay unambiguous, clients **omit** absent fields rather than serializing `null`. This keeps "I didn't touch this field" semantically distinct from "I cleared this field."

### D5 — System-entry protection: defense in depth (UI + server)
The server is the source of truth (`LabelCatalogService` rejects modify/delete on `origin: system`). The Web UI additionally hides/disables delete and edit-submission for `origin: system` rows so users never get an error for a predictable, policy-locked action. The CLI does **not** pre-check origin (it doesn't fetch the entry); it surfaces the server's rejection clearly. Rationale: a client-side origin check would require an extra read, and the server already enforces it.

### D6 — `supportedValues` input format
CLI: comma-separated (`--supported-values auth,ui,persistence`), matching `add`. Web: a single text input (comma- or newline-separated) parsed into a string array, with empty entries rejected — simplest editor that satisfies the spec. A richer chip/tag editor is explicitly deferred to a future enhancement.

## Risks / Trade-offs

- **[Two label surfaces confuse users]** (issue `LabelEditor` vs project catalog section) -> Mitigation: distinct naming ("Label catalog" tab vs issue "Labels" field) and a one-line in-section hint that the catalog is advisory and does not restrict issue labels.
- **[Partial-update ambiguity: null vs omitted]** -> Mitigation: D4 — clients always omit absent fields; documented contract.
- **[System-entry protection depends on server]** -> Mitigation: D5 — UI hides the action; server still enforces; CLI surfaces the rejection.
- **[Settings tab list growing]** -> Mitigation: acceptable; the tab list is already horizontally scrollable (`SettingsPage.tsx:110`), and a catalog tab is cohesive with other project-scoped sections.
- **[Catalog can accumulate stale/unused entries]** -> Mitigation: out of scope (issue non-goal); no auto-pruning now. Editing/removing entries is the manual lever this issue provides.

## Migration Plan

No data migration and no server change. The PATCH endpoint and `LabelDefinition` storage already exist.

- **Deploy:** ship the CLI `label update` subcommand and the Web `LabelCatalogSection` + data slice. Server requires no restart for schema/API reasons.
- **Rollback:** revert the CLI and Web changes. Because the server and persisted catalog are unchanged, rollback has no data impact; any catalog entries users created remain valid server-side data.
- **Compatibility:** no API contract change; existing `mo label list/add/remove` and the issue `LabelEditor` are untouched.

## Open Questions

- Exact tab label, icon, and ordering within Settings (minor UX; suggest label "Label catalog", placed near "Repositories" since both are project-scoped configuration).
- Whether the `supportedValues` editor should later become a chip/tag UI (deferred — D6 uses a text input for now).
- Whether to surface catalog suggestions as typeahead inside the issue `LabelEditor` (explicitly out of scope here; candidate for a follow-up issue).
