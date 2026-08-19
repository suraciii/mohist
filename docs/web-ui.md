# Web UI Guide

The Web UI is Mohist's fallback operations and visualization plane, not a daily
collaboration workspace. Users normally interact with a connected Mohist Agent
in Slack, an IDE, or another environment, or operate Mohist through an External
Agent. Open the Web UI to inspect global or complex state, verify execution
evidence, change configuration, or take over manually when an external entry
point is unavailable.

Fallback does not mean incomplete. Critical operations such as start, approve,
reject, recover, stop, and configure must be available. A Mohist Agent must also
be configurable and usable for direct conversation. Mohist remains the state
authority for every operation. The Web UI adds no separate state or rules.

Open `http://localhost:3456`. Every page should answer what happened, why it
happened, whether a person must act, and which actions are currently safe.

## Page Map

Most pages belong to one Project and live under `/<projectName>/`; opening `/`
redirects to the current Project. Only application Settings sections and the
device authorization confirmation page (`/device`) live outside this prefix.

- **Board (Home):** `/<projectName>/` — default page for global progress and
  Issues that need attention.
- **Issues:** `/<projectName>/issues` — the Issue list.
- **Issue details:** `/<projectName>/issues/<number>` — execution state,
  evidence, and manual operations for one Issue.
- **Issue files:** `/<projectName>/issues/<number>/files` — changed files and
  diff for one Issue.
- **Agents:** `/<projectName>/agents` and `/<projectName>/agents/<agentId>` —
  configure, test, and start Mohist Agents; inspect Jobs, Sessions, and
  external Connections. `/<projectName>/agent-sessions/new` starts a task-first
  session, and `/<projectName>/connections/<connectionId>` diagnoses one
  Connection.
- **AgentSession:** `/<projectName>/sessions/<sessionId>` — session ownership,
  state, result, diagnostic evidence, and recovery.
- **Epics:** `/<projectName>/epics` and `/<projectName>/epics/<number>` — Epic
  list and details.
- **Inbox:** `/<projectName>/inbox` — the notification history.
- **Insights:** `/<projectName>/insights` — delivery trends such as
  throughput, completion, stage duration, and cost.
- **Activity:** `/<projectName>/activity` — live Activity feed.
- **Runners:** `/<projectName>/runners` and `/<projectName>/runners/<runnerId>`
  — connected Runners and their state.
- **Workspaces:** `/<projectName>/workspaces` and
  `/<projectName>/workspaces/<name>` — Project Workspaces.
- **Logs:** `/<projectName>/logs` — system logs.
- **Settings:** `/settings/<section>` for application sections and
  `/<projectName>/settings/<section>` for Project sections.
- **Archived:** `/<projectName>/archived` — archived Issues.

Use the top navigation to change pages.

## From Global Attention to Safe Action

The Board answers which work needs attention across the Project. Issue details
answers why one item needs attention and which action is safe. Separating these
decision scopes keeps global triage scannable without hiding the evidence needed
for manual takeover.

The Board groups Issues as Backlog, In Progress, Done, or Cancelled. Cards expose
identity, priority, current Workflow stage, and health because those facts are
enough to choose what to inspect next. Priority, label, title search, and sort
controls narrow that decision; their URL state is shareable so two people can
review the same view.

**Needs attention** elevates blocked work and pending Approvals above ordinary
progress. Runner unavailability remains a separate warning because it is a
system constraint that can affect many Issues, not a new state inferred for
each Issue. An Issue may still start and wait for Runner capacity.

## Issue Details

This page supports manual takeover of one Issue. It keeps five kinds of context
together so a person does not act from a status label alone:

- **Intent and ownership:** Issue description, Project, Repository, Epic,
  labels, priority, and prerequisites.
- **Execution position:** Workflow stage, Task progress, selected Workflow
  Profile, health, and current activity.
- **Change evidence:** Definition, artifacts, commits, diff summary, and branch
  drift.
- **Diagnosis:** Blocked cause, convergence information, and the recommended
  recovery action.
- **Collaboration and control:** Comments and only the actions valid for the
  authoritative current state.

Desktop may place these groups across a main area and sidebar; mobile may stack
them. Layout does not change their meaning or the available operations.

### Available Buttons

The buttons follow the authoritative state. Backlog offers Start. Running
shows a running indicator and Force Stop, because an Inline Agent is executing
and can be stopped forcibly. Awaiting Approval offers Approve and Reject.
Blocked offers Retry, Resume, Rerun, and Stop, and the page emphasizes the
recommended action available now. Done offers Close and Archive.

## Issue Files

URL: `/<projectName>/issues/<number>/files`

This page lists every file changed by one Issue and includes a diff view.

## Agents

