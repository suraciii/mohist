## Context

`mo update server/runner` is driven by `SystemUpdateService` and four collaborator
types, all currently declared in a single 1074-line file
(`packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs`):

- `FileSystemSystemUpdateStore` (+ `ISystemUpdateStore`) — file lock + atomic
  temp-file rename persistence.
- `ProcessSystemUpdateCommandRunner` (+ `ISystemUpdateCommandRunner`,
  `SystemCommandRequest`/`SystemCommandResult`) — wraps `systemctl`/`dotnet`.
- `HttpSystemReadinessProbe` (+ `ISystemReadinessProbe`, `SystemReadinessResult`)
  — health/root/bundled-asset HTML probing.
- `SystemUpdateJobState` — the job record with `ActiveStatuses`/`TerminalStatuses`.

The main service additionally owns 7 orchestration responsibilities (start,
build → restart-server → wait-for-reconnect, CLI outcome recording, supersession,
runtime-consistency reporting, command execution logging, failure handling).

Two structural defects sit on top of this:

1. **CQS violation.** The polling endpoint
   `GET /api/system/update/status` → `GetStatusEnvelopeAsync` →
   `GetLatestStatusAsync` nominally *reads* status, but `GetLatestStatusAsync`
   (`SystemUpdateService.cs:436-525`) silently drives the state machine: it
   supersedes stale `waiting-for-reconnect` jobs, persists the waiting
   transition, and (on readiness + hash match) restarts the runner, marks the
   job `succeeded`, and releases the file lock. A caller cannot tell from the
   method name that invoking it mutates the world.

2. **Duplicated transition templates.** The "construct next state → append log
   → save → maybe release lock" sequence is hand-rolled at 13+ sites
   (`:416-425`, `:453-464`, `:476-484`, `:492-500`, `:636-644`, `:832-840`,
   `:846-853`, `:881-891`, `:905-916`, `:920-933`, `:1008-1017`, `:1021-1026`,
   `:1039-1052`). A `FailAsync` helper already exists, yet every `catch` block
   and the non-zero-exit branch reconstruct `state with { Status = "failed", ... }`
   inline, so each copy can drift independently.

Constraints carried over unchanged (see proposal Non-Goals): Linux + systemd
assumption retained, no cross-platform service-manager abstraction, no change to
the file-lock mechanism, no change to the terminal-status set, no rewrite of
state-transition *semantics* — this is a behavior-preserving relocation.

The existing test suite
(`packages/server/tests/.../SystemUpdateServiceSpecs.cs`) pins the *side-effectful*
behavior to the `GetLatestStatusAsync` method name (8 specs). That is the key
tension this design must reconcile.

## Goals / Non-Goals

**Goals:**
- Decompose the monolith into one cohesive type-group per file under the same
  `Mohist.Server.SystemInfo` namespace, with all type/member names preserved so
  DI wiring (`MohistServiceRegistration:87-89`) and the HTTP contract are
  untouched.
- Make the status query entry point strictly read-only; move every
  state-machine advancement off the query path onto an explicit command method,
  preserving transition semantics and ordering exactly.
- Consolidate the duplicated build-next-state / append-log / save /
  release-lock templates and the "mark failed and save" pattern into shared
  helpers, so there is exactly one definition of each transition.
- Leave the already-healthy sibling types byte-for-byte unchanged.

**Non-Goals:**
- No cross-platform service-manager abstraction (launchd / Windows Service).
- No change to the file-lock / atomic-write mechanism.
- No change to state-transition *semantics* (statuses written, log content,
  ordering, lock-release points, 200-entry log bound).
- No change to the terminal-status set or the JSON wire shape.
- No introduction of `TimeProvider` injection. The current `DateTimeOffset.UtcNow`
  usage is retained as-is to keep this a pure refactor; wall-clock injection is
  a separate concern.

## Decisions

### 1. File split: one cohesive type-group per file, same namespace and names

Each extracted collaborator moves to its own file under
`SystemInfo/`, keeping `namespace Mohist.Server.SystemInfo` and every
type/member name:

- `SystemUpdateJobState.cs` — `SystemUpdateJobState` record +
  `ActiveStatuses`/`TerminalStatuses`.
- `FileSystemSystemUpdateStore.cs` — `ISystemUpdateStore` +
  `FileSystemSystemUpdateStore` (owns lock acquisition, atomic rename,
  `SaveIfCurrentAsync`).
