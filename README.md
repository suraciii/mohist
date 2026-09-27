# Mohist

Mohist is an Agent-oriented software factory built for the transition from
workshop-style development to industrial, automated software production.

People use Agents to organize requirements and build, maintain, and control
workflows. Those workflows use Agents and programs to implement requirements
automatically. People evaluate delivery results and use the evidence to
improve both the requirements and the workflows, without directing every task.

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
 +-------+   +------------------+   +--------------+
 | Slack |   | IDE / Agent host |   | Web UI / CLI +------------+
 +---+---+   +---------+--------+   +--------------+            |
     +-------------+   +---------------+                        |
                   vAgent Connection   v                        |
           +--------------+   +----------------+                |
           | Mohist Agent |   | External Agent |                |
           +-------+------+   +--------+-------+                |
                   +---------+---------+                        |
                             vSkill + mo                        |
                     +---------------+               direct use |
                     | Mohist Server |<-------------------------+
                     +-------+-------+
                             |
                             vdispatch
                        +--------+
                        | Runner |
                        +----+---+
                             |
                             vexecutes in
                +------------------------+
                | Workspace / repository |
                +------------------------+
```

## Workflow

A Workflow Profile defines how an Issue is executed. Its stages,
tasks, checks, and approval points are configurable. The default Profile is
`mohist/local`:

```text diagram
   +-------+
   | Draft |
   +---+---+
       |
       vmark ready
  +---------+
  | Backlog |
  +----+----+
       |
       vstart
   +------+
   | Plan |
   +---+--+
       |
       v
   +-------+
   | Build |
   +---+---+
       |
       v
   +-------+
   | Check |
   +---+---+
       |
       v
 +-----------+
 | Integrate |
 +-----+-----+
       |
       v
   +------+
   | Done |
   +------+
```

Draft and Backlog belong to the Issue lifecycle rather than the Profile. This
readiness boundary keeps incomplete requirements out of execution; the Profile
begins only after the Issue is ready and explicitly started. `Plan` is the
first stage of the default Profile, not a requirement for every Workflow.

Multiple Issues advance concurrently and independently. In the default
Profile, Plan and Check stop at Approval Points. An authorized Agent can
provide the `Approve` or `Request Changes` decision; a person does not have to
approve each stage. Other Profiles define their own stages and decisions. See
[Workflow Profile](specs/workflow/profiles/spec.md).

## Event Responses

Workflows, Issues, Epics, Runners, and AgentSessions produce events. Agent event
routing lets you configure automatic Agent responses. An Agent can approve as a
proxy, analyze failures, summarize progress, create follow-up Issues, and notify
the owner. See [Agent Event Routing](specs/agent/event-routing/spec.md) and
[Agent Supervision](specs/agent/supervision/spec.md).

## Documentation

Start with [Getting Started](docs/getting-started.md). See
[Philosophy of Software Development](docs/philosophy.md) for the development
paradigm and why Mohist exists,
[Product Vision](docs/vision.md) for the product direction, and the
[documentation index](docs/README.md) for the complete reading path.
Cross-domain architecture and decisions are under [`design/`](design/README.md).
Feature-local behavior and design contracts are under
[`specs/`](specs/README.md).

## Repository Structure

- `packages/server/`: control plane (ASP.NET Core + Orleans)
- `packages/runner/`: execution plane (TypeScript)
- `packages/web/`: Web UI (React)
- `packages/go/mohist-cli/`: static `mo` CLI
- `specs/`: feature-local product and design specifications
- `docs/`: user documentation
- `design/`: cross-domain architecture, conventions, and decisions
- `eng/`: repository engineering practices

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md).

## License

MIT
