## Context

Epic tracking already has the core MVP surfaces: persisted Epic and EpicIssue tables, an Orleans `EpicGrain`, REST endpoints, Web list/detail/create pages, and issue detail primary Epic projection. The remaining work is usability and correctness hardening for that existing capability.

The highest-priority defect is in linked issue projection: `LinkedIssueDto` and `EpicQueryService` currently map issue health into the DTO status field and issue status into the stage field. Because Epic progress uses the projected status to decide whether a linked issue is delivered, delivered counts and ready-to-mark-done state are unreliable. The Web model also lacks an explicit `stage` field even though the server shape intends to expose one.

Users currently see truncated Epic UUIDs instead of stable human-readable references. Epics also cannot be edited after creation, Add Issue uses a flat selector that does not scale to realistic issue counts, and lifecycle actions are too permissive: Mark Done can be invoked before all linked work is delivered, Close does not force an explicit unlink confirmation, and terminal actions can be repeated.

This design follows the proposal and `epic-tracking` spec. It preserves Epic as a separate goal container rather than executable workflow work, keeps progress projected from linked issue state at read time, and maintains ID lookup compatibility for existing stored references and URLs.

## Goals / Non-Goals

**Goals:**

- Correct linked issue projection so status, stage, and health are distinct fields and Epic progress counts delivered issues from actual issue lifecycle status.
- Assign project-scoped Epic numbers at creation time and expose them through backend DTOs, lookup routes, and Web UI labels.
- Preserve compatibility for existing ID-based routes and references while adding explicit number-based lookup and number-or-id detail route compatibility.
- Replace flat Add Issue selection with searchable candidate filtering and clear unavailable-state explanations.
- Add metadata editing for title, description, and priority without affecting linked issue membership or issue workflow state.
- Guard Epic lifecycle actions on both server and Web surfaces, including Mark Done readiness checks, Close confirmation, and terminal-action prevention.
- Add focused server and Web specs for the corrected behavior.

**Non-Goals:**

- Nested Epics, roadmap/Gantt views, Explore-to-Epic crystallization, multi-Epic membership, Epic workflow execution, and audit history.
- CLI support; the project guidance states the old CLI scope has been removed.
- Persisted progress counters. Progress remains a read-time projection over linked issue state.
- Automatic Epic completion when all linked issues are delivered.

## Decisions

### 1. Fix linked issue projection by making DTO fields explicit

Use a `LinkedIssueDto` shape that includes `Status`, `Stage`, `Health`, and `Priority` as separate fields, and update query projection to populate them from the matching issue row properties. Prefer named arguments or object-style construction where practical so future field additions do not silently shift positional values.

Progress calculation should use the projected or source issue lifecycle status only, treating `done` and `completed` as delivered per spec. Health values such as `green`, `active`, or `blocked` must not count as delivered.

Alternatives considered:

- Keep the DTO as-is and only reorder positional constructor arguments. This is the smallest code diff but keeps the same class of bug possible.
- Rename fields to domain-specific names such as `lifecycleStatus` and `workflowStage`. This is clearer, but it would be a broader API contract change than needed for the existing Web/API vocabulary.

### 2. Store Epic numbers as nullable project-scoped integers

Add `Number INT NULL` to the Epic row model and database schema. New Epics receive the next available number within their project during creation. Existing Epics can remain nullable for backward-compatible migration; Web display falls back to the existing short ID label only when a number is absent.

Number allocation should follow the existing issue-number pattern as closely as possible so project scoping, concurrency assumptions, and test setup remain consistent. The API DTOs for list, detail, progress, and issue primary Epic projection should include the number.

Alternatives considered:

- Backfill numbers for all existing Epics during migration. This gives every row a number immediately but increases migration risk and requires deterministic ordering decisions for historical rows.
- Generate numbers only in memory from list order. This avoids schema change but breaks stable references and number-based lookup.

### 3. Add number lookup without replacing ID lookup

Expose `GET /api/epics/by-number/{number}` for explicit number lookup. Keep existing `GET /api/epics/{id}` compatible with UUID/id references and extend that route to resolve number-or-id values, matching the issue requirement for stored references and existing URLs. Where a Web helper accepts an Epic reference from older data, resolve number-or-id only when the contract calls for compatibility; avoid making every internal method accept ambiguous strings.

The Web should display `#N` as the primary label when `number` is present, with short IDs only as fallback or secondary hover/debug context. This applies to Epic list, Epic detail, and issue primary Epic labels.

Alternatives considered:

- Make only `/api/epics/by-number/{number}` support numbers and leave `/api/epics/{id}` ID-only. This is more self-documenting, but it misses the issue requirement that the existing detail route accept number-or-id.
- Replace IDs in URLs with numbers everywhere. This improves readability but creates unnecessary routing churn and compatibility risk for existing URLs.

### 4. Keep Add Issue filtering client-driven over existing issue data

Implement the searchable Add Issue control on the Epic detail page using the existing available issue data fetched by the page. The control filters by issue number and title, excludes already linked issues from selectable results, disables candidates that are closed, archived, or not startable, and displays the reason inline. Start eligibility reasons should include the blocking issue number when prerequisites prevent starting.

