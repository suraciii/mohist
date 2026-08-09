# Core Concepts

These concepts explain how Mohist continuously advances work to Done.

## In One Paragraph

> You usually stay in your existing workspace. An **Agent Connection** can
> expose a configured **Mohist Agent** there, or a third-party **External Agent**
> can operate Mohist through a **Skill**. Within a **Project**, Mohist records a
> product goal as an **Issue** and uses a **Workflow** to advance it to Done.
> Multiple related Issues form an **Epic**. An **Inline Agent** executes a task,
> while a **Mohist Agent** acts as a stable proxy that accepts delegated work or
> responds to events.

## Project

A Project corresponds to one real product. It is the product scope and execution
boundary in Mohist. It is not the user's chat or collaboration workspace.

- Each Project declares one or more git **repositories** as execution resources.
  Each repository has a resource name and base branch, and one is the default.
- Each Issue in a Project has a **target repository**. Without an explicit
  target, it uses the default repository.
- Only one Project can be active at a time. Use `mo project use` to switch the
  active CLI Project.
- Data is fully isolated between Projects.

```bash
mo project create my-app --path /path/to/repo   # Register this as the default repository.
mo project use my-app
mo runner status   # Show Runners and shared execution capacity.
```

**Multiple-Project example**: Create one Project for side project A and another
for side project B. Switch between them as necessary.

**Multiple-repository example**: If the product server and Web UI are separate
repositories, declare both in one Project. Each Issue routes to its target
repository. See [Repositories](repositories.md).

Execution within a Project occurs in a **Workspace**. An Issue gets a clean
Workspace when it starts. An interaction entry point, such as a Slack channel,
has a persistent Workspace. See [Workspace](workspaces.md).

## Issue

An Issue is one unit of work that can enter the production line.

- A title and body that describe the requirement
- Priority from p0, the highest, to p4, the lowest
- Free-text labels
- A target repository in which the work executes; see
  [Repositories](repositories.md)
- State such as stage, health, and `approvalState` after it enters a Workflow
- A complete set of OpenSpec artifacts after completion: proposal, design,
  specs, tasks, and review

If one requirement crosses multiple repositories, split one Issue into
**sub-issues**. The parent Issue tracks the complete requirement, and each
sub-issue moves through its own Workflow. See
[Composite Issues and Sub-issues](sub-issues.md).

**Key Issue properties**:

| Property | Meaning |
|---|---|
| `status` | backlog / in-progress / done / cancelled |
| `isDraft` | Whether the requirement is still being prepared and therefore cannot start |
| `workflowStage` | plan / build / check / integrate / done, the position in the Workflow |
| `health` | active / queued / attention / paused / blocked / cancelled / done, the execution health |
| `approvalState` | Whether the Issue is at an approval point and waiting for an `approve` or `reject` decision |

See [Issue Management](issues.md).

## Workflow

A Workflow is the production line that moves a ready Issue from an idea to
merged code. Draft and Backlog remain outside the Workflow so requirement
readiness and execution state cannot be confused. The default Mohist Workflow
has five stages:

```text diagram
Draft --mark ready--> Backlog --start--> Plan
Plan --approve--> Build --automatic--> Check
Plan --reject--> Plan
Check --approve--> Integrate --automatic--> Done
Check --reject--> Build
```

Before the Workflow starts, Draft protects an incomplete requirement and
Backlog identifies work that may start. Each Workflow stage then has one
purpose:

- **Plan**: An Inline Agent understands the requirement and produces the
  proposal, design, specs, and tasks.
- **Build**: An Inline Agent writes code and runs tests according to the tasks.
- **Check**: An Inline Agent reviews its output.
- **Integrate**: Mohist merges the branch into the base branch.

