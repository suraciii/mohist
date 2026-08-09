# Getting Started

Goal: Start Mohist from zero in 30 minutes. Use a Mohist Agent, a third-party
External Agent, or `mo` to move one real Issue through the complete Workflow and
see its code merged. The Web UI is the fallback operations and visualization
plane. You can also use it to configure and use a Mohist Agent directly.

## Prerequisites

| Tool | Version | Check command |
|---|---|---|
| .NET SDK | 11.0+ | `dotnet --version` |
| Node.js | 22.19.0+ | `node --version` |
| npm | 10+ | `npm --version` |
| opencode CLI | Must start successfully | `opencode --version` |

If `opencode` is not installed, follow the
[official opencode documentation](https://opencode.ai). Mohist does not include
an AI model. An Inline Agent uses OpenCode to execute tasks.

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

To try a Mohist Agent directly, follow
[Agents and AgentSessions](agent-sessions.md) to create and start one. To let a
third-party External Agent use Mohist, install the Mohist Skill into a supported
local Agent:

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

## 6. Configure the Inline Agent Model

Mohist invokes an LLM through opencode. Confirm that opencode works:

```bash
# Confirm that opencode can start.
opencode --help
```

Set a model directly in the task `options` when the Workflow needs a specific
selection. You can also configure model, reasoning effort, and Runtime-specific
variant in Workflow Variables and pass them with `options: ${{ vars.agent }}`.
An Inline Agent does not take a model or reasoning effort from an existing
Session. See [`mohist/opencode` Action](actions/opencode.md) for the complete
configuration. The closed Reasoning effort configuration contract is target
behavior pending saved Agent execution configuration delivery.[^433]

[^433]: Delivery gap [#433](https://github.com/suraciii/mohist/issues/433): saved execution configuration contract. It has no dependency on #434.

## 7. Create Your First Project

Create a Project with the CLI:

```bash
mo project create my-app --path /path/to/your/repo
mo project use my-app
```

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

---

Implementation source: `package.json`, `global.json`, and
`Directory.Build.props` in the repository root.
