# Product Vision

Mohist is an Agent-oriented software factory built for the transition from
workshop-style development to industrial, automated software production. It
serves a development process that runs through executable workflows rather
than depending on people to organize and advance every task.

People use Agents and other tools to build, maintain, and control that
process. Within it, workflows organize Agents and programs to implement
requirements automatically and return delivery results with evidence. The
[philosophical foundation](philosophy.md) explains why this change in software
production calls for a different kind of development product.

This vision describes the intended development model and product direction,
not the implementation status of each capability.

## Goal

Enable people to build and maintain workflows that turn requirements into
verifiable software results without coordinating every task. Product
acceptance, business analysis, and execution evidence guide improvements to
both requirements and workflows.

## Two Development Loops

### Outer Loop: People Direct and Improve Development

People direct this loop. They use Agents as tools for business analysis,
product acceptance, organizing requirements, and building and maintaining
workflows. Agents can perform complex analysis and operations within that
role.

People use delivery results and business understanding to organize and
prioritize requirements. They select workflows, decide what to execute, and
observe the results. They also use execution evidence to improve workflow
definitions, Agent configuration, checks, and other development conditions.
Not every new requirement requires a workflow change.

This work continues when execution succeeds, not only when it fails. People
retain control over product goals and development methods without directing
every task inside a workflow.

