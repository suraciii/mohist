# Architecture & Persistence Audit

> **Scope:** `packages/server/src/Mohist.Server/` — bounded-context structure, DI / hosting, persistence (EF Core, Orleans grain state), config, error handling, observability.
> **Out of scope (handled by sibling agents):** Workflow state machine semantics, Issue domain / bus subscription, event-bus / SignalR delivery, runner / session runtime details, code-quality style.
> **Date:** 2026-06-08
> **Status:** Read-only review. No code was modified.

---

## 0. Executive Summary

| Question | Answer |
|---|---|
| Is the architecture production-ready? | **No.** The "ASP.NET Core + Orleans" re-architecture is structurally sound on paper — single-DbContext, IStateStore abstraction, outbox-style event publication — but it is **not yet ready to scale past a single silo** and the Bounded-Context boundaries documented in `AGENTS.md` are not actually enforced by the code. A new contributor cannot tell which BC owns `ProjectWorkflowProfiles`, `Issues`, or `WorkflowRuns` without grepping. |
| Top 3 architectural risks | **R1 (P0):** Cross-BC data access is pervasive — `ProjectQuerier` reads `db.ProjectWorkflowProfiles`, `ProjectGrain` writes to it, `IssueQuerier` reads `db.WorkflowRuns` + `db.WorkflowLeases`, `WorkflowProfileManager` reads `db.Issues` — none of these calls go through a service boundary, so a schema rename in Workflow BC silently breaks Issue / Project. **R2 (P0):** `WorkflowGrain` writes the run + events transactionally, but lease / variables are saved **outside** that transaction (`WorkflowGrain.cs:958-974` → `CommitAsync`); a silo crash between the two writes leaves the run state ahead of the lease. **R3 (P0):** The IStateStore abstraction is broken — 5 of 8 implementations throw `NotSupportedException` for `ListAsync` and `DeleteAsync`; `AgentSessionStore` overrides `SaveAsync` with three overloads that don't line up with the interface. The interface has outlived its purpose. |
| Top 3 quick wins | **W1:** Make `IStateStore<T>` split into `IKeyValueStore<T>` (Load/Save) and `IKeyValueListStore<T>` (List) — eliminates 10 `NotSupportedException` throws and clarifies per-store capability. **W2:** Change the IStateStore SaveAsync / LoadAsync / DeleteAsync / ListAsync signatures to take `CancellationToken` and pass the request CT down (currently dropped in 5 stores). One line per method. **W3:** Replace the hand-rolled `BeginTransactionAsync` + `catch (DbUpdateConcurrencyException) { Rollback; throw; }` blocks in `WorkflowRunStore.cs:45-60` and `AgentSessionStore.cs:48-79` with `try/finally` (the using statement will dispose / rollback anyway) — one less place where an exception is silently swallowed. |

**Counts: 10 P0, 26 P1, 21 P2, 6 P3.** Full table below.

---

## 1. Bounded-Context Boundaries

The `AGENTS.md` `## 核心实现结构` block describes Workflow, Issue, Project, Epic, Runner, Sessions, SystemInfo, Events as peer Bounded Contexts. The code does not enforce this.

### 1.1 Cross-BC table access (`grep "db\.(WorkflowRuns|WorkflowLeases|WorkflowVariables|WorkflowStageLocks|BacklogStates|ProjectWorkflowProfiles|IssueWorkflowProfiles|Issues|Projects|...)`)

| From BC | Reads | Writes |
|---|---|---|
| `Project/Grains/ProjectGrain.cs:252, 262-278` | `db.ProjectWorkflowProfiles` | `db.ProjectWorkflowProfiles` (Workflow BC table) |
| `Project/Services/ProjectQuerier.cs:83, 96` | `db.ProjectWorkflowProfiles` | — |
| `Issue/Services/IssueQuerier.cs:188, 193` | `db.WorkflowRuns`, `db.WorkflowLeases` | — |
| `Issue/Services/WorkflowProfiles/MohistDefaultIssueWorkflowProfile.cs:41` | `db.ProjectWorkflowProfiles` | — |
| `Workflow/Services/WorkflowProfileManager.cs:64, 100, 138, 183, 198, 229, 261` | `db.ProjectWorkflowProfiles`, `db.Issues`, `db.IssueWorkflowProfiles` | — |
| `Workflow/Services/IssueWorkflowProfileManager.cs:88, 101, 128, 139, 175, 187, 204, 224` | `db.IssueWorkflowProfiles` | `db.IssueWorkflowProfiles` |
| `Issue/Services/IssueQuerier.cs:319, 345` | `db.Issues`, `db.Epics` | — |

`WorkflowRunRow`, `IssueRow`, `ProjectRow`, `IssueWorkflowProfile`, `ProjectWorkflowProfile`, `EpicRow` are all read by BCs that don't own them. There is no `IWorkflowRunReadModel` or `IProjectProfileReader` between the layers — only the raw `IDbContextFactory<MohistDbContext>` injected into grain + service constructors.

### 1.2 P0 — `ProjectGrain` directly mutates `ProjectWorkflowProfiles`

`packages/server/src/Mohist.Server/Project/Grains/ProjectGrain.cs:7-10` imports `Mohist.Server.Workflow.Domain`, `.Workflow.Grains`, `.Workflow.Services`. Lines 252-278 call `UpsertWorkflowProfileVariablesAsync` which writes to `db.ProjectWorkflowProfiles` — a Workflow-BC table. The `Workflow/Services/ProjectWorkflowProfileManager` is the same responsibility with the same SQL. There are now **two writers** for one table.

**Fix:** Remove `UpsertWorkflowProfileVariablesAsync` from `ProjectGrain` and route project-variable writes through `ProjectWorkflowProfileManager` (the service it already depends on). Promote `ProjectWorkflowProfileManager` to a `IProjectProfileWriter` interface so the dependency direction is one-way (Project → Workflow API).

### 1.3 P1 — `IssueQuerier` reads `db.WorkflowRuns` and `db.WorkflowLeases`

`packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:188, 193` reads Workflow BC tables to project an Issue view. This couples Issue BC to the Workflow schema. If `WorkflowRunRow.State` JSON contract changes, IssueQuerier breaks silently.

**Fix:** Introduce `IWorkflowReadModel` (in Workflow BC) exposing the read API IssueQuerier needs (`GetRunProjectAsync(runId)`, `GetLeaseSummaryAsync(runId)`). Inject the interface into IssueQuerier.

### 1.4 P1 — `WorkflowProfileManager` reads `db.Issues`

`packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileManager.cs:138` reads `db.Issues.AsNoTracking()` directly. Same concern as above — the Workflow BC takes a hard dep on the Issue BC's row schema.

**Fix:** Expose `IIssueReadModel` from Issue BC for the two fields WorkflowProfileManager actually needs.

### 1.5 P2 — `MohistDefaultIssueWorkflowProfile` reads `db.ProjectWorkflowProfiles`

`packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/MohistDefaultIssueWorkflowProfile.cs:41` reads `db.ProjectWorkflowProfiles` from Issue BC. Even though `IssueWorkflowProfileRegistry` already depends on the Workflow managers, this read bypasses the service boundary.

---

## 2. DbContext & Persistence Layer

### 2.1 P0 — `IStateStore<T>` interface is broken in 5 of 8 implementations

The interface (`packages/server/src/Mohist.Server/Infrastructure/Data/IStateStore.cs:3-9`) requires:

```csharp
public interface IStateStore<T> where T : class
{
    Task<T?> LoadAsync(string key);
    Task<IReadOnlyList<T>> ListAsync();
    Task SaveAsync(string key, T state);
    Task DeleteAsync(string key);
}
```

Implementations that throw `NotSupportedException` for `ListAsync` and/or `DeleteAsync`:

| File | `ListAsync` | `DeleteAsync` |
|---|---|---|
| `Infrastructure/Data/Issue/IssueStore.cs:49, 51` | throw | throw |
| `Infrastructure/Data/Issue/IssueCounterStore.cs:38, 40` | throw | throw |
| `Infrastructure/Data/Epic/EpicCounterStore.cs:38, 40` | throw | throw |
| `Infrastructure/Data/Workflow/WorkflowBacklogStore.cs:50` | throw | OK |
| `Infrastructure/Data/Workflow/WorkflowLeaseStore.cs:50` | throw | OK |
| `Infrastructure/Data/Workflow/WorkflowStageLockStore.cs:45` | throw | OK |
| `Infrastructure/Data/Workflow/WorkflowVariablesStore.cs:37, 38` | throw | throw |

`AgentSessionStore` (`Infrastructure/Data/Sessions/AgentSessionStore.cs:10-14`) bypasses the interface entirely: `IAgentSessionStore : IStateStore<AgentSession>` is registered, then 3 overloads of `SaveAsync` are added directly on `IAgentSessionStore` (one with `events`, one with `events + runtimeEvents`). Consumers depend on `IAgentSessionStore`, not `IStateStore<AgentSession>`. The base interface methods are dead code.

