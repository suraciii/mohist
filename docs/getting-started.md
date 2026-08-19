# Getting Started

Goal: Start Mohist from zero in 30 minutes. Use a Mohist Agent, a third-party
External Agent, or `mo` to move one real Issue through the complete Workflow and
see its code merged. The Web UI is the fallback operations and visualization
plane. You can also use it to configure and use a Mohist Agent directly.

## Prerequisites

You need .NET SDK 11.0 or later (`dotnet --version`), Node.js 22.19.0 or later
(`node --version`), npm 10 or later (`npm --version`), and an Agent Runtime:
the OpenCode CLI (`opencode --version`) or a configured Pi Runtime.

For OpenCode, follow the [official opencode documentation](https://opencode.ai)
when the CLI is not installed. Pi runs through the Runner's in-process Pi SDK
and uses the Runner user's Pi configuration. Mohist does not include an AI
model. A Workflow Profile selects the concrete Inline Agent Action.

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

This command compiles the ASP.NET Core server and CLI. The first build can take
longer because it restores NuGet packages.

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

Mohist supports three complementary paths. They operate the same Project,
Issue, and Workflow. They do not create three separate sets of state.

- **Mohist Agent**: Stores identity, Instructions, execution configuration, and
  Skills in Mohist. Use it directly from the Web UI or CLI, then connect the
  same Agent to Slack when necessary.
- **Third-party External Agent**: Runs in Slack, an IDE, or another product. It
  uses the Mohist Skill and `mo` to query, delegate to, and operate Mohist. It
  is not a Mohist Agent.
- **Direct `mo` use**: Suitable for deterministic manual operations, scripts,
  and troubleshooting.

To try a Mohist Agent directly, use the task-first startup path after selecting
or creating a Project. `mo agent start` creates the Agent and launches its first
work in one step. The definition-first `mo agent create` and `mo agent launch`
commands remain available when you want to configure a reusable Agent before
starting work. To let a third-party External Agent use Mohist, install the
Mohist Skill into a supported local Agent:

```bash
mo skill install
```

You can then make a request in the Slack workspace, IDE, or other interaction
location where the External Agent runs. For example, ask, "Which Mohist Issues
are advancing, and do any need my attention?" The External Agent reads the
appropriate Skill and uses `mo` to query or operate Mohist. See
[Skills](skills.md) for the complete mechanism.

The rest of this guide uses a third-party External Agent or `mo`. You do not
need to create a Mohist Agent first. If you do not use an External Agent, run
the `mo` commands in this guide directly.

## 6. Configure the Inline Agent Action and Model

The selected Workflow Profile determines the Inline Agent Action. The
`mohist/github-pr` Profile defaults to `mohist/opencode`; Project Workflow
settings can bind it to another compatible Action such as `mohist/pi` without
copying the Profile. A WorkflowRun fixes this choice when it starts.

When using OpenCode, confirm that its CLI works:

```bash
# Confirm that opencode can start.
opencode --help
```

Without an explicit model, the selected Action uses its Runtime default. The
model selector requests the catalog for the effective Profile Action before a
Run starts, and for the Run-bound Action while that Run is active. To select a
model explicitly, set it directly in the task `options`, or configure it in
Workflow Variables and pass it with `options: ${{ vars.agent }}`. See the
[`mohist/opencode` Action](actions/opencode.md) and
[`mohist/pi` Action](actions/pi.md) for Runtime-specific configuration.

## 7. Create Your First Project

Create a Project with the CLI:

```bash
mo project create my-app --path /path/to/your/repo
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
2. Enters the **Plan** stage, where an Inline Agent analyzes the requirement and
   produces the proposal, design, specs, and tasks.

## 10. Wait for Plan to Finish

The Plan stage usually takes 5 to 20 minutes, depending on Issue complexity and
model speed. You can:

- Ask the External Agent, "How far has #1 advanced, and are there any problems?"
- Run `mo issue logs 1` to read detailed logs.
- Run `mo issue view 1` to read current state.
- Open the Web UI Issue details to see complete progress and execution evidence.

After Plan finishes, the Issue stops in **awaiting approval**. The Workflow is
waiting for an Approval decision.

## 11. Approve or Reject Plan

Ask the External Agent to summarize the Plan artifacts, risks, and
recommendation. You can also open the Web UI Issue details to inspect the latest
artifacts:

- `proposal.md`: The Inline Agent's understanding of the requirement
- `design.md`: Design decisions
- `specs/`: Specification changes
- `tasks.json`: Steps that the Build stage will execute
- `self-review.md`: The Inline Agent's own review

Approve the output when it is sound. Reject it with a reason when it needs a
change; the Inline Agent will plan again. This step handles a Workflow approval
point. The operation can come from an External Agent, the Web UI, CLI, a Mohist
Agent, or other automation. An External Agent or automation should provide
attribution. A person who acts directly can omit it.

```bash
mo run approve --issue 1                              # Approve
mo run reject --issue 1 --message "Changes required"  # Reject
```

## 12. Observe Build, Check, and Integrate

After Approval, the Workflow advances automatically:

- **Build**: An Inline Agent writes code and runs tests according to
  `tasks.json`.
- **Check**: An Inline Agent reviews its output and can wait for another
  Approval.
- **Integrate**: Mohist merges the Workspace branch into the base branch.

If any stage fails, the Issue enters blocked state. See
[Troubleshooting](troubleshooting.md) for recovery.

## 13. Verify the Issue

After Integrate finishes, the Issue enters Done:

- The Workspace branch is merged into your base branch.
- Your repository contains the code changes.
- All artifacts remain under `openspec/changes/issue-1/` as an audit record.

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
