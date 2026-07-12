# Agent Execution Model

This document defines the abstraction boundaries shared by Workflow, Agent, Session, Runner,
and runtime adapters. Runtime-specific behavior belongs in files such as
[`opencode-runtime.md`](opencode-runtime.md).

## Layers

| Layer | Concept | Owner | Authoritative state |
|---|---|---|---|
| Definition | Mohist Agent | Agent context | identity, instructions, config, skills, subscriptions, status |
| Work | TaskRun | Workflow context | Workflow task lifecycle, result, output, recovery |
| Work | AgentJob | Agent context | one Mohist Agent execution lifecycle and result |
| Execution contract | Action | Workflow context | `uses`/`with` input and output contract for one work dispatch |
| Conversation | AgentSession | Session context | transcript, context, usage, runtime binding, lineage |
| Runtime | Runtime Session | external runtime | physical conversation and provider execution state |
| Adapter | OpenCodeRuntime, future Pi adapter | runner process | protocol, process, events, reconciliation, errors |

`Inline Agent` is a product usage mode, not another entity or bounded context. It means a
Workflow TaskRun directly selects a runtime-specific Action and supplies its input without
resolving a Mohist Agent.

## Canonical terms

- **Mohist Agent (Named Agent)**: project-scoped reusable definition with stable Agent ID.
- **Inline Agent**: direct Action invocation configured by a Workflow task; no Agent ID.
- **AgentJob**: one execution of a Mohist Agent, using a launch-time snapshot of the Agent.
- **AgentSession**: stable logical conversation and audit record; never the Agent identity or
  work lifecycle owner.
- **Runtime Session**: physical conversation owned by OpenCode, Pi, or another backend.
- **OpenCode runtime agent**: OpenCode's native `agent` selection. It is configuration inside
  the OpenCode adapter, not a Mohist Agent.

## Invocation paths

| Path | Work owner | Runner entry | AgentSession origin |
|---|---|---|---|
| Workflow direct action | TaskRun | `mohist/opencode` Action adapter | Workflow |
| Mohist Agent launch | AgentJob | AgentJob executor | Agent launch |

```text
Workflow: TaskRun -> mohist/opencode Action adapter --+
                                                       +-> OpenCodeRuntime -> Runtime Session
Agent: Mohist Agent -> AgentJob -> AgentJob executor --+
```

The paths share Runner execution and Session infrastructure. They do not share work owners:
TaskRun remains authoritative for Workflow work; AgentJob remains authoritative for Mohist
Agent work. Each entry passes its already resolved AgentSession target to `OpenCodeRuntime`,
which reports runtime facts to that Session. Shared runtime code must not create a
Workflow -> Agent domain dependency.

## Action semantics

`mohist/opencode` is a runtime-specific Action. It answers "execute this turn with OpenCode."
It does not accept an Agent ID, resolve an Agent name, read Agent definitions, or create an
AgentJob. Direct Workflow use is therefore Inline Agent execution.

Future runtime Actions such as `mohist/pi` sit at the same layer. This design intentionally
does not define a `mohist/agent` contract. That name is reserved for the later Mohist Agent
design and must not be introduced here as a runtime alias or generic wrapper around
`mohist/opencode`.

The AgentJob path must not dispatch through the public `mohist/opencode` Action contract.
Its executor receives an Agent-owned execution request after the Agent definition has been
resolved and snapshotted. The Workflow Action adapter and AgentJob executor may both call the
same `OpenCodeRuntime` deep module. Runtime implementation is the reuse point; Action is not.

## Work lifecycle versus conversation

TaskRun and AgentJob own decisions:

- pending/running/terminal state;
- success/failure and result;
- retry, recovery, or Workflow advancement.

AgentSession owns facts:

- user/agent messages and tool calls;
- context and usage;
- model/runtime observations;
- current Runtime Session binding and lineage.

The Workflow Action adapter reports a work result to TaskRun. The AgentJob executor reports a
work result to AgentJob. Both report runtime facts to AgentSession. AgentSession events never
advance Workflow and never make an AgentJob terminal. A failed AgentSession operation may be
evidence used by the work owner, but Session is not the judge.

