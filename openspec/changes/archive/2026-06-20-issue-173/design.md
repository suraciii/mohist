## Context

Prerequisite #178 extracted the `Epic` aggregate into a typed domain model under `packages/server/src/Mohist.Server/Epic/`. The current lifecycle is `active → done | closed`, enforced in `Epic.Transitions.cs` via `MarkDone` / `Close`, each guarded by `EnsureNotTerminal` (only blocks `done|closed`). Close has a destructive side-effect: `EpicGrain.ApplyPendingEvents` watches for the `EpicClosed` domain event and calls `RemoveAllLinkedIssues` (`EpicGrain.cs:269-287`), unbinding every linked issue. There is no reversible "park" state.

The Epic write path is `EpicGrain.SetStatusAsync` (`EpicGrain.cs:111-147`), exposed by `EpicRoutes.cs` as one route per action: `POST /epics/:id/done` and `POST /epics/:id/close`, both delegating to `SetStatusRouteAsync`. `EpicQuerier` reads back `EpicRow` and projects progress via `EpicProgress.Build`; `EpicProgress.IsTerminal` already returns true only for `Done|Closed` (`EpicProgress.cs:22`).

On the web side, `packages/web/src/widgets/app-shell/ui/Header.tsx:7-47` `usePageTitle()` is a pure route derivation. For `/epics/:id` it renders `Epic #${params.id?.slice(0, 8)}` (Header.tsx:19,33) — a truncated raw id (e.g. `Epic #epic_313`). The issue route works because it reads `params.number` from a `:number` route. The frontend `EpicStatus` enum (`entities/epic/model/types.ts`) has `Active|Done|Closed`; list grouping and `StatusBadge` live in `pages/epics/ui/EpicListPage.tsx`, and the detail action row in `pages/epic-detail/ui/EpicDetailPage.tsx:387-418`. An existing `useEpic(id)` hook (`entities/epic/api/queries.ts:16`) fetches `EpicDetail` including `number`.

Constraints: status column is `varchar(32)` so `"paused"` needs no schema change; only `PauseReason` needs a new nullable column. `db.Database.Migrate()` runs on startup (`Program.cs:28`), so schema ships as an EF migration.

## Goals / Non-Goals

**Goals:**
- Add a reversible, non-terminal `Paused` Epic status that preserves all linked issues and stays editable.
- Lock the transition graph: `active ↔ paused`, `paused → closed` allowed, `paused → done` forbidden.
- Persist an optional pause reason and surface it on the detail page.
- Expose pause/resume over the API and the web UI (dedicated list group + detail action).
- Fix the Epic detail topbar to show `Epic #<number>` instead of a truncated raw id.

**Non-Goals (per issue):**
- Paused does not stop workflows from running on an Epic's issues (Epic status and issue execution stay decoupled this round).
- No pause/resume history timeline; no auto-pause.
- No `blocked` Epic concept (deliberately `Paused` to avoid collision with issue-health `blocked`).
- No change to Close's unbind semantics (#179 owns that) — this design only guarantees Pause/Resume never trigger unbinding.
- No change to issue or other-route topbar titles.

## Decisions

### D1. Reuse `EpicStatusChanged`; do NOT introduce `EpicPaused`/`EpicResumed` events
Pause and Resume emit the existing `EpicStatusChanged(old, new)` record only. The grain's unbind side-effect keys exclusively off `EpicClosed` (`EpicGrain.cs:275`), so a status-only event cannot accidentally unbind.
- **Alternative considered:** new `EpicPaused`/`EpicResumed` event types added to the `EpicEvent` union. Rejected — they would carry no data beyond what `EpicStatusChanged` already records and would force updates to `EpicEventAssertions` and every union consumer for no behavioral gain.

