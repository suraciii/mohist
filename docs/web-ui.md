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
  `/<projectName>/agents/<agentId>` list the Project's effective named Agents:
  stored Project Agents plus unshadowed built-in Workflow Agents, each with an
  origin marker. The pages configure, test, and start them, and show Jobs,
  Sessions, and external Connections.
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
- **Runners:** `/runners` and `/runners/<runnerId>` show the Server-global
  Runner inventory and each Runner's current state. These routes work without a
  selected Project.
- **Workspaces:** `/<projectName>/workspaces` and
  `/<projectName>/workspaces/<name>` show Project Workspaces.
- **Logs:** `/<projectName>/logs` shows system logs.
- **Settings:** `/settings/<section>` shows application settings and
  `/<projectName>/settings/<section>` shows Project settings.
- **Archived:** `/<projectName>/archived` lists archived Issues.

Use the top navigation to change pages.

## Dashboard execution summary

The Dashboard must distinguish current execution from old records that need
review. Its Runner headline and capacity use the
[fleet summary](runner.md#fleet-summary); Session labels and counts use
[current execution evidence](agent-sessions.md#current-execution-evidence).
The sidebar and board warnings must preserve those same meanings.

- Show queued, confirmed running, and **Needs verification** work separately.
  Do not infer current execution from a nonempty historical Session list or
  describe missing telemetry as a confirmed report of active Runner work.
- Every work card must identify its real target. A Session without an Issue
  link opens `/<projectName>/sessions/<sessionId>` and shows its Agent or
  Session label. Never manufacture Issue number zero or an `/issues/0` link.
  A genuine Issue card keeps its Issue link; Session evidence on it can link
  separately to the Session.
- A record without enough identity to build a valid link shows a non-clickable
  **Target unavailable** label. It must not fabricate an identifier.
- If a linked Issue or Session cannot be found, its page must show an explicit
  not-found message and a return link to the relevant list. Permission and
  network failures must remain distinguishable from a missing object; none
  may leave a blank detail pane.

A Session last observed executing seventeen days ago, with no current owner
confirmation, must appear under **Needs verification**, not as confirmed
running. If it has no Issue attribution, its card must open the Session even
when the Runner currently reports zero occupied slots.

## Board and Issue Review

The Board answers which work needs attention across the Project. Issue details
answers why one item needs attention and which action is safe. This separation
keeps triage scannable without hiding evidence needed for manual takeover.

The Board groups Issues as Backlog, In Progress, Done, or Cancelled. Cards show
identity, priority, current Workflow stage, and health. Priority, label, title
search, and sort controls narrow the view. URL state is shareable so people can
review the same view.

Needs attention elevates blocked work and pending Approval Points. The Board
also shows a separate warning from the Server-global Runner projection because
presence, control, admission, drain, and capacity can affect many Projects. An
Issue may still start and wait for Runner capacity.

Issue details keeps these decisions together:

- **Intent and ownership:** Issue description, Project, Repository, Epic,
  labels, priority, and prerequisites.
- **Execution position:** Workflow stage, Task progress, selected Workflow
  Profile, the named Agents responsible for execution with their effective
  configuration, health, and current Activity. Model configuration routes to the
  Agents page; the Issue page has no model selector.
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
The list shows the Project's effective named Agents: stored Project Agents plus
the unshadowed built-in Workflow Agents (`mohist/planner`, `mohist/builder`,
`mohist/reviewer`). Each entry carries its origin, `built-in` or `project`; a
Project Agent that shadows a built-in name is marked as overriding it. The
application-owned `mohist-slack` manager never appears in this list.

Before a Session starts, the list shows avatar, name, description, origin,
active or archived state, Readiness, Runtime, model, Reasoning Effort, true
Variant, Skills, active and queued work counts, and external Connection health.
An unset Model is presented as **Runtime default**. Runner availability and
capacity remain separate from Agent Readiness.

A built-in Agent's detail shows its Mohist-owned definition and its effective
execution configuration. **Customize** materializes a same-name Project Agent
from the built-in definition in one Server operation and opens it for editing;
the client never copies built-in text. Customize fails with a named conflict
when an active same-name Agent already exists; that Agent is edited directly.
An archived same-name Agent appears as the shadowing entry with its state and an
explicit repair path; it never silently falls back to the built-in.

For a Project without stored Agents, the primary entry point is
`/<projectName>/agent-sessions/new`. Enter the prompt, attachments, and context
references first. The Agent field defaults to **New Agent for this task**. Leave
it unchanged to create and launch a new Agent through one task-first request.
Select an existing Agent to use its stored execution definition. A new Agent
selects Runtime, Model, Reasoning Effort, and Variant inline. The Runtime control
starts at `pi` and the Model control at **Runtime default** (unset). Models,
Reasoning Efforts, and true Variants come from the selected Runtime catalog. No
Project value prefills these controls.

A task-first Agent carries only the values the user supplies; unset fields fall
to Runtime behavior. A value the catalog cannot verify is saved and shown as
**not yet verified**; a complete catalog that proves incompatibility rejects the
write. No state silently substitutes another Runtime, model, effort, or variant.
See
[Agents and AgentSessions](agent-sessions.md#model-effort-and-variant-selection).

A successful launch opens the returned AgentSession URL. The Session header
links to the Agent detail page, where name, description, Instructions, and
Skills can be refined for later AgentJobs. An in-flight Session keeps its
launch snapshot. Conflicts identify the earlier idempotency attempt. Pending
launches ask the user to retry with the same key. Unresolved execution
configuration names the repair on the Agent definition. The composer keeps the
task and context while showing these rejections.

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

Summary shows the latest saved conversation and execution record. It updates
as Mohist saves progress, rather than displaying each arriving text fragment.
New saved progress and accepted or queued Input must appear without a manual
reload.

Summary omits recognized internal setup and recalled-memory blocks from input,
reply, and reasoning text. It keeps the surrounding conversation, including
every ordinary paragraph. An omitted block must not leave an empty message or
pretend that an attachment was sent. Unknown or unfinished blocks remain
visible. This is a readability rule, not a guarantee that sensitive content is
hidden.

Select **Raw** to inspect the original text and event payloads from the same
record, including the internal blocks omitted by Summary. Switching back to
Summary must not display text retained from Raw.

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
progress, AgentSession start and end activity, Runner connection changes, and
current Runner status evidence. Runner links always use `/runners/<runnerId>`
and remain valid independently of the selected Project. Status evidence keeps
offline, disconnected, blocked, draining, full-capacity, and active-work facts
separate; an empty active-work list is not an idle or health verdict.

## Runners

URLs: `/runners` and `/runners/<runnerId>`.

Runner status is an application-scoped projection assembled by Server from
Runner definitions, current presence and control observations, credentials,
drain fences, and active Workflow and AgentJob owner ledgers. The list and
detail pages do not resolve a Project or apply a Project filter. A known offline
Runner remains visible with its configured capacity even when live build and
Runtime details are unavailable.

Each row exposes identity, presence (`online`, `stale`, or `offline`), control
(`connected` or `disconnected`), admission (`ready` or `blocked`) with stable
reason codes, per-Runtime readiness and catalog facts, used/total capacity,
drain, active owner rows, and Server-provided next actions. Workflow and
AgentJob owners remain distinct. Capacity and active-work counts are shown
independently from admission, and secondary summaries use admission, drain,
capacity, and active-work language rather than `idle` or `busy`.

The displayed `observedAt` value identifies one observational read. Sources may
change while the projection is assembled, and a later claim or other mutation
remains authoritative. The page never turns a status read into a recovery
command; it renders only Server-provided install, start, re-enroll, wait, or
correction actions. `mo runner status` reads this remote projection, while
`mo service status runner` reads only the local service-manager unit.

## Logs

URL: `/<projectName>/logs`

This page shows Server and Runner system logs for failure diagnosis.

## Settings

URL: `/settings/<section>` for application sections and
`/<projectName>/settings/<section>` for Project sections.

Application sections:

- **Scheduling:** timeouts and concurrency for Agent execution.
- **System:** logging, Runtime identity, and local-source update status.
- **Preferences:** user preferences and read-only reference information.

Project sections:

- **Repositories:** Git Repositories associated with the Project.
- **Workflows:** the Workflow new Issues inherit, the named Agents its Tasks use
  with their effective configuration, the Project verification command used by
  built-in Profiles, and the read-only system catalog. Model configuration
  routes to the Agents page.
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

- The Dashboard can present an old active Session as current execution and
  link an unattributed Session to Issue zero. Missing Issue details can leave
  the content pane blank.
- Agent definitions have no avatar setting or avatar display.
- Built-in Workflow Agents are not shown in the Agent list or detail page, and
  Customize does not yet materialize a Project override. Project Workflows and
  Issue details do not yet show the named Agents responsible for execution.
- The application Coder Agent page, the Issue model selector, and Stage model
  overrides still exist, and the application **Runtime** settings section is not
  yet named **Scheduling**. The new-Agent path still prefills and explains a
  Project-level execution default.
- AgentJob has no result view separate from its continuing AgentSession.
- The Web UI does not expose Slack Connection owner transfer, credential
  rotation or revalidation, Enable, Disable, or Delete.
- Tool entries do not yet use the full sentence, salience, or collapse rules.

Confirmed-missing recovery, Compact, and Reset are implemented, and their
context boundaries appear in the timeline.
