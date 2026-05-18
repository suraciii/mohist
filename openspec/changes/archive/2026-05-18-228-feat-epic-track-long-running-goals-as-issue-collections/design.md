## Context

Mohist currently models executable work as issues. Issues have workflow state, can be started, may own worktrees, and appear on the Board. That model is intentionally narrow: one issue is one executable work unit. Long-running goals that span many issues are currently represented indirectly through labels, search, or user memory, which makes it hard to see overall progress, current blockers, and the next issue to inspect.

This change adds Epics as a separate planning and tracking surface. An Epic is a named, described, prioritized collection of existing issues. It does not execute work and does not participate in the issue workflow. Its progress is derived from linked issue state at read time, so users do not maintain a second source of truth for delivered count or next issue.

The referenced specs define the Epic tracking domain plus the required storage, API, CLI, and Web UI surfaces.

## Goals / Non-Goals

**Goals:**

- Persist Epic records with `title`, `description`, `priority`, `status`, and timestamps.
- Persist primary Epic membership for issues while enforcing that one issue belongs to at most one Epic in the first version.
- Project Epic progress from linked issues: delivered count, total count, blocked issues, active issues, and next issue.
- Expose Epic list/detail/create/update-membership/lifecycle operations through the server API.
- Add Web UI surfaces for Epic list, Epic creation, Epic detail, issue add/remove, lifecycle actions, and issue-detail backlink.
- Add `mo epic create/list/show/add-issue/remove-issue/close` as thin CLI commands over the shared API client.
- Keep the existing issue workflow, Board lanes, worktree handling, prerequisites, and start behavior unchanged.

**Non-Goals:**

- No nested Epics, milestones, roadmaps, Gantt views, or scope history.
- No workflow execution, worktree creation, or `start` operation for Epics.
- No automatic Epic completion. `done` is explicit user action.
- No structured success criteria, decision history, or separate progress entity.
- No many-to-many primary membership in the first version.
- No use of Epic membership as prerequisite or dependency semantics.
- No Explore-to-Epic crystallization path.

## Decisions

### D1: Model Epics Separately From Issues

Add a new `epics` table and repo/service layer instead of extending the `issues` table with an issue type. Epics have a smaller lifecycle (`active`, `done`, `closed`) and intentionally lack workflow fields such as stage, run state, worktree, branch, and task/check execution data.

This keeps the workflow engine simple: every row in `issues` remains executable work, and every row in `epics` is a tracking container. The Board and `issue start` paths can continue to query issues without filtering out pseudo-issues.

**Alternatives considered:** Extending `issues` with `kind = issue | epic` would reuse existing storage and identifiers, but it would spread defensive checks across workflow, Board, CLI, and worktree code to prevent Epics from being started or displayed as issue work. Using labels only would avoid schema work, but would not enforce primary membership or provide a stable Epic surface.

### D2: Store Membership in a Dedicated Join Table With a Unique Issue Constraint

Add an `epic_issues` table with `epic_id`, `issue_id`, `created_at`, and a unique constraint on `issue_id`. The table represents the first-version primary Epic relationship. Deleting or removing membership only deletes the join row; it never changes issue status or workflow data.

The unique `issue_id` constraint defines the one-primary-Epic rule at the database boundary. The service should translate constraint failures into a clear domain error such as "Issue #N already belongs to Epic #M" by looking up the existing membership before or after the failed insert.

**Alternatives considered:** Storing `epic_id` directly on `issues` would be simpler for the one-Epic case, but it couples issue persistence to Epic functionality and makes future membership metadata harder to add. Allowing many-to-many membership now would be more flexible, but would add interpretation complexity to progress, issue detail, and CLI output before the product needs it.

### D3: Project Progress in the Epic Service at Read Time

Do not persist `Progress`. The Epic service should load linked issues and derive:

- `deliveredCount`: linked issues whose status represents delivered/done work.
- `totalIssueCount`: linked issue count.
- `blockedIssues`: linked issues in blocked or blocked-equivalent state if such state exists in the current issue model.
- `activeIssues`: linked issues currently in active workflow states.
- `nextIssue`: first blocked issue, otherwise first active issue, otherwise first backlog issue, otherwise `null` with a presentation message of ready to mark done.

The projection should be implemented once in the backend service and returned by API responses so Web and CLI show consistent counts and next-issue selection. Ordering should be deterministic, preferably by membership creation order and then issue id, so repeated reads do not appear unstable.

**Alternatives considered:** Persisting counters would make list queries cheaper, but introduces stale data whenever issue state changes and requires update hooks across workflow transitions. Computing separately in Web and CLI would avoid backend work, but duplicates business rules and risks inconsistent next-issue recommendations.

### D4: Provide a Small Epic Service Interface Above Repos

Introduce an Epic domain/service module that owns validation and orchestration:

- `createEpic(input)` validates title, description, priority, and initializes `active` status.
- `listEpics()` returns Epics with projected progress summaries.
- `getEpic(id)` returns Epic detail, linked issues, and projected progress.
- `addIssue(epicId, issueId)` verifies both records exist and enforces primary membership.
- `removeIssue(epicId, issueId)` removes the link without touching the issue.
- `markDone(epicId)` and `closeEpic(epicId)` update only Epic status.
- `getIssueEpic(issueId)` supports issue-detail backlink rendering.

Repos should remain persistence-focused; progress rules and lifecycle rules belong in the service. This gives the API and CLI one deep interface and prevents Epic rules from leaking into UI components or command handlers.