The Agent list discovers and manages Mohist Agents in the Project. Before the
user opens a Session, it shows avatar, name, description, active or archived
state, `ready`, `needs-setup`, or `unknown` Readiness, Runtime, model, stored
Reasoning Effort, true Variant, active and queued work counts, and external
Connection health. Runner availability and capacity are shown separately and
cannot appear as `needs-setup`.

The primary entry point for a Project without Agents is the task-first session
composer at `/<projectName>/agent-sessions/new`. Enter the prompt, attachments, and
context references first. The Agent field defaults to **New Agent for this task**;
leaving it unchanged creates and launches a new Agent through one task-first
request. Selecting an existing Agent keeps the definition-first launch path and
uses that Agent's stored execution definition.

Execution configuration follows the Project surface. A configured
`defaultExecutionConfig` is shown as the **Recommended execution configuration**
for tasks in the Project. It requires no extra question; **Adjust** opens the
catalog-backed Runtime and Model selectors, and adjusted values are submitted as
hints. When no Project default exists, the create-new path requires Runtime and
Model inline. Models, Reasoning Efforts, and true Variants come from the
selected Runtime catalog, the same catalog used by the Agent definition editor.

A successful launch opens the returned AgentSession URL. The session header links
to the created Agent detail page, where name, description, Instructions, and
Skills can be refined for later AgentJobs; an in-flight session keeps its launch
snapshot. Conflicts identify the earlier idempotency attempt, pending launches
say to retry with the same key, and unresolved execution configuration names
both repairs: choose Runtime and Model or configure the Project default. The
composer keeps the entered task and context while showing these rejections.

The Agents empty state leads with **Start with a task**. **Configure an Agent**
remains available as the secondary definition-first entry point.

Agent details contain four continuous areas:

1. **Definition:** Avatar, name, description, Instructions, Runtime, Model,
   catalog-backed Reasoning Effort, true Variant, Skills, concurrency limit,
   and active or archived state. Pi thinking levels are stored as Reasoning
   Effort, never as Variants. Mohist Runtime capabilities and Readiness drive
   the controls. Effort options come from the selected model catalog, never
   from true Variants. A gap links directly
   to the corresponding field or credential setting. `needs-setup` disables
   launch. `unknown` still accepts work and reports "Waiting for Runner
   validation." `ready` without Runner capacity queues work instead of showing
   a configuration error.
2. **Start session:** Submit a real task and optional Issue, Epic, or Repository
   context. This creates AgentJob, AgentSession, the first SessionInput, and the
   first AgentTurn. It is the normative test entry point before a Slack
   Connection is added.
3. **Work and conversations:** Show AgentJob result separately from AgentSession
   Activity. Do not present a failed Job as a failed Session.
4. **Slack:** Show Agent Readiness, Slack installation progress, Connection
   health, and identity synchronization separately. Add Slack is an
   interruptible step flow with one emphasized next step. The allowlist searches
   workspace members by name and avatar. The page supports owner transfer,
   credential rotation, revalidation, Enable, Disable, and Delete. It does not
   compress these states into one Connected or Failed label.

After an Agent is archived, Start session and Add Slack are unavailable.
Historical Jobs, Sessions, and Slack Connections remain readable. Readiness
does not block Add Slack for an active Agent because Connection health and
execution readiness are separate. An Agent edit affects only new Jobs, and the
page states this timing before save.

### Implementation Gaps

The Agent list and detail pages show Readiness, Availability, active and queued
work, configuration, direct launch, and Session history. Slack Connections have
guided setup, diagnostics, access policy management, identity facts, and
uncertain-delivery recovery.

Agent definitions have no avatar setting or avatar display. AgentJob has
no result view separate from its continuing AgentSession. The Web UI does not
expose Slack Connection owner transfer, credential rotation or revalidation,
Enable, Disable, or Delete.

## AgentSession

Open an AgentSession from the Workflow Session list on an Issue or the Session
list for a Mohist Agent.

The page shows an execution conversation from a Workflow or Mohist Agent. A
Workflow-origin Session is primarily an evidence and diagnostic view. An Agent
launch Session is also a fallback direct conversation entry and accepts a
complete Follow-up. It need not become the daily workspace, but it must not be
a read-only or incomplete debug page.

The first viewport explains the Session before showing its content:

- Why it was created and which Issue, Workflow task, or Mohist Agent work it
  serves.
- Whether it is queued, executing, idle, or unknown; which inputs belong to the
  current AgentTurn; and the most recent result.
- Whether a person must act and which operations are safe now.

### Session Timeline

The timeline presents content in occurrence order. Routine progress should be
easy to scan, while required intervention must be immediately visible.