- `ProcessSystemUpdateCommandRunner.cs` — `ISystemUpdateCommandRunner` +
  `ProcessSystemUpdateCommandRunner` + `SystemCommandRequest`/`SystemCommandResult`.
- `HttpSystemReadinessProbe.cs` — `ISystemReadinessProbe` +
  `HttpSystemReadinessProbe` + `SystemReadinessResult` (owns health/root/asset
  HTML parsing).
- `SystemUpdateService.cs` — orchestration only.

**Rationale:** Namespaces and names are part of the DI/HTTP contract surface,
so keeping them identical is the lowest-risk split. Keeping `SystemUpdateService.IsActive`
(`:963`, referenced by the store at `:119`) as a member of the service preserves
that cross-type reference without introducing a new shared static.

**Alternatives considered:**
- *Partial classes across files.* Rejected — hides the type count behind one
  name and does not address the "one responsibility per file" readability goal.
- *Sub-namespace (`SystemInfo.Updates`).* Rejected — changes the namespace, which
  the composition spec forbids and which would ripple into DI/JSON options.
- *Sub-folder without namespace change.* Acceptable mechanically, but the spec
  fixes the directory to `SystemInfo/`, so files stay flat there.

### 2. CQS split: a command method drives advancement; the query becomes pure

`GetLatestStatusAsync` is split into two public methods:

- **Query — `GetLatestStatusAsync`** (read-only): reads the latest persisted
  state via the store, projects it to `SystemUpdateStatusResponse`, returns it.
  It performs *no* `_commandRunner` dispatch, *no* `_store.SaveAsync`, *no*
  `ReleaseLockAsync`. It may read runtime facts (`_getSystemInfo`,
  `_readinessProbe`) only if needed to *describe* progress, but never to
  persist. (See Decision 2b for the readiness-read boundary.)
- **Command — `AdvanceActiveJobAsync`** (new): owns the three advancement
  branches currently hidden inside the query:
  1. *Supersede* a stale `waiting-for-reconnect` job whose runtime hash has
     advanced past its source HEAD.
  2. *Persist the waiting transition* when readiness fails and the stage/reason
     would change (preserving the dedup gate).
  3. On readiness success + hash match: persist the ready transition → restart
     the runner (if a runner unit exists) → mark `succeeded` → release lock,
     in that exact ordering (`SystemUpdateService.cs:491-519`).

**Polling orchestration:** `GetStatusEnvelopeAsync` (the sole polling entry,
called by `SystemRoutes.cs:100`) becomes **command-then-query**: it invokes
`AdvanceActiveJobAsync`, then `GetLatestStatusAsync`, then wraps the read into
the envelope. The *same polling flow* that advances the job today still advances
it; it just does so through an explicit command. Observable responses from the
endpoint are identical in status sequence and content.

**Rationale:** This is the smallest change that satisfies "the query is pure"
without altering the polling contract or the web client. The method name
`AdvanceActiveJobAsync` signals mutation; `GetLatestStatusAsync` reads true.

**Alternatives considered:**
- *Keep mutation in the query, just rename it.* Rejected — the violation is that
  a *read-shaped call* mutates; renaming alone does not create a read path.
- *Split the polling endpoint into `POST .../advance` + `GET .../status`.*
  Rejected — changes the HTTP contract and forces a web-client change for no
  semantic gain; the spec mandates the polling endpoint still reflects progress.
- *Route the command through a background worker instead of inline in the
  request.* Rejected — changes timing/visibility semantics and risks losing the
  "poll observes progression" guarantee.

### 2b. Where runtime facts may be read

To keep the query honest while preserving progress reporting, the **command**
is the path that reads `_getSystemInfo`/`_readinessProbe` and decides
advancements. The **query** reads only persisted state from the store (plus, if
strictly necessary to populate a response field already derived by the command,
a non-mutating `_getSystemInfo` for the running hash). The deciding rule: *any
read whose result changes what gets persisted belongs on the command path; the
query never interprets facts into a state transition.*

### 3. Transition helpers: one persist path + one failure definition

Two shared helpers own every state transition:

- **`PersistTransitionAsync`** (new) owns the shared tail:
  `append log (bound to 200) → set UpdatedAt → SaveAsync → optionally
  ReleaseLockAsync`. Call sites build their `next` state via `with` (field
  deltas are inherently per-transition) and hand it plus a `SystemUpdateLogEntry`
  and a `releaseLock` flag to the helper. This removes the inline
  `AppendLog(...) + SaveAsync` pairs scattered across the pipeline.
