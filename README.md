# Mohist

Mohist is an Agent-friendly AI software production-line control system for
individual developers.

It turns product ideas into executable Issues. Agents continuously plan,
implement, check, and integrate each Issue according to its Workflow. An Agent
acts as the owner's proxy and can occupy a pipeline position that a person can
also occupy. Quality comes from clear inputs, automated checks, review tasks,
Approval decisions, failure recovery, and human escalation when necessary.

Users usually stay in Slack, an IDE, or another existing workspace. A configured
Mohist Agent is available directly from the Web UI or CLI. It can also connect
to Slack as an independent bot. A third-party External Agent uses the Mohist
Skill and `mo` to query, delegate to, and operate the execution layer. The
Mohist Web UI is a fallback operations and visualization plane. Use it to
configure and test Agents, view global state, inspect execution evidence, and
take over manually. It is not another daily workspace.

## Product Interfaces

- **Mohist Agent**: An independently usable Agent in a Project. Start it from
  the Web UI, CLI, an Agent Connection, an event, or a comment mention. Its
  configuration and execution semantics stay the same across all entry points.
- **Agent Connection**: Exposes a configured Mohist Agent in an existing place,
  such as Slack. The connection handles only identity, messages, and
  presentation.
- **Mohist Skill + `mo`**: The path through which a third-party External Agent
  uses Mohist. A person can use the same commands directly.
- **Web UI**: The fallback operations and visualization plane. It provides all
  critical operations and can configure, start, and continue a Mohist Agent.
- **Notifications**: Push changes that need attention to the user's existing
  chat tools. Notifications do not own execution state.

## System Overview

The arrows show how a work request reaches the execution environment.

```text diagram
[Slack] -- Agent Connection --> [Mohist Agent] ---------+
[IDE / Agent host] --> [External Agent] -- Skill + mo --+--> [Mohist Server]
[Web UI / CLI] -- direct use ---------------------------+
                                                            |
                                                            | dispatch
                                                            v
                                                         [Runner]
                                                            |
                                                            | executes in
                                                            v
                                                 [Workspace / repository]
```

## Workflow

A Workflow Profile defines how an Issue enters the production line. Its stages,
tasks, checks, and approval points are configurable. The default Profile is
`mohist/local`:

```text diagram
Draft --mark ready--> Backlog --start--> Plan -> Build -> Check -> Integrate -> Done
```

Draft and Backlog belong to the Issue lifecycle rather than the Profile. This
readiness boundary keeps incomplete requirements out of execution; the Profile
begins at Plan only after the Issue is ready and explicitly started.

Multiple Issues advance concurrently and independently. Key stages, such as
Plan and Check, stop at approval points. The Workflow continues after it
receives an `approve` or `reject` decision. See
[Workflow Profile](docs/workflow-profiles.md).

## Event Responses

Workflows, Issues, Epics, Runners, and AgentSessions produce events. Agent event
routing lets you configure automatic Agent responses. An Agent can approve as a
proxy, analyze failures, summarize progress, create follow-up Issues, and notify
the owner. See [Agent Event Routing](docs/event-routing.md) and
[Agent Supervision](docs/agent-supervision.md).

## Implementation Status

Available: the five-stage Workflow with approval points; Epics, composite
Issues, and sub-issues; the `mo` CLI; the authenticated Web UI; direct Mohist
Agent launch and sessions; the External Agent API with personal access token
(PAT) authentication; Hermes notifications, event routing, Agent supervision,
mentions, and Issue watch; Agent Skills execution and concurrency limits;
OpenCode and Pi Runtimes, the GitHub PR Profile, and Slack Agent Connections;
metrics, route diagnostics, and the application log contract.

Integration in progress: Workflow Profile UI migration; automatic observability
anomaly notifications.

Not implemented or proposal: Mobile PWA and Web Push.

## Documentation

Start with [Getting Started](docs/getting-started.md). See
[Product Vision](docs/vision.md) for the product direction and the
[documentation index](docs/README.md) for the complete reading path.
Architecture and design documents are under [`design/`](design/README.md).

## Repository Structure

- `packages/server/`: control plane (ASP.NET Core + Orleans)
- `packages/runner/`: execution plane (TypeScript)
- `packages/web/`: Web UI (React)
- `packages/cli/`: `mo` CLI
- `docs/`: user documentation
- `design/`: architecture and design documentation
- `openspec/`: change artifacts produced by Workflows

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md).

## License

MIT
