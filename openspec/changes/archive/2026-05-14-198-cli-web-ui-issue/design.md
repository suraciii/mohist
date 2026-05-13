## Context

This change closes a parity gap that now exists entirely at the product surface layer rather than in storage: the server already accepts `priority` on create/update and already validates/stores `model`, but the Web UI issue dialogs do not expose priority and the CLI create path does not expose model. The Kanban board also renders a single unfiltered issue list from `useIssues(projectId)` and only applies stage grouping plus done-column collapsing, so users have no way to preserve a focused view as backlog size grows.

Two constraints shape the design:
- Keep issue data ownership in the existing issue API and repo layers; do not add parallel state stores for board controls.
- Preserve current mobile/desktop board structure, where desktop renders all columns and mobile renders a single selected column, so filtering and sorting must feed both layouts from the same derived view model.

## Goals / Non-Goals

**Goals:**
- Let `mo issue create` accept `--model` and pass it through the same create request as title/body/labels/priority.
- Add priority controls to Web UI create/edit issue dialogs without introducing a second priority vocabulary or custom mapping layer.
- Add a single board query state model that supports priority filter, label filter, title search, and sort mode, and persists that state in the URL.
- Reuse current API validation for malformed model IDs and current issue data fetching for board contents unless a server-side filter is clearly required.

**Non-Goals:**
- Adding new issue storage fields or database migrations.
- Changing issue workflow semantics, stage grouping rules, or done-column archive behavior.
- Adding full-text backend search or a dedicated board query endpoint.
- Supporting different sort modes per stage column; the sort choice is global across the board.

## Decisions

### D1: CLI create remains a thin pass-through and reuses server-side model validation

`mo issue create` will gain `--model <provider/model>` and include it in the existing `POST /api/issues` payload after `ingestBody()` resolves `--body`, `--body-file`, `@file`, or `-`. The CLI will not implement its own model parser beyond passing the string along and surfacing the API error with exit code 1.

This keeps validation rules in one place: `api/issues.ts` already rejects invalid model format on create/update with a clear `provider/model` error, and the body-ingestion path is already the only CLI-specific complexity worth owning locally.

**Alternatives considered:**
- Validate model format in the CLI before the request: duplicates API rules and creates another place to keep error text in sync.
- Add a second `mo issue update --model` call after create: works, but preserves the current two-step friction and splits a single user intent across two write paths.

### D2: Web priority UI reuses existing issue field semantics and shared visual helpers

The Web UI dialogs will treat `priority` as a normal issue attribute alongside `title`, `body`, and `labels`: `CreateIssueDialog` owns a local `priority` state defaulting to `p2`, `EditIssueDialog` hydrates from `issue.priority`, and `api.createIssue` / `api.updateIssue` request types gain `priority` so the field is sent explicitly.

For presentation, the design should extend the existing Web priority display helper set rather than inventing dialog-only colors. The current `IssueCard` already uses `label-colors.ts` for priority text formatting, while the CLI owns the semantic mapping (`p0/p1` red, `p2` yellow, `p3` green, `p4` gray). The implementation should extract or centralize a small priority-style helper in the Web layer so cards and form controls use the same color semantics.

**Alternatives considered:**
- Hardcode priority styles separately inside each dialog: fastest, but repeats the same mapping and will drift from card display.
- Reuse CLI formatting code directly in the Web bundle: not viable because the CLI helper depends on `chalk` and terminal formatting.

### D3: Board enhancement is a URL-backed client-side query layer on top of one issue fetch

The board will continue fetching the project issue list once through `useIssues({ projectId })`, then derive the displayed board with a pure transformation pipeline:
1. Parse board query state from `location.search`
2. Filter by selected priorities
3. Filter by selected labels
4. Filter by case-insensitive title search
5. Group by stage
6. Sort issues within each stage by the selected sort mode

The query state should live in a small board-specific hook or helper pair, not scattered through `KanbanBoard` event handlers. The URL is the source of truth so refresh, navigation, and mobile/desktop rendering all restore the same focused view automatically.

Client-side filtering is the right trade-off here because the board already needs the full cross-stage issue set and stage counts must update together. Pulling filters down into a local derived layer keeps the API surface simple and avoids partial stage fetches or repeated round-trips while the user types in search.

**Alternatives considered:**
- Add backend query params for search, multi-priority, multi-label, and sort: possible, but turns a responsive board interaction into repeated server requests and expands the API contract for a UI-only concern.
- Keep filter state only in React state: simpler short-term, but fails the URL persistence requirement and makes mobile/refresh behavior inconsistent.

### D4: Sorting is global state with per-column application

The board exposes one selected sort mode (`priority`, `number`, `updated`) and applies it to every stage column. Each column can render its own sort switcher UI, but that control writes the same shared sort key into URL state.

This keeps the interface deep but simple: users learn one board sort setting, and the implementation avoids a matrix of per-stage sort parameters that would be hard to represent in the URL and noisy on mobile.

Expected ordering:
- `priority`: `p0`..`p4`, then stable tie-break by `updatedAt` desc, then `number` desc
- `number`: issue number desc
- `updated`: `updatedAt` desc

Using explicit tie-breakers avoids cards jumping unpredictably when many issues share the same primary sort value.

**Alternatives considered:**
- Independent sort per column: more flexible, but higher cognitive and URL complexity than the problem warrants.
- Preserve repo default ordering and only sort backlog: makes behavior inconsistent across columns.

### D5: Filter controls are additive and tolerant of missing data

Priority filter supports multi-select because the primary use case is “show p0/p1 together,” while label filter should also support multi-select to avoid forcing users into repeated single-label toggles. Search matches title only, case-insensitively, which is enough to satisfy the acceptance criteria without turning the board into a full issue search surface.

Missing or null priority values should be normalized to `p2` in board sorting/filtering so older or malformed API data does not break focused views. This matches the existing repo/service default.

**Alternatives considered:**
- Single-select priority filter: cannot represent the explicit p0/p1 use case well.
- Search title + body + labels: broader, but less predictable and more expensive than the stated requirement.

## Risks / Trade-offs

- **[Client-side filtering on very large projects]** → Mitigation: keep one fetch and use memoized pure transforms; this is acceptable for the current issue scale and can be moved server-side later without changing URL semantics.
- **[Priority color semantics drift between card, dialog, and CLI]** → Mitigation: define one Web priority-style helper and reference the same semantic mapping everywhere in the Web layer.
- **[URL query churn while typing search]** → Mitigation: debounce or replace-history updates for the search box so every keystroke does not create a noisy browser history entry.
- **[Current change directory has no generated specs yet]** → Mitigation: keep design scoped to proposal + existing capability contracts and ensure implementation updates the matching delta specs before merge.

## Migration Plan

1. Extend OpenSpec delta specs for `cli-interface`, `http-api`, and `web-ui` to capture the new create parity and board behavior.
2. Update CLI create command to accept `--model` and include it in the existing create payload.
3. Update Web API/types/hooks and add priority controls to create/edit dialogs.
4. Add board query-state parsing + serialization, then layer filter/sort/search UI on top of the existing Kanban data flow for both desktop and mobile views.
5. Verify behavior with targeted tests for CLI create body/model combinations and UI tests for board query-state restoration where coverage already exists.
6. Rollback strategy: if the board query layer causes regressions, the UI changes can be reverted independently of CLI parity because no storage or migration changes are introduced.

## Open Questions

None.
