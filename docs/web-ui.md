# Web UI Guide

The Web UI is Mohist's fallback operations and visualization plane. Users
normally work in Slack, an IDE, another interaction surface, or through an
External Agent. Open the Web UI to inspect complex state, verify evidence,
change configuration, or take over when another entry point is unavailable.

## Product Commitments

- The Web UI remains complete for critical operations: start, approve, request
  changes, recover, stop, and configure.
- A Mohist Agent can be configured, launched, and continued directly in the Web
  UI. A Slack Connection is not required.
- Mohist remains the authority for state. The Web UI adds no second state model
  or Workflow rule.
- Every page explains what happened, why it happened, whether a person must
  act, and which actions are safe now.
- The Web UI keeps Agent Readiness, execution availability, Job state, Session
  Activity, and Connection health separate.
- User actions use the same Server-owned operations as the CLI and other
  interaction surfaces.

Open `http://localhost:3456`.

## Page Map

Most pages belong to one Project and use the `/<projectName>/` prefix. Opening
`/` redirects to the current Project. Application Settings and the device
authorization confirmation page (`/device`) are outside this prefix.

- **Board (Home):** `/<projectName>/` shows global progress and Issues that
  need attention.
- **Issues:** `/<projectName>/issues` lists the Project Issues.
- **Issue details:** `/<projectName>/issues/<number>` shows execution state,
  evidence, and safe manual operations for one Issue.
- **Issue files:** `/<projectName>/issues/<number>/files` shows changed files
  and the diff for one Issue.
- **Agents:** `/<projectName>/agents` and
  `/<projectName>/agents/<agentId>` configure, test, and start Mohist Agents,
  and show Jobs, Sessions, and external Connections.
- **New AgentSession:** `/<projectName>/agent-sessions/new` starts a task-first
  Session.
- **Connection:** `/<projectName>/connections/<connectionId>` diagnoses one
  external Connection.
- **AgentSession:** `/<projectName>/sessions/<sessionId>` shows ownership,
  state, result, evidence, and recovery.
- **Epics:** `/<projectName>/epics` and `/<projectName>/epics/<number>` show
  Epic lists and details.
- **Inbox:** `/<projectName>/inbox` shows notification history.
- **Insights:** `/<projectName>/insights` shows delivery trends such as
  throughput, completion, stage duration, and cost.
- **Activity:** `/<projectName>/activity` shows the live Activity feed.
- **Runners:** `/<projectName>/runners` and
  `/<projectName>/runners/<runnerId>` show connected Runners and their state.
- **Workspaces:** `/<projectName>/workspaces` and
  `/<projectName>/workspaces/<name>` show Project Workspaces.
- **Logs:** `/<projectName>/logs` shows system logs.
- **Settings:** `/settings/<section>` shows application settings and
  `/<projectName>/settings/<section>` shows Project settings.
- **Archived:** `/<projectName>/archived` lists archived Issues.

Use the top navigation to change pages.

## Board and Issue Review

The Board answers which work needs attention across the Project. Issue details
answers why one item needs attention and which action is safe. This separation
keeps triage scannable without hiding evidence needed for manual takeover.

The Board groups Issues as Backlog, In Progress, Done, or Cancelled. Cards show
identity, priority, current Workflow stage, and health. Priority, label, title
search, and sort controls narrow the view. URL state is shareable so people can
review the same view.

Needs attention elevates blocked work and pending Approval Points. Runner
unavailability remains a separate warning because it can affect many Issues.
An Issue may still start and wait for Runner capacity.

Issue details keeps these decisions together:

- **Intent and ownership:** Issue description, Project, Repository, Epic,
  labels, priority, and prerequisites.
- **Execution position:** Workflow stage, Task progress, selected Workflow
  Profile, health, and current Activity.
- **Change evidence:** Definition, artifacts, commits, diff summary, and branch
  drift.
- **Diagnosis:** blocked cause, convergence information, and the recommended
  recovery action.
- **Collaboration and control:** comments and only the actions valid for the
  current state.

Desktop may place these groups in a main area and sidebar. Mobile may stack
them. Layout does not change their meaning or available operations.

### Available Buttons

Buttons follow authoritative state:

