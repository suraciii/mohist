# Web UI

The Web UI is Mohist's fallback operations and visualization plane. It presents
authoritative state, evidence, relationships, and safe actions for global
review or manual takeover. The primary conversation may remain in Slack, an
IDE, or another external surface. Fallback means infrequent, not incomplete.

The Web UI is also a complete direct Mohist Agent client for configuration,
launch, Follow-up, Job results, Session evidence, and recovery. Open
`http://localhost:3456`. Every page answers what happened, why it happened,
whether a person must act, and which actions are safe now.

## Core Decisions

- Web owns rendering, view state, drafts, and user intent. The Server owns
  state and domain rules.
- Web, CLI, and the Slack adapter use the same Agent API. Web adds no launch
  configuration override because the Agent editor owns configuration.
- Push is observation. The UI reconciles authoritative queries after reconnect.
- A Web action must use the same Server-owned intent as every other client.
- The UI emphasizes Project attention, Issue and Epic progress, Workflow state,
  diffs, Session evidence, and system health.

## System Boundary

```text diagram
                                                                       +--------------------+
                                                                   +-->| Project event push |
+-------------+    +--------+    +------------+    +--------------+|   +----------+---------+
| User action +--->| Web UI +--->| Domain API +--->| Server state ++              |
+-------------+    +--------+    +------------+    +--------------+|   +------------------+
                        ^                                  ^       +-->| Runner execution |
                        |                                  |           +---------+--------+
                        |                                  |                     ||
                        +----------------------------------+---------------------++
                                                           +---------------------+
```

- Web owns presentation. Query hooks own data retrieval and cache invalidation.
  UI state stores view preferences, filters, and drafts,
  never Workflow truth.
- Web never interprets Workflow rules. The Server owns authoritative state.
  Workflow decisions stay in the Workflow context on the Server.
- The Runner owns Shell, Agent, and Git execution. Runner details stay behind
  the API.
- The Agent context owns Agent definitions and AgentJob results through the
  Agent API. The API owns Connection binding and policy.
- A committed Server decision, not a push event, is the source of truth.
- Project Settings > Workflows edits the single Project verification command
  through the dedicated Server API. The command is frozen into future
  WorkflowRuns, not stored in Web Variables.

## Navigation and Attention

Project pages use `/<projectName>/`. Application Settings and device
authorization use `/settings/<section>` and `/device`. Domain-identity paths
identify Issues and Epics by Project and number:

```text literal
/projects/{projectId}/issues/{issueNumber}
/projects/{projectId}/epics/{epicNumber}
```

WorkflowRun paths use `workflowRunId`; Issue and Epic numbers do not resolve to
separate internal IDs. The page map is:

- Board: `/<projectName>/`.
- Issues: `/<projectName>/issues`.
- Issue details and files:
  `/<projectName>/issues/<number>` and
  `/<projectName>/issues/<number>/files`.
- Agents: `/<projectName>/agents` and
  `/<projectName>/agents/<agentId>`.
- New Session: `/<projectName>/agent-sessions/new`; existing Sessions use
  `/<projectName>/sessions/<sessionId>`.
- Epics: `/<projectName>/epics` and
  `/<projectName>/epics/<number>`.
- Inbox, Insights, Activity, Runners, Workspaces, and Logs use their matching
  paths under `/<projectName>/`; details append the resource identity.
- Project Settings use `/<projectName>/settings/<section>`.
- Archived Issues use `/<projectName>/archived`.

The Board is the global attention view. It groups Backlog, In Progress, Done,
and Cancelled Issues. Cards show identity, priority, current Workflow stage,
and health. Filters narrow the view, and their URL state is shareable. Blocked
work and pending Approval Points need attention. Runner unavailability remains
a separate system warning, not an inferred Issue state.

Issue details support manual takeover with intent and ownership, execution
position, change evidence, diagnosis, and state-valid collaboration actions.
Layout may change across desktop and mobile, but meaning and available actions
must not change. Buttons follow authoritative state: Backlog offers Start,
running work offers Force Stop, an Approval Point offers Approve and offers
Request Changes only when its Definition declares Feedback Tasks, blocked work
offers Retry, Resume, Rerun, and Stop. Done offers Close and Archive.

## Agent Product Surface

