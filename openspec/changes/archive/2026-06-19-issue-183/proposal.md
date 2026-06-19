## Why

The workflow module's domain model exists only implicitly in code, and several expressions actively leak a *wrong* model that misleads readers and future changes: `WorkflowRunOwnsSession` implies the workflow owns agent sessions when they are actually peer aggregates linked only by task references; a `_lastRunnerId` field on the workflow grain cannot be told apart from claim state versus pure grain infrastructure; and the independence of `WorkflowRun.Status` and `TaskRun.Status`, plus the single-runner invariant, are nowhere declared — inviting refactors that wrongly derive one state machine from the other. With TaskRun having just gained a full lifecycle, this is the right moment to make the model explicit before the ambiguity multiplies.

## What Changes

- Rename the workflow↔session association judgment (`WorkflowRunOwnsSession` and its call site in `AgentSessionQuerier`) to express *association* — driven by the claim's runner identity — not ownership, with a comment stating the single-runner invariant is the real basis for the check.
- Clarify the semantics of the workflow grain's cached runner-identity field (`_lastRunnerId`): fold it into the claim model as a derived attribute or rename/comment it as a pure grain-infrastructure recovery fallback, so its role is unambiguous to readers.
- Capture the independence of `WorkflowRun.Status` and `TaskRun.Status` as explicit spec requirements, expressed in code via tests and/or documentation: the two state machines describe different aggregates and do not derive each other.
- Capture the single-runner invariant as an explicit spec requirement: one WorkflowRun is claimed by at most one runner for its whole lifecycle, and a `Running` TaskRun's `RunnerId` equals `Claim.RunnerId` as a *derivation* of that invariant — not as two facts kept in sync.
- Capture AgentSession as a peer-level aggregate associated with WorkflowRun only by TaskRun session references, with no parent-child ownership.

## Capabilities

### New Capabilities

_None._ This change makes the existing workflow-run model explicit; the issue's Non-Goals forbid introducing new domain concepts.

### Modified Capabilities

- `workflow-run`: gains spec-level requirements for the domain-model invariants that are currently only implicit — the single-runner claim invariant, the independence of `WorkflowRun.Status` and `TaskRun.Status`, and AgentSession as a peer aggregate associated by TaskRun references rather than owned. These requirements drive the rename of the session-association judgment in `AgentSessionQuerier` and the clarification of the workflow grain's cached runner-identity field.

## Impact

- **Code**: `Workflow/Services/Sessions/AgentSessionQuerier.cs` (association-judgment rename + invariant comment); `Workflow/Grains/WorkflowGrain.cs` (`_lastRunnerId` semantics); `Workflow/Domain/Run/*` (invariant comments/docs).
- **Specs**: `openspec/specs/workflow-run/spec.md` gains invariant requirements; delta spec under `openspec/changes/issue-183/specs/workflow-run/`.
- **Tests**: new tests asserting status-independence, the single-runner invariant, and the peer-level session association.
- **API surface**: no public API shape change (per Non-Goals); cross-context call sites keep their interface unless a rename is required to eliminate ambiguity.
- **Out of scope**: the AgentSession aggregate itself, new domain concepts (multi-runner, task pause/resume), and any rewrite of the workflow grain.
