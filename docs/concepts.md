# Core Concepts

Mohist advances product work through Issues, Workflows, Agents, and Runners.
A Project isolates one product and its execution resources. This document gives
the mental model that connects those concepts.

## Product Commitments

- A Project isolates its Issues, configuration, repositories, and execution data.
- An Issue is one unit of work with one Project-scoped identity and one target Repository.
- A Workflow advances a ready Issue from Plan to Done through its configured stages.
- An Epic groups Issues for one product goal and supplies one ready Issue at a time.
- A Mohist Agent has one reusable configuration and uses the same AgentJob launch boundary from every entry point.
- An AgentSession records continuing conversation context. An AgentJob owns each top-level execution.
- A WorkflowRun owns orchestration state and binds one complete Workflow Definition when it starts.
- An Agent Connection exposes one Agent in an external interaction location without copying its configuration.
- An Approval Point waits for Approve or Request Changes. Feedback Tasks apply requested changes before the same Approval Point is shown again.
- A Runner executes resolved Agent work and reports facts. It does not decide product state.

## Project

A Project is one real product's scope and execution boundary. It is not the
user's chat or collaboration workspace.

- Each Project declares one or more git **Repositories**. Each Repository has a
  resource name and base branch, and one Repository is the default.
- Each Issue has one target Repository. Without an explicit target, it uses the
  Project default.
- Only one Project can be active in the CLI at a time. Use `mo project use` to
  switch it.
- Data is fully isolated between Projects.

```bash
mo project create my-app --path /path/to/repo --verification-command "npm run verify"   # Register the repository and its verification command.
mo project use my-app
mo runner status   # Show Runners and shared execution capacity.
```

A Project may declare separate Repositories for a product server and Web UI.
Each Issue still chooses one target Repository. See [Repositories](repositories.md).

Execution occurs in a **Workspace**. An Issue gets a clean Workspace when it
starts. An interaction entry point, such as a Slack channel, has a persistent
Workspace. See [Workspace](workspaces.md).

## Issue

An Issue is one unit of work that can enter the production line. It contains:

- A title and body that describe the requirement.
- A priority and free-text labels.
- One target Repository.
- Stage, health, and `approvalState` after it enters a Workflow.
- Plan and review artifacts after a Workflow produces them.

When one requirement spans multiple Repositories, split it into child Issues.
The parent Issue tracks the complete requirement, and each child Issue runs its
own Workflow. See [Composite Issues and Child Issues](composite-issues.md).

See [Issue Management](issues.md).

## Workflow

A Workflow is the production line that moves a ready Issue from an idea to
merged code. Draft and Backlog remain outside the Workflow so requirement
readiness and execution state stay separate. The default Workflow has five
stages: Plan, Build, Check, Integrate, and Done.

- **Plan**: A configured Mohist Agent understands the requirement and produces
  `PLANS/PLAN.md`, `PLANS/DESIGN.md`, and the executable task list.
- **Build**: A configured Mohist Agent writes code and runs tests according to
  the task list.
- **Check**: A configured Mohist Agent reviews the output.
- **Integrate**: Mohist merges the branch into the base branch.