People remain the final harness: they can inspect and revise the goals,
acceptance criteria, and methods that guide automatic execution. This does
not require a human approval step after every WorkflowRun. The
[philosophy](philosophy.md#humans-are-the-final-harness) explains this final
judgment and correction responsibility.

### Inner Loop: Workflows Execute Requirements Automatically

The inner loop receives requirements for execution, runs the selected
workflows, returns delivery results, and continues with further requirements
or waits. Agents and programs are its automatic executors. They perform
tasks, produce artifacts, evaluate results, and handle feedback according to
the definitions and authority available to them.

Users define the stages and evaluation rules. Agents can make authorized
decisions without waiting for a person. Correction and recovery belong to
this execution; the inner loop also continues across requirements. A
WorkflowRun represents one execution, while later requirements can start
others. Continuous development does not require one Agent process to run
indefinitely.

### How the Loops Connect

Requirements and authorized execution controls move into the inner loop.
Delivery results and execution evidence return to the outer loop. People use
that feedback both to organize further requirements and to maintain the
workflows. The diagram shows these relationships, not mandatory stages.
The dotted connection means that the definition governs execution.

```text diagram
+ Outer loop - person uses Agent --------------------+
|              +-----------------------+             |
|              | Business analysis and |             |
|              |      acceptance       |<-----------+|
|              +-----------+-----------+            ||
|             +------------+------------+           ||
|             v                         v           ||
| +-----------------------+  +--------------------+ ||
| | Organize requirements |  | Build and maintain | ||
| +-----------+-----------+  |      workflow      | ||
|             |              +----------+---------+ ||
|             +------------+............+           ||
|                          v                        ||
|            + Inner - auto execution --+           ||
|            |+----------------------+  |           ||
|            || Receive requirements |<+|           ||
|            |+-----------+----------+ ||           ||
|            |            |            ||           ||
|            |            v            ||           ||
|            | +---------------------+ ||           ||
|            | | Agents and programs | ||           ||
|            | |    execute tasks    | ||           ||
|            | +----------+----------+ ||           ||
|            |            |            ||           ||
|            |            v            |+-----------+|
|            |+----------------------+ ||            |
|            || Delivery results and | ||            |
|            ||       evidence       | ||            |
|            |+-----------+----------+ ||            |
|            |            |            ||            |
|            |            v            ||            |
|            |  +------------------+   ||            |
|            |  | Continue or wait +---+|            |
|            |  +------------------+    |            |
|            +--------------------------+            |
+----------------------------------------------------+
```

Product acceptance evaluates whether delivered software meets the requirement
and addresses the business need. Workflow Approval Points govern progress
within an execution; they do not replace this evaluation. A failure to meet
an existing requirement calls for correction. A changed business goal calls
for an explicit requirement change, not a silent change during implementation.

The two Agent roles do not define new Agent resource types. A Mohist Agent can
be a person's tool or a workflow executor. External Agent describes where an
Agent runs, not which loop owns its purpose. The [glossary](../CONTEXT.md)
remains the source of domain terms.

## Product Capability Areas

People organize work around outcomes, not internal resources. These areas
describe what Mohist helps them accomplish; they are not a menu or a second
domain model. [Domain Analysis](../design/domain-analysis.md) assigns business
rules to their owners. Each area links to representative features, not a
delivery-status inventory.

### Work Organization

Express a goal, divide the work, decide its order, and see what was delivered.
This area covers requirements, decomposition, prerequisites, and goal-level
progress; it does not decide how an execution runs or recovers. Its primary
subdomain is Issue, including Epic organization. Start with
[Issues](../specs/issue/lifecycle/spec.md), [Composite Issues](../specs/issue/decomposition/spec.md), and
[Epics](../specs/issue/epics/spec.md).

### Delivery Workflows

Build and maintain reusable workflows, then run them to implement
requirements. Workflow owns the stages, checks, Approval Points, and recovery
rules. It uses Agent execution and Workspace resources without owning their
lifecycles. Start with [The Workflow](../specs/workflow/execution/spec.md) and
[Workflow Profiles](../specs/workflow/profiles/spec.md).

### Agent Collaboration

Configure an Agent, delegate a task, continue the conversation, and receive its
result. Direct work does not require creating an Issue. Agent owns reusable
execution capability and Connections; Session owns the continuing conversation
and its inputs, Turns, and evidence. Start with
[Agents and AgentSessions](../specs/agent/execution/spec.md), [Subagents](../specs/session/subagents/spec.md), and
[Slack interaction](../specs/integrations/slack/interaction/spec.md).

### Operations and Supervision

Understand what is happening, identify work that needs attention, and take an
authorized next action. This area combines facts from Workflow, Session,
Runner, and other owners; it does not introduce another state authority.
AgentOps assembles read-side views, while controls act through the domain that
owns the operation. Start with
[Activity](../specs/agent-ops/activity/spec.md),
[Agent Supervision](../specs/agent/supervision/spec.md), and
[Observability](../specs/platform/observability/spec.md).

Project and Repository configuration, Workspace, Runner, and access controls
support these areas. They remain explicit resources without each becoming a
top-level user task. Slack, CLI, Web, and API expose the same capabilities
through different interaction locations.

## End-to-End Paths

The areas support three connected paths:

1. **Workflow construction and maintenance:** use an Agent to define how work
   proceeds, check the definition, and examine its execution results. Use
   findings to revise the definition, context, or checks, and evaluate the
   effect on later executions. Improving a definition and controlling an
   existing WorkflowRun are distinct operations.
2. **Software delivery:** organize a requirement as an Issue and execute it
   through the selected Workflow. Its definition controls the tasks,
   dependencies, verification, and feedback needed for delivery. The person
   evaluates the result and uses it to refine requirements and workflows.
   Deployment belongs to the path only when the Workflow defines it; a merge
   alone does not establish a deployed result.
3. **Direct delegation:** select an Agent, launch a task, continue or stop its
   Session as needed, and return the result to the originating context. A
   recorded delivery attempt alone does not establish that the caller received
   the result.

These paths use the same execution resources and supervision capabilities.
When an outcome is unknown or work fails, expose the evidence and the safe
recovery or human decision needed to continue. Keep execution and delivery
outcomes distinct rather than reporting uncertainty as success.

## How People Use Mohist

People normally stay in Slack, an IDE, or another existing workspace. Mohist
executes work, records evidence, and returns results there. The long-term goal
is to complete more than 90% of daily queries, delegations, and operations in
those existing places without requiring an Agent to run outside Mohist.

A configured Mohist Agent works directly in the Web UI or CLI and can appear in
an external interaction location through an Agent Connection. Configure its
Instructions, execution settings, and Skills once in Mohist. AgentJobs launch
work and AgentSessions preserve the continuing session.

An External Agent can use the Mohist Skill and `mo` to query state, delegate
work, and perform operations. It returns the result to its existing
conversation. It does not become a Mohist Agent resource.

Mohist Issues and Workflows form the execution layer. AgentSessions retain the
traceable execution record. The Web UI is a fallback operations and
visualization plane, not a workspace that users must adopt.

## How Workflows Operate

- **Issues carry intent:** Work enters as an Issue with requirements,
  discussion, and history relevant to its work. Readiness and the decision
  to start remain separate from execution. See
  [The Workflow](../specs/workflow/execution/spec.md).
- **Definitions make development methods reusable:** A Workflow Definition
  declares stages, tasks, checks, Approval Points, and recovery. A WorkflowRun
  applies its bound definition to one Issue; maintaining the definition and
  controlling a particular WorkflowRun are different activities. See
  [Workflow Definition](../specs/workflow/definition/spec.md).
- **Agents perform work:** Workflow tasks and direct entry points use the same
  Mohist Agent launch boundary. AgentSessions retain execution evidence and can
  recover from interruption.
- **Evaluation follows the definition:** Automated checks and authorized
  Agent judgment can evaluate results and direct correction. An Approval
  Point requires a decision, not necessarily a human decision. Product
  acceptance in the outer loop remains distinct from these execution rules.
- **Results preserve context:** Each result identifies the relevant work,
  software version, and available evidence. Agents can distinguish an
  observed failure from a cause that still needs investigation.
- **Events keep work moving:** Event routing can trigger supervision, failure
  handling, and progress responses without making a person watch every step.
- **Limits remain visible:** When work cannot continue within its authority,
  Mohist shows where it stopped, what it tried, and what decision is needed.
  Routine feedback and recovery stay inside automatic execution; handling an
  exception is only one part of the person's outer-loop work.

## Principles

- **Agents work independently:** A Mohist Agent is configurable, startable,
  continuable, and able to read results before it has an external Connection.
- **One Agent, many entry points:** The Web UI, CLI, Slack, and automation use
  the same Agent capability. Entry points handle identity, protocol, and
  presentation without keeping another copy of the Agent definition.
- **Agent-friendly interfaces first:** A Mohist Agent has a stable invocation
  interface. An External Agent can discover and operate Mohist with a Skill and
  `mo`. Critical capabilities must not exist only in the Web UI.
- **Requirements support autonomous execution:** An Issue carries the
  objective, acceptance criteria, context, and boundaries needed for its
  work. Exploration can clarify an unknown within that scope. A decision
  outside the scope returns to the person rather than being invented by the
  executor or making routine human approval a requirement.
- **Useful feedback over output volume:** Checks should answer a specific
  question and identify what they evaluated. Agents use proportionate checks
  and distinguish established facts from interpretations and unknowns.
  Feedback quality depends on whether it supports the next decision, not on
  how much output it produces.
- **Experience improves development methods:** People use Agents to turn
  verified findings into better Workflow Definitions, Skills, project
  context, and automated checks. They evaluate whether the change reduces
  repeated failures or unnecessary work. Repeating executions alone does not
  improve the method.
- **Autonomy has defined limits:** Clear requirements, relevant feedback,
  and improved methods support unattended work. They do not guarantee that
  every requirement can be resolved automatically. Execution stays within
  its authority and reports when a decision outside that authority is needed.
- **One state arbiter:** The Server decides Workflow state. A Runner
  reports execution facts.
- **Reliability before breadth:** Add no mechanism that Mohist does not need.
  Make every important step visible in the Issue record.

## What Mohist Is Not

- Mohist is not an IDE, chat tool, or collaboration workspace. Users keep their
  daily collaboration in existing interaction locations while Mohist executes
  work and records evidence.
- Mohist does not replace existing build, test, or deployment systems. It uses
  their results to verify work and organize further action. The selected
  Workflow determines whether delivery includes integration or deployment.

## Direction

Mohist develops both sides of the same working model: people can use Agents
to build and maintain effective workflows, and those workflows can execute
requirements without repeated human coordination. Improving a workflow means
changing how subsequent requirements are handled, not only repairing the
current result.

Supervision Agents can handle authorized Approval and failure routing inside
automatic execution. Mentions and event routing make that help configurable
and revocable. They do not replace the person's role in product acceptance,
business analysis, and workflow maintenance. See
[Agent Supervision](../specs/agent/supervision/spec.md) and
[Agent Event Routing](../specs/agent/event-routing/spec.md).

The same Agent should remain useful wherever the work starts. Agents should
work independently in Mohist, join existing interaction locations with
independent identities, and let External Agents use the same domain actions.
See [Agents and AgentSessions](../specs/agent/execution/spec.md), [Slack](../specs/integrations/slack/enrollment/spec.md),
[Skills](../specs/agent/skills/spec.md), and [CLI Reference](../specs/interfaces/cli/spec.md).

Workflows should support work whose shape becomes clear only during
execution, larger plans, and reliable fallback supervision. Session trees,
Epics, composite Issues, the Web UI, and mobile supervision with anomaly
notifications extend that direction without moving daily collaboration into
Mohist. See [Subagents and Session Trees](../specs/session/subagents/spec.md), [Planning with
Epics](../specs/issue/epics/spec.md), [Composite Issues and Child Issues](../specs/issue/decomposition/spec.md), and
[Web UI Guide](../specs/interfaces/web/spec.md).

Parallel work depends on structure more than on scale. Workers need
clear scopes, shared interface agreements, and integration evidence. Increasing
the number of Agents is useful only when their results can be combined and
evaluated against the requirement.

Progress means more requirements reach useful, verifiable results with less
manual coordination and repeated correction. Agent counts, execution counts,
and token consumption do not measure that outcome by themselves.