Agent list and detail are management and test surfaces. Before a Session starts,
they expose avatar, name, description, active or archived state, `ready`,
`needs-setup`, or `unknown` Readiness, Runtime, model, stored Reasoning Effort,
true Variant, active and queued work, and Connection health. Runner
availability is not Agent Readiness. An offline Runner must not appear as
`needs-setup`.

The task-first composer at
`/<projectName>/agent-sessions/new` accepts Prompt, attachments, and context
references before Agent selection. Agent fields are edited before launch. New
Agent for this task creates and launches one Agent. An existing Agent uses its
stored definition. Runtime, Model, and Skills are not launch overrides. The
request uses the same Agent API as CLI and Slack, with authenticated actor and
source metadata.

A Project `defaultExecutionConfig` is the Recommended execution configuration
and requires no extra question. Adjust submits catalog-backed values as hints.
Without a Project default, a new Agent requires Runtime and Model inline.
Models, Reasoning Efforts, and true Variants come from the selected Runtime
catalog. Agent edits affect new Jobs;
an in-flight Session keeps its launch snapshot.

A successful launch opens the returned AgentSession URL. Conflicts identify the
earlier idempotency attempt. Pending launches instruct the user to retry with
the same key. Unresolved configuration identifies both repairs: choose Runtime
and Model or configure the Project default. The composer keeps the task and
context after these rejections. An empty Agent list leads with Start with a
Task; Configure an Agent remains the secondary definition-first path.

Agent details provide these concerns:

- **Definition:** identity, Instructions, Runtime, Model, Reasoning Effort,
  true Variant, Skills, concurrency, lifecycle, and Readiness. `needs-setup`
  disables launch. A missing configuration links to the Agent edit location.
  `unknown` accepts work and reports pending Runner validation. `ready` without
  capacity queues work.
- **Start session:** a real task and optional Issue, Epic, or Repository context
  create AgentJob, AgentSession, first SessionInput, and first AgentTurn.
- **Work:** AgentJob result is separate from AgentSession Activity. A failed Job
  is not a failed Session.
- **Slack:** Readiness, installation progress, Connection health, and identity
  alignment remain separate. Setup is resumable and exposes one next action.
  The panel supports owner transfer, credential rotation, revalidation, Enable,
  Disable, and Delete without compressing them into one Connected or Failed
  label.

After archival, Start session and Add Slack are unavailable. Historical Jobs,
Sessions, and Connections remain readable. Readiness does not block Add Slack
for an active Agent because Connection health and execution readiness are
separate. A Connection panel exposes setup, access policy, identity alignment,
and health. Allowlist display names and avatars are for human control only;
authorization uses stable identity, and Web never reads Slack tokens.

## AgentSession

A Workflow-origin Session emphasizes evidence and diagnosis. An Agent-launch
Session is also a fallback direct conversation client and accepts a complete
Follow-up. The first viewport shows origin, current Activity, AgentTurn inputs,
latest result, and required human action.

The page exposes model, usage, compaction, Follow-up, Stop, Compact, and Reset
under the same AgentSession. An uncertain Stop is Unknown. Compact uses the
current Runtime capability. Reset uses empty Runtime context while retaining
recorded conversation. Timeline derivation,
domain-action recognition, collapse, salience, and raw view belong to
[`session-timeline.md`](session-timeline.md). AgentJob result remains separate
from Session Activity and AgentTurn progress.

## Frontend Module Boundary

The Web application follows Feature-Sliced Design. Layering, slice exports, and
enforcement are defined once in
[`packages/web/AGENTS.md`](../packages/web/AGENTS.md). This document does not
repeat those rules.

## Presentation Preference

Use dense, scannable screens. Do not use a landing page or chat-first
application composition. A direct AgentSession may use a conversation layout
because conversation is the task on that route, but it is not the application
home.

Order primary screens by attention-first production overview, Issue execution
details, Approval and recovery, execution evidence, and Runner state. Mobile
adaptation is useful but is not a core scenario.

## Non-Goals

- Web does not replace Slack, an IDE, or another daily collaboration surface.
- Web does not create a second state store, domain rule set, or Workflow
  interpreter.
- Web does not expose Runner internals or add a domain action unavailable
  through the shared API.

## Status

The current UI provides Project board, Issue, Agent, Session, Epic, Activity,
Runner, Workspace, Logs, Settings, and Archive surfaces. Session rendering is
still a conversational message view without the TimelineItem derivation layer,
salience policy, or raw event view. Agent avatar configuration, a separate
AgentJob result view, and several Slack Connection management actions remain
unavailable.
