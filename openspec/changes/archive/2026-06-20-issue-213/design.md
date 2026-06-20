## Context

Runner state has no dedicated surface. The Web UI embeds the runner list as a card on the Activity page (`packages/web/src/pages/activity/ui/ActivityPage.tsx` → `RunnerListCard`), and there is no terminal path to runner status. The backend, however, already exposes everything the listing needs: `GET /api/projects/{projectRef}/runners` (`packages/server/src/Mohist.Server/Api/RunnerStatusRoutes.cs`) returns `RunnerStatusListResponse { runners: RunnerStatusView[] }`, where each `RunnerStatusView` carries `id`, `kind`, `hostname`, `scope { type, projectId, projectName }`, `status`, `registeredAt`, `lastHeartbeatAt`, `connectionState`, `capabilities`, `coderModels`, `capacity { usedSlots, totalSlots }?`, and `activeWork?`. The `status` field is server-derived by `RunnerStatusService.DeriveStatus` into exactly four values: `idle`, `busy`, `stale`, `offline`. `ListEligibleRunnersAsync(projectId)` returns global runners plus runners scoped to that project — so the response is already the full set the listing must show.

The Web already has the building blocks: `entities/runner` (`useRunners()` polls every 5s, `RunnerStatusRow`, `deriveRunnerSummary`) and `widgets/runner-status` (`RunnerList`, `RunnerSummary`/`RunnerSummaryBadge`). The CLI has a `mo runner` command group (`MohistCliCommands.Server.cs` → `RunnerCommands`) for service management, a generic `PrintWithOutputAsync` + `TableShape` enum + plain-text `TableRenderer`, and `ProjectRefOption` for project overrides.

This change is purely additive on the surfaces: one new Web page, one new CLI subcommand, one relocation on the Activity page. No server, domain, persistence, or API-contract change.

## Goals / Non-Goals

**Goals:**
- Give runners a dedicated, read-only home on both the Web UI (new `Runners` page + nav entry) and the CLI (`mo runner list`).
- Present the shared `idle`/`busy`/`stale`/`offline` taxonomy, a scope filter (all / global / this project), a top status-count summary bar, the full per-row field set, and a start-command empty state — consistently across both surfaces.
- Restore the Activity page to a session-only view while keeping its status-bar runner overview badge as a quick indicator, now linking to the Runners page.

**Non-Goals** (per proposal/issue):
- Runner control actions (deregister, reconnect, pause), history/trend stats, single-runner detail view, real-time log streaming, and any server/API contract change.
- Expanding per-row active-work context.

## Decisions

### D1: No backend/API change — client-side scope filtering and status aggregation
Both surfaces already receive the complete data set from `GET /api/projects/{projectRef}/runners` (global + current-project runners, with server-derived `status`). Scope filtering and the 4-way status counts are pure functions of that returned data, computed where the data is rendered.
- **Alternative considered:** add a `?scope=` query param and a server-side count endpoint. **Rejected** — it duplicates scope semantics into a second source of truth and adds API surface for data the client already holds. The server stays the single authority for the `status` derivation; the client owns only presentation-time filtering.

### D2: Web — a new `RunnersPage` that composes existing widgets, not a fork
Add `pages/runners/ui/RunnersPage.tsx` and a `/runners` route under `ProjectRouteScope` in `App.tsx`. The page reuses `useRunners()` (its 5s `refetchInterval` satisfies live refresh), `RunnerList`/`RunnerRow`, and the `RunnerStatusRow` contract. New code is limited to: a page-level scope filter (local `useState`, default `all`), and a `RunnerStatusSummaryBar` that derives `idle`/`busy`/`stale`/`offline` counts from the scope-filtered rows. Add a `runners` entry to `primaryNav` in `AppSidebar.tsx`.
- **Alternative considered:** build a brand-new table component. **Rejected** — `RunnerRow` already renders the full required field set (id, kind, status badge, scope, capacity, heartbeat freshness, hostname) and handles missing capacity; forking would duplicate that logic and the empty-state start hint.

### D3: Activity page — remove embedded list, repoint the badge, add a link
Drop the `<RunnerListCard />` section from `ActivityPage` so it returns to a session-only view. Keep `<RunnerSummaryBadge />` inside the `StatusBar` as the global quick indicator, but repoint its click target from `/activity` to `/runners` (it currently navigates to `/activity`, which would become a self-link/no-op). Add a small explicit link from Activity to the Runners page.
- **Alternative considered:** leave the badge pointing at Activity. **Rejected** — the badge's affordance is "see runner detail", which now lives on the Runners page.