- Backlog offers **Start**.
- Running shows a running indicator and **Force Stop**.
- An Approval Point offers **Approve** and, when the Definition declares
  Feedback Tasks, **Request Changes**.
- Blocked offers **Retry**, **Resume**, **Rerun**, and **Stop**, with the
  recommended action emphasized.
- Done offers **Close** and **Archive**.

## Issue Files

URL: `/<projectName>/issues/<number>/files`

This page lists every file changed by one Issue and includes a diff view.

## Agents

The Agent list and detail page are the Project's management and test surface.
Before a Session starts, the list shows avatar, name, description, active or
archived state, Readiness, Runtime, model, Reasoning Effort, true Variant,
Skills, active and queued work counts, and external Connection health. Runner
availability and capacity remain separate from Agent Readiness.

For a Project without Agents, the primary entry point is
`/<projectName>/agent-sessions/new`. Enter the prompt, attachments, and context
references first. The Agent field defaults to **New Agent for this task**. Leave
it unchanged to create and launch a new Agent through one task-first request.
Select an existing Agent to use its stored execution definition.

A Project `defaultExecutionConfig` appears as the **Recommended execution
configuration**. It needs no extra question. **Adjust** opens the catalog-backed
Runtime and Model selectors and submits adjusted values as hints. Without a
Project default, the create-new path asks for Runtime and Model inline. Models,
Reasoning Efforts, and true Variants come from the selected Runtime catalog.

A successful launch opens the returned AgentSession URL. The Session header
links to the Agent detail page, where name, description, Instructions, and
Skills can be refined for later AgentJobs. An in-flight Session keeps its
launch snapshot. Conflicts identify the earlier idempotency attempt. Pending
launches ask the user to retry with the same key. Unresolved execution
configuration names the repair: choose Runtime and Model or configure the
Project default. The composer keeps the task and context while showing these
rejections.

The Agents empty state leads with **Start with a task**. **Configure an Agent**
remains the secondary definition-first entry point.

Agent details contain four areas:

1. **Definition:** Avatar, name, description, Instructions, Runtime, Model,
   catalog-backed Reasoning Effort, true Variant, Skills, concurrency limit,
   and active or archived state. Pi thinking levels are stored as Reasoning
   Effort, never as Variants. A configuration gap links to the matching field or
   credential setting. `needs-setup` disables launch. `unknown` accepts work
   and reports that it is waiting for Runner validation. `ready` without Runner
   capacity queues work instead of showing a configuration error.
2. **Start session:** Submit a real task and optional Issue, Epic, or Repository
   context. This creates the AgentJob, AgentSession, first SessionInput, and
   first AgentTurn. It is the test entry point before a Slack Connection is
   added.
3. **Work and conversations:** Show AgentJob result separately from
   AgentSession Activity. Do not present a failed Job as a failed Session.
4. **Slack:** Show Agent Readiness, installation progress, Connection health,
   and identity synchronization separately. Add Slack is an interruptible step
   flow with one emphasized next step. The page supports owner transfer,
   credential rotation, revalidation, Enable, Disable, and Delete.

After an Agent is archived, Start session and Add Slack are unavailable.
Historical Jobs, Sessions, and Slack Connections remain readable. Readiness does
not block Add Slack for an active Agent. An Agent edit affects only new Jobs,
and the page states this timing before save.

## AgentSession

Open an AgentSession from the Workflow Session list on an Issue or from the
Session list for a Mohist Agent.

A Workflow-origin Session is primarily an evidence and diagnostic view. An
Agent launch Session is also a fallback direct conversation entry and provides
a complete Follow-up composer. The page must not become a read-only or
incomplete debug page.

The first viewport explains:

- why the Session was created and which Issue, Workflow Task, or Mohist Agent
  work it serves;
- whether it is queued, executing, idle, or unknown, which inputs belong to the
  current AgentTurn, and the most recent result;
- whether a person must act and which operations are safe now.

### Session Timeline

The timeline presents content in occurrence order. Routine progress must be
scannable, while required intervention must be visible immediately.

- Each entry states what happened, to which target, and with what result.
  Arguments, complete output, and diffs are collapsed by default.
