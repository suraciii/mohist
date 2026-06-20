## Why

Epics only offer two destructive-ish exits today: `Close` (which unbinds all linked issues via `EpicClosed` → `RemoveAllLinkedItems`) and `Mark Done` (which requires every issue complete). Users need a reversible middle state to park an Epic without losing its issue links or polluting their "推进" view, and the word must not collide with issue-health "blocked". Separately, the Epic detail topbar renders `Epic #` + a truncated raw id (e.g. `Epic #epic_313`) instead of the human `Epic #<number>`, so users cannot tell which Epic they are on at a glance — unlike the working `Issue #N` binding.

## What Changes

- Add a `Paused` lifecycle status to `EpicStatus` (distinct from issue-health "blocked"; Chinese label 「暂停」)
- Add `Active ↔ Paused` transitions (`Pause()` / `Resume()`) and allow `Paused → Closed`, while forbidding `Paused → Done` (must Resume first)
- Keep `Paused` non-terminal (`IsTerminal` stays `done|closed`); entering `Paused` MUST NOT unbind any linked issues
- Optional pause-reason text persisted on the Epic and shown on the detail page
- Expose pause/resume over the API (new resume route or extended status-set semantics) and a `useResumeEpic` frontend mutation
- Epic list renders a dedicated `Paused` group (after `Active`, before `Done`) with an amber badge distinct from Active(green)/Done(blue)/Closed(grey); Paused Epics are de-emphasized in the "推进" view
- Detail page adds a `Pause` button (beside Edit / Mark Done / Close) with a confirm dialog; it becomes `Resume` while Paused
- Fix the Epic detail topbar to show `Epic #<number>` (e.g. `Epic #1`) instead of the raw id truncation, parity with `Issue #N`; other route titles unchanged

## Capabilities

### New Capabilities

_None_ — all changes extend existing capabilities.

### Modified Capabilities

- `epic-tracking`: `EpicStatus` gains `Paused`; Epic Domain Model and Epic Lifecycle requirements change to define Pause/Resume semantics, the non-terminal nature of Paused, the `Paused → Done` prohibition, and the invariant that Pause preserves all linked issues. Paused Epics are excluded from "推进" emphasis while progress projection itself is unchanged.
- `http-api`: Epic API Endpoints requirement changes to expose pause and resume actions (new `POST /epics/:id/resume` and pause entrypoint) and to return the optional pause reason.
- `web-ui`: Epic detail page gains Pause/Resume action with confirm dialog and persisted reason display; Epic list gains the Paused group with an amber badge and de-emphasis; Epic detail topbar title is corrected to render `Epic #<number>` (resolving the raw-id truncation bug) without affecting other route titles.

## Impact

- **Domain model (`Epic` aggregate from #178)**: new `Paused` enum value; new `Pause()`/`Resume()` transition methods; `EpicAlreadyTerminalException` must NOT fire on Paused (only on `done|closed`); `EpicProgress.IsTerminal` stays `done|closed`.
- **API surface**: new resume endpoint (and pause entrypoint) on the Epic routes; existing done/close/mark-status paths reused where possible; `EpicDto` carries optional pause reason.
- **Frontend**: `StatusBadge` and list grouping must add the Paused branch; detail page action row gains Pause/Resume; `Header.usePageTitle()` must resolve the Epic `number` on `/epics/:id` (reuse `useEpic`) rather than slicing the raw id; `App.tsx` route and list navigation may keep id-based paths since the API accepts id or number.
- **Out of scope (per Non-Goals)**: Paused does not stop workflows from starting on its issues; no pause/resume history timeline; no auto-pause; close-unbind semantics deferred to #179; issue/other-route topbar titles untouched.
- **Dependencies**: builds on the Epic aggregate extracted in prerequisite #178; risk is medium due to the shared status machine reused by mark-done/close and the app-shell route-driven title plumbing.