**Fix:** Split into two interfaces (or a generic capability flag):

```csharp
public interface IKeyValueStore<T> where T : class
{
    Task<T?> LoadAsync(string key, CancellationToken ct = default);
    Task SaveAsync(string key, T state, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public interface IKeyValueListStore<T> : IKeyValueStore<T> where T : class
{
    Task<IReadOnlyList<T>> ListAsync(CancellationToken ct = default);
}
```

Then have only `AgentSessionStore`, `ProjectQuerier`-style readers implement `IKeyValueListStore<T>`. Drop 10 `throw new NotSupportedException` calls.

### 2.2 P0 — `WorkflowGrain` lease and variables are not in the same transaction as the run

`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:958-974`:

```csharp
private async Task CommitAsync(IReadOnlyList<WorkflowEvent> events, string? reason = null, bool saveVariables = false)
{
    if (_run is not null)
    {
        _runDirty = true;
        await SaveRunAsync(events);   // <-- transaction: run row + event rows
    }

    if (saveVariables)
    {
        await SaveLeaseAsync();       // <-- separate DbContext, no transaction
        await SaveVariablesAsync();
    }
    ...
}
```

`SaveRunAsync` → `WorkflowRunStore.SaveAsync` opens a transaction and commits the run row + event rows together. `SaveLeaseAsync` / `SaveVariablesAsync` are then called **after** the transaction completes, each on its own `DbContext`. A silo crash between these three writes leaves the run in the new state but the lease / variables stale — on next activation the grain loads the new run with a lease that says a previous work item is still dispatched.

**Fix:** Extend `WorkflowRunStore.SaveAsync` to accept the lease + variables and write all three in the same transaction. Or — for fewer contract changes — put the lease/variables into the same `WorkflowRunRow.State` JSON column (they're already serialized to JSON). The current 3-table split is the cause.

### 2.3 P0 — `IWorkflowRunStore.SaveAsync` rollback path is dead code; the using-statement already rolls back

`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:45-60`:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
try
{
    await StageRunAsync(db, run, ct);
    var stagedEvents = await WorkflowEventPersistence.StageAsync(db, run.Id, events, ct);
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    Publish(stagedEvents);
}
catch (DbUpdateConcurrencyException)
{
    await transaction.RollbackAsync(ct);
    throw;
}
```

- Catches only `DbUpdateConcurrencyException`. Any other exception escapes the try block, the `await using` rollback runs anyway — but the explicit `RollbackAsync` is misleading.
- More importantly: `Publish(stagedEvents)` is called **after** `CommitAsync` but **inside the try** (no — it's after CommitAsync, but in the try block). If `Publish` throws (e.g. the bus dispatch throws — which is rare but possible), the transaction has already committed, the bus is in a partial state, and the exception propagates to the caller. There's no re-entrant guard to dedupe. The catch handler then catches `DbUpdateConcurrencyException` only.
- The same pattern is in `AgentSessionStore.cs:48-60` and `AgentSessionStore.cs:65-79`.

**Fix:** Wrap the entire body in a `try / finally`; let the using-statement roll back on exception. Move `Publish` out of the try block to a separate post-commit step, with its own try/catch that logs but does not throw. The current pattern silently swallows the wrong exception type and double-rollbacks on dispose.

### 2.4 P0 — `IssueGrain` event handler mutates grain state off the grain thread

`packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:89-114`:

```csharp
private void OnWorkflowCompleted(CloudEvent evt)
{
    if (_issue is null) return;
    var wrId = TryGetExtension(evt, "workflowrunid");
    if (wrId is null || wrId != _issue.ActiveWorkflowRunId) return;
    if (_issue.Status != Domain.IssueStatus.InProgress) return;
    _ = CompleteWorkAsync(wrId);     // <-- fire-and-forget
}
```

`OnWorkflowCompleted` is a `void` callback (not async). It calls `CompleteWorkAsync(wrId)` and discards the `Task`. `CompleteWorkAsync` is `async Task` and starts executing synchronously up to the first `await` — which is `await _issueStore.SaveAsync(_issue.Id, _issue)` (line 454) — at which point the continuation is scheduled on a thread pool thread.

Orleans grains are single-threaded; mutations to `_issue` are expected to come from a single reentrant-aware call site. Here the mutation happens on the bus dispatcher's thread (which can be the WorkflowGrain's grain thread that emitted the event) and the continuation happens on a thread-pool thread. Two events firing near-simultaneously (e.g. `WorkflowRunCompleted` + a `WorkflowRunFailed` arriving late) can both pass the guard, both call `CompleteWorkAsync` / `AbortWorkAsync`, both mutate `_issue.Complete(...)` or `_issue.AbortWorkflow(...)` (which is idempotent at the domain level), and both call `SaveIssueAsync` from different threads. The `_issue` field is not concurrent-safe.

**Fix:** The handler should be a `Task` delegate (`Func<CloudEvent, Task>`); the bus should `await` it; or it should enqueue the work back into the grain via `this.AsReference<IIssueGrain>(...).EnqueueOnGrainAsync(...)` so all state mutations happen on the grain's activation thread. See design/event-mechanism.md §Failure Modes — currently the design says fire-and-forget is OK because IssueGrain's single-thread model is preserved, but the implementation does not actually re-enter the grain.

### 2.5 P1 — `WorkflowEventPersistence` computes event IDs via `MAX(Id)+1` in app code

`packages/server/src/Mohist.Server/Infrastructure/Data/Events/WorkflowEventPersistence.cs:21-24`:

```csharp
var nextId = (await db.Events
    .Where(e => e.Source == source)
    .Select(e => (long?)e.Id)
    .MaxAsync(ct) ?? 0) + 1;
```

This is documented as a workflow-domain P2 (#13) but is also a persistence-layer concern. Two concurrent writers for the same workflow run can both compute the same `nextId` and one insert will fail with a primary-key violation, or — if both pass through the SQLite write lock — the second one overwrites. Currently the only writer per run is the WorkflowGrain (single-threaded), but the architecture has no guard preventing other writers (e.g. an IssueGrain event for the same run, or a future API that appends directly).

**Fix:** Add a sequence or auto-increment to the `Events` table; or push ID allocation into the domain event constructor and let EF assign it.

### 2.6 P1 — `IStateStore<T>.LoadAsync` / `SaveAsync` / `DeleteAsync` / `ListAsync` don't accept `CancellationToken`

`packages/server/src/Mohist.Server/Infrastructure/Data/Issue/IssueStore.cs:26, 33` and all the other `IStateStore<T>` implementations. `WorkflowGrain.OnActivateAsync(ct)` (line 59) receives a CT, calls `_leaseStore.LoadAsync(GrainKey)` (line 62) — the CT is dropped. The grain deactivation CT is similarly dropped.

**Fix:** Add `CancellationToken ct = default` to all four `IStateStore<T>` methods. ~20 lines of change.

### 2.7 P1 — `IssueCounterStore` has no concurrency control

`packages/server/src/Mohist.Server/Infrastructure/Data/Issue/IssueCounterStore.cs:23-36` does `FindAsync` then `SaveChangesAsync` with no ETag / rowversion. The Orleans grain `IssueCounterGrain` (line 24-29) is single-threaded, so within a silo the counter is safe, but:

- The DB has no protection against another process (e.g. a future migration script, or a second silo) writing to `IssueCounters`.
- The `Next` column has no ETag / version. Two writers race.

**Fix:** Add an ETag / rowversion column on `IssueCounterRow` and pass it to `UPDATE WHERE ProjectId=@id AND Next=@old` (EF Core `IsConcurrencyToken`).

### 2.8 P1 — `ETag` optimistic-lock pattern is hand-rolled and non-portable

`packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs:196` declares `entity.Property<long>("ETag").IsConcurrencyToken()`. `WorkflowRunStore.cs:71, 77` manually maintains `OriginalValue + 1` on every save. The pattern works for SQLite but:

- The `MetadataProjectId` computed-column on `WorkflowRunRow` (`MohistDbContext.cs:197-199`) uses `json_extract(State, '$.Metadata.Annotations.projectId')` — SQLite syntax. AGENTS.md says the future target is PostgreSQL; this won't work there.
- The ETag is incremented on every save (`WorkflowRunStore.cs:77`), including when state hasn't logically changed. Doubles write traffic.
- The "ETag" is misnamed — it's a per-save bump, not a "version" in the HTTP sense. The test in `tests/.../WorkflowRunStoreSpecs.cs` documents it as "versioned audit trail" (workflow-domain P2 #17), which contradicts the concurrency-token claim.

**Fix:** Either (a) use the standard EF Core `IsRowVersion()` pattern (which auto-increments on UPDATE), or (b) drop the ETag column and rely on grain single-thread + `_runDirty` for concurrency (the grain model already enforces it). Add a real `ProjectId` column on `WorkflowRunRow` to remove the JSON-extract hack.

### 2.9 P1 — `WorkflowRunRow` has no real `ProjectId` column

`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunRow.cs:6-17` only has `WorkflowRunId`, `State`, `MetadataProjectId` (computed). The migration creates an index on the computed column (`Migrations/20260605025642_InitialSchema.cs:457-458`). The DbContext has `entity.HasIndex(e => e.MetadataProjectId)` (`MohistDbContext.cs:199`).

Issues:
- The `ProjectId` is duplicated in two places: the source-of-truth `IssueRow.State` JSON (Issue-side) and the `WorkflowRunRow.State.Metadata.Annotations.projectId` JSON (Workflow-side). If they drift, the index points at a stale value.
- SQLite can index a computed column; PostgreSQL needs a real column.
- Large-scale queries by project require this index, but the index re-computes from the JSON every insert (storage cost).

**Fix:** Add a real `ProjectId` column on `WorkflowRunRow` (and on `WorkflowLeaseRow` / `WorkflowVariablesRow` — same pattern). Populate it from the Issue's `ProjectId` at `StartAsync` time. Drop the computed-column.

### 2.10 P1 — `IEventBus` is Singleton; `IssueGrain` subscribes in `OnActivateAsync` — re-subscribe race

`packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs:90`: `services.AddSingleton<IEventBus, InMemoryEventBus>();`. `IssueGrain.OnActivateAsync` adds subscriptions; `OnDeactivateAsync` disposes them. This is balanced **per activation** but the bus is a process-wide dictionary. If a grain is deactivated, then reactivated on a different thread before `OnDeactivateAsync` runs, `OnActivateAsync` adds a second subscription, and a single emit fires the handler twice.

**Fix:** Subscribe in `OnActivateAsync` is fine if you also keep an in-grain "subscribed" flag. Or move subscriptions to a `Lazy<IDisposable>` field initialized in the constructor (Orleans guarantees a single instance per activation). Or — most robust — make the subscriptions per-grain-instance via a `OnTypeScoped` overload that cleans up automatically when the grain is GC'd.

### 2.11 P1 — `IStateStore<T>` is registered as `Scoped` but consumed by Singleton grains

`MohistServiceRegistration.cs:58-67` registers all 8 `IStateStore<T>` implementations as `Scoped`. The grain classes (e.g. `WorkflowGrain`, `IssueGrain`, `IssueCounterGrain`) are Singletons in Orleans' DI. Orleans emits "Scoped service from singleton" warnings and creates a new scope per call (grain method call resolves its services in a per-call scope). This works in practice but:
- It's fragile — any new developer adding a Scoped dependency that holds state will be surprised.
- `WorkflowLeaseStore` and similar are stateless wrappers around `IDbContextFactory<>`; they should be Singleton (or Transient).

**Fix:** Register the stores as Singleton (they hold no per-request state). Or — for explicitness — register as Transient. Both fix the scoped-from-singleton warning.

### 2.12 P2 — `AgentSessionStore` SaveAsync overloads don't line up with `IStateStore<AgentSession>`

`packages/server/src/Mohist.Server/Infrastructure/Data/Sessions/AgentSessionStore.cs:10-14`:

```csharp
public interface IAgentSessionStore : IStateStore<AgentSession>
{
    Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, CancellationToken ct = default);
    Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, IReadOnlyList<AgentSessionRuntimeEventRow> runtimeEvents, CancellationToken ct = default);
}
```

The base `IStateStore<AgentSession>.SaveAsync(string, AgentSession)` is inherited but only ever invoked with 1 argument (line 47 of `AgentSessionStore.cs`). The 2-arg overload is dead code. The 3-arg and 4-arg overloads don't compile against the base interface (no default parameter is in the interface contract) — they only work when the consumer holds `IAgentSessionStore`.

**Fix:** Move the 2-arg `SaveAsync` to `IAgentSessionStore` only (drop from `IStateStore<>`). Or, as part of the §2.1 refactor, give `IAgentSessionStore` a single `SaveAsync` method that takes both events lists (both are always written together).

### 2.13 P2 — `BeginTransactionAsync` is used without an `ExecutionStrategy`

`WorkflowRunStore.cs:46` and `AgentSessionStore.cs:49, 66` call `db.Database.BeginTransactionAsync(ct)` directly. `UseSqlite` is configured without `EnableRetryOnFailure`, so no `IExecutionStrategy` is registered, which is correct for SQLite (you can't retry transactions). However, the `AddDbContextFactory` call (`MohistServiceRegistration.cs:55-56`) does not configure an execution strategy. If a future move to PostgreSQL or SQL Server is made (AGENTS.md's stated goal), this code will start throwing at runtime. There is no comment marking these sites as SQLite-only.

**Fix:** Add a `// SQLite-only: direct transaction; needs ExecutionStrategy wrapping for Npgsql/SqlServer` comment, or wrap the calls in `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`.

