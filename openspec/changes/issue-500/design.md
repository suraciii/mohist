## Context

The [proposal](proposal.md) identifies three test-driven leaks in the server production surface: grain interfaces expose forced deactivation, four grains override their Orleans identity for tests, and constructor nullability permits dependencies that the normal composition root always supplies to be omitted. The [`server-production-contracts`](specs/server-production-contracts/spec.md) and [`agent-session-persistence-observation`](specs/agent-session-persistence-observation/spec.md) specs require those contracts to be removed without changing production behavior. Separately, AgentSession specs force a deferred write through `FlushForTestAsync` because their only other option is polling storage or waiting for the timer.

`MohistServiceRegistration` already registers workflow profile resolution, event push, background work, AgentJob storage, and the AgentJob dispatch observer. `AgentSessionGrain.FlushAsync` already defines the authoritative persistence ordering and failure behavior: state plus domain events commit first, then transcript data commits; transcript failure retains only transcript data for the next timer attempt. The design preserves that implementation boundary and exposes its actual storage completions only to the test host.

The affected stakeholders are server maintainers and the server unit/spec suites. API, CLI, Runner, persistence schema, and workflow consumers are not changed.

## Goals / Non-Goals

**Goals:**

- Make production grain interfaces, constructors, and identity resolution represent only production behavior.
- Keep service availability and no-op behavior explicit in composition rather than hidden behind nullable constructors.
- Replace forced AgentSession flushes with deterministic observation of real deferred persistence.
- Preserve lifecycle, profile resolution, dispatch, persistence ordering, and retry behavior while migrating the affected test suites.

**Non-Goals:**

- Change AgentSession batching, timer cadence, persistence schema, or error recovery semantics.
- Change any API, CLI, Runner protocol, workflow transition, or grain responsibility.
- Remove `WorkflowGrain.BindProfileForTest`; it remains outside this mechanical-contract cleanup.
- Make caches and non-authoritative diagnostic sinks mandatory when their absence is already a valid production mode.

## Decisions

### 1. Grain lifecycle and identity remain Orleans-owned

Remove `DeactivateForTestAsync` from the six grain contracts and implementations. Add a shared SpecTests helper around `IManagementGrain.ForceActivationCollection(TimeSpan.Zero)` and migrate rehydration assertions to it. The helper keeps the Orleans-wide operation visible and avoids reintroducing per-grain controls.

Remove `GrainKeyForTest` and all fallback key readers from Agent, Epic, Issue, and Workflow grains. Tests that need activation behavior will use an `InProcessTestCluster` and a known grain key; direct tests will keep only domain or collaborator behavior that does not require an Orleans identity.

Alternative considered: retain internal setters or move the methods to test-only interface extensions. Both leave production grain implementations conditional on a test shape and allow direct tests to bypass the authoritative activation path, so they are rejected.

### 2. Required collaborators are non-null at construction

Change `IWorkflowProfileProvider`, `IEventPushQueue`, `IBackgroundTaskLauncher`, `IAgentJobStore`, and `IAgentJobDispatchObserver` constructor parameters and fields to non-nullable wherever their registrations guarantee availability. Remove default parameter values, null coalescing, and branches reachable only when a test omitted a required service. In particular, `WorkflowProfileManager` always resolves a bound profile through its provider and no longer falls back to legacy template resolution because the provider is absent.

`EventDispatcherService` always receives an `IEventPushQueue`. Composition selects `EventPushQueue` for the normal host or explicitly registers `NullEventPushQueue` for a composition that deliberately has no live push worker. Tests that construct affected components provide a real collaborator or a named fake.

Keep only genuinely optional dependencies nullable, such as rule-expression caching and event-match failure reporting. Their declarations will carry a short reason that they are non-authoritative side channels.

Alternative considered: retain nullable parameters and validate them only in production DI. That preserves unreachable fallback branches and lets direct tests assert behavior no shipped composition can exhibit, so it is rejected.

### 3. Test-only persistence probes decorate storage adapters