Submission remains disabled when there is no selected selectable candidate. Existing duplicate-membership error handling remains as a defensive server response for races or stale data.

Alternatives considered:

- Add a dedicated server-side Epic candidate search endpoint. This would scale better for very large projects but is unnecessary for the current page shape and would expand the API surface beyond the issue scope.
- Hide unavailable candidates entirely. This reduces UI noise but fails the requirement to explain why a visible issue cannot be added.

### 5. Add metadata update through the Epic grain and PATCH route

Add an `UpdateAsync` method to `IEpicGrain`/`EpicGrain` that accepts optional updates for title, description, and priority, persists them, and updates `UpdatedAt`. Add `PATCH /api/epics/{id}` with a request shape parallel to the existing issue update pattern.

The update operation must not modify Epic status, linked issue membership, or issue workflow state. The Web detail page should expose an edit affordance, save through the PATCH API, and invalidate/refetch Epic queries after success.

Alternatives considered:

- Update directly through a repository/query service without the grain. This may be shorter but bypasses the existing Epic domain boundary.
- Reuse create dialog for editing. This reduces UI components but can blur create/update semantics; reuse is acceptable only if the UX remains explicit.

### 6. Enforce lifecycle guards server-side and mirror them in the UI

Server-side Mark Done must check projected progress before changing status. If linked issues remain undelivered, return a client-visible 4xx and leave the Epic unchanged. Done and closed Epics should reject repeated terminal actions.

Close Epic should remain explicit and destructive only to Epic membership: it sets the Epic to `closed` and removes EpicIssue links, but does not alter issue workflow state, prerequisite data, worktrees, or issue status. The Web must show a confirmation dialog with the linked issue count before calling close.

The UI should disable Mark Done until `readyToMarkDone` is true and explain the remaining issue count. Terminal Epic detail pages should show the final status and not offer repeated terminal actions.

Alternatives considered:

- Rely only on UI disabling for Mark Done. This is unsafe because API clients or stale tabs can still issue invalid transitions.
- Automatically mark done when all linked issues are delivered. The spec explicitly rejects automatic completion; users must make the lifecycle decision.

## Risks / Trade-offs

- `[Risk] Concurrent Epic creation could allocate duplicate numbers if allocation is implemented as max+1 without an adequate uniqueness guard.` -> Mitigation: follow the existing Issue number allocation pattern and add a project/number uniqueness constraint if the current storage layer supports it safely.
- `[Risk] Nullable numbers mean older Epics may still render fallback short IDs until backfilled or edited.` -> Mitigation: keep fallback display and ID lookup compatibility; consider a later low-risk backfill if users need numbers for historical Epics.
- `[Risk] Number lookup is project-scoped, but routes must know the active project context.` -> Mitigation: use the same project resolution mechanism as existing Epic list/create/detail APIs and test lookup in that context.
- `[Risk] Client-side Add Issue search may become slow for very large projects.` -> Mitigation: keep the implementation simple for this issue; introduce a server search endpoint later if real project sizes require it.
- `[Risk] Progress semantics depend on string status values such as `done` and `completed`.` -> Mitigation: centralize delivered-status checks in the Epic query/progress code and cover them with server specs.
- `[Risk] Close Epic removes membership links and may surprise users.` -> Mitigation: require explicit confirmation that includes the number of linked issues to be unlinked.
- `[Risk] Route compatibility changes can accidentally break existing UUID URLs or make numeric references ambiguous.` -> Mitigation: keep ID-based route tests, add explicit by-number tests, and add number-or-id detail route compatibility tests.

## Migration Plan

1. Add the nullable `Number` column to the Epic persistence model and database migration. If a uniqueness index is added, scope it by project and allow nulls for pre-existing rows.
2. Update Epic creation to allocate the next project-scoped number for new Epics.
3. Update DTOs and query projections to include Epic number and corrected linked issue fields.
4. Add number lookup route and preserve existing ID lookup behavior while allowing the existing detail route to resolve number-or-id references.
5. Add Epic metadata PATCH route and grain update method.
6. Add lifecycle guard checks in server operations before updating Epic status or unlinking memberships.
7. Update Web API types and UI surfaces for `#N` labels, searchable Add Issue, edit flow, and guarded lifecycle actions.
8. Add and run server specs for progress projection, number lookup, PATCH, and Mark Done rejection; add Web specs for numbered display, searchable Add Issue behavior, edit flow, and lifecycle disabled/confirmation states.

Rollback strategy: because the schema addition is nullable and existing ID routes remain valid, rollback can disable the new UI/API paths while leaving the `Number` column in place. If application rollback is required, old code should continue to ignore the extra nullable column. Avoid destructive rollback of assigned numbers unless a follow-up migration is explicitly planned.

## Open Questions

- Should historical Epics be backfilled with numbers in this issue, or is nullable fallback sufficient until a later maintenance task?
- Should Epic number uniqueness be enforced by a database index immediately, or only by creation logic following the current Issue implementation pattern?
- What exact `startEligibility` source should the Add Issue control use if multiple issue list/query shapes expose eligibility differently?
