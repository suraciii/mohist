# Mohist Glossary

This glossary defines the product and Agent execution language shared across
Mohist contexts. See [`design/agent-execution.md`](design/agent-execution.md) for
lifecycle, event, and module boundaries.

## Product Interfaces

**External Agent**:
An Agent with which the user interacts outside Mohist and that reads or operates
Mohist on the user's behalf. It is not a Mohist resource, and Mohist neither
schedules nor runs it.

_Avoid_: Mohist Agent, Inline Agent

**Agent Connection**:
A persistent relationship that exposes one Mohist Agent in an external
interaction location and contains its identity, invocation scope, and
availability there. It contains no copy of the Mohist Agent's Instructions,
execution configuration, or Skills and owns no AgentSession.

**Mohist App**:
The Mohist management entry point installed once in a Slack workspace. It
establishes the workspace connection and manages Agent Connections but neither
represents a business Agent nor performs Agent App work.

**Agent App**:
The dedicated Slack App and bot for one Agent Connection, presenting a Mohist
Agent as an independent identity without copying its definition. Agent Apps for
the same Mohist Agent in different workspaces are independent external
identities.

**Slack Bot**:
The client identity that represents a Mohist Agent in Slack through an Agent
Connection. It is neither another Mohist Agent nor an External Agent.

**Web UI**:
Mohist's fallback plane for observation, visualization, manual operations, and
takeover, with direct configuration, launch, and continuation of Mohist Agents.
It is neither the user's daily collaboration workspace nor the primary
interaction entry point.

## Agent Execution

**Action**:
The input and output contract by which a work owner delegates one execution to
a Runner. It has no Agent identity and owns no work lifecycle.

**Inline Agent**:
A use of Agent capability in which a Workflow task selects a Runtime Action
directly and supplies its input. It is not a persistent resource and has no
Agent ID.

**Agent Definition Reference**:
A use of Agent capability in which a Workflow task references a Mohist Agent
definition with `uses: mohist/agent`. Its definition snapshot resolves at
dispatch, and it creates neither an AgentJob nor an Agent identity.

**Mohist Agent**:
A predefined, reusable Agent resource within a Project that has a stable
identity and can start independently. The Web UI, CLI, Agent Connections, event
routing, and comment mentions are entry points to the same Agent.

**Agent Readiness**:
Mohist's unified diagnosis of whether an Agent execution configuration is
complete, with value `ready`, `needs-setup`, or `unknown`; it is not the
`active` or `archived` lifecycle. `needs-setup` includes an actionable gap, and
an entry point cannot infer Ready or Failed from `unknown`.

**Agent Availability**:
Whether a matching Runner can execute an Agent definition immediately or the
work must wait for a Runner, capacity, or validation. It is a transient
execution condition and does not change Agent Readiness.

**AgentJob**:
One unit of work created when a Mohist Agent starts, owning launch scheduling
state, result, and recovery and associated with the first AgentTurn in an
AgentSession. It is neither the continuing conversation nor the arbiter of
subsequent Follow-up work.

**SessionInput**:
One ordered input with stable identity that an AgentSession has accepted. One
AgentTurn can process one or more consecutive SessionInputs.

**AgentTurn**:
One continuous Runtime processing period in an AgentSession that contains one
or more ordered SessionInputs and distinguishes waiting, execution, and result
states. It is not a new top-level unit of work.

**AgentSession**:
The stable logical session and audit record that Mohist owns, with ordered
SessionInputs, AgentTurns, replies, and execution facts. The end of one
AgentTurn does not close it.

_Avoid_: "Session completed," "Session failed," or "Session closed" for the
result of one execution

**Activity**:
Whether an AgentSession has a nonterminal AgentTurn, with value `idle`, `active`,
or `unknown`; `active` covers a queued Turn and a Runtime that is executing,
while AgentTurn state gives the phase. Activity is not the success or failure
result of one unit of work.

**Runtime Session**:
A physical conversation owned by OpenCode, Pi, or another execution backend
that can be cached, resumed, replaced, or reclaimed. It does not determine
whether an AgentSession can accept more input.

**Runtime Binding**:
The routing facts that associate an AgentSession with its current Runtime,
physical Session, Runner, and related resources and that can be replaced as one
unit. It is neither the AgentSession identity nor physical Session history.

## Workflow Decisions

**Approval**:
An `approve` or `reject` decision about the output of a Workflow stage. An
approver signature is optional attribution rather than a validity condition;
an unsigned Approval records no operator.

**approval point**:
A Workflow state that waits for an Approval before its output can continue
through the pipeline.

_Avoid_: quality gate

## Authentication and Access

**Administrator (Admin)**:
Mohist's only user, with all capabilities. The system has no concept of a second
user.

**Principal**:
A caller identity that Mohist recognizes as an Administrator, service, or
Agent. Every control-plane access belongs to a Principal, whose identity
determines its capabilities.

**Credential**:
A token that proves a Principal's identity and can be issued, revoked, or
expired independently, with only its hash stored by Mohist. A service
Credential is a deployment-level machine identity, such as one held by a Slack
adapter, and is not a user.

**Agent Identity**:
The attribution identity of a Mohist Agent as a Principal, to which Mohist
attributes the Agent's actions. Its external delivery identity is an independent
bot and does not impersonate the Administrator.

_Avoid_: "user" for a service or Agent Principal; "login" for Runner or
integration Credential authentication

## Workspace

**Workspace**:
A named, persistent Project execution environment that holds an Origin,
Repository references, archive state, and one materialized root directory. Its
reserved root layout contains `REPOS/`, `PLANS/`, `RESEARCH/`, and `.scratch/`.
It exists across AgentSessions and WorkflowRuns, and all consumers with the same
Origin share it.

**Repository Checkout**:
The materialized Git working copy of one Project Repository inside a Workspace,
at `REPOS/<repository-name>/`. Its path and Workflow branch are execution facts
of the target Repository, not Workspace identity or user Variables.

**Origin**:
A Workspace creation source and unique resolution key: an Issue, an interaction
context such as a Slack channel or Web conversation, or an explicit creation.
At most one active Workspace exists for the same Origin at one time.

**Materialization**:
A Workspace directory instance on one Runner, with routing facts that determine
where subsequent execution is scheduled. Its directory can be reclaimed or
lost with the Runner. For an Issue Workflow, rematerialization reconstructs the
target Repository checkout from Git and previously captured Workspace artifacts
from Mohist's Artifact Store. Interactive rematerialization recreates only the
reserved layout and Repository access grants. Neither path changes Workspace
identity.

_Avoid_: worktree or Runner directory as the Workspace identity
