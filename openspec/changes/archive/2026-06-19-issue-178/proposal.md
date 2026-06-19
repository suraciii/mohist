## Why

The Epic module is an anemic model: there is no `Epic` aggregate root, no `EpicStatus` enum, and no domain events. Every business rule — terminal guards, mark-done readiness, the close→unlink side effect — is inlined inside `EpicGrain`, which operates directly on `EpicRow` + `DbContext` via magic strings (`"active"`/`"done"`/`"closed"`, `"p0"`..`"p4"`) and procedural code. This scatters rules across the grain, leaves them undescribable, and hides invariants (the Status/Health confusion bug and the invisible close side effect at `EpicGrain.cs:118-125` are both symptoms). It also forces upcoming lifecycle features (#173 Paused, #177 auto-done) to keep piling ad-hoc branches onto the grain. We need a complete, describable, concise Epic domain model so rules are encapsulated and features can be built cleanly on top of it.

## What Changes

- Add an `Epic/Domain/` layer mirroring `Issue/Domain/`:
  - `Epic.cs` — aggregate root (partial): `Id`/`ProjectId`/`Number` + private state fields
  - `Epic.Transitions.cs` — `Create` / `Update` / `MarkDone` / `Close` / `Pause` / `Resume`; each method = precondition guard + state change + `RecordEvent`
  - `EpicStatus.cs` — enum `Active`, `Done`, `Closed`, replacing magic status strings
  - `EpicPriority.cs` — value object `p0..p4` + `From()` + ordering (reused by #171)
  - `Events/EpicEvent.cs` — `EpicCreated` / `EpicUpdated` / `EpicPriorityChanged` / `EpicIssueLinked` / `EpicIssueUnlinked` / `EpicStatusChanged` / `EpicClosed`
  - `EpicLifecycleExceptions.cs` — moved from `Grains/` into `Domain/`
- Encapsulate the locked invariants inside `Epic.Transitions`:
  - State machine: `Active→Done` (MarkDone), `Active→Closed` (Close), terminal refusal → `EpicAlreadyTerminalException`
  - `MarkDone(IReadOnlySet<int> undeliveredLinkedNumbers)`: non-terminal + empty undelivered set, else `EpicNotReadyToMarkDoneException`; completion = linked issue status `done|completed` (health ignored), defined in one place
  - No-duplicate link set enforced inside the aggregate
- Thin `EpicGrain` to a load→call domain method→persist arrangement (mirroring `IssueGrain`/`IssueStore`); the close→unlink-all side effect is preserved but now driven by the `EpicClosed` event into grain persistence, making it explicit and describable
- Refactor `EpicProgress` into a pure projection consuming the typed model (`IsTerminal`/`IsCompleted` built on `EpicStatus` + the single completion judgment), eliminating Status/Health confusion at the source
- **No observable behavior change**: same transitions, guards, exceptions, and side effects; **no DTO or API contract changes**

## Capabilities

### New Capabilities

None. The refactor introduces internal domain structure but no new observable capability.

### Modified Capabilities

None. The existing `epic-tracking` spec already defines all observable Epic behavior (domain model with `active`/`done`/`closed`, primary issue membership, projected progress, explicit mark-done/close lifecycle). This change preserves that contract; only the internal implementation is restructured.

## Impact

- **Affected code**: `packages/server/src/Mohist.Server/Epic/` — new `Domain/` layer; `Grains/EpicGrain.cs` thinned to load→domain→persist; `Services/EpicProgress.cs` becomes a pure projection; `Grains/EpicLifecycleExceptions.cs` relocated to `Domain/`
- **Reference structure**: `packages/server/src/Mohist.Server/Issue/Domain/` (`Issue.cs`, `Issue.Transitions.cs`, `IssueStatus.cs`, `IssuePriority.cs`, `Events/IssueEvent.cs`) — the refactor mirrors this layout
- **Persistence**: `Infrastructure/Data/Epic/EpicRow` remains the persistence model; mapping lives in the grain. No schema or migration changes
- **API/DTO**: unchanged — no routes, request/response shapes, or contracts altered
- **Tests**: existing Epic tests, including `EpicApiSpecs`, must pass unchanged; no new external behavior to test
- **Risk**: medium — cross-cutting architectural refactor of the entire Epic module (aggregate + grain + persistence mapping); behavior-preserving, but a transfer or guard regression could silently break mark-done/close