### D2. New domain exception for the `paused → done` prohibition
Add `EpicPausedCannotMarkDoneException` in `EpicLifecycleExceptions.cs`. `MarkDone` gains an explicit guard **before** its existing logic: `if (_status is EpicStatus.Paused) throw new EpicPausedCannotMarkDoneException(Id);`. The existing `EnsureNotTerminal(EpicStatus.Done)` only blocks `done|closed` and deliberately stays unchanged, so `EpicAlreadyTerminalException` never fires on `paused` (satisfies the invariant in the issue). Map the new exception to API code `EPIC_PAUSED_CANNOT_MARK_DONE` (HTTP 409) alongside the existing `EPIC_NOT_READY_TO_MARK_DONE`/`EPIC_ALREADY_TERMINAL` handlers in `EpicRoutes.SetStatusRouteAsync`.
- **Alternative considered:** reuse `EpicNotReadyToMarkDoneException`. Rejected — its message/shape ("N undelivered issues") would mislead a user whose real problem is the paused state.

### D3. `Pause`/`Resume` transition methods
- `Pause(string? reason, DateTime? now)`: idempotent on already-paused (no-op, mirroring `LinkIssue` duplicate semantics); throws `EpicAlreadyTerminalException` if `done|closed`; otherwise sets `_status = Paused`, stores reason, `Touch`, records `EpicStatusChanged`.
- `Resume(DateTime? now)`: requires `paused` (otherwise no-op); clears the stored reason; sets `_status = Active`; records `EpicStatusChanged`.
- `Close` is unchanged at the domain level — `EnsureNotTerminal(Closed)` already permits `paused → closed`. Only the `ToStatusName`/`StatusName`/`ParseStatus` maps gain a `Paused => "paused"` branch.
- **Alternative considered:** make `Pause` on a paused Epic throw. Rejected — idempotent no-op matches the established `LinkIssue` pattern and is safer under retries.

### D4. Dedicated API routes, not SetStatus overloading
Add `POST /epics/:id/pause` (optional body `{ reason?: string }`) and `POST /epics/:id/resume`, mirroring the existing `/done` `/close` one-action-per-route convention. Add `PauseAsync(string? reason)` and `ResumeAsync()` to `IEpicGrain`; both reuse the grain's existing load → mutate → `MapToRow` → `ApplyPendingEvents` → save flow. Keep `SetStatusAsync` untouched (still serves `/done` and `/close`).
- **Alternative considered:** extend `SetStatusAsync(string status)` with a `"paused"`/`"active"` branch and thread reason through. Rejected — SetStatus is directional (it computes undelivered counts for done), and overloading it with both directional resume logic and an optional reason muddies one method; dedicated routes keep each action's validation local and the reason a natural body parameter.

### D5. Persist `PauseReason` as a nullable column
Add `string? PauseReason` to `Epic`, `EpicRow`, and the three DTOs (`EpicDto`, `EpicWithProgressDto`, `EpicDetailDto`). Add a nullable column via a new EF migration; `Migrate()` applies it on startup. `MapToRow`/`Materialize` and `EpicQuerier.ToWithProgressAsync`/`ToDetailAsync` carry it through. Pause sets it; Resume clears it; Close leaves it for the record.
- **Alternative considered:** store the reason only in `EpicUpdated`/a free-text field. Rejected — a first-class column is queryable, maps cleanly to the DTO the web already consumes, and costs one nullable `text`.

### D6. Topbar fix: resolve Epic number in `Header` via `useEpic` (Option A)
`Header` already runs a query (`useAgentStatus`). Extend `usePageTitle` (or a small `useEpicPageTitle` hook) so that on `/epics/:id` it calls `useEpic(params.id)` and returns `Epic #${epic.number ?? params.id.slice(0,8)}`. While loading, fall back to `Epic #…`. The detail page already fetches the same `['epics', projectId, id]` entry, so react-query dedupes — no extra network cost beyond the existing detail load. Server already resolves both id and number at `GET /epics/:id` (`EpicRoutes.cs:33-40`), so this works whether the segment is an id or a number.
- **Alternative considered (Option B):** change the route to `/epics/:number` and have `Header` read `params.number`. Rejected this round — it ripples through list navigation (`EpicListPage.tsx:52` uses `epic.id`), the `Issue Detail Epic Backlink` link target, and every test, with no user-visible benefit over Option A. The route can still be migrated later if desired.

