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

### 3. Test-only persistence probes observe complete timer cycles

Add an internal persistence-cycle reporter at the `PersistCallback` boundary. Each timer invocation creates one session-scoped cycle identifier before calling `FlushAsync`, then reports exactly one terminal outcome after `FlushAsync` returns or throws: `succeeded` only when the complete deferred flush succeeds, `transcript-failed` when state/events committed but transcript remains pending, or `state-failed` when the state/event transaction fails and the activation is quarantined. Immediate `FlushPendingTranscriptAsync` and immediate transcript-evidence writes do not create a cycle report.

The production composition supplies a no-op reporter. SpecTests replaces it with a checkpointed probe that records cycle identifiers and terminal outcomes using `TaskCompletionSource` with asynchronous continuations. A test captures a session checkpoint before causing deferred data, then awaits the next reported cycle for that session. Recorded outcomes make a cycle observable even when it completes before the wait starts, and allow tests to distinguish a full durable flush from either failure mode without inspecting arbitrary storage calls.

The helper replaces `FlushForTestAsync` calls with this correlated cycle observation. The reporter is an internal grain implementation collaborator, not an `IAgentSessionGrain` operation; it does not write data, schedule work, block timer execution, or alter `FlushAsync` ordering, clearing, retry, or quarantine decisions. The no-op production reporter therefore adds no persistence operation or timer work when no test is observing.

Alternative considered: decorate `IAgentSessionStore` and `IAgentSessionTranscriptStore`. The same transcript store is invoked by the synchronous input fence, immediate evidence persistence, and timer-driven persistence, so a decorator can observe only individual writes and cannot correlate them to one deferred `FlushAsync` outcome. This is rejected. Another public grain method repeats the current test-only control; polling storage or using wall-clock delays is nondeterministic and violates the server test policy.

### 4. Preserve the existing persistence failure split

The reporter observes the terminal result but does not decide retry, clear pending data, or alter ordering. `FlushAsync` remains the sole owner of state/event clearing, transcript commit, timer disposal, and activation quarantine. Tests cover successful writes, state/event failure quarantine, and transcript-only retry through the correlated cycle observation rather than by forcing a flush.

Alternative considered: move retry or aggregation into the probe. That would make test infrastructure a second authority for AgentSession persistence and could diverge from production behavior, so it is rejected.

### 5. Migrate tests by behavior category and retain architecture guards

Update tests in three groups: activation/rehydration tests use the shared management helper; direct grain tests use actual cluster keys or narrower pure collaborators; deferred-persistence tests use probe checkpoints. Constructor tests become explicit about their fakes and no longer rely on omitted dependencies. Extend architecture tests to reject the removed public test methods, key overrides, nullable required collaborators, and forced-flush references while permitting the declared optional side channels.

Alternative considered: make the compiler fixes first and address test failures opportunistically. The broad call-site count makes that approach obscure the intended replacement for each behavior; grouping by behavior keeps each migration verifiable.

## Risks / Trade-offs

- [ForceActivationCollection deactivates all eligible test-cluster activations, not one grain] -> Serialize only tests sharing the management operation and give the shared helper a clear name; existing suites already use this mechanism.
- [Required constructor changes expose incomplete direct test composition] -> Treat compilation failures as the migration inventory and provide explicit fakes at every affected test boundary.
- [A deferred cycle can complete before a test begins waiting] -> Use checkpointed, recorded cycle outcomes rather than one-shot notifications.
- [A state write and transcript write have different success/failure outcomes] -> Report the final `FlushAsync` result, with distinct state-failed and transcript-failed outcomes, and retain dedicated coverage for both paths.
- [An immediate transcript write could be mistaken for deferred persistence] -> Create reports only in `PersistCallback`, never in storage adapters or immediate transcript paths.
- [Architecture guards could ban valid optional dependencies] -> Scope the rule to the audited required services and retain explicit allowlisted cache and diagnostic sink cases.

## Migration Plan

1. Add the shared grain-collection helper, internal no-op persistence-cycle reporter, and checkpointed SpecTests reporter/probe, then prove its cycle correlation and terminal outcomes with focused tests.
2. Convert all `FlushForTestAsync` callers to await the correlated deferred-persistence cycle or existing deterministic scheduler control; remove the grain method only after no callers remain.
3. Convert activation tests to the management helper and direct key-dependent tests to real cluster activation; remove deactivation methods and key overrides.
4. Make audited dependencies required, register the intended event-push implementation explicitly in each composition root, and remove unreachable branches.
5. Add or update architecture guards, then run server build, unit tests, spec tests, and architecture tests.

No persisted data or external contract migration is required. Rollback is a normal code rollback: the change has no schema or protocol change. A rollback restores the prior internal test hooks only if the test migration itself blocks release; it does not require data repair.

## Open Questions

None. The concrete probe API can be named to match existing SpecTests support conventions, provided it remains test-only, checkpointed, and reports only complete deferred-persistence cycles.