**Alternatives considered:** Putting all logic in route handlers or CLI commands would minimize new files, but would duplicate validation and projection. Building a generic collection service would be premature because the first product concept is specifically Epics with explicit lifecycle and progress semantics.

### D5: Add REST Endpoints Matching User Operations

Add Epic endpoints under a single route group, for example:

- `GET /api/epics`
- `POST /api/epics`
- `GET /api/epics/:id`
- `POST /api/epics/:id/issues`
- `DELETE /api/epics/:id/issues/:issueId`
- `POST /api/epics/:id/done`
- `POST /api/epics/:id/close`

Extend issue detail responses, or provide an adjacent issue membership lookup, so the Issue Detail page can display the primary Epic link. Prefer extending the existing issue detail shape if that page already receives a rich issue payload; otherwise keep the lookup explicit to avoid widening unrelated issue list responses.

API responses should include structured error codes for not found, invalid input, and issue-already-in-epic so the Web UI can show specific feedback during add-issue.

**Alternatives considered:** A generic CRUD endpoint with partial status updates would reduce route count, but lifecycle verbs make it harder to accidentally treat Epic status as arbitrary editable progress. GraphQL or client-side composition is not aligned with the current API style.

### D6: Keep CLI as a Thin Server-Backed Client

Implement `mo epic` as a command group that calls the same API client used by other server-backed commands. CLI output should be readable first and machine-friendly where existing CLI conventions allow. Commands should map directly to the accepted operations:

- `mo epic create --title ... --description ... --priority ...`
- `mo epic list`
- `mo epic show <id>`
- `mo epic add-issue <epic-id> <issue-id>`
- `mo epic remove-issue <epic-id> <issue-id>`
- `mo epic close <id>`

If the product needs a done operation from CLI, use `mo epic done <id>` or an explicit `--done` action only if consistent with existing command naming. The acceptance criteria explicitly require close, while the product behavior also requires marking done; the implementation should expose both unless later scoped down.

**Alternatives considered:** Direct SQLite access from CLI would work offline but bypass server validation and duplicate progress projection. Folding Epic commands into `mo issue` would obscure that Epics are not executable issues.

### D7: Add Web UI as a Separate Navigation Surface

Add `Epics` to the main navigation and implement separate list/create/detail pages. Do not add Epics to Board lanes. The detail page should lead with status, priority, progress, next issue, description, and linked issues; issue management can be a simple add-by-issue-id flow in the first version.

Issue Detail should render a compact "Part of Epic" backlink when the issue has membership. The backlink should use the backend-provided primary Epic summary rather than searching client-side.

**Alternatives considered:** Showing Epics on the Board would increase visibility but conflicts with the Board's issue-workflow meaning. Building a sophisticated issue picker or success-criteria editor in v1 would add UI complexity before the core tracking loop is validated.

### D8: Preserve Existing Workflow Semantics by Absence, Not Guard Proliferation

Epics should not be startable because they are not issues and should not have commands or API endpoints that enter the workflow engine. Existing workflow queries should not need special `where kind != epic` filters. Any Epic-specific lifecycle action should stay in the Epic service.

**Alternatives considered:** Adding explicit guards inside every workflow/start/worktree path could prevent misuse, but it creates broad modification surface and future maintenance risk. Keeping Epics out of the issue table eliminates most invalid states by construction.

## Risks / Trade-offs

- [Progress state mapping may be ambiguous if issue statuses do not directly map to delivered, blocked, active, and backlog] → Centralize the mapping in one backend helper and cover it with focused tests using current issue statuses.
- [Read-time projection can make Epic list queries heavier when many Epics link many issues] → Batch-load linked issues for list responses instead of issuing one query per Epic; add indexes on `epic_issues.epic_id` and `epic_issues.issue_id`.
- [The one-Epic rule may be too restrictive later] → Use a join table now so future multi-membership can relax the unique `issue_id` constraint without moving data out of the issue row.
- [CLI and Web may drift in wording or next-issue behavior] → Return projected progress from the API and keep clients presentation-only.
- [Deleting or closing linked issues may create confusing Epic progress] → Do not cascade workflow changes; make projection tolerate missing or closed issues and show their current issue status clearly in the linked issue list.
- [Users may expect done to auto-trigger when all linked issues are delivered] → Keep `done` manual and show "ready to mark done" when there is no next issue.

## Migration Plan

1. Add a SQLite migration for `epics` and `epic_issues` with indexes and the unique `issue_id` constraint.
2. Add Epic repo methods and service-level projection, validation, lifecycle, and membership operations.
3. Add API routes and extend issue detail membership data.
4. Add API client methods, React Query hooks, navigation entry, Epic list/create/detail pages, add/remove issue UI, lifecycle buttons, and issue-detail backlink.
5. Add `mo epic` CLI commands using the shared API client.
6. Add tests for migration, service projection, duplicate membership error handling, API routes, and CLI command mapping where existing test patterns support it.
7. Verify that Board queries and issue start behavior remain issue-only and unchanged.

Rollback is straightforward before users create Epics: remove the route/UI/CLI code and migration. After users create Epics, rollback should leave the new tables in place or include an explicit backup/export step before dropping them, because Epic membership is user-authored data.

## Open Questions

- Which exact existing issue statuses count as delivered, blocked, active, and backlog in the projection helper?
- Should `mo epic done <id>` be included alongside the required `mo epic close <id>` to match the Web lifecycle action?
