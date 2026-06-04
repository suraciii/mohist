## Why

Epic tracking is present but not reliably usable: linked issue progress is currently miscomputed because issue status and health are mapped into the wrong fields, and users must identify Epics by truncated UUIDs. Closing these gaps now makes Epic progress trustworthy and gives users safe, searchable, editable Epic management before the MVP behavior becomes depended on.

## What Changes

- Fix linked issue projection so Epic progress counts completed issues from actual issue status and exposes both status and health consistently to the Web UI.
- Add project-scoped, user-readable Epic numbers and use `#N` labels across Epic list, detail, issue primary Epic display, and Epic lookup APIs.
- Add API support for resolving Epics by number, including `GET /api/epics/by-number/{number}` and number-or-id compatibility on the existing detail route for stored references and existing URLs.
- Replace the flat Add Issue selector with searchable issue selection that excludes already-linked issues and prevents unavailable issues from being chosen without explanation.
- Add Epic metadata editing for title, description, and priority.
- Guard lifecycle actions so Mark Done is only available when linked issue progress is ready, closed or done Epics cannot repeat terminal actions, and Close Epic requires confirmation that linked issues will be unlinked.
- Add server and Web specs covering progress projection, numbered Epic display/lookup, searchable Add Issue behavior, metadata editing, and lifecycle guards.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `epic-tracking`: Update Epic progress projection, user-facing identification, issue membership UX, metadata editing, and lifecycle safety requirements for the existing Epic tracking capability.

## Impact

- Backend Epic domain, DTOs, queries, Orleans grain methods, REST routes, and database schema/migrations.
- Web Epic list/detail/create-related surfaces, Issue detail primary Epic display, API client types, and Epic page tests.
- Existing Epic APIs remain compatible with ID-based access; a number-based lookup route is added and the existing detail route accepts number-or-id references.
- No CLI impact; the CLI scope referenced by older follow-ups is removed.