- **Each entry reads as one sentence:** What happened, to which target, and
  with what result. Examples: "Edited `runtime.rs` (+12/-3)" and "Ran tests:
  passed." Arguments, complete output, and diff are collapsed by default.
- **Mohist operations appear as domain actions:** Commenting on an Issue,
  deciding an Approval, and advancing a Workflow appear as domain actions with
  links to their targets instead of being hidden in command output.
- **Failure is prominent; reads are quiet:** Failed execution and actions that
  need judgment remain prominent. Routine reads and searches are subdued and
  consecutive entries collapse into a summary. Failure and critical actions
  never collapse or become hidden behind collapsed entries.
- **Every input has state:** Each user message shows SessionInput acceptance and
  delivery. Several inputs can belong to one AgentTurn. Queued, executing, and
  terminal Turn phases appear in the timeline.
- **Silence is a state:** Queued work, waiting for a backend, idle, and unknown
  state are explicit. The timeline never leaves uncertainty about whether the
  Session is active.
- **Context boundaries are visible:** Compact and Reset create divider entries
  such as "Context reset." Earlier content remains visible; later work begins
  with empty context.

A raw event view shows the same underlying timeline data without presentation
processing. Use it to diagnose why an execution produced its result.

The page also supports:

- Model, usage, compaction records, and current Activity.
- Follow-up that joins the current execution while active or starts a new
  execution while idle.
- Stop of a queued or active Turn. An uncertain stop remains explicitly
  Unknown.
- Compact through the current backend's native capability.
- Reset so later input continues from empty Runtime context while recorded
  conversation content remains.

See [Action Contracts](actions/README.md#shared-semantics-for-agent-execution-actions)
for Compact, Reset, and missing-Session recovery. Both remain under the same
AgentSession. The page marks reset context and does not show physical Session
history. See [Agents and AgentSessions](agent-sessions.md) for Session origins
and identity.

### Implementation Gaps

The page is a conversational message view. Tool calls have categorized
rendering, but entries do not use sentence phrasing, salience, or collapse
rules. Mohist domain actions have no separate presentation.
SessionInput acceptance and AgentTurn state have independent evidence but are
not part of the timeline. There is no raw event view. Confirmed-missing recovery,
Compact, and Reset are implemented, and their context boundaries appear in the
timeline.

## Epics

URLs: `/<projectName>/epics` and `/<projectName>/epics/<number>`

### List

Epics are grouped by current work. Advancing, waiting to start, waiting or
blocked, and idle groups appear first. Paused, Done, and Closed each have their
own section; Done and Closed are collapsed by default. Each card shows number,
state, priority, completed and total count, and current activity or next action.
Select a card to open details.

### Details

- Header actions reflect the current lifecycle state: Start Epic, Pause,
  Resume, Mark Done, and Close Epic.
- Three summaries show **Progress**, with delivered and total count plus done
  readiness; **Next Issue**, with advancement state; and **Current Activity**,
  with linked Issues grouped by health.
- Linked Issues below can be added or removed. Each has a Start action. A Graph
  tab shows dependencies.

See [Planning with Epics](epics.md) for action availability and transitions.

## Activity

URL: `/<projectName>/activity`

The live Activity feed shows fine-grained events:

- Issue state changes.
- Workflow stage progress.
- AgentSession start and end activity.
- Runner connection and disconnection.

Use it to answer what happened recently.

## Logs

URL: `/<projectName>/logs`

This page shows Server and Runner system logs for failure diagnosis.

## Settings

URL: `/settings/<section>` for application sections and
`/<projectName>/settings/<section>` for Project sections.

Application sections:

- **Coder Agent:** The coder-agent model, with per-stage overrides.
- **Runtime:** How Mohist schedules external coder agent sessions.
- **System:** Logging, runtime identity, and local-source update status.
- **Preferences:** User preferences and read-only reference information.

Project sections:

- **Repositories:** Git Repositories associated with the Project.
- **Workflows:** The Workflow new Issues inherit, plus the read-only system
  catalog.
- **Templates:** Project Prompt templates, which can override system templates
  or add project-unique keys.
- **Label catalog:** The labels the Project suggests for Issues. The catalog
  is advisory; edits do not change existing Issue labels.
- **Inbox:** Which notification kinds the Web inbox receives.

See [Workflow Profiles](workflow-profiles.md) and [Runner Guide](runner.md).

## Archived

URL: `/<projectName>/archived`

This page lists archived Issues and can unarchive them.

## Mobile

The Web UI has basic mobile adaptation, including a mobile board layout, but it
is not a core scenario.

Current mobile support:

- Board columns through stage tabs.
- Readable basic Issue details.

See [Mobile PWA and Push Notifications](../design/decisions/mobile-pwa.md) for
the open decision record on a complete mobile Workflow.

---

Implementation source: `packages/web/`.