Add a SpecTests support probe that decorates `IAgentSessionStore` and `IAgentSessionTranscriptStore` in test host registrations. Each decorator delegates to the existing store, then records the completed or failed write with the session identity and a monotonic operation sequence. The probe exposes checkpoints and awaitable completion for a session, so a test subscribes at a known checkpoint, triggers the command, and awaits the required state/event and/or transcript write boundary without polling the database.

The helper replaces `FlushForTestAsync` calls with the narrowest observation required by the assertion. It uses `TaskCompletionSource` with asynchronous continuations and recorded outcomes, so a write that completed before the wait is still observable and a failed write is reported rather than mistaken for a successful flush. Fixtures using fake stores receive the same decorators; database-backed specs register them around the existing scoped stores.

The production service graph does not register the probe or decorators. `AgentSessionGrain`, its public interface, timer, and `FlushAsync` stay free of test-specific dependencies; removing `FlushForTestAsync` therefore adds no production persistence operation, timer, or blocking work.

Alternative considered: add a production `IAgentSessionPersistenceObserver` or another grain method. A no-op observer still expands the grain's production constructor and every flush path for test synchronization; another grain method repeats the current test-only control. Both are rejected. Polling the database or using wall-clock delays is also rejected because it is nondeterministic and violates the server test policy.

### 4. Preserve the existing persistence failure split

The probe observes writes but does not decide retry, clear pending data, or alter ordering. `FlushAsync` remains the sole owner of state/event clearing, transcript commit, timer disposal, and activation quarantine. Tests cover successful writes, state/event failure quarantine, and transcript-only retry through the new observation rather than by forcing a flush.

Alternative considered: move retry or aggregation into the probe. That would make test infrastructure a second authority for AgentSession persistence and could diverge from production behavior, so it is rejected.

### 5. Migrate tests by behavior category and retain architecture guards

Update tests in three groups: activation/rehydration tests use the shared management helper; direct grain tests use actual cluster keys or narrower pure collaborators; deferred-persistence tests use probe checkpoints. Constructor tests become explicit about their fakes and no longer rely on omitted dependencies. Extend architecture tests to reject the removed public test methods, key overrides, nullable required collaborators, and forced-flush references while permitting the declared optional side channels.

Alternative considered: make the compiler fixes first and address test failures opportunistically. The broad call-site count makes that approach obscure the intended replacement for each behavior; grouping by behavior keeps each migration verifiable.

## Risks / Trade-offs

- [ForceActivationCollection deactivates all eligible test-cluster activations, not one grain] -> Serialize only tests sharing the management operation and give the shared helper a clear name; existing suites already use this mechanism.
- [Required constructor changes expose incomplete direct test composition] -> Treat compilation failures as the migration inventory and provide explicit fakes at every affected test boundary.
- [Storage writes can complete before a test begins waiting] -> Use checkpointed, recorded probe outcomes rather than one-shot notifications.
- [A state write and transcript write have different success/failure outcomes] -> Let tests await the boundary they assert and retain dedicated coverage for state quarantine and transcript-only retry.
- [Test decorators can drift from actual persistence behavior] -> Decorators only delegate and record post-call outcomes; they must not transform data, schedule writes, or implement retry.
- [Architecture guards could ban valid optional dependencies] -> Scope the rule to the audited required services and retain explicit allowlisted cache and diagnostic sink cases.

## Migration Plan

1. Add the shared grain-collection helper and checkpointed persistence probe/decorators to SpecTests support, then prove their success and failure behavior with focused tests.
2. Convert all `FlushForTestAsync` callers to the probe or existing deterministic scheduler control; remove the grain method only after no callers remain.
3. Convert activation tests to the management helper and direct key-dependent tests to real cluster activation; remove deactivation methods and key overrides.
4. Make audited dependencies required, register the intended event-push implementation explicitly in each composition root, and remove unreachable branches.
5. Add or update architecture guards, then run server build, unit tests, spec tests, and architecture tests.

No persisted data or external contract migration is required. Rollback is a normal code rollback: the change has no schema or protocol change. A rollback restores the prior internal test hooks only if the test migration itself blocks release; it does not require data repair.

## Open Questions

None. The concrete probe API can be named to match existing SpecTests support conventions, provided it remains test-only, checkpointed, and decorator-based.