### D7. List grouping + detail action UI
- `entities/epic/model/types.ts`: add `Paused = 'paused'` to `EpicStatus` and `pauseReason?: string | null` to `Epic`/`EpicDetail`/`EpicWithProgress`.
- `EpicListPage.tsx`: add a `pausedEpics` filter and a `Paused` section ordered between `Active` and `Done`; extend both `StatusBadge` color maps (list + detail) with `Paused => 'bg-amber-100 text-amber-700'`. De-emphasize paused cards (muted opacity) so they don't compete with active work in the "推进" view.
- `EpicDetailPage.tsx`: in the action row, render `Pause` (opens a confirm `Dialog` with an optional reason `Input`) when `Active`; render `Resume` when `Paused`. Hide/disable `Mark Done` when `Paused` with a "Resume first" hint (in addition to the existing `!readyToMarkDone` guard at `EpicDetailPage.tsx:336`). Show `pauseReason` near the status badge when present. Add `usePauseEpic`/`useResumeEpic` mutations to `entities/epic/api/queries.ts` + `client.ts` (`pauseEpic(id, reason?)`, `resumeEpic(id)`).

## Risks / Trade-offs

- **[MarkDone silently succeeds on a Paused Epic if the guard is forgotten]** → D2 adds an explicit `Paused` check at the top of `MarkDone`, backed by a unit test mirroring `MarkDone_OnDoneEpic_ThrowsAlreadyTerminal` (`EpicTransitionsSpecs.cs:27`). Existing `EnsureNotTerminal` is intentionally narrow, so this is the one place the bug could hide.
- **[Pause/Resume accidentally triggers unbind]** → D1 guarantees only `EpicClosed` drives `RemoveAllLinkedIssues`; add a spec asserting linked-issue count is unchanged after Pause (contrast with Close).
- **[Header shows stale `Epic #…` while the number query loads]** → acceptable: matches the loading state elsewhere; react-query dedupes against the detail fetch so it is transient. Fallback keeps the `#` prefix so layout is stable.
- **[EF migration on existing databases]** → `PauseReason` is nullable; no backfill. Adding `Paused` to the status enum needs no schema change (column is `varchar(32)`).
- **[Rollback surfaces paused Epics as Active]** → on revert, `ParseStatus` already defaults unknown statuses to `Active` (`EpicGrain.cs:262-267`), so a paused Epic degrades gracefully to Active; the nullable `PauseReason` column is harmless to old code. Two-way roll back is safe.
- **[Pause reason on Close is preserved]** → intentional (audit), trade-off is a non-empty reason on a closed Epic; surfacing it on closed detail is fine since the status badge already says Closed.

## Migration Plan

1. Backend: add `Paused` enum value, `Pause`/`Resume` methods, `EpicPausedCannotMarkDoneException`, `PauseReason` on `Epic`/`EpicRow`/DTOs, grain `PauseAsync`/`ResumeAsync`, new EF migration, `EpicRoutes` `/pause` + `/resume`, status name maps, and the `EPIC_PAUSED_CANNOT_MARK_DONE` handler.
2. Frontend: extend `EpicStatus` enum + types, add `pauseEpic`/`resumeEpic` clients and hooks, add the `Paused` list section + amber badge, add the Pause/Resume detail action + reason dialog + display, and the `Header` number resolution.
3. Tests: domain specs (pause/resume transitions, paused→done rejected, reason persisted/cleared, links preserved); API specs (`/pause`, `/resume`, `paused` done rejected with code); web tests (Paused section ordering, Pause/Resume buttons toggle, topbar shows `Epic #<number>`).
4. Deploy: `db.Database.Migrate()` applies the column on server boot; no data backfill; frontend build ships. Rollback is `dotnet ef database update <prev>` plus a revert of the deploy — paused rows degrade to Active as noted above.

## Open Questions

- **Resume reason handling:** D5 clears `PauseReason` on Resume for a clean slate. If we later want a pause/resume history (a Non-Goal today), the reason would need to live on a history row instead — flag for #177/#179 alignment.
- **D6 long-term route shape:** keep `/epics/:id` (Option A) or eventually migrate to `/epics/:number` for full parity with issues (Option B). This issue picks A for blast radius; the route migration can be revisited separately.
- **Paused Epic aggregation:** whether Paused Epics should appear in any future dashboard/decision counts is explicitly out of scope here and left to the epic-experience epic (#11).
