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

| Page | Purpose |
|---|---|
| **Board (Home)** | Default page for global progress and Issues that need attention |
| **Issue details** | Execution state, evidence, and manual operations for one Issue |
| **Issue files** | Changed files and diff for one Issue |
| **Agents** | Configure, test, and start Mohist Agents; inspect Jobs, Sessions, and external Connections |
| **AgentSession** | Understand session ownership, state, result, diagnostic evidence, and recovery |
| **Epics** | Epic list and details |
| **Activity** | Live Activity feed |
| **Logs** | System logs |
| **Settings** | Project configuration |
| **Archived** | Archived Issues |

Use the top navigation to change pages.

## Board (Home)

### Board Columns

The board groups Issues by status:

- **Backlog:** Not started.
- **In Progress:** Running in a Workflow.
- **Done:** Complete.
- **Cancelled:** Cancelled and hidden by default. Select **Show cancelled** to
  expand it.

### Card Content

Each card shows:

- Issue number and title.
- Priority from P0 through P4.
- Status such as blocked, approval, running, waiting, or drift.
- Workflow stage such as Plan, Build, Check, or Integrate.
- Health such as active, paused, or blocked.
- A pulsing running indicator while an Inline Agent works.

### Filters and Sort

The toolbar above the board provides:

- **Priority:** Select one or more P0 through P4 values.
- **Labels:** Select from a list.
- **Search:** Search by title.
- **Sort:** Sort by Priority, Number, or Updated.

The page URL contains the filters and can be shared directly.

### Needs Attention Banner

An amber **Needs attention** banner appears above the board when an Issue is
blocked or waiting for an Approval. Select it to open the relevant work.

### Runner Unavailable Banner

A warning appears above the board when Runner is disconnected and Workflow
progress is affected. An Issue can still start and wait for an available Runner.

## Issue Details

This page shows one Issue and its manual operations. Desktop uses two columns;
mobile uses one.

### Header

- Number and title.
- Priority, Workflow Stage, Health, and Running indicators.
- Labels.
- Primary Epic, with a link when present.
- Creation and update time.

### Main Area

From top to bottom:

1. **Workflow progress:** Current Workflow stage and task progress.
2. **Workflow Profile selector:** Select a Project Profile for this Issue.
3. **Diff summary:** Base and head branches, ahead and behind counts, and
   changed-file count.
4. **Branch state:** Branch status and rebase availability.
5. **Description:** Rendered Markdown from the Issue body.
6. **Workflow Definition:** Definition used by the current run.
7. **Commits:** Commits for this Issue.
8. **Comments:** Comment history and new-comment input.

### Sidebar

From top to bottom:

1. **Details:** Issue Stage, Workflow Stage, Project, and Repository.
2. **Latest artifacts:** Plan or Check artifacts such as `proposal.md` and
   `review.md`.
3. **Base Drift Detected:** Base-branch drift, when present.
4. **Workflow Blocked:** Cause and recommended recovery, when present.
5. **Convergence:** Convergence information, when present.
6. **Actions:** Current operations such as Start, Approve, Retry, and Stop.
7. **Model selector:** Model for the complete Workflow or individual stages.
8. **Prerequisites:** Dependency list and Add Prerequisite input, when present.

### Available Buttons

| State | Buttons | Meaning |
|---|---|---|
| Backlog | Start | Start the Workflow |
| Running | Running indicator and Force Stop | An Inline Agent is executing and can be stopped forcibly |
| Awaiting Approval | Approve and Reject | Decide the Approval |
| Blocked | Retry, Resume, Rerun, and Stop | The page emphasizes the recommended action available now |
| Done | Close and Archive | Handle terminal work |

## Issue Files

URL: `/issues/<number>/files`

This page lists every file changed by one Issue and includes a diff view.

## Agents

The Agent list discovers and manages Mohist Agents in the Project. Before the
user opens a Session, it shows avatar, name, description, active or archived
state, Ready, Needs setup, or Unknown Readiness, Runtime and model, active and
queued work counts, and external Connection health. Runner availability and
capacity are shown separately and cannot appear as Needs setup.

Agent details contain four continuous areas:

1. **Definition:** Avatar, name, description, Instructions, Runtime, Model,
   Variant, Skills, concurrency limit, and active or archived state. Mohist
   Runtime capabilities and Readiness drive the controls. A gap links directly
   to the corresponding field or credential setting. Needs setup disables
   launch. Unknown still accepts work and reports "Waiting for Runner
   validation." Ready without Runner capacity queues work instead of showing a
   configuration error.
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

The Agent list, edit, direct launch, and Session read surfaces exist. AgentJob,
SessionInput, AgentTurn, concurrency, and queue information are not yet fully
summarized on the Agent page. Avatar, Readiness, Slack Connection, and setup are
not implemented.

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
- Cancellation of a queued Turn or requested stop of an active Turn. An
  uncertain stop remains explicitly Unknown.
- Compact through the current backend's native capability.
- Reset so later input continues from empty Runtime context while recorded
  conversation content remains.

See [Action Contracts](actions/README.md#shared-semantics-for-agent-execution-actions)
for Compact, Reset, and missing-Session recovery. Both remain under the same
AgentSession. The page marks reset context and does not show physical Session
history. See [Agents and AgentSessions](agent-sessions.md) for Session origins
and identity.

### Implementation Gaps

The current page is a conversational message view. Tool calls have categorized
rendering, but entries do not yet use sentence phrasing, salience, or collapse
rules. Mohist domain actions are not recognized for separate presentation.
SessionInput acceptance and AgentTurn state have independent evidence but are
not part of the timeline. There is no raw event view. Missing-Session recovery
is not implemented, so the page cannot show that later work starts from empty
context. Create the implementation Issue from the timeline spec.

## Epics

URLs: `/epics` and `/epics/<id>`

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

URL: `/activity`

The live Activity feed shows fine-grained events:

- Issue state changes.
- Workflow stage progress.
- AgentSession start and end activity.
- Runner connection and disconnection.

Use it to answer what happened recently.

## Logs

URL: `/logs`

This page shows Server and Runner system logs for failure diagnosis.

## Settings

URL: `/settings/<section>`

Settings has six sections:

| Section | Purpose |
|---|---|
| **OpenCode** | OpenCode models and configuration |
| **Runtime** | Runner state and concurrent capacity |
| **Repositories** | Git Repositories associated with the Project |
| **Workflows** | Project Workflow Profile collection and default Profile |
| **Prompts** | Project Prompt editing |
| **System** | System configuration |

See [Workflow Profiles](workflow-profiles.md) and [Runner Guide](runner.md).

## Archived

URL: `/archived`

This page lists archived Issues and can unarchive them.

## Mobile

The Web UI has basic mobile adaptation, including a mobile board layout, but it
is not currently a core scenario.

Current mobile support:

- Board columns through stage tabs.
- Readable basic Issue details.

Current limitations:

- Approval buttons are small and easy to activate accidentally.
- Long Issue bodies are difficult to read on a small screen.
- Settings are not mobile-friendly.

See [Mobile PWA and Push Notifications](mobile-pwa.md) for the unimplemented
proposal for a complete mobile Workflow.

---

Implementation source: `packages/web/`.
