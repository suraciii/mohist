# Agent Execution Model

This document defines the single Mohist Agent execution path. It separates Agent
work, logical Sessions, physical Runtime Sessions, and Runtime adapters by
ownership. Workflow is an execution origin and orchestrator, not another work
owner. Runtime-specific behavior belongs in [`runtimes/`](../../interfaces/runtimes/catalogs/spec.md).
Canonical internal read schemas and fencing belong in [`conventions.md`](../../../design/conventions.md).
Authentication, transport, and public projections belong in [`agent-api.md`](../../interfaces/agent-api/design.md).

[Session inputs and Turns](../../session/input-and-turns/design.md) define launch
convergence, admission, and continuing input. [Session recovery](../../session/recovery/design.md)
defines physical Binding replacement, recovery fencing, and context operations.

## Design Drivers

Three forces shape the model:

- **Stable identity.** A user must follow one logical conversation when a Runner
  or physical Runtime Session is replaced. Public Session identity cannot belong
  to an external Runtime.
- **Unknown external effects.** A timeout or lost response does not prove that a
  Runtime command failed. Mohist must preserve `unknown` and reconcile the
  original identity before retrying.
- **Cross-owner convergence.** AgentJob owns work lifecycle. AgentSession owns
  conversation state. They do not share a transaction, so durable identities
  and messages must converge observations without moving either decision to the
  other owner.

Exposing a physical Runtime Session as the public Session would remove a binding
layer but would change user identity on replacement and leak provider lifecycle.
Mohist therefore exposes a stable AgentSession and treats the physical Runtime
Session as its replaceable current Binding.

## Ownership and Call Paths

- Mohist Agent owns identity, Instructions, execution configuration, Skills,
  archival, and readiness.
- Workflow owns orchestration state. A `mohist/agent` task names an Agent,
  supplies input and attribution, and consumes the AgentJob result. Mechanical
  Actions do not create AgentJobs.
- AgentJob is the sole top-level execution owner. It owns lifecycle, result,
  retry, and recovery for one work item from any launch origin.
- AgentSession owns Input order, Turns, Transcript, Activity, context, usage,
  and current Binding.
- Runtime Session owns the physical provider Session and execution facts.
- The Runner process owns the Runtime adapter, provider protocol, process
  resources, event reconciliation, and error classification.

There is one work-owner path:

```text diagram
Workflow mohist/agent task / Web / CLI / Connection / event / mention
                         |
                         v
                 Agent AgentJob
                  |           |
                  v           v
          AgentSession    resolved Action
                              |
                              v
                     Runtime adapter --> Runtime Session
```

Every origin enters through the AgentJob launch boundary. The Agent context
validates readiness and snapshots Instructions, execution configuration, Skills,
Runtime, and Workspace for the accepted launch. A missing, archived, or
not-ready Agent fails launch instead of using a Runtime-specific Workflow path.
AgentJob retry preserves that snapshot unless the caller creates a new launch.

Execution resolution is an accepted caller hint where the entry point allows
one, then the Agent definition, then Runtime behavior for unset fields: an unset
Runtime resolves to `pi`, and the Runtime chooses the model at dispatch for an
unset Model. The Server writes the resolved Runtime explicitly into every new
AgentJob and AgentSession execution snapshot. Runner accepts only the canonical
`pi` and `opencode` values and fails a missing or unknown dispatch Runtime; it
does not own default selection. Existing AgentJobs and Sessions keep their
persisted configuration and are never reinterpreted by a later Agent edit.

A provider adapter may translate ingress and delivery, but it cannot snapshot an
Agent, own a Runtime Session, or decide a work result. Workflow cannot select a
Runtime, construct an Action, dispatch to Runner, or own execution retry.

Direct API authentication and Project authorization finish before resource or
idempotency lookup, admission, durable write, or external effect. Responses and
events expose only the public projection in [`agent-api.md`](../../interfaces/agent-api/design.md).
Canonical models, physical Binding, workspace paths, prompt or memory content,
and Runner control remain private.

There is no Inline Agent or Agent Definition Reference path. A Workflow worker
is a real Mohist Agent. Its `mohist/agent` task creates an ordinary AgentJob and
AgentSession through the same launch boundary as every other entry point.

## Module Ownership

- Workflow owns Profile, WorkflowRun, Stage and Task ordering, checks, Approval,
  and advancement. It consumes AgentJob results but does not own execution.
- Agent owns Mohist Agent, AgentJob, Action contracts, execution snapshots,
  Runner dispatch, retry, recovery, and result validation.
- Session owns AgentSession identity, source and Workflow attribution, Inputs,
  Turns, Activity, Binding, Transcript, context, and usage.
- Runner executes resolved Agent work and reports capacity and physical facts. It
  does not arbitrate AgentJob or logical Session state.
- Runtime adapters hide provider SDK, protocol, process, cache, and error detail.
  They do not define public Session identity or idempotency.
- Web, CLI, and trusted integrations consume canonical Server projections. Direct
  API callers consume only the public projection in [`agent-api.md`](../../interfaces/agent-api/design.md).

Server is the sole arbiter for Binding, Activity, admission, and operation
results. Runner cannot replace Binding or close AgentSession because a process
exited.

## Public Execution Context

Durable launch metadata may retain a filesystem `workspacePath` for internal
dispatch, recovery, and storage lookup. It is not a public execution-context
fact. Agent-scoped Session lists and summaries expose only Issue, Epic,
Repository, and named Workspace. CLI and Web types consume those read models
and cannot reconstruct or display the materialization path.
