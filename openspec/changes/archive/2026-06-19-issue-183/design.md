## Context

The workflow module's domain model is correct in behavior but only *implicit* in code, and a few expressions leak a wrong model that misleads readers:

- `WorkflowRunOwnsSession` (`AgentSessionQuerier.cs:589`, one call site at `:554`) judges whether an agent session is associated with a run via the claim's runner identity, but its name implies the workflow *owns* the session. AgentSession is actually a peer aggregate linked only by `TaskRun` session references.
- `_lastRunnerId` (`WorkflowGrain.cs:29`) caches the most recent runner identity and is used as a fallback in `GetClaimedRunnerIdAsync` (`:449`). A reader cannot tell whether it is claim shadow-state or grain infrastructure. In practice `ReleaseClaim()` is never called, so the fallback is currently redundant with `Claim?.RunnerId` — but its *role* is undeclared.
- `WorkflowRun.Status` and `TaskRun.Status` are independent state machines, but this is stated nowhere. `WorkflowStatusMapper` only *projects* task state to views; there is no `status = f(tasks)` recompute. However, `FailTask` (`WorkflowRun.Task.cs:49`) does set `run.Status = Failed` on task failure, which a naive reader could mistake for "status derivation".

This change makes these invariants explicit (naming, comments, tests, docs) without changing runtime behavior. Scope is the workflow aggregate and its direct consumers (querier). Out of scope: the AgentSession aggregate, new domain concepts, rewriting the workflow grain, public API shape (unless a rename is required to remove ambiguity).

See `proposal.md` for motivation and `specs/workflow-run/spec.md` for the normative requirements.

## Goals / Non-Goals

**Goals:**
- Rename the session-association judgment so it expresses *association by reference*, not ownership, with a comment citing the single-runner invariant as the basis.
- Declare the role of the cached runner-identity field so it is unambiguous (recovery/infrastructure vs. claim state).
- Express the three invariants — single-runner claim, status independence, peer-level session association — in code via domain unit tests and doc comments.

**Non-Goals:**
- Changing runtime behavior; no semantics shift, only explicitness.
- Rewriting the workflow grain or removing `_lastRunnerId` (removal is a behavior change).
- Touching the AgentSession aggregate itself.
- Changing public API shape, except the minimal rename needed to remove the ownership implication.

## Decisions

### D1: Rename `WorkflowRunOwnsSession` → `IsSessionAssociatedWithRun`

Rename the private static method (and its single call site) to `IsSessionAssociatedWithRun` and add an XML doc comment stating: the session is *associated* with the run by reference (via `TaskRun`), never owned; the check uses the single-runner claim invariant (`Claim.RunnerId == session.RunnerId`) as its basis — that invariant is the real reason a runner identity can relate to a session.

**Alternatives considered:**
- *Keep the name, add a comment only.* Rejected — the name itself leaks the wrong model and is the primary source of confusion the issue targets.
- *Move the logic into the `WorkflowRun` domain aggregate.* Rejected — the method consumes `AgentSessionRecord` (a persistence/querier DTO), so it is a query concern, not pure domain. Keeping it in `AgentSessionQuerier` preserves the boundary.

**Blast radius:** one file, one private call site. No interface or public API change.

### D2: Declare `_lastRunnerId` as grain-infrastructure recovery state

Rename `_lastRunnerId` → `_lastKnownRunnerId` and add a doc comment: it is a **non-authoritative cache** of the most recent runner for recovery/reconciliation when the authoritative `Claim` is absent; it is *not* part of the claim domain model and does not represent an active claim. The authoritative runner identity remains `WorkflowRun.Claim.RunnerId`. `IsClaimed` already returns false without a claim, satisfying "recovery identity is not an active claim."

**Alternatives considered:**
- *Fold into the claim model as a derived attribute.* Rejected — the claim model (`WorkflowClaimInfo`) has no "released/last" concept, and `ReleaseClaim()` is never invoked, so there is no claim-model slot for it.
- *Remove the field entirely.* Rejected — it is out of scope (a behavior change to the recovery fallback) and risks the "do not rewrite the grain" non-goal. Flagged as a follow-up in Open Questions.

**Blast radius:** grain-private field plus the `?? _lastKnownRunnerId` fallback. `GetClaimedRunnerIdAsync`'s signature is unchanged, so the production consumer (`RunnerGrain.IsWorkRunnableForWorkflowAsync`) and existing tests are unaffected.