A Session command is not a work dispatch. Follow-up during an active work-owned turn becomes
input to that turn. Follow-up while idle starts a user-initiated conversation turn and records
only command/runtime facts; it does not create a TaskRun or AgentJob. Compact and Reset follow
the same Session-only ownership rule.

## AgentSession origins

Every AgentSession has exactly one immutable origin.

### Workflow origin

Addressed by `(projectId, workflowRunId, sessionName)`. Reusing the same name within the same
WorkflowRun continues the logical conversation. Omitting an explicit name uses Work ID, so
unrelated tasks do not share context accidentally.

### Agent launch origin

Minted for one Mohist Agent launch and associated with the resolved Agent ID. One Mohist
Agent can create many AgentJobs and AgentSessions. Editing or archiving the Agent later does
not change the origin or launch-time execution snapshot.

Equal prompt, model, runtime, workspace, or configuration never merges two origins. A Session
cannot move from Workflow origin to Agent origin or vice versa.

Origin-specific routes are lookup/convenience surfaces. Both resolve to the canonical
AgentSession resource keyed by `sessionId`; neither `(workflowRunId, sessionName)` nor
`agentId` replaces Session identity.

Follow-up, Compact, Reset, transcript, and query operate on that canonical resource. Origin-
specific CLI or API paths may resolve it first, but must not implement a second Session
lifecycle.

## Logical and physical Session identity

AgentSession ID is stable for the logical conversation. Runtime Session identity is an
external physical facet:

```json
{
  "runtime": "opencode",
  "runtimeSessionId": "ses_..."
}
```

Runtime or work-directory change and Reset may replace the physical binding and append
lineage without changing AgentSession identity or origin. Compact and model/runtime-agent
selection changes do not replace the physical binding.

The persisted current binding contains the minimum control data required after Runner
restart: `runtime`, `runtimeSessionId`, `runnerId`, and `workDir`. Lineage records `runtime`,
`runtimeSessionId`, and `boundAt`.

## Mohist Agent launch

The Agent context owns launch composition:

1. resolve the active Mohist Agent by ID or name;
2. snapshot Agent ID, instructions, config, and launch prompt into AgentJob input;
3. mint and open an AgentSession with Agent-launch origin;
4. dispatch the AgentJob to an eligible Runner;
5. let the runtime executor operate only on the composed turn input and Session binding.

The runtime adapter never fetches the Agent definition. This prevents concurrent Agent edits
from changing in-flight bytes and keeps runtime modules independent from the Agent context.

## Module boundaries

- Workflow owns TaskRun and `uses`/`with` Action contracts.
- Agent owns Mohist Agent, AgentJob, launch composition, AgentJob execution request, and
  report validation.
- Session owns AgentSession identity, metadata, transcript, usage, and lineage.
- Runner context owns resource presence and capacity, not Agent or Session semantics.
- runner process executes dispatches and adapts external runtimes; it owns no business entity.

Runtime adapters accept Mohist-owned turn/session requests and return normalized facts. They
must not expose SDK types, resolve Agent definitions, decide Workflow transitions, or own job
status.

## Invariants

- An Action is not an Agent.
- AgentSession is not an Agent and not a work owner.
- Inline Agent has no Agent ID or reusable definition.
- Mohist Agent has stable identity and can own many executions and Sessions.
- TaskRun and AgentJob are mutually exclusive work owners for a dispatch.
- Every AgentSession has one immutable origin.
- Runtime Session replacement never changes AgentSession origin or logical identity.
- OpenCode's `agent` option never refers to a Mohist Agent.
- AgentJob execution never depends on a Workflow Action name or Action Input contract.
- Sharing `OpenCodeRuntime` never creates a Workflow -> Agent context dependency.

## Implementation gap

Current code already has separate Agent, AgentJob, and AgentSession aggregates, and dispatch
distinguishes Workflow from AgentJob ownership. Terminology and adapter boundaries still
leak the old model: `GenericAgentSession` means Agent-launch Session, AgentJob defaults to
the Workflow-owned `mohist/acp-agent` Action, and the ACP action itself branches on both owner
kinds. The OpenCode replacement must route AgentJob through an Agent-owned execution request
while both paths share `OpenCodeRuntime`.
