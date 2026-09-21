# Product Vision

Mohist is an Agent-native application that provides an Agent product and a
programmable software factory. People can use a Mohist Agent as their own
Agent or use Mohist through an External Agent.

The factory turns the path from idea to delivery into a production line that
people can define, run, and supervise. An Issue enters and a deliverable
leaves. People retain direction and judgment throughout delivery, while
Agents perform delegated work.

## Goal

When the production line is clear enough and Agent execution is reliable
enough, one person can deliver as much as a small team while retaining the
ability to understand, evaluate, and redirect the work.

## Philosophy

[Philosophy of Software Development](philosophy.md) examines development as a
purposeful practical activity. Human intentions guide the work; building
and using its results can also change how humans understand those intentions.
Review and correction continue this activity. Humans communicate intent
through dialogue with Agents, which connect that intent to program operations
and return results for human judgment.

Mohist applies that perspective through a software factory. It amplifies
human capacity to act while preserving users' ability to inspect evidence,
question assumptions, and redirect work. Its organization must demonstrate
its value through the results it helps users achieve.

## How People Use Mohist

Agent interaction is the first priority. People primarily express intent,
delegate work, and discuss results in natural-language conversations with
Agents. They can use a Mohist Agent directly or converse in existing spaces
such as Slack and Discord.

Agent interaction must support the complete work cycle: express an idea,
clarify requirements, delegate work, inspect progress, resolve exceptions,
make decisions, examine results, and correct direction. The Agent must
translate intent into operations and explain results using execution facts
that the user can inspect and question.

The [Web UI](web-ui.md) focuses on displaying progress, relationships,
evidence, and results. People can use these views alongside Agent
conversations to understand and judge the work. Direct controls remain
available for configuration, decisions, and manual takeover.

A Mohist Agent works with the user as their own Agent: discussing goals,
organizing work, and explaining results. It can join the user's communication
space through an Agent Connection. Its Instructions, execution settings, and
Skills belong to the Agent.

Users can also keep an Agent from another product. That External Agent uses
the Mohist Skill and `mo` to operate Mohist and returns results to its existing
conversation. It does not become a Mohist Agent resource.

Users can clarify, question, and revise their requests while work proceeds.
The Agent must relate new input to the ongoing work and explain any decision
needed to change direction.

Mohist Issues and Workflows form the execution layer. Agents use programmatic
interfaces to record intent, start work, read evidence, and apply decisions.
Users do not have to translate their requests into Mohist's resource model.

## How the Factory Operates

- **Issues carry intent:** Work enters as an Issue with requirements,
  discussion, and history. Readiness stays outside execution so incomplete
  requirements do not consume capacity. See [The Workflow](the-workflow.md).
- **Workflows define the line:** A Workflow Definition declares stages, tasks,
  checks, Approval Points, and recovery. Changing the definition changes the
  production line.
- **Agents perform work:** Workflow tasks and direct entry points use the same
  Mohist Agent launch boundary. AgentSessions retain execution evidence and can
  recover from interruption.
- **Quality has a gate:** Automated checks control stage exits. Approval Points
  stop important work until a person approves or requests changes.
- **Results are traceable to a cause:** Each result points to its stage,
  change, or check, with evidence. An Agent can find why a step failed
  without reading full logs.
- **Events keep work moving:** Event routing can trigger supervision, failure
  handling, and progress responses without making a person watch every step.
- **Exceptions reach people:** When work stops, Mohist reports the stopping
  point, attempted actions, and required decision in the user's conversation.
  People supervise exceptions instead of watching every step.

## Product Principles

- **Agents are reusable capabilities:** A Mohist Agent can be configured,
  started, continued, and queried through programmatic interfaces. Its
  capabilities remain independent of any communication channel.
- **Connections carry conversations:** An Agent Connection handles identity,
  protocol, and presentation. It does not keep another copy of the Agent
  definition or implement its own reasoning.
- **Application interfaces serve Agents first:** Every product operation must
  have a stable programmatic interface with explicit inputs, results, and
  failures. Agents use these interfaces to act and inspect state and evidence.
  The Web UI uses the same operations. Skills explain how Agents use these
  interfaces.
- **Issues carry complete objectives:** An Issue contains the requirements,
  acceptance criteria, and boundaries needed to start work. When new evidence
  requires a decision outside those boundaries, the Agent must bring that
  decision to the user. An Agent proposal does not become a user decision
  without the user's approval.
- **Feedback density over volume:** Each result is local to a change or a
  stage, quick to get, and objectively verifiable. An Agent uses the cheapest
  check that can answer its current question. Feedback that is slow or does
  not identify a cause is a product defect.
- **Each run reduces the cost of the next:** Verified lessons go back into
  Skills, repository context, and automated checks. A defect that gets past
  the checks becomes a new check. The next task starts with what the last
  task learned.
- **Delegation follows three properties:** Work can run unattended when its
  objective is complete, its feedback shows causes, and its lessons return to
  the system. Mohist improves these three properties so more work can be
  delegated safely. People keep objectives, boundaries, and risk judgment.
- **One state arbiter:** The Server decides production-line state. A Runner
  reports execution facts.
- **Reliability before breadth:** Add no mechanism that Mohist does not need.
  Make every important step visible in the Issue record.

## What Mohist Is Not

- Mohist is not an IDE or a general-purpose chat or collaboration tool. It
  connects to existing communication spaces and provides views for directing
  and inspecting software delivery.
- Mohist is not CI. CI verifies a commit. Mohist advances a complete unit of
  work from requirement to integration.

## Direction

Mohist is moving toward a factory that reduces routine execution and
coordination work so people can focus on direction, trade-offs, and results.
People can inspect and redirect work even when no failure has been reported.
Supervision Agents can handle proxy Approval and failure routing within their
delegated authority, while mentions and event routing make that help
configurable and revocable. See [Agent Supervision](agent-supervision.md)
and [Agent Event Routing](event-routing.md).

The same Agent should remain useful wherever the conversation starts. Agents
join existing communication spaces with independent identities, while
External Agents use the same domain actions through programmatic interfaces.
See [Agents and AgentSessions](agent-sessions.md), [Slack](slack.md),
[Skills](skills.md), and [CLI Reference](cli-reference.md).

The factory should support work whose shape becomes clear only during
execution, larger plans, and reliable supervision. Session trees, Epics, and
composite Issues organize that work while people continue to supervise it
through conversation. See [Subagents and Session Trees](subagents.md),
[Planning with Epics](epics.md), and
[Composite Issues and Child Issues](composite-issues.md).

Parallel work depends on structure more than on scale. Workers need
non-overlapping groups, written assignments, isolated work areas, interfaces
that stay unchanged, and a commit after each verified step. Clear, verifiable
bulk work, such as cleanup, migration, and hardening, is rarely scheduled
by hand. This is the first class of work that parallel delegation makes
economical.