### 2.14 P2 — `MohistDbContext` exposes 20 DbSets; no Bounded-Context enforcement

`packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs:26-45` — single `DbContext` with 20 `DbSet<>` properties covering Workflow, Issue, Project, Epic, AgentSession, Events. AGENTS.md says "Bounded Contexts as namespace directories" — but everything is in one DbContext, and any service can query any `DbSet`. The `ArchUnitNET` rules in `tests/Mohist.Server.Tests/Architecture/` should be checked for cross-namespace DbSet usage enforcement.

**Fix:** (a) Add ArchUnitNET rules that forbid non-owning BCs from accessing cross-BC DbSets. (b) Plan for a per-BC DbContext split when the platform leaves single-silo (note: this is a non-trivial change because the `IDbContextFactory<MohistDbContext>` is registered in 25+ places).

### 2.15 P2 — `BeginTransactionAsync` happens in `AgentSessionStore.SaveAsync` for an event-only insert

`AgentSessionStore.cs:65-79` opens a transaction just to add a range of `AgentSessionRuntimeEventRow` and update the session state. If the runtime events are immutable audit trail (they should be — they're the agent transcript), a transaction is overkill. A `SaveChanges` would be enough, and the concurrency check on the `AgentSession` row is what matters. The transaction here is also double-counting with the `SaveChangesAsync` rollback on dispose.

**Fix:** Drop the explicit transaction. EF Core's `SaveChangesAsync` is already atomic per-row.

### 2.16 P2 — `IssueCounterState` has no test for concurrent increment

Related to §2.7. There is no spec test for two `IIssueCounterGrain.NextAsync` calls happening "concurrently" (although the grain model prevents this). The migration also doesn't include an `UpdatedAt` or version column.

### 2.17 P3 — `WorkflowGrain` reloads lease + variables separately on every activation

`WorkflowGrain.OnActivateAsync` (line 59-67) does three sequential `LoadAsync` calls. On a fresh activation after silo restart, all three rows must be present and consistent. There is no test for "what if the lease row exists but the run row doesn't" (orphan lease).

**Fix:** Add a consistency check in `OnActivateAsync`: if `_lease` exists and `_run` is null, log and discard the lease. Add a test.

### 2.18 P3 — `OnDeactivateAsync` swallows disposal exceptions

`packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:79-87`:

```csharp
public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
{
    foreach (var sub in _subscriptions)
    {
        try { sub.Dispose(); } catch { /* swallow */ }
    }
    _subscriptions.Clear();
    return Task.CompletedTask;
}
```

The catch-all is documented as "best effort" but hides real bugs (e.g. an event-bus implementation that fails to remove from its dictionary).

**Fix:** Log the swallowed exception at Warning level. Don't crash the silo, but make the failure visible.

---

## 3. Orleans Silo & Grain State

### 3.1 P1 — `MohistSiloRegistration` only supports single-silo localhost

`packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistSiloRegistration.cs:5-20`:

```csharp
public static ISiloBuilder ConfigureMohistSilo(this ISiloBuilder silo, IConfiguration configuration)
{
    silo.UseLocalhostClustering();
    silo.UseAdoNetReminderService(options => { ... });
    silo.ConfigureLogging(logging => { logging.AddConsole(); });
    return silo;
}
```

No `UseAdoNetClustering` — silo membership is in-memory and dies with the process. No `AddAdoNetGrainStorage` — even though no grain uses `IPersistentState<T>`, the platform needs it for distributed state. AGENTS.md says the goal of moving to Orleans is "distributed capability, reliability, and maintainability" — the current silo config provides none of that.

The IStateStore<T> abstraction is designed to be Orleans-agnostic (it could back onto any storage), but the actual implementation is a per-key SQLite row. Two silos → two SQLite files → inconsistent state.

**Fix:** Decide: either (a) commit to single-silo + SQLite, document it, and add a startup check that prevents running a second instance; or (b) wire up `UseAdoNetClustering` + `AddAdoNetGrainStorage` (or PostgreSQL) and migrate the IStateStore<T> to use `IPersistentState<T>` where appropriate.

### 3.2 P1 — `IPersistentState<T>` vs `IStateStore<T>` inconsistency

`WorkflowGrain` and `IssueGrain` use `IStateStore<T>`. `ProjectGrain` and `EpicGrain` use raw `IDbContextFactory<>` (no abstraction at all). `RunnerGrain` keeps everything in-memory and stores nothing (its state is rebuilt from `RegisterAsync` calls). `AgentSessionGrain` uses `IAgentSessionStore` (its own extended interface). There is no convention. A new developer adding a grain has no clear answer for "how do I store my state?"

**Fix:** Document and enforce one of: (a) `IPersistentState<T>` with `[StorageProvider(...)]` for state that should be in a real grain storage; (b) `IStateStore<T>` for state that needs custom logic (outbox, computed columns, etc.); (c) raw `IDbContextFactory<>` only for read-only grains. The current mix of all three for no clear reason is the bigger sin.

### 3.3 P1 — `WorkflowGrain.OnDeactivateAsync` drops in-flight events

`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:69-84`:

```csharp
public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
{
    if (!_runDirty || _run is null) return;
    try
    {
        await _runStore.SaveAsync(_run, ct);
        _runDirty = false;
    }
    catch (Exception ex)
    {
        _log.LogError(ex, "Workflow {Id} flush on deactivation failed; in-memory mutations will be lost until next activation reloads state", GrainKey);
    }
}
```

This calls `SaveAsync(WorkflowRun, CancellationToken)` — the **1-arg** overload (`WorkflowRunStore.cs:36-41`). It saves the run row only, **not the events list**. The events are stored in the `_run` object's internal collection (e.g. `_run.PendingEvents`, `_run.CompletedEventLog`), and are NOT serialized into the run JSON (only the state is). So events generated since the last `CommitAsync` are dropped on deactivation.

This is also called out as workflow-domain P1 #5, but the architectural concern is that grain state and outbox events have **separate lifecycles** in the same grain, with the wrong save path taken on deactivation.

**Fix:** Deactivation must call the 2-arg `SaveAsync(_run, events)` overload — collect pending events from the same source `CommitAsync` uses, then write both. Or, better: keep the pending events list as a field and flush it on deactivation.

### 3.4 P1 — `IssueGrain.OnActivateAsync` race with `OnDeactivateAsync`

`packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:63-87`. See §2.10. The `OnActivateAsync` subscribes; the bus is shared. If the silo re-activates the grain on a different thread before `OnDeactivateAsync` runs, the same handler is subscribed twice. The `_subscriptions.Clear()` on deactivate prevents the re-subscribe from seeing the old tokens, but the bus-internal list still has the old handler reference for the duration of the dispatch.

**Fix:** Use a single combined `IDisposable` field that's lazily assigned. Compare-and-swap in `OnActivateAsync` to avoid double-subscribe.

### 3.5 P2 — `WorkflowGrain.ReceiveReminder` swallows errors

`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:86-93`:

```csharp
public async Task ReceiveReminder(string reminderName, TickStatus status)
{
    if (!string.Equals(reminderName, WorkHeartbeatReminderName, StringComparison.Ordinal))
        return;
    await CheckLeaseAgeAsync();
    await EnsureWorkHeartbeatAsync();
}
```

No try/catch. If `CheckLeaseAgeAsync` throws (e.g. SQLite is locked, or the bus throws), the reminder is lost and `EnsureWorkHeartbeatAsync` is not called. Orleans will re-fire the reminder on its next tick, but the failure is silent.

**Fix:** Wrap the body in try/catch and log at Warning. Don't crash the silo.

### 3.6 P2 — `WorkflowGrain.RegisterOrUpdateReminder` runs on every CommitAsync

`WorkflowGrain.cs:322-335` is called from `OnWorkflowStartedAsync`, `OnWorkflowResumedAsync`, `OnWorkflowApprovedAsync`, `EnsureWorkHeartbeatAsync` — i.e. from every event handler that signals "the workflow should be making progress". The reminder is registered or updated each time. Orleans' `RegisterOrUpdateReminder` is cheap but not free; the round trip is per-recommit.

**Fix:** Only register once per status change (e.g. on `Running → Paused`, `Paused → Running`). Or, given the heartbeat is already 1-minute period, accept the cost and document it.

### 3.7 P3 — `WorkflowGrain` heartbeat reminder uses `5s due` + `60s period` but lease timeout is `5m`

`WorkflowGrain.cs:20-22, 95`. The first reminder fires at 5 seconds (to start the heartbeat loop), subsequent reminders at 60s. The lease is considered expired after 5 minutes. Worst case: a lease becomes stuck 4m59s after the last check and is detected up to 60s later → 5m59s total. Acceptable but a P2.

---

## 4. Configuration

### 4.1 P0 — Duplicate `StripJsoncComments` implementations

Two files implement the same function:

- `packages/server/src/Mohist.Server/Infrastructure/Config/ConfigService.cs:358-407`
- `packages/server/src/Mohist.Server/Infrastructure/Config/MohistConfigurationExtensions.cs:33-85`

Both strip `/* */` and `//` comments from a JSONC file, hand-written, slightly different styles. If the JSONC grammar is extended (e.g. trailing comma support, JSONL support), one is updated and the other isn't.

**Fix:** Move `StripJsoncComments` to a single static class in `Infrastructure/Config/`. Have both `ConfigService` and `MohistConfigurationExtensions` call it.

### 4.2 P0 — `AddMohistConfigFile` doesn't reload on change despite the parameter

`packages/server/src/Mohist.Server/Infrastructure/Config/MohistConfigurationExtensions.cs:8-28`:

```csharp
public static IConfigurationBuilder AddMohistConfigFile(
    this IConfigurationBuilder builder,
    string? path = null,
    bool optional = true,
    bool reloadOnChange = true)
{
    ...
    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(cleaned));
    return builder.AddJsonStream(stream);
}
```

`AddJsonStream` does not watch the file. `reloadOnChange: true` is read but ignored. Changes to `~/.mohist/config.jsonc` require a process restart to take effect. The runtime `ConfigService` reads the file on every operation (`ConfigService.cs:196-215, 217-262`), but the IConfiguration tree is loaded once at startup.

**Fix:** Use `builder.AddJsonFile(path, optional, reloadOnChange)` instead of `AddJsonStream`. Watch the file with `FileSystemWatcher`. The runtime ConfigService then becomes a thin view over `IConfiguration`.

### 4.3 P1 — `ConfigService` schema is hardcoded; `SetAsync` accepts any value once the key exists

`packages/server/src/Mohist.Server/Infrastructure/Config/ConfigService.cs:14-28` declares 12 known keys. `SetAsync` (line 73-88) checks `_schema.ContainsKey(key)` (so unknown keys are rejected), but if the key exists, `value` is serialized as-is. `Validate` (line 179-191) is defined but is not called from `SetAsync` — only used by the `/api/config` validation endpoint.

**Fix:** Call `Validate` from `SetAsync`. Reject invalid values at write time, not just at read time.

### 4.4 P1 — `ConfigService` writes the file with no atomicity

`ConfigService.cs:217-262` reads, mutates JSON in memory, writes. Two concurrent `SetAsync("model", ...)` calls can read the same file, both compute their mutation on top, and the second write wins. No file lock, no `FileShare.None`, no read-modify-write transactional semantics.

**Fix:** Take a `SemaphoreSlim` (process-wide) around read-modify-write. Or use `Rename` semantics: write to a `.tmp` file and rename. Or have one writer service that serializes the writes.

### 4.5 P1 — Direct `Environment.GetEnvironmentVariable` / `Environment.GetFolderPath` despite `EnvironmentAbstractions.BannedApiAnalyzer`

`Mohist.Server.csproj:17, 22` references `EnvironmentAbstractions` and `EnvironmentAbstractions.BannedApiAnalyzer`. Despite the analyzer, 10 files still use `Environment.GetEnvironmentVariable` or `Environment.GetFolderPath` directly:

- `Infrastructure/Config/ConfigService.cs:36`
- `Infrastructure/Config/MohistConfigurationExtensions.cs:15`
- `Infrastructure/Hosting/MohistServiceRegistration.cs:142`
- `Infrastructure/Workspace/MohistWorkspaceLayout.cs:23`
- `SystemInfo/SystemInfoService.cs:156`
- `SystemInfo/SystemdInstallDetector.cs:109`
- `SystemInfo/SystemUpdateService.cs:212`
- `Api/FsRoutes.cs:14, 43`
- `Api/LogsRoutes.cs:15`
- `Api/StatusRoutes.cs:88, 98, 104`

The analyzer is supposed to ban these but doesn't. Either the rule isn't shipped with the analyzer, the analyzer is suppressed, or the rule is too narrow.

**Fix:** Verify the analyzer's rule is being applied (`<NoWarn>` not set). Replace direct calls with `IEnvironmentVariableProvider` / `IPathProvider` (if it exists) or a new `IHomeDirectoryProvider`. The current state is "we have the abstraction but don't use it."

### 4.6 P1 — Direct `File.ReadAllText` / `File.WriteAllText` despite `IFileSystem` abstraction

Same pattern as §4.5. `IFileSystem` is registered (`MohistServiceRegistration.cs:98`) and `PhysicalFileSystem` exists, but ~15 places still call `File.*` directly:

- `Api/LogsRoutes.cs:22, 25`
- `Api/StatusRoutes.cs:96, 98, 104`
- `Issue/Services/WorkflowProfiles/MohistWorkflow.cs:18, 20`
- `Workflow/Services/Prompts/FilePromptLoader.cs:110`
- `Infrastructure/Config/ConfigService.cs:198, 203, 224, 226, 261`
- `Infrastructure/Config/MohistConfigurationExtensions.cs:19, 23`
- `Infrastructure/Hosting/MohistWebRegistration.cs:42, 46, 53`

**Fix:** Use `IFileSystem` everywhere. Add an `IFileSystemAsync` for the async read/write paths.

### 4.7 P2 — `IConfiguration` key shapes drift between code paths

`ConfigService` reads keys as `Mohist:Config:<key>` (line 333) and writes them as `Mohist.Config.<key>` (line 245 path array). The startup code reads `Mohist:Host`, `Mohist:Port`, `Mohist:DbPath`, `Mohist:ServerUrl`, `Mohist:RunnerRoot`, `Mohist:SqliteConnectionString` (`MohistServiceRegistration.cs:108, 132, 136, 159` and `Program.cs:14-15`). The `Mohist:Config:*` and `Mohist:*` namespaces are separate trees — keys set via `ConfigService.SetAsync` are NOT visible to `IConfiguration["Mohist:Host"]`.

This means runtime config (e.g. "agent.timeout" set by the user via the Web UI) goes to a different tree from environment variables / appsettings.json. A `Mohist__Config__agentTimeout` env var is read; a `Mohist__agentTimeout` env var is ignored. Confusing.

**Fix:** Document the two trees. Either (a) merge them, or (b) have `ConfigService` re-bind to `IConfiguration` on every read, or (c) have one tree only.

### 4.8 P2 — `ConfigService.SetAgentModelAsync` clears `model` but the "agent" tree has it

`ConfigService.cs:162-177` clears the `model` key after setting `agent.model`. The legacy "model" key is therefore never used by anything that reads from `Mohist:Config:model`. The clear is a guard against the old field shadowing the new one, but it's only effective if something else reads the old field — and nothing in this codebase does. Dead code.

**Fix:** Drop the legacy `model` field altogether. Document the migration path in the API response.

---

## 5. DI & Hosting

### 5.1 P0 — `IHostedService.StopAsync` is empty in 3 of 4 hosted services

`packages/server/src/Mohist.Server/Events/Hub/EventBridge.cs:39`, `Sessions/Services/AgentSessionRunnerBridge.cs:55`, `Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:46` all implement `StopAsync` as `Task.CompletedTask`. ASP.NET Core 8+ uses the `StopAsync` to drain in-flight work. With an empty implementation, subscriptions are disposed via `Dispose()` (which all three also implement), but any in-flight `async void` handler keeps running on the thread pool after `StopAsync` returns. The host reports "stopped" but the handlers are still running. If a handler reads from a now-disposed singleton (e.g. `IDbContextFactory<>` is fine, but `IHubContext` is request-scoped), it crashes.

**Fix:** `StopAsync` should:
1. Dispose the bus subscriptions (synchronously block on a `SemaphoreSlim` until any in-flight handler completes).
2. Log the number of in-flight handlers.
3. Return.

For the `async void` handlers (see §5.3), the better fix is to make them `async Task` and route them through a `Channel<T>` consumed by a dedicated worker.

### 5.2 P1 — `IEventBus` emits synchronously on the emitter's thread

`packages/server/src/Mohist.Server/Infrastructure/Events/InMemoryEventBus.cs:130-148`. `DispatchTyped` iterates handlers and calls each synchronously. A handler that does 50ms of work blocks the emitter for 50ms. WorkflowGrain.CommitAsync is the emitter; if a handler does DB work synchronously, the grain is blocked for that duration. This is called out as event-bus P0 R2 but is an architectural concern about the bus being in-process and synchronous.

**Fix:** Either (a) document the bus as "handlers must be fast and side-effect only" and refactor the slow handlers (e.g. IssueGrain handler) to enqueue work back to the grain, or (b) make `Emit` truly fire-and-forget (`EmitAsync` that doesn't await handlers).

### 5.3 P1 — `async void` event handlers

`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionRunnerBridge.cs:63` and `Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:54`:

```csharp
private async void OnRunnerDisconnected(CloudEvent evt) { ... }
private async void OnWorkflowCompleted(CloudEvent evt) { ... }
```

`async void` is dangerous — an unhandled exception in an `async void` method crashes the process (it propagates to `SynchronizationContext` and then to the thread pool's unhandled exception handler). The try/catch inside both handlers **is** the only protection. If a future change moves an awaited call outside the try/catch, the process crashes silently.

**Fix:** Change to `async Task`; have the bus accept `Func<CloudEvent, Task>` handlers; have `Emit` await them (or fire-and-forget via `EmitAsync` that returns immediately and tracks tasks).

### 5.4 P1 — `MohistSiloRegistration` does not set grain class map

`MohistSiloRegistration.cs:5-20` configures clustering, reminders, and logging. It does not call `silo.ConfigureApplicationParts(...)` or any explicit grain discovery. Orleans will scan the assembly and find the grains. This works but is implicit — a grain class in a different assembly won't be picked up. There's no `Configure<MohistGrainTypeMapOptions>` or equivalent.

**Fix:** Add an explicit grain types list. Or document the auto-discovery.

### 5.5 P2 — `IHubContext<MohistHub, IEventsClient>` is request-scoped indirectly

`EventBridge` is a Singleton; it injects `IHubContext<MohistHub, IEventsClient>`. `IHubContext` is a transient resolution from `IServiceProvider` per hub. The hub itself is request-scoped (per SignalR connection). `IHubContext` is fine to inject as a singleton reference. But `EventBridge.ForwardToHub` does `_ = _hub.Clients.Group(...).OnEvent(...)` — the `_ = ` discards the Task. If the SignalR client group is mid-reconnect, the Task can fail and be unobserved.

**Fix:** `await _hub.Clients.Group(group).OnEvent(...)` in a try/catch that logs.

### 5.6 P2 — `RunnerConnectionTracker` is a Singleton with a process-wide dictionary

`packages/server/src/Mohist.Server/Runner/Services/SignalR/RunnerConnectionTracker.cs`. If two silos run, the tracker on silo A doesn't know about connections to silo B. When a runner reconnects to silo B and the SignalR connection drops on silo A, the bus emits `RunnerDisconnected` for a connection that may still be alive on B. The AgentSessionRunnerBridge will mark those sessions failed on both silos.

**Fix:** Use a distributed dictionary (Redis) or a bus-broadcast of connection events so all silos see all disconnects.

---

## 6. Error Handling

### 6.1 P0 — `ExceptionMiddleware` only handles two exception types; everything else becomes a 500

`packages/server/src/Mohist.Server/Api/ExceptionMiddleware.cs:9-25`:

```csharp
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (WorkflowDomainException ex) { /* 409 */ }
    catch (InvalidOperationException ex) { /* 404 */ }
});
```

Any other exception (e.g. `DbUpdateConcurrencyException` from a grain call, `JsonException` from a malformed body, `ArgumentException` from a query parameter, `OperationCanceledException` from a client disconnect) propagates to ASP.NET Core's default 500 handler with no logging, no `ProblemDetails`, and an opaque empty body to the client. The existing `ExceptionMiddleware` is mounted **before** the route group filters, so it's the right place — but it doesn't log.

**Fix:** Add a generic `catch (Exception ex) { _log.LogError(ex, "Unhandled API exception"); return ApiResults.Fail(...) }`. Also catch `OperationCanceledException` explicitly and return 499 / 408.

### 6.2 P0 — `IssueGrain.StartWorkAsync` throws `InvalidOperationException` for everything

`packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:150-198` throws `InvalidOperationException` for: missing project, missing repository, missing prompts, eligibility failure, etc. `ExceptionMiddleware` maps `InvalidOperationException` to 404 (`ExceptionMiddleware.cs:22`). So a "no default repository" 404 is misleading; a "missing prompts" should be 400. The catch-all on the API route (`IssueRoutes.Lifecycle.cs:40-43`) catches `InvalidOperationException` and returns 409 — different from the middleware.

**Fix:** Define a typed exception hierarchy (`IssueNotFoundException`, `IssueConflictException`, `IssueValidationException`). Map each in the middleware. Stop using `InvalidOperationException` for control flow.

### 6.3 P1 — No `ProblemDetails` — custom `ApiResponse<T>` envelope

`packages/server/src/Mohist.Server/Api/ApiResponse.cs:1-19`. The response is `ApiResponse<T> { Success, Data, Error, Code, Details }`. This is not `application/problem+json`. Clients that expect RFC 7807 (`Microsoft.AspNetCore.Mvc.ProblemDetails`) won't find the standard fields (`type`, `title`, `status`, `detail`, `instance`).

**Fix:** Either (a) accept the custom envelope and document it as the project's contract; (b) add `services.AddProblemDetails()` and convert `ApiResponse` failures to `ProblemDetails` for failure responses; (c) add `type`, `title`, `status`, `detail` fields to `ApiResponse<T>` and emit `application/problem+json` for failures.

### 6.4 P1 — API routes don't take `CancellationToken`

`packages/server/src/Mohist.Server/Api/IssueRoutes.Lifecycle.cs`, `IssueRoutes.WorkflowControl.cs`, etc. — none of the route handlers accept `CancellationToken`. When a client disconnects mid-request, the handler keeps running. The grain calls continue, the DB queries continue, the bus emits continue.

**Fix:** Add `CancellationToken ct` to every route handler. Pass to grain calls and DB queries. Configure `RequestAborted` as the default.

### 6.5 P1 — `EnsureIssue` throws on every IssueGrain method

`IssueGrain.cs:457-461`. `EnsureIssue()` throws `InvalidOperationException` if `_issue` is null. This is mapped to 404 in the middleware but the grain code path is "user accessed an issue that doesn't exist" — the message includes the grain key, leaking the internal ID. Logging at `LogWarning` (or `LogInformation` for expected 404s) is not done.

**Fix:** Have the grain return a typed `Result<>` or just a `bool` and let the route decide the status code. Don't leak the grain key in the error message.

### 6.6 P2 — `try { sub.Dispose(); } catch { }` in IssueGrain

`IssueGrain.cs:83-84`. See §2.18.

### 6.7 P2 — `WorkflowGrain.OnDeactivateAsync` swallows all errors

`WorkflowGrain.cs:78-83`. See §3.3. The catch logs the error but the grain is gone, so a subsequent re-activation may reload stale state. The race between `OnDeactivateAsync` and a queued method call is real.

**Fix:** Distinguish "lost the race" (logged, grain re-loads) from "real error" (escalate, halt silo). Add a "stale" detection — compare `_run`'s in-memory `ETag` (if you had one) to the persisted one before deactivation.

---

## 7. Observability

### 7.1 P1 — No OpenTelemetry / metrics / distributed tracing

`grep -r "OpenTelemetry\|Metrics\|prometheus\|ActivitySource" packages/server/src/Mohist.Server --include="*.cs"` returns no matches. The system has structured logs (`ILogger<T>` with named placeholders, no string concatenation) but no metrics, no traces, no spans around grain calls. The Orleans silo emits its own traces via the configured `AddConsole()` logger, but the application's own calls (grain → grain → DB) are not correlated.

**Fix:** Add `services.AddOpenTelemetry().WithTracing(...).WithMetrics(...)` in `MohistServiceRegistration`. Wrap `WorkflowGrain.CommitAsync` and `WorkflowRunStore.SaveAsync` in `Activity` spans. Emit metrics for: workflow start, workflow complete, workflow failed, workflow lease expired, agent session terminal.

### 7.2 P1 — Sensitive data in logs

`IssueGrain.cs:405-411` logs the `issue.Title`, `issue.Body`, `project.Id`, `project.Name`, `issue.Number` into the `BuildVariables` JSON — but only the variable dict, not the log. However, `IssueGrain.CreateAsync` (line 351-359) takes `title` and `body` and stores them; on next log line (e.g. `_log.LogInformation("Issue {Key} started workflow {WrId}", ...)` line 196), only the IDs are logged. Good.

But: `WorkflowGrain.cs:923-955` (`EmitStageChanged`) logs the stage name, action, reason. `WorkflowRunState` is a JSON blob; on save, `_run` is serialized to JSON, which can include user-supplied data (issue body, prompts, file paths). If a logger is configured to capture EF parameters at Information level, those leak.

**Fix:** Audit the JSON-serialized columns. Add a `[JsonIgnore]` or scrubbing filter for `body` and `repository.path` before logging the deserialized object. Don't log `WorkflowRun` directly; log a summary record.

### 7.3 P2 — `AddConsole()` only — no file/structured sink

`MohistSiloRegistration.cs:14-17` adds only `AddConsole()`. `appsettings.json` may add more sinks, but if not, the silo writes to stdout. The `logs/` directory mentioned in `AGENTS.md` is written by `Api/LogsRoutes.cs` (a route that lists log files) — there's no in-process log-to-file sink.

**Fix:** Add a Serilog or NLog configuration that writes to `~/.mohist/logs/`. Keep the console for systemd journal capture.

---

## 8. Cross-BC / Module Structure

### 8.1 P1 — `Mohist.Api` references Bounded-Context internals

`packages/server/src/Mohist.Server/Api/IssueRoutes.WorkflowControl.cs:4-5` imports `Mohist.Server.Workflow.Domain` and `Mohist.Server.Workflow.Grains` to handle a 409 from `WorkflowDomainException` — a domain type that should be in Workflow, not propagated to the API layer. The catch is in the API route; the domain type is from Workflow.

This is an inverted dependency: the API layer is downstream of Workflow (correct), but the API layer catches a Workflow-specific exception type. A new error code from Workflow requires an API change.

**Fix:** Catch generic `ConflictException` (or `DomainException` base) in the API layer; map to status codes via a `IExceptionToStatusCodeMapper`. Workflow-specific types stay in Workflow.

### 8.2 P2 — `Mohist.Api` route files are 1000+ lines

`packages/server/src/Mohist.Server/Api/IssueRoutes.*.cs` totals 1073 lines across 8 files. This is acceptable for ASP.NET Core convention (one `MapGroup` per file) but means cross-cutting concerns (auth, validation, project resolution) are duplicated.

**Fix:** Extract the `try { ... } catch (InvalidOperationException) { return ApiResults.Conflict(...); }` pattern into a `RouteConvention` or `EndpointFilter`. The endpoint filter pipeline is already in use (`AddEndpointFilter<ProjectResolutionEndpointFilter>`).

### 8.3 P3 — `AgentSessionRunnerBridge` is in `Sessions/Services/` but subscribes to `RunnerDisconnected` from `RunnerHub`

`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionRunnerBridge.cs` is in Sessions BC but is the bridge for the Runner's lifecycle. It imports `Mohist.Server.Sessions.Grains` and `Mohist.Server.Infrastructure.Orleans.GrainKey` but not Runner — yet the event source is Runner.

**Fix:** Either rename to `RunnerLifecycleBridge` and place in `Events/` or in a new `Bridges/` directory. Or document why it lives in Sessions.

---

## 9. Migrations

### 9.1 P0 — Single migration (`InitialSchema`) covers everything

`packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260605025642_InitialSchema.cs` is 616 lines and creates 22 tables, 21 indexes, in one shot. Any future schema change will be a delta on top. This is fine for a new project but means:

- If a developer has a database from an earlier commit, they get the full rebuild every time.
- There's no way to bisect which migration introduced a regression.
- `git log` on the migration tells you nothing about the evolution.

**Fix:** Acceptable for now; flag for splitting when the next schema change happens.

### 9.2 P1 — `WorkflowRunRow.ETag` column has no `DEFAULT`

`Migrations/20260605025642_InitialSchema.cs:325`: `ETag = table.Column<long>(type: "INTEGER", nullable: false)`. No default value, but `WorkflowRunStore.StageRunAsync` sets it to 1 on insert. If a row is inserted via raw SQL (e.g. a migration script) without setting ETag, the NOT NULL constraint fires.

**Fix:** Add `defaultValue: 1L` to the migration and to the DbContext column config.

### 9.3 P1 — `WorkflowRunRow.MetadataProjectId` is `[DatabaseGenerated(Computed)]` but only `IsRowVersion`-like

`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunRow.cs:15-16`:

```csharp
[DatabaseGenerated(DatabaseGeneratedOption.Computed)]
public string? MetadataProjectId { get; set; }
```

`DatabaseGeneratedOption.Computed` is EF's hint for "set by the database, not by EF". SQLite supports this for `GENERATED ALWAYS AS` virtual columns. The migration has `computedColumnSql: "json_extract(State, '$.Metadata.Annotations.projectId')", stored: true` (line 324). The DbContext has `entity.Property(e => e.MetadataProjectId).HasComputedColumnSql(...)` (line 198). All consistent.

But this is the **only** place where the JSON-extract pattern is used. The `IssueRow` uses the same pattern (`MohistDbContext.cs:181-186`). It's a pattern, not a coincidence. The architecture treats JSON blobs as the source of truth and projects searchable columns from them.

**Issue:** if the JSON key path changes (e.g. `Annotations.projectId` → `Annotations.ProjectId`), the computed column silently returns NULL and the index becomes empty. No test catches this. No migration script handles the rename.

**Fix:** Add a spec test that creates a WorkflowRun with a JSON key that doesn't match and asserts the index is empty. Document the contract.

### 9.4 P2 — `IssuePrerequisiteRow` has no `UpdatedAt` or audit columns

`Infrastructure/Data/Issue/IssuePrerequisiteRow.cs`. Same for `EpicIssueRow`. Other tables have `CreatedAt` / `UpdatedAt`; these don't. Either the audit columns are intentionally missing (the row is the join, not a domain entity) or it's an oversight.

**Fix:** Add `CreatedAt` to the row, set to `DateTimeOffset.UtcNow` on insert.

### 9.5 P3 — Migration files have not been regenerated since initial schema

`packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/` has only `20260605025642_InitialSchema` and its `Designer.cs` and `ModelSnapshot.cs`. If any column or index was added to a row class after the migration was generated, the model snapshot is stale and `dotnet ef migrations add` will produce a "remove all and re-add" migration. This is checked into git, so the divergence is real and visible.

**Fix:** Audit each `*Row.cs` against the migration. If they diverge, regenerate.

---

## 10. CT & Async Hygiene

### 10.1 P1 — `IssueWorkflowReconciliationService` uses `Task.Delay` instead of `PeriodicTimer`

`packages/server/src/Mohist.Server/Issue/Services/IssueWorkflowReconciliationService.cs:40-67`. The classic `Task.Delay + while loop` pattern. If the work takes longer than `ReconciliationPeriod` (1 day), the next run starts immediately. A drift-correction issue.

**Fix:** Use `PeriodicTimer` (.NET 6+).

### 10.2 P1 — `IssueWorkflowReconciliationService` only reconciles 500 issues per run

`IssueWorkflowReconciliationService.cs:81-85`. `.Take(500)`. With a 1-day period, the long tail of stuck issues never gets reconciled. If 1000 issues get stuck at once, the second half waits 2 days.

**Fix:** Loop until 0 results. Or scale `Take` with the size of the working set.

### 10.3 P2 — `IEventBus.Emit` doesn't take a CT

`InMemoryEventBus.cs:37-68`. Emit is synchronous and doesn't need a CT, but the **handlers** it dispatches are async (most of them). The bus doesn't pass a CT to the handler, so the handler's `await` can't be canceled.

**Fix:** Pass a `CancellationToken` (from a `CancellationTokenSource` linked to the bus's lifecycle) to each handler.

### 10.4 P2 — `EnsureWorkHeartbeatAsync` doesn't take a CT

`WorkflowGrain.cs:322-335`. Called from `OnActivateAsync(ct)` (line 66) — the CT is dropped. Called from `ReceiveReminder` (line 92) — no CT available.

**Fix:** Add CT to all internal methods that the grain forwards from public ones.

### 10.5 P3 — `WorkflowGrain.OnDeactivateAsync` passes its CT to `_runStore.SaveAsync(_run, ct)` but not to the event-only overload

`WorkflowGrain.cs:75`: `await _runStore.SaveAsync(_run, ct);` — the 1-arg overload. If the 2-arg overload were called (per §3.3), it would take a CT. The 1-arg overload also takes a CT, so this is consistent — but the deactivation path should be calling the 2-arg overload to also flush events.

---

## 11. Summary Table

| # | Severity | Title | File |
|---|----------|-------|------|
| 1 | **P0** | Cross-BC table access: `ProjectGrain` writes `ProjectWorkflowProfiles` directly | `Project/Grains/ProjectGrain.cs:252-278` |
| 2 | **P0** | `IStateStore<T>` is broken: 5 of 8 implementations throw `NotSupportedException` | `Infrastructure/Data/IStateStore.cs:3-9` + impls |
| 3 | **P0** | `WorkflowGrain` lease/variables are saved outside the run+events transaction | `Workflow/Grains/WorkflowGrain.cs:958-974` |
| 4 | **P0** | `IWorkflowRunStore.SaveAsync` rollback is dead code; `Publish` in try-block post-commit | `Infrastructure/Data/Workflow/WorkflowRunStore.cs:45-60` |
| 5 | **P0** | `IssueGrain` event handler mutates grain state off the grain thread | `Issue/Grains/IssueGrain.cs:89-114, 242-254` |
| 6 | **P0** | Duplicate `StripJsoncComments` implementations in Config layer | `Infrastructure/Config/ConfigService.cs:358` + `MohistConfigurationExtensions.cs:33` |
| 7 | **P0** | `AddMohistConfigFile` ignores `reloadOnChange: true`; runtime config edits need restart | `Infrastructure/Config/MohistConfigurationExtensions.cs:8-28` |
| 8 | **P0** | `IHostedService.StopAsync` is empty in 3 of 4 hosted services | `Events/Hub/EventBridge.cs:39`, `Sessions/Services/AgentSessionRunnerBridge.cs:55`, `Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:46` |
| 9 | **P0** | `ExceptionMiddleware` only handles 2 exception types; everything else is opaque 500 | `Api/ExceptionMiddleware.cs:9-25` |
| 10 | **P0** | `IssueGrain.StartWorkAsync` uses `InvalidOperationException` for all failures (4xx vs 5xx confusion) | `Issue/Grains/IssueGrain.cs:150-198` + `Api/ExceptionMiddleware.cs:22` |
| 11 | **P1** | Cross-BC table access: `IssueQuerier` reads `WorkflowRuns` / `WorkflowLeases` | `Issue/Services/IssueQuerier.cs:188, 193` |
| 12 | **P1** | Cross-BC table access: `WorkflowProfileManager` reads `db.Issues` | `Workflow/Services/WorkflowProfileManager.cs:138, 183` |
| 13 | **P1** | Cross-BC table access: `MohistDefaultIssueWorkflowProfile` reads `ProjectWorkflowProfiles` | `Issue/Services/WorkflowProfiles/MohistDefaultIssueWorkflowProfile.cs:41` |
| 14 | **P1** | `WorkflowEventPersistence` computes event IDs via `MAX+1` in app code | `Infrastructure/Data/Events/WorkflowEventPersistence.cs:21-24` |
| 15 | **P1** | `IStateStore<T>.LoadAsync/SaveAsync/DeleteAsync/ListAsync` don't accept `CancellationToken` | `Infrastructure/Data/Issue/IssueStore.cs:26, 33` + 6 other impls |
| 16 | **P1** | `IssueCounterStore` has no concurrency control / ETag | `Infrastructure/Data/Issue/IssueCounterStore.cs:23-36` |
| 17 | **P1** | ETag optimistic-lock pattern is hand-rolled and non-portable; `MetadataProjectId` uses SQLite-only json_extract | `Infrastructure/Data/Db/MohistDbContext.cs:196-199` + `Infrastructure/Data/Workflow/WorkflowRunStore.cs:71, 77` |
| 18 | **P1** | `WorkflowRunRow` has no real `ProjectId` column (relies on computed column) | `Infrastructure/Data/Workflow/WorkflowRunRow.cs:6-17` + `Migrations/20260605025642_InitialSchema.cs:324` |
| 19 | **P1** | `IStateStore<T>` Singleton re-subscribe race in `IssueGrain` | `Issue/Grains/IssueGrain.cs:63-87` + `Infrastructure/Hosting/MohistServiceRegistration.cs:90` |
| 20 | **P1** | `IStateStore<T>` registered Scoped but consumed by Singleton grains (silent from-singleton) | `Infrastructure/Hosting/MohistServiceRegistration.cs:58-67` |
| 21 | **P1** | `MohistSiloRegistration` only supports single-silo localhost; no `UseAdoNetClustering` | `Infrastructure/Hosting/MohistSiloRegistration.cs:5-20` |
| 22 | **P1** | `IPersistentState<T>` vs `IStateStore<T>` vs raw `IDbContextFactory<>` — no convention | `Workflow/Grains/WorkflowGrain.cs`, `Project/Grains/ProjectGrain.cs`, `Epic/Grains/EpicGrain.cs` |
| 23 | **P1** | `WorkflowGrain.OnDeactivateAsync` drops in-flight events (calls 1-arg `SaveAsync`) | `Workflow/Grains/WorkflowGrain.cs:69-84` |
| 24 | **P1** | `ConfigService.SetAsync` doesn't call `Validate`; any value is accepted if the key exists | `Infrastructure/Config/ConfigService.cs:73-88` |
| 25 | **P1** | `ConfigService` read-modify-write has no atomicity (concurrent `SetAsync` can lose updates) | `Infrastructure/Config/ConfigService.cs:217-262` |
| 26 | **P1** | Direct `Environment.GetEnvironmentVariable` / `Environment.GetFolderPath` despite `BannedApiAnalyzer` | 10 files (see §4.5) |
| 27 | **P1** | Direct `File.*` despite `IFileSystem` abstraction | 15 files (see §4.6) |
| 28 | **P1** | `IEventBus.Emit` runs handlers synchronously on the emitter's thread | `Infrastructure/Events/InMemoryEventBus.cs:130-148` |
| 29 | **P1** | `async void` in `WorktreeCleanupService.OnWorkflowCompleted` and `AgentSessionRunnerBridge.OnRunnerDisconnected` | `Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:54` + `Sessions/Services/AgentSessionRunnerBridge.cs:63` |
| 30 | **P1** | No OpenTelemetry / metrics / distributed tracing | (no matches) |
| 31 | **P1** | API routes don't take `CancellationToken`; client disconnect doesn't cancel | `Api/IssueRoutes.*.cs` (all files) |
| 32 | **P1** | Single migration `InitialSchema` for 22 tables + 21 indexes | `Infrastructure/Data/Migrations/20260605025642_InitialSchema.cs` |
| 33 | **P1** | `WorkflowRunRow.ETag` has no `DEFAULT 1` for raw-SQL insert paths | `Migrations/20260605025642_InitialSchema.cs:325` |
| 34 | **P1** | `WorkflowRunRow.MetadataProjectId` is computed-column over JSON; no test catches key-path changes | `Infrastructure/Data/Workflow/WorkflowRunRow.cs:15-16` + `MohistDbContext.cs:198` |
| 35 | **P1** | `IssueWorkflowReconciliationService` uses `Task.Delay` loop, only reconciles 500 issues per run | `Issue/Services/IssueWorkflowReconciliationService.cs:40-85` |
| 36 | **P1** | `Mohist.Api` route handlers catch Workflow domain types — inverted dependency | `Api/IssueRoutes.WorkflowControl.cs:117-120` |
| 37 | **P2** | `AgentSessionStore` SaveAsync overloads don't line up with `IStateStore<AgentSession>` | `Infrastructure/Data/Sessions/AgentSessionStore.cs:10-14` |
| 38 | **P2** | `BeginTransactionAsync` used without `ExecutionStrategy`; not portable to Npgsql/SqlServer | `WorkflowRunStore.cs:46` + `AgentSessionStore.cs:49, 66` |
| 39 | **P2** | `MohistDbContext` exposes 20 DbSets with no BC enforcement | `Infrastructure/Data/Db/MohistDbContext.cs:26-45` |
| 40 | **P2** | `AgentSessionStore.SaveAsync` uses transaction for a single event insert | `Infrastructure/Data/Sessions/AgentSessionStore.cs:65-79` |
| 41 | **P2** | `IssueCounterState` has no test for concurrent increment | (missing test) |
| 42 | **P2** | `ConfigService` schema and `IConfiguration` keys live in different trees (`Mohist:Config:*` vs `Mohist:*`) | `Infrastructure/Config/ConfigService.cs:14-28, 244-258` |
| 43 | **P2** | `ConfigService.SetAgentModelAsync` clears `model` field that's never read | `Infrastructure/Config/ConfigService.cs:162-177` |
| 44 | **P2** | `OnDeactivateAsync` swallows disposal exceptions | `Issue/Grains/IssueGrain.cs:79-87` |
| 45 | **P2** | `WorkflowGrain.ReceiveReminder` swallows errors | `Workflow/Grains/WorkflowGrain.cs:86-93` |
| 46 | **P2** | `WorkflowGrain.RegisterOrUpdateReminder` runs on every CommitAsync | `Workflow/Grains/WorkflowGrain.cs:322-335` |
| 47 | **P2** | No `ProblemDetails` — custom `ApiResponse<T>` envelope | `Api/ApiResponse.cs:1-19` |
| 48 | **P2** | `EnsureIssue` throws on every IssueGrain method; leaks grain key in error | `Issue/Grains/IssueGrain.cs:457-461` |
| 49 | **P2** | `AddConsole()` only — no file/structured log sink | `Infrastructure/Hosting/MohistSiloRegistration.cs:14-17` |
| 50 | **P2** | `IHubContext.OnEvent` fire-and-forget in `EventBridge.ForwardToHub` | `Events/Hub/EventBridge.cs:50-63` |
| 51 | **P2** | `RunnerConnectionTracker` is a Singleton; broken for multi-silo | `Runner/Services/SignalR/RunnerConnectionTracker.cs` |
| 52 | **P2** | `IEventBus.Emit` doesn't propagate CT to async handlers | `Infrastructure/Events/InMemoryEventBus.cs:37-68` |
| 53 | **P2** | `EnsureWorkHeartbeatAsync` doesn't accept CT | `Workflow/Grains/WorkflowGrain.cs:322-335` |
| 54 | **P2** | Sensitive data risk: `WorkflowRunState` JSON is logged if a logger captures EF parameters | `Workflow/Grains/WorkflowGrain.cs:1115-1142` |
| 55 | **P2** | `IssuePrerequisiteRow` / `EpicIssueRow` have no `CreatedAt` audit column | `Infrastructure/Data/Issue/IssuePrerequisiteRow.cs` + `Infrastructure/Data/Epic/EpicIssueRow.cs` |
| 56 | **P2** | `Mohist.Api` route files are 1000+ lines with duplicated try/catch | `Api/IssueRoutes.*.cs` (8 files, 1073 lines) |
| 57 | **P2** | `AgentSessionRunnerBridge` is in `Sessions/Services/` but bridges Runner lifecycle | `Sessions/Services/AgentSessionRunnerBridge.cs` |
| 58 | **P3** | `WorkflowGrain` reloads lease + variables separately on activation; no orphan check | `Workflow/Grains/WorkflowGrain.cs:59-67` |
| 59 | **P3** | `WorkflowGrain` heartbeat reminder is 5s/60s but lease timeout is 5m | `Workflow/Grains/WorkflowGrain.cs:20-22, 95` |
| 60 | **P3** | `WorkflowGrain.OnDeactivateAsync` calls 1-arg `SaveAsync` (drops events) | `Workflow/Grains/WorkflowGrain.cs:75` |
| 61 | **P3** | `MohistSiloRegistration` does not set grain class map; relies on auto-discovery | `Infrastructure/Hosting/MohistSiloRegistration.cs:5-20` |
| 62 | **P3** | Migration files have not been regenerated; risk of stale `ModelSnapshot` | `Infrastructure/Data/Migrations/MohistDbContextModelSnapshot.cs` |
| 63 | **P3** | `MohistConfigurationExtensions.AddMohistConfigFile` reads file twice (once at startup, once on demand) | `Infrastructure/Config/MohistConfigurationExtensions.cs:8-28` |

---

## 12. Recommendations (priority order)

1. **Fix the IStateStore abstraction** (§2.1) — split into `IKeyValueStore<T>` and `IKeyValueListStore<T>`; add `CancellationToken`. This is the single highest-leverage cleanup. ~50 lines of change, eliminates 10 `NotSupportedException` throws, unblocks the CT propagation fix.
2. **Move lease + variables into the same transaction as the run** (§2.2) — extend `WorkflowRunStore.SaveAsync` to take both. Eliminates the silo-crash partial-state risk. ~30 lines.
3. **Tighten BC boundaries** (§1) — add `IWorkflowReadModel`, `IProjectProfileWriter`, `IIssueReadModel` interfaces. Move all cross-BC DbSet access behind them. ~150 lines, but unblocks future BC splits.
4. **Add OpenTelemetry** (§7.1) — one-line `AddOpenTelemetry()` config; wrap `WorkflowGrain.CommitAsync` and `WorkflowRunStore.SaveAsync` in `Activity` spans. Required for any production observability story.
5. **Wire `UseAdoNetClustering` + storage** (§3.1) — or commit to single-silo and document. The current "Orleans" framing in AGENTS.md is aspirational; the silo config is localhost-only.
6. **Fix `IssueGrain` event handler thread-safety** (§2.4) — re-enter the grain via `this.AsReference<IIssueGrain>(...)` or use a typed Task delegate on the bus. ~20 lines.
7. **Add `CancellationToken` to every API route** (§6.4) — ~3 hours of mechanical work; pays off the moment a client disconnects.
8. **Replace custom `ApiResponse<T>` with `ProblemDetails`** (§6.3) — or, at minimum, add `application/problem+json` for failures.
9. **De-duplicate `StripJsoncComments`** (§4.1) — 5-minute change.
10. **Wire `IFileSystem` / `IEnvironmentVariableProvider` everywhere** (§4.5, §4.6) — the abstractions exist; the analyzer should be enforcing them. Find why it isn't, then replace direct calls.