### D3: Express the invariants via domain unit tests + doc comments

Add a `WorkflowRunInvariantSpecs.cs` under `Specs/Workflow/Domain/` (pure unit tests, same style as `TaskLifecycleSpecs.cs`) plus enum/field doc comments:

- **Single-runner invariant:** test that `ClaimBy` rejects a second claim while one exists (`ClaimBy` already throws); test that a `Running` task's `RunnerId` equals `Claim.RunnerId`. Doc comment on `WorkflowClaimInfo`/`WorkflowRun.Claim`.
- **Status independence:** test (1) a `Running` run with *no* `Running` task stays `Running` (the divergence example from the issue); (2) `Paused`/`Stopped`/`AwaitingApproval` only result from workflow-level commands, never from a task status. Doc comment on the `WorkflowRunStatus` and `TaskRunStatus` enums stating they are independent.
- **Peer-level session association:** doc comment on `AgentSessionQuerier` and the renamed method (D1). The association contract is verified at the domain level via D1's comment; a full querier test would need a DB fixture (querier tests do not yet exist) and is not required to satisfy the "explicit expression" criterion.

**Alternatives considered:**
- *Documentation-only (a markdown doc).* Rejected — the issue explicitly accepts "tests or docs," and tests are stronger because they fail on future violations, which directly defends against the "future refactors writing it wrong" concern.
- *Full querier integration test for association.* Deferred — high setup cost; the naming + comment + single-runner test already express the contract.

### D4: Reconcile task-failure → workflow-failure with status independence

`FailTask` sets `run.Status = Failed` on task failure. This is **not** a violation of status independence: "independence" means neither status is a *continuous/functional derivation* of the other (`status = f(other status)`), and that `Running`-states may diverge. Task-failure→run-failure is an **event-driven policy reaction** by the workflow aggregate (a task *result* is an input event; the workflow decides its own transition), distinct from "the run recomputes its status from the set of task statuses." There is no `SyncStatusFromTasks`-style function.

The status-independence test (D3) is therefore scoped to: (a) `Running`-divergence, (b) command-result provenance (`Paused`/`Stopped`/`AwaitingApproval` from commands only), and (c) absence of a recompute function. `FailTask`'s propagation stays unchanged and is documented as a policy reaction.

## Risks / Trade-offs

- **[Spec scenario "task transition only mutates TaskRun state" reads as absolute]** -> The `specs/workflow-run/spec.md` scenario "A task status transition does not mutate the workflow status" says the transition "SHALL only mutate the TaskRun aggregate's own state." Taken literally this conflicts with `FailTask`. Mitigation: the D3 test scopes "status transition" to the non-terminal transitions (`Pending→Running`, `Running→Completed`) which indeed only mutate the task, and `FailTask` is documented as a separate policy reaction. This interpretation is listed in Open Questions for the review stage to confirm against the issue author's intent.
- **[Naming change touches a cross-context consumer]** -> Per the issue's trade-off, we keep call-site interfaces and only rename implementation-side symbols. D1 and D2 are both private/internal with no public-API impact, so this risk does not materialize here.
- **[`_lastRunnerId` is currently redundant (ReleaseClaim never called)]** -> Renaming clarifies role without fixing the redundancy. Mitigation: flagged as a follow-up rather than silently removing (removal is a behavior change and out of scope).
- **[Querier association comment without an integration test]** -> A future change could silently reintroduce an ownership-shaped name/logic. Mitigation: the single-runner domain test + the renamed-method comment establish the contract; a later change can add an integration test when querier test fixtures exist.

## Migration Plan

No data migration, no API contract change, no deployment ordering. The change is naming, comments, and additive tests. Rollback is a plain revert — no persisted state is affected.

## Open Questions

- **FailTask status propagation vs. status independence:** Does the issue author agree that task-failure→run-failure is an acceptable *policy reaction* (and thus `FailTask` stays as-is), or does the "互不推导" invariant intend to forbid *any* workflow-status change triggered by a task status? This design assumes the former (consistent with the issue's `Running`-divergence framing); the review stage should confirm. If the latter, it would expand scope into `FailTask` behavior, which is currently a Non-Goal.
- **Should the now-redundant `_lastRunnerId` fallback be removed in a follow-up?** `ReleaseClaim()` has no callers, so the `?? _lastRunnerId` fallback never differs from `Claim?.RunnerId` today. Removal is deferred (behavior change, out of scope) but worth a separate issue.
