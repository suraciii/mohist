# Getting Started

This guide takes a new deployment from source to one merged Issue. Choose a
Mohist Agent, a third-party External Agent, or `mo`; all three paths use the
same Project, Issue, and Workflow state. The Web UI is the fallback operations
and visualization plane.

## Product Commitments

- A new deployment can build Mohist, install `mo`, start Server and Runner, and
  choose an interaction path in this order.
- The guide keeps every command and input needed to move one real Issue from
  creation through Plan, approval, Build, Check, Integrate, and Done.
- Mohist keeps Agent, Project, Issue, Workflow, and execution state unified
  across the Web UI, CLI, and External Agent paths.
- A lost CLI response can be retried with the same idempotency key without
  starting a second Agent launch.
- Plan and Check evidence remains available for approval and later diagnosis.

## Prerequisites

You need .NET SDK 11.0 or later (`dotnet --version`), Node.js 22.19.0 or later
(`node --version`), npm 10 or later (`npm --version`), Go 1.25 or later
(`go version`), and an Agent Runtime: the OpenCode CLI (`opencode --version`)
or a configured Pi Runtime.

For OpenCode, follow the [official opencode documentation](https://opencode.ai)
when the CLI is not installed. Pi runs through the Runner's in-process Pi SDK
and uses the Runner user's Pi configuration. Mohist does not include an AI
model. Workflow Agent tasks run named Mohist Agents; each Agent definition
selects its backend.

## 1. Get the Source and Install Dependencies

```bash
git clone <your-fork-or-mohist-url> mohist
cd mohist
npm ci
```

`npm ci` installs all Web UI and Runner dependencies from the lockfile.

## 2. Build Mohist

```bash
npm run build
```

This command compiles the ASP.NET Core server and CLI, Web, Runner, and the
optional statically linked Slack adapter at
`packages/go/mohist-slack/bin/build/mohist-slack` (or `mohist-slack.exe` on
Windows). `mo install slack` promotes that artifact to the service's runtime
path; rebuilding the repository never overwrites a running adapter. The first
build can take longer because it restores NuGet and Go modules. If the default
Go module proxy is unreachable, set a reachable `GOPROXY`, such as
`https://goproxy.cn,direct`, before building.

## 3. Install the `mo` CLI

Install the CLI before its first use:

```bash
npm run install:cli
mo --version
```

The repository command packages the local CLI and installs the global .NET tool
as `mo`.

## 4. Start the Core Processes

The Server and Runner are the core processes required for Mohist execution.
Start Server in one terminal:

```bash
npm run dev:server
```

After Server is ready, install and start an authenticated Runner from a second
terminal in the same repository:

```bash
mo install runner --repo-root "$PWD"
mo runner status
```

The installer requests a one-time enrollment from Server, starts Runner as a
managed service, and lets Runner keep its own machine credential. Later starts
can use `mo service start runner` without enrolling again.

Start the Web UI when you need the fallback operations and visualization plane:

```bash
# Optional terminal 3: Web UI development server
npm run dev:web
```

Open `http://localhost:3456` to view the board and configure or use a Mohist
Agent directly. A third-party External Agent and `mo` do not require the Web UI.

> For production or continuous operation, see
> [Self-hosting](self-host.md). You do not need to start these development
> processes separately in that configuration.

## 5. Select an Interaction Path

All interaction paths operate the same Project, Issue, and Workflow state.
Choose one:

- **Mohist Agent:** A reusable Mohist resource with identity, Instructions,
  execution configuration, and Skills. Use it from the Web UI or CLI.
- **Third-party External Agent:** An Agent in Slack, an IDE, or another product.
  It uses the Mohist Skill and `mo` to operate Mohist. It is not a Mohist Agent.
- **Direct `mo`:** Deterministic manual operations, scripts, and troubleshooting.

To use a Mohist Agent, select or create a Project, then run `mo agent start`.
This task-first command creates the Agent and launches its first work. Use
`mo agent create` followed by `mo agent launch` when you want to configure the
Agent before starting work.

To use an External Agent, install the Mohist Skill in that Agent:

```bash
mo skill install
```

Then ask it to query or operate Mohist, for example: "Which Mohist Issues are
advancing, and do any need my attention?" See [Skills](skills.md). The rest of
this guide uses an External Agent or `mo`; you do not need to create a Mohist
Agent first.

## 6. Configure the Agent Model

Workflow Agent tasks use named Mohist Agents (`mohist/planner`,
`mohist/builder`, and `mohist/reviewer` by default). The Agent definition owns
the backend, model, optional Reasoning Effort, and variant. A Workflow task
cannot override them.

When using OpenCode, confirm that its CLI works:

```bash
# Confirm that opencode can start.
opencode --help
```

Without a model, the Agent uses its runtime default. Configure a model on the
Agent or in Project Agent settings. See [Agents and AgentSessions](agent-sessions.md)
and [Workflow Profiles](workflow-profiles.md#agent-tasks).

## 7. Create Your First Project

Create a Project with the CLI:

```bash
mo project create my-app --path /path/to/your/repo --verification-command "npm run verify"
mo project use my-app
```

### Start your first Agent task (recommended)

Task-first startup is the default first-run path when you have work to do but
do not need to design an Agent first. If this Project has a default execution
configuration, the task is enough:

```bash
mo agent start --prompt "Inspect this repository and report the highest-priority next step"
```

When the Project does not have a default, list available models and provide the
execution hints explicitly:

```bash
mo agent model list --runtime opencode
mo agent start \
  --prompt "Inspect this repository and report the highest-priority next step" \
  --runtime opencode --model provider/model
```

The command prints the Agent, AgentJob, AgentSession, first Input, first Turn,
workspace, status, and canonical observation links. In table mode it also
prints a generated idempotency key before the request when one was not supplied.
Retry a lost response with the same key; an accepted retry returns the original
identities without starting a second launch. Use
[Agents and AgentSessions](agent-sessions.md) to refine the created Agent after
launch. The definition-first `mo agent create` then `mo agent launch` flow
remains the deliberate configuration path.

An External Agent with the Mohist Skill can perform the same operation. For the
manual fallback path, use the Web UI:

1. Select **Create Project**.
2. Enter the Project name, such as `my-app`.
3. Enter a resource name for the initial repository, such as `server`, and its
   Git URL.
4. Confirm the base branch. The default is `main`.
5. Enter the verification command that this Project should run, such as `npm run verify`.

## 8. Create Your First Issue

Use a simple, clear, and verifiable Issue as the trial:

> Title: Add hello world endpoint
>
> Body: Add a `GET /hello` endpoint that returns `{ "message": "hello" }`.

You can say this directly to an External Agent:

```text literal
Create a ready Issue in my-app. Add GET /hello and return { "message": "hello" }.
```

The External Agent structures the requirement and creates the Issue with `mo`.
To use the CLI directly, run:

```bash
mo issue create "Add hello world endpoint" \
  --body "Add a GET /hello endpoint that returns {\"message\":\"hello\"}." \
  --ready
```

The fallback path is **New Issue** in the upper-right corner of the Web UI board.

## 9. Start the Issue

Ask the External Agent to start the Issue it created, or run:

```bash
mo issue start 1
```

For the fallback path, select the Issue on the Web UI board to open its details,
then select **Start**.

Mohist then:

1. Creates or reuses the named Workspace `issue-1` from the target Repository.
2. Enters the **Plan** stage, where a Mohist Agent Task analyzes the requirement
   and produces the plan, design record, and executable task list under `PLANS/`.

## 10. Wait for Plan to Finish

The Plan stage usually takes 5 to 20 minutes, depending on Issue complexity and
model speed. You can:

- Ask the External Agent, "How far has #1 advanced, and are there any problems?"
- Run `mo issue logs 1` to read detailed logs.
- Run `mo issue view 1` to read current state.
- Open the Web UI Issue details to see complete progress and execution evidence.

After Plan finishes, the Issue stops in **awaiting approval**. The Workflow is
waiting for a decision at an Approval Point.

## 11. Approve or Request Changes

Ask the External Agent to summarize the Plan artifacts, risks, and
recommendation. You can also open the Web UI Issue details to inspect the latest
artifacts:

- `PLANS/PLAN.md`: The Mohist Agent's understanding of the requirement and its
  proposed approach
- `PLANS/DESIGN.md`: Design decisions; the file always exists and states when no
  separate design is needed
- `PLANS/tasks.json`: The ordered task list that the Build stage will execute

A delegated approver can read the same recorded evidence through the CLI:

```bash
mo run artifact list --issue 1
mo run artifact get --issue 1 <artifact-id>
```

Approve the output when it is sound. Select Request Changes with a reason when
it needs revision. The configured Feedback Tasks apply the feedback, the Plan
Checks run again, and the Workflow returns to the same Approval Point. The Plan
Tasks do not run again. This action is available only when the bound
Definition declares Feedback Tasks. The operation can come from an External
Agent, the Web UI, CLI, a Mohist Agent, or other automation.

```bash
mo run approve --issue 1                                       # Approve
mo run request-changes --issue 1 --message "Changes required"  # Request Changes
```

## 12. Observe Build, Check, and Integrate

After the Plan is approved, the Workflow advances automatically:

- **Build**: A Mohist Agent writes code and runs tests according to the approved
  task list.
- **Check**: A separate Mohist Agent Task reviews the output and records its
  findings as evidence, then the Stage waits at another Approval Point.
- **Integrate**: Mohist enables auto-merge on the pull request and waits until
  it is merged.

If any stage fails, the Issue enters blocked state. See
[Troubleshooting](troubleshooting.md) for recovery.

## 13. Verify the Issue

After Integrate finishes, the Issue enters Done:

- The pull request is merged into your base branch.
- Your repository contains the code changes.
- Plan and review artifacts remain inspectable from the Issue as run
  artifacts.

In your repository, verify that `GET /hello` works.

## Next Steps

- [Skills](skills.md): Let an External Agent query, delegate to, and operate
  Mohist
- [Agents and AgentSessions](agent-sessions.md): Configure and use a Mohist
  Agent directly
- [Slack](slack.md): Bring a tested Agent to Slack and use the Mohist App to
  manage its Agent Connection conversationally
- [Core Concepts](concepts.md): Understand all terms used in this guide
- [Issue Management](issues.md): Learn prerequisites, comments, force stop, and
  retry
- [Planning with Epics](epics.md): Organize separate Issues into a product plan
  that advances automatically
- [Workflow Profile](workflow-profiles.md): Adapt the Workflow to your working
  style
- [CLI Reference](cli-reference.md): See all `mo` commands, options, and exit
  codes

## Implementation Gaps

Pi installation and provider credential setup remain outside Mohist. Configure
Pi in the Runner user's environment before using a Pi-backed Agent.