Plan and Check enter an **Approval Point** by default. The Workflow waits for
Approve or Request Changes before it continues. See [Approval Point](#approval-point).

### Workflow Profile

A Workflow Profile defines the stages, task order, Agent for each executable
task, checks, Feedback Tasks, recovery, and Approval Points. A Project may have
multiple Profiles and selects one default Profile. An Issue inherits that
Profile or selects another Profile in the same Project.

A Profile does not copy Agent configuration, Variables, or Prompt bodies.
Project, Issue, and Run Variables merge by scope. Prompts are configured only
in the Project.

An Issue may select no Workflow with `mo issue create --no-workflow`. It then
runs no production line. Starting moves it directly to in progress, and
completion uses `mo issue done`, `mo issue close`, or the linked GitHub Issue's
lifecycle. See [GitHub](github.md).

The built-in Profiles are:

- `mohist/local`: The complete five-stage process with local integration. This
  is the default.
- `mohist/github-pr`: Uses a GitHub pull request in the Integrate stage.

See [Workflow Profile](workflow-profiles.md).

## Epic

An Epic organizes Issues around one product goal and supplies ready work to the
production line. A new Epic is `idle`. **Start** begins automatic advancement.
After one linked Issue finishes, a running Epic starts the next Issue that can
advance.

See [Planning with Epics](epics.md) for the complete lifecycle.

## External Agent, Mohist Agent, and Agent Connection

An **External Agent** interacts with the user outside Mohist. It may run in
Slack, an IDE, or another Agent host. It uses the Mohist Skill and `mo` to
query, delegate, or operate Mohist. It is not a Mohist resource, and Mohist
does not schedule it.

A **Mohist Agent** is a reusable Agent resource in one Project. It has a stable
ID, name, Instructions, Skills, and execution configuration. A Workflow task,
the Web UI, CLI, Agent Connection, event routing, or comment mention starts it
through the same AgentJob launch boundary. Workflow tasks do not select
Runtime-specific Actions or create anonymous Agent capability.

An **Agent Connection** exposes one Mohist Agent in an external interaction
location. A Slack Agent Connection lets a Slack Bot represent a specified
Agent. Slack receives messages and presents replies. The Agent owns
understanding, execution, and the AgentSession. The Connection does not copy
Agent configuration and cannot switch to another Agent within one conversation.

Slack has two App identities:

- The **Mohist App** is the management entry point installed once in a Slack
  Workspace. It establishes the Workspace connection and manages Agent
  Connections. It is not a business Agent or an Agent App.
- An **Agent App** is an execution entry point. Each connected Agent has a
  separate Slack App and Bot identity that accepts work and returns results.

Management operations and work tasks use separate identities. One identity
does not send on behalf of the other.

An **AgentSession** records messages, context, usage, Activity, and the current
Runtime Session for a conversation. An **AgentJob** owns every top-level Agent
execution, including a `mohist/agent` Workflow task. A **WorkflowRun** owns
orchestration state, its complete bound Definition, and the AgentJob reference.
Subsequent input continues the same AgentSession but does not rewrite the
AgentJob. Each accepted input is a **SessionInput**. One continuous Runtime
processing period is an **AgentTurn**. One AgentTurn can process multiple
SessionInputs in order.

Messages, execution, and work results therefore do not share one state. See
[Agents and AgentSessions](agent-sessions.md) and [Slack](slack.md).

## Approval Point

An Approval Point waits for a decision about the current Stage output:

- **Approve** completes the Stage and lets the Workflow advance.
- **Request Changes** records Approval Feedback and runs the configured
  Feedback Tasks. It is nonterminal and does not fail the Approval Point.

Request Changes is available only when the active WorkflowRun's complete bound
Workflow Definition contains a non-empty `approval.feedback.tasks` list.
WorkflowRun validates the request, the current Approval Point, and the complete
Feedback Task list before recording the decision. A validation failure changes
no Approval Point, Stage, Approval Feedback, Task, Check, WorkflowRun status,
event, revision, or other Run state.

There is no separate Reject decision. Mohist does not create a default
Feedback Task or add an Agent, Prompt, Session, timeout, or publication step.
Stop is terminal when the Run must not continue.

Mohist applies Approval Feedback in this order:

1. Run the declared Feedback Tasks in order.
2. Run the current Stage Checks again.
3. Return to the same Approval Point.

The original Stage Tasks do not run again. An approver may select Request
Changes more than once.

Each `mohist/agent` Task names its Agent and may name a Session. Mohist reuses a
named Session only when the Agent and Workspace are the same. An omitted
Session requests no named reuse. AgentJob owns execution, AgentSession owns
conversation continuity, and WorkflowRun owns Approval Point state.

The complete bound Definition controls the Stages, Approval Feedback, and
recovery behavior for the complete WorkflowRun. Later Profile edits affect only
future WorkflowRuns. Variables and Prompt bodies keep their separate
dispatch-time behavior. See [Workflow Profile](workflow-profiles.md) and
[Workflow Definition Reference](workflow-definition.md).

An approver can be a person, a Mohist Agent, a script, or external automation.
All use the same Approve and Request Changes actions. Authentication identity
determines attribution for decisions and comments. `--display-name` is only a
presentation alias. See [Authentication and Access](auth.md).

## Skill

A **Skill** is a reusable description of an Agent capability. An External Agent
can install a Mohist-distributed Skill to understand Mohist domain actions and
operating boundaries. A Mohist Agent can select Skills in its configuration.
An entry point cannot add or remove Skills for that Agent.

Mohist distributes Skills for domain actions such as exploring a requirement,
creating Issues and Epics, and operating Mohist from an External Agent.
An External Agent loads Skills as needed, sends intent through `mo`, and returns
state or results to its original conversation. A Mohist Agent's Skills are part
of its configuration and are fixed for the unit of work when it starts. See
[Skills](skills.md).

## How the Concepts Fit Together

```text diagram
      +-------+    +----------------+
      | Slack |    | External Agent |
      +---+---+    +--------+-------+
          |                 |
          v                 v
+------------------+    +-------+
| Agent Connection |    | Skill |
+---------+--------+    +---+---+
          +--------+--------+
                   v
           +--------------+
           | Mohist Agent |
           +-------+------+
                   |
                   v
              +---------+
              | Project |
              +---------+
```

Within one Project, an Epic supplies Issues. Each Issue moves through its
Workflow. Agent-backed tasks run a Mohist Agent, and mechanical Actions remain
Workflow orchestration:

```text diagram
                      +---------+
                      | Project +------------------+
                      +----+----+                  |
                           |                       |
                           v                       |
                       +------+                    |
                       | Epic |                    |
                       +---+--+                    |
                           |                       |
                           v                       |
                       +-------+                   |
                       | Issue |<------------------+
                       +---+---+
                           |
                           v
              + Workflow (mohist/local) +
              |        +------+         |
              |        | Plan |         |
              |        +---+--+         |
              |            |            |
              |            v            |
              |        +-------+        |
              |        | Build |        |
              |        +---+---+        |
              |            |            |
              |            v            |
              |        +-------+        |
              |        | Check |        |
              |        +---+---+        |
              |            |            |
              |            v            |
              |      +-----------+      |
              |      | Integrate |      |
              |      +-----+-----+      |
              |            |            |
              |            v            |
              |        +------+         |
              |        | Done |         |
              |        +------+         |
              +------------+------------+
              +------------+------------+
              v                         v
      +--------------+         +-----------------+
      | Mohist Agent |         | User repository |
      +-------+------+         +--------+--------+
              |                         |
              v                         v
 +-------------------------+  +------------------+
 | AgentJob + AgentSession |  | Product advances |
 +-------------------------+  +------------------+
```

Keep one mental model: a Project is the product and execution boundary; an Epic
owns a goal and supplies work; an Issue is the workpiece; a Workflow is the
production line; Mohist Agents are the workers; AgentJob owns each execution;
and AgentSession records continuing conversation. The Web UI is the fallback
operations and visualization plane.

## Implementation Gaps

Current WorkflowRun source, status, and recovery reads still consult live
Profile data in some paths. Current Profile update guards still inspect active
Runs.

---

Implementation source: See the domain decomposition in
[`design/domain-analysis.md`](../design/domain-analysis.md).
