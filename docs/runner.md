# Runner Guide

Runner is Mohist's execution backend. Server decides what to do; Runner performs
the work.

## Why Runner Exists

Mohist separates the **control plane from the execution plane**.

- **Server, the control plane:** Maintains state, makes decisions, and emits
  events.
- **Runner, the execution plane:** Executes Actions, operates Git, writes files,
  and connects to execution backends such as OpenCode.

This separation matters because:

- Runner can crash, restart, or be replaced without losing Server state.
- Future Runner instances can execute concurrently on different machines with
  different capacities.
- Server validates ownership instead of trusting Runner reports implicitly.

## Starting Runner

```bash
npm run dev:runner
# Or
mo service start runner
```

After startup, Runner:

1. Connects to local Server at `http://localhost:3456` by default.
2. Registers itself and declares its capacity and capabilities.
3. Waits for work.
4. Starts execution when Server assigns a task.

Start Runner **after Server**. It cannot connect while Server is unavailable.

## Checking Runner State

```bash
mo server status
# Output includes Runner state
```

The Web UI also shows Runner state:

- The Runner-unavailable banner above the board.
- Settings > Runtime.
- Runner heartbeat events on the Activity page.

## Concurrent Capacity

Runner has a maximum concurrency limit of 8 by default:

- At most eight tasks execute at once.
- A ninth Issue waits for capacity after it starts.

Change capacity through Settings > Runtime in the Web UI or through Runner
startup options. See `mo service start runner --help`.

Do not increase capacity without accounting for resource use:

- Each executing AgentSession consumes CPU and memory.
- Excess concurrency can trigger model API limits, overload the host, and cause
  Git lock conflicts.
- A capacity of 4 to 8 is appropriate for a personal development machine.

## Execution Ownership

Runner owns host-specific side effects because they are replaceable execution
state. Server owns durable work decisions because a Runner can disappear at any
time. This boundary explains why an interrupted Runner can be replaced without
changing Workflow truth, and why unreported or uncommitted files cannot be
treated as a durable result.

For one task, Runner prepares an isolated workspace, resolves the declared
Action input, invokes the execution backend, and reports facts and outputs to
Server. It validates declared output expectations before reporting success and
reclaims the workspace when the Workflow no longer needs it. The complete
Action contract is in [Action Contracts](actions/README.md).

## Workspace Location

The default path is `workspaces/<workflow-run-id>/` under the Runner data
directory. The WorkflowRun ID determines both path and branch. Neither includes
the Issue title or Repository name.

This is rebuildable execution state. Runner reclaims it after the WorkflowRun.
Commit code that must be retained to the corresponding remote branch first. Do
not manually delete or change the workspace branch, marker, or origin while a
task runs.

## Runner Failure

If the Runner process crashes:

- A Workflow that has not begun execution waits for an available Runner.
- Mohist first attempts to recover an executing task automatically.
- When automatic recovery fails, the Issue enters blocked health and shows the
  cause and recommended recovery action.

Workflow state is not lost because it lives in Server, not Runner.

## Multiple Runners (Future)

Mohist currently assumes one host and one Runner. Future support includes:

- One Runner on each of several machines.
- Server scheduling tasks across Runners.
- Different Runner capabilities, such as Docker support.

This work remains on the roadmap and is not supported today.

## Debugging Runner

### Runner Logs

```bash
mo service logs runner          # Operational logs from service-manager
# Or inspect stdout from the Runner process directly
```

### Execution Logs for One Issue

```bash
mo issue logs <number>
mo issue events <number>             # Event stream
mo session list --issue <number>     # AgentSessions for the Issue
```

### Common Runner Problems

| Symptom | Cause | Resolution |
|---|---|---|
| The board shows "No runner is connected" | Runner is not running | Run `npm run dev:runner` |
| An Issue waits after starting | No Runner has capacity | Start Runner; the Workflow continues automatically |
| A task produces no output for a long time | OpenCode is stuck | Run `mo run pause --issue <number>` and inspect logs |
| Workspace identity error | Marker, branch, or origin was changed manually | Preserve required commits, remove that workspace, and retry |
| Git push failed | Remote Repository permission | Configure an SSH key or token |

## Runner Configuration

Configure Runner behavior with environment variables or a configuration file.
See `mo service start runner --help`. Configurable values include:

- Concurrent capacity.
- Server URL.
- Workspace path.

Runner does not select a global Runtime backend through `type`. A Workflow
task's `uses` value selects an execution-backend Action such as
`mohist/opencode` or `mohist/pi`. Action Input supplies model options. See
[Action Contracts](actions/README.md).

## Self-hosting

For a long-running Runner managed as a service instead of foreground
`dev:runner`, see [Self-hosting](self-host.md).

---

Source: `packages/runner/` and
`packages/server/src/Mohist.Server/Runner/`.
