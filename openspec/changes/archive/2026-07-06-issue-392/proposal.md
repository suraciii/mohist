## Why

Epic `done` has stopped meaning "this milestone is really over". When epic #40's first batch of child issues (#381/#383/#386) reached terminal state, `AutoMarkDoneIfReadyAsync` correctly flipped it to `done`. But when two new open issues (#387/#388) were later linked to it, `LinkIssueAsync` took the `targetIsTerminal` branch (`EpicGrain.cs:107` `if (!targetIsTerminal)`) which only adds the association without re-evaluating epic state — so the epic stayed `done` with two undelivered children, autopilot got stuck, and the status no longer reflected reality. The terminal-epic link path was deliberately designed as "archive-only" (`EpicGrain.cs:80-82`), but that design trades away the trustworthiness of `done`. This change makes epic status always answer "is there still open work under this milestone?".

## What Changes

- **New state transition `done` → `running` ("wake-up")**: linking an open issue to a `done` epic automatically reverts the epic to `running` in the same commit as the link. The wake-up is implicit (no user `resume`/`start` required); after waking, autopilot picks up the newly linked open issue through the existing reconcile path.
- **`done` epic + terminal-issue link stays `done`**: linking an already-terminal issue to a `done` epic does NOT wake the epic — there is no new open work.
- **`closed` link rejected (BREAKING)**: linking any issue to a `closed` epic is rejected. `closed` becomes a true terminal state ("abandoned milestone"); only explicit `Reopen` exits it. This is the decision point the issue defers to the plan stage; the default per the issue body is rejection. **BREAKING** for any caller relying on archive-link to a `closed` epic.
- **Batch link follows the same rules**: `POST /{id}/issues:batch` (`EpicRoutes.cs:105`) evaluates wake-up/reject per the same conditions — a batch containing any open issue to a `done` epic wakes the epic; a batch to a `closed` epic is rejected.
- **Active-membership rows re-established on wake-up**: a woken `done` epic re-adds `EpicActiveIssues` rows for the newly linked open issue (mirroring `ReopenAsync` at `EpicGrain.cs:514-526`), so the cross-aggregate uniqueness invariant and autopilot selection see it.
- **Pin existing invariant 2 with tests**: `MarkDone` / auto-done rejecting while open linked issues exist (`EpicNotReadyToMarkDoneException`) is already implemented (`Epic.Transitions.cs:116-117`); this change adds regression tests to freeze it as part of the same "status mirrors reality" contract.
- **Drop the "archive-only link to terminal epic" capability**: the comment at `EpicGrain.cs:80-82` documenting intentional archive-link semantics is removed; terminal-epic link no longer means "record without affecting state". **BREAKING** (intentional) for anyone depending on it — the issue body flags this as expected breakage.

## Capabilities

Each capability below gets a `specs/<name>/spec.md` describing the required behavior this change establishes.

- `epic-status-revival`: The epic status-mirrors-reality contract — `done` epics wake to `running` on open-issue link (single + batch), `done` epics stay `done` on terminal-issue link, `closed` epics reject all links, active-membership rows are re-established on wake-up so autopilot picks up the new issue, and `MarkDone`/auto-done is rejected while open linked issues exist.

## Impact

- **Domain** (`packages/server/src/Mohist.Server/Epic/Domain/`): `Epic.Transitions.cs` gains an internal wake-up transition (`done` → `running`), and `LinkIssue` / a new closed-link guard surface the right domain exceptions. `EpicLifecycleExceptions.cs` may gain a `closed`-link-rejected exception type. State enum itself (idle/running/paused/done/closed) is unchanged — no schema migration.
- **Grain** (`packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs`): `LinkIssueAsync` (L70-134) and `LinkIssuesAsync` (L136-255) lose the unconditional `if (!targetIsTerminal)` guard around state mutation; they instead branch on `done` vs `closed` and, for `done` + open issue, perform the wake-up + active-membership re-establishment in the same `SaveChangesAsync` transaction. The closed-link path throws / returns a conflict outcome.
- **API** (`packages/server/src/Mohist.Server/Api/EpicRoutes.cs`): batch link error mapping gains the closed-rejection case (new 409 error code, alongside existing `EPIC_ALREADY_TERMINAL`). Single link already surfaces domain exceptions via the existing handler.
- **CLI**: no change — `mo epic link` already routes through the same endpoints; wake-up is transparent to the caller.
- **Web**: no change required; the epic status badge already reflects whatever the grain returns.
- **Tests** (`packages/server/tests/Mohist.Server.Tests/Specs/Epic/`): `EpicLifecycleSpecs.cs` / `EpicMembershipSpecs.cs` / `EpicBatchMembershipSpecs.cs` gain scenarios — done+open wakes to running, done+terminal stays done, closed rejects link, batch with open wakes, MarkDone-with-open-issue rejected. Per `design/testing.md`: time via `TimeProvider`, no real DB/network, fake grains for `IIssueGrain.StartWorkAsync`.
- **Decision records**: the two plan-stage decisions (`done` wakeable / `closed` locked; dropping archive-link) get recorded in `design/` or an issue comment per acceptance criteria.
- **Historical epic #40**: acceptance only requires confirming a handling strategy (data fix vs. accept as legacy); no automated migration is in scope.
- **Out of scope**: the five-state enum, autopilot start/pause/resume internals, active-membership uniqueness constraint, auto-done trigger condition, `unlink` behavior, and any epic #40 data fix.
