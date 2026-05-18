## Why

Mohist issues work well for a single executable unit, but users lack a first-class way to track long-running goals that span many related issues. Adding Epics gives users a lightweight progress surface for product features, system refactors, and capability-building efforts without overloading issue labels, search, memory, or the workflow board.

## What Changes

- Add Epics as named, described, prioritized collections of issues with `active`, `done`, and `closed` states.
- Persist Epic records and primary Epic-to-Issue membership while enforcing that an issue belongs to at most one primary Epic in the first version.
- Project Epic progress from linked issue state, including delivered/total counts, blocked issues, active issues, and a simple next-issue recommendation.
- Add Web UI navigation and pages for listing Epics, creating Epics, viewing Epic detail, adding/removing linked issues, and marking Epics done or closed.
- Show a linked Epic reference on Issue Detail so users can navigate from an issue back to its long-running goal.
- Add server APIs for Epic CRUD-style reads/writes, issue membership management, lifecycle actions, and progress projection.
- Add CLI support for `mo epic create`, `mo epic list`, `mo epic show`, `mo epic add-issue`, `mo epic remove-issue`, and `mo epic close` as server-backed thin-client commands.
- Keep Epics outside the issue workflow: Epics do not run workflow, create worktrees, appear in Board lanes, replace prerequisites, or auto-complete.

## Capabilities

### New Capabilities

- epic-tracking

### Modified Capabilities

- local-issue-store
- http-api
- cli-interface
- web-ui

## Impact

- SQLite schema and data access add Epic persistence and Epic-Issue membership tables, likely via new repos alongside `IssueRepo` and existing migration/versioning code.
- Domain/services add Epic lifecycle and progress projection logic while continuing to derive delivery and next-issue state from existing Issue fields.
- HTTP API adds Epic endpoints and extends issue detail/list data where needed to expose primary Epic linkage.
- CLI adds a new `epic` command group using the shared server-backed API client pattern.
- Web UI adds desktop/mobile navigation, Epic list/detail/create/add-issue surfaces, API client methods, React Query hooks, and Issue Detail backlink rendering.
- Existing issue workflow, workflow engine, worktree manager, Board lanes, prerequisites, and start behavior should remain unchanged except for optional display of Epic membership.