- **`FailAsync`** (generalized from `:1039`) becomes the *single* definition of
  the `failed` transition. Its parameters cover every existing failed-state
  shape: `reason`, optional `stage` (defaults to current), optional `outcome`,
  optional `unavailableCapability`, optional `logStage`/`logMessage`, and
  `releaseLock`. The four inline failed-state constructions
  (`:416-425` StartAsync catch, `:846-853` RunUpdate catch, `:1008-1017`
  non-zero exit, `:920-933` restore failure) all route through it. The
  `recovered` transition (`:905-916`) is *not* a failure, so it keeps its own
  shape but still routes its persist+release through `PersistTransitionAsync`.

**Rationale:** The append/bound/save sequence is identical everywhere; only the
field deltas differ, so the helper owns the invariant (including the single
200-entry cap definition) and call sites own their deltas. Generalizing
`FailAsync` rather than adding a parallel method keeps "how a job fails"
defined exactly once.

**Alternatives considered:**
- *A separate `SystemUpdateTransitionService`.* Rejected — these are private
  orchestration helpers, not a collaborator; extracting a service would add a DI
  surface and a type the composition spec does not ask for.
- *Extension methods on `SystemUpdateJobState`.* Rejected — they cannot reach
  the store/lock without being passed in, so they would re-introduce the same
  inline plumbing they aim to remove.
- *A single mega-helper that takes every field as optional.* Rejected as the
  persist granularity; field construction stays at call sites for readability.

## Risks / Trade-offs

- **[The 8 `GetLatestStatusAsync_*` specs pin behavior to the soon-to-be-pure
  method]** -> Migrate them to assert against the polling *flow*
  (`GetStatusEnvelopeAsync`) and/or the new command (`AdvanceActiveJobAsync`),
  and add new specs asserting the query path issues zero command dispatches,
  zero saves, and zero lock releases. "No regression" is honored at the
  *polling-endpoint / system-update-flow* level, not at the `GetLatestStatusAsync`
  *method* level — because the method's behavior is intentionally changing
  (that is the CQS fix). This reconciliation is explicit in the spec's
  success criteria.
- **[Polling still mutates within one `GET`]** -> CQS is satisfied at the
  method/layer level (command vs. query are distinct), but the HTTP verb is
  still `GET`. Mitigation: this is the documented trade-off of Decision 2; the
  alternative (a `POST` advance endpoint) was rejected as contract-breaking.
  The command path is named and separable, so a future verb split is a
  mechanical follow-up with no logic change.
- **[Split could accidentally move `SystemUpdateService.IsActive`, breaking the
  store's reference at `:119`]** -> Keep `IsActive` on `SystemUpdateService`;
  do not relocate it during the split (it is not one of the four extracted
  collaborator groups).
- **[Shared helper subtly changes timestamp/lock-release ordering]** -> The
  helper preserves the existing pattern: a single `DateTimeOffset.UtcNow`
  stamps both `UpdatedAt` and the log entry per transition, and
  `ReleaseLockAsync` runs strictly *after* `SaveAsync`. Tests assert ordering
  (ready → restart → succeeded → release), not wall-clock values.
- **[Test flakiness from `DateTimeOffset.UtcNow`]** -> Pre-existing and
  out of scope; the refactor neither adds nor removes wall-clock reads. Noting
  it so a future `TimeProvider` pass is not conflated with this change.

## Migration Plan

This is a behavior-preserving, server-only refactor. No data migration, no
contract change, no new dependency.

1. Land the change on a branch; run `npm test` (server) and the web/runner
   typechecks. All migrated + new system-update specs must pass.
2. Verify the diff touches *only* files under `SystemInfo/` plus the test spec
   file — the sibling files listed in the composition spec must show no edits.
3. Deploy via `mo update server` (the managed restart path; do not `dotnet run`,
   per AGENTS.md, to avoid runner-id drift).
4. Post-deploy smoke: trigger one `mo update server` cycle and confirm the
   polling endpoint still progresses `waiting-for-reconnect → succeeded` and
   that a stale job still transitions to `superseded` with the lock released.

**Rollback:** Revert the commit and `mo update server`. There is no persisted
state shape change, so a rollback is immediate with no data conversion.

## Open Questions

- None blocking. The one judgment call already resolved above: "no regression"
  applies at the polling-endpoint level, so the existing `GetLatestStatusAsync_*`
  specs are migrated (renamed/re-pointed) rather than left to fail.