- Mohist operations appear as domain actions with links to their targets.
- Failed execution and actions that need judgment remain prominent. Routine
  reads and searches are subdued. Consecutive routine entries may collapse into
  a summary, but failures and critical actions remain visible.
- Each input shows SessionInput acceptance and delivery. Several inputs may
  belong to one AgentTurn. Queued, executing, and terminal Turn phases appear.
- Silence is explicit. Queued work, waiting for a backend, idle, and unknown
  state never look like missing data.
- Compact and Reset create visible divider entries. Earlier content remains
  visible, and later work begins with empty context.

A raw event view shows the same underlying timeline data without presentation
processing and helps diagnose a result.

The page also supports model, usage, compaction records, current Activity,
Follow-up, Stop of a queued or active Turn, Compact, and Reset. Follow-up joins
the current execution while active or starts a new execution while idle. An
uncertain Stop remains Unknown. Compact uses the current backend's native
capability. Reset keeps the same AgentSession and makes later input use empty
Runtime context without showing physical Session history.

See [Action Contracts](actions/README.md#shared-semantics-for-agent-execution-actions)
for Compact, Reset, and missing-Session recovery. See
[Agents and AgentSessions](agent-sessions.md) for Session origins and identity.

## Epics

URLs: `/<projectName>/epics` and `/<projectName>/epics/<number>`

### List

Epics are grouped by current work. Advancing, waiting to start, waiting or
blocked, and idle groups appear first. Paused, Done, and Closed each have their
own section. Done and Closed are collapsed by default. Each card shows number,
state, priority, completed and total count, and current Activity or next action.
Select a card to open details.

### Details

- Header actions reflect lifecycle state: **Start Epic**, **Pause**,
  **Resume**, **Mark Done**, and **Close Epic**.
- Three summaries show **Progress**, **Next Issue**, and **Current Activity**.
  Linked Issues are grouped by health.
- Linked Issues can be added or removed. Each has a Start action. A Graph tab
  shows dependencies.

See [Planning with Epics](epics.md) for action availability and transitions.

## Activity

URL: `/<projectName>/activity`

The live Activity feed shows recent Issue state changes, Workflow stage
progress, AgentSession start and end activity, and Runner connection changes.
Use it to answer what happened recently.

## Logs

URL: `/<projectName>/logs`

This page shows Server and Runner system logs for failure diagnosis.

## Settings

URL: `/settings/<section>` for application sections and
`/<projectName>/settings/<section>` for Project sections.

Application sections:

- **Coder Agent:** the coder-agent model, with per-stage overrides.
- **Runtime:** how Mohist schedules external coder Agent Sessions.
- **System:** logging, Runtime identity, and local-source update status.
- **Preferences:** user preferences and read-only reference information.

Project sections:

- **Repositories:** Git Repositories associated with the Project.
- **Workflows:** the Workflow new Issues inherit, the Project verification
  command used by built-in Profiles, and the read-only system catalog.
- **Templates:** Project Prompt templates, which can override system templates
  or add Project-unique keys.
- **Label catalog:** labels the Project suggests for Issues. The catalog is
  advisory and does not change existing Issue labels.
- **Inbox:** notification kinds the Web inbox receives.

See [Workflow Profiles](workflow-profiles.md) and [Runner Guide](runner.md).

## Archived

URL: `/<projectName>/archived`

This page lists archived Issues and can unarchive them.

## Mobile

The Web UI has basic mobile adaptation, including a mobile Board layout, but
mobile is not a core scenario. Current support covers Board columns through
stage tabs and readable basic Issue details.

Implementation source: `packages/web/`.

## Implementation Gaps

The implementation currently has these gaps:

- Agent definitions have no avatar setting or avatar display.
- AgentJob has no result view separate from its continuing AgentSession.
- The Web UI does not expose Slack Connection owner transfer, credential
  rotation or revalidation, Enable, Disable, or Delete.
- Tool entries do not yet use the full sentence, salience, or collapse rules.
- Mohist domain actions have no separate presentation.
- SessionInput acceptance and AgentTurn state have independent evidence but are
  not part of the timeline.
- There is no raw event view.

Confirmed-missing recovery, Compact, and Reset are implemented, and their
context boundaries appear in the timeline.
