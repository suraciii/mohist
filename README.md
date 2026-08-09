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

```text
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

```text
Draft -> Plan -> Build -> Check -> Integrate -> Done
```

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

| Available | Integration in progress | Design complete (spec finalized) |
|---|---|---|
| Five-stage Workflow, approval points, automatic Epic advancement | Agent supervision preset, comment mentions, Issue following | Composite Issues and sub-issues |
| `mo` CLI, Web UI, direct Mohist Agent launch and sessions | Profile collection migration | Agent API external invocation contract, Slack Agent Connection |
| Hermes notifications, event routing | Agent Skills execution and concurrency limits | Mobile PWA |
| OpenCode / Pi runtime, GitHub PR Profile | | Observability |

The corresponding Issues track items that are in progress or have a finalized
spec. See the "Implementation Gaps" section in each document.

<!-- TODO: Add a Web UI screenshot. -->

## Documentation

Start with [Getting Started](docs/getting-started.md). See
[Product Vision](docs/vision.md) for the product direction and the
[documentation index](docs/README.md) for the complete reading path.
Architecture and design documents are under [`design/`](design/README.md).

## Repository Structure

```text
packages/
  server/    Control plane (ASP.NET Core + Orleans)
  runner/    Execution plane (TypeScript)
  web/       Web UI (React)
  cli/       mo CLI
docs/        User documentation
design/      Architecture and design documentation
openspec/    Change artifacts produced by Workflows
```

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md).

## License

MIT