### D4: CLI — `list` subcommand under the existing `mo runner` group, with its own fetch+filter+render flow
Add `BuildList(api)` to `RunnerCommands.Build`. Reuse `MohistCliCommands.ProjectRefOption()` (`--project`/`--project-id`) and `OutputOption()` (`-o table|json`), and add a new `--scope all|global|project` option (default `all`). The command resolves the project via `ResolveProjectIdAsync`, calls `GET /api/projects/{id}/runners`, extracts `data.runners`, filters by `scope.type` client-side, then renders. In JSON mode it dumps the filtered response verbatim; in table mode it renders via a new `TableShape.RunnerList`.
- **Rationale for a custom flow** (rather than the generic `PrintWithOutputAsync`): the generic helper fetches-then-renders with no pre-render data transformation, but scope filtering is a data concern. A small dedicated fetch+filter step keeps the renderer pure.
- **Alternative considered:** a new top-level `mo runners` command. **Rejected** — the `mo runner` group already exists and the issue specifies adding a `list` subcommand.

### D5: CLI color — ANSI escapes in the status cell only, no new dependency
`TableRenderer` is plain text (`PadRight`, no color) and the project has no terminal-color dependency. Extend it with `RenderRunnerList` (new `TableShape.RunnerList` enum entry) that embeds ANSI escape codes in the status cell only — idle = green, busy = blue, stale = yellow, offline = dim — leaving other cells and the table algorithm untouched (`WriteTable` passes cell strings straight to `_out.WriteLine`, so embedded escapes render correctly). Gate emission on `NO_COLOR` env / `Console.IsOutputRedirected` so piped/non-TTY output stays plain.
- **Alternative considered:** adopt Spectre.Console. **Rejected** — a new dependency for a single colored cell is disproportionate.

### D6: Shared status taxonomy — server-derived, displayed as-is
Both surfaces read `RunnerStatusView.status` directly and never re-derive it. The four values (`idle`/`busy`/`stale`/`offline`) originate solely in `RunnerStatusService.DeriveStatus`; this is the contract boundary the spec's "shared four-value status taxonomy" requirement rests on. Row field rendering mirrors the existing `RunnerStatusRow` shape on both surfaces.

## Risks / Trade-offs

- **[Badge repoint changes existing UX]** → Low impact; the new target is the correct destination once the list moves. Call out in the changelog.
- **[ANSI color on non-TTY / Windows terminals]** → Gate on `NO_COLOR` / `Console.IsOutputRedirected`; fall back to plain status text. Add a CLI test asserting no escapes when output is redirected.
- **[Scope "this project" correctness depends on eligible-list invariant]** → `--scope project` and the web "current project" filter rely on `ListEligibleRunnersAsync(projectId)` returning only the current project's project-scoped runners (never another project's). Add/confirm a server test asserting no foreign-project runners leak into the eligible set.
- **[Two surfaces polling the same endpoint]** → Both Web mounts share the TanStack Query key `['runners', projectId]`, so concurrent views issue one network request, not two. 5s polling is existing behavior and acceptable for status observation.
- **[Missing capacity on offline runners]** → `RunnerStatusView.capacity` is nullable; `RunnerRow` already renders "unavailable" rather than 0. The CLI renderer must mirror this (blank/`-`, not `0/0`).

## Migration Plan

- **Deploy:** purely additive — new route/nav, new CLI subcommand, Activity relocation. No schema, config, or data migration; no feature flag required. Web and CLI ship together.
- **Rollback:** revert the commit; no state to clean up. The runner-status API and `mo runner` service subcommands are untouched.
- **Compat:** no public API shape change; existing `RunnerStatusListResponse` consumers are unaffected. `mo runner list` is a new subcommand name and does not collide with existing service subcommands.

## Open Questions

- **Nav icon:** which lucide icon for the `Runners` entry (e.g. `Server` / `Cpu`)? Minor visual choice; decide during build to match the existing nav set.
- **CLI heartbeat freshness format:** mirror the web's relative age ("3m ago") vs. an absolute timestamp? Plan: relative age for consistency with the web row; confirm during build.
- **CLI status filter:** should `mo runner list` also accept `--status busy`? Out of scope for this issue (spec only requires project + scope filters); defer to a follow-up if asked.