Some stages, Plan and Check by default, enter an **approval point** after
completion. They wait for an `approve` or `reject` decision. The approver does
not have to be a specific type of actor. See [Approval](#approval).

See [The Workflow](the-workflow.md).

### Workflow Profile

A Workflow is not hard-coded. Each Project can have multiple **Workflow
Profiles** and select one default Profile. An Issue can inherit the default or
select another Profile in the same Project.

A Profile defines stages, tasks, checks, recovery, and Approval. It does not
store Variables or Prompt bodies. Project, Issue, and Run Variables merge by
scope. Prompts are configured only in the Project.

The built-in Profiles are:

- `mohist/local`: The complete five-stage process with local integration; this
  is the default.
- `mohist/github-pr`: Uses a GitHub pull request in the Integrate stage.

See [Workflow Profile](workflow-profiles.md).

## Epic

An Epic continuously supplies work for one product goal. A new Epic is `idle`
by default. **Start** begins automatic advancement. After one linked Issue
finishes, the Epic starts the next Issue that can advance.

See [Planning with Epics](epics.md) for the complete lifecycle.

## External Agent, Inline Agent, Mohist Agent, and Agent Connection

An External Agent interacts with the user outside Mohist. For example, it can
run in Slack, an IDE, or another Agent host. It uses the Mohist Skill and `mo`
to query, delegate to, or operate the execution layer. It is not a Mohist
resource, and Mohist does not schedule it.

An Inline Agent is the use of Agent capability that a Workflow invokes directly
through an Action such as `mohist/opencode`. It has no independent Agent ID. A
Mohist Agent has a stable ID, name, Instructions, and configuration in one
Project. It can start from the Web UI, CLI, an Agent Connection, event routing,
or a comment mention.

An Agent Connection exposes one Mohist Agent in an external interaction
location. For example, a Slack Agent Connection lets a Slack Bot represent a
specified Mohist Agent. Slack receives messages and presents replies. The
Mohist Agent still owns understanding, execution, and the session. The
connection does not copy Agent configuration and cannot switch to another Agent
within one conversation.

A Slack connection presents two types of App to the user. The **Mohist App** is
the workspace management entry point and is itself a built-in Mohist Agent.
Users talk to it in natural language to connect, adjust, diagnose, and create
Agents. An **Agent App** is an execution entry point. Each connected Mohist Agent
has a separate Slack App and bot identity that accepts work and returns results
directly. Management operations and work tasks use clear, separate identities.
One identity does not send on behalf of the other.

An AgentSession is not an Agent or a work result. It records the messages,
context, usage, Activity, and current Runtime Session for a conversation. A
Workflow TaskRun owns Workflow work. An AgentJob owns the first execution of one
Mohist Agent launch. Subsequent input continues the same AgentSession but does
not rewrite the AgentJob. Each accepted input in an AgentSession is a
SessionInput. One continuous Runtime processing period is an AgentTurn. One
AgentTurn can process multiple SessionInputs in order. Messages, execution, and
work results therefore do not share one state.

See [Agents and AgentSessions](agent-sessions.md) for the complete relationship,
[Slack](slack.md) for Slack behavior, and
[`mohist/opencode` Action](actions/opencode.md) for OpenCode Action configuration.

## Approval

An Approval is an `approve` or `reject` decision about the output of a Workflow
stage. A completed stage can enter an approval point and wait for an approver to
decide whether the output can continue. The approver does not have to be a
person. Approval is a role position in the production line.

Several mechanisms can provide a decision at an approval point:

- Automated checks can verify tests, lint results, and artifact completeness.
- A Mohist Agent or script can read evidence and invoke `approve` or `reject`.
- The owner can invoke `approve` or `reject` when human judgment is necessary.

The Workflow does not distinguish these sources. A Mohist Agent, CLI, Web UI,
or external automation decides who initiates Approval. The Approval result is
still only `approve` or `reject`.

Authentication identity determines attribution for Approvals and comments. The
history records the caller identity, such as you, a machine, or an Agent, rather
than a claimed identity. `--display-name` is only a presentation alias. It lets
the interface show a friendly name and does not affect attribution. The Mohist
authentication layer resolves attribution; the caller does not declare it. See
[Authentication and Access](auth.md).

## Skill

A Skill is a reusable description of an Agent capability. An External Agent
can install a Mohist-distributed Skill to understand Mohist domain actions and
operating boundaries. A Mohist Agent can select Skills in its configuration and
use the same capabilities through every entry point. An entry point cannot add
or remove Skills for the Agent.

Mohist distributes four Skills:

| Skill | Purpose |
|---|---|
| `mohist` | Operate Mohist from an External Agent, including Issue creation, Approval, and status queries |
| `mohist-explore` | Explore a requirement from the product perspective and produce a structured Issue that can enter a Workflow |
| `mohist-create-issue` | Create an independently deliverable Issue from an established requirement |
| `mohist-create-epic` | Create a product goal and organize and advance its Issues |

An External Agent typically loads these Skills as necessary, sends intent to
Mohist through `mo`, and returns execution state and results to its original
conversation. The Skills of a Mohist Agent are part of the Agent configuration
and are fixed for the unit of work when it starts. See [Skills](skills.md).

## How the Concepts Fit Together

```text diagram
[Slack] -- Agent Connection --> [Mohist Agent]
                                      |
                                      | AgentJob + AgentSession
                                      v
                                  [Project]

[External Agent in IDE / host] -- Mohist Skill + mo --> [Project]
[Web UI / CLI] -- direct Agent or domain use ------------> [Project]

[Project]
   |
   +--> [Epic] -- groups --> [Issue]
   |
   +-----------------------> [Issue]
                                 |
                                 | Workflow (mohist/local)
                                 v
                  Plan -> Build -> Check -> Integrate -> Done
                                 |
                                 | merges code
                                 v
                         [User repository]
                                 |
                                 v
                        [Product advances]
```

Keep one mental model: **You usually stay in an existing workspace. A Mohist
Agent is an independently usable proxy, and an Agent Connection only brings it
to Slack. An External Agent can also use Mohist through a Skill. A Project is
the product and execution boundary. An Epic owns a goal and supplies work. An
Issue is the workpiece. A Workflow is the production line. An Inline Agent
executes a task directly. An AgentSession records an execution session. The Web
UI is the fallback operations and visualization plane.**

---

Implementation source: See the domain decomposition in
[`design/domain-analysis.md`](../design/domain-analysis.md).
