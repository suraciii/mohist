# Configuration

Configure the reusable Agent before [launching work](../execution/spec.md).
[Readiness](../readiness/spec.md) evaluates whether that definition can run.

## Mohist Agent

A Mohist Agent is a first-class Project resource. It stores:

- A stable ID and a presentation identity of name, avatar, and description.
- Instructions and execution configuration.
- Skills.
- A concurrency limit and `active` or `archived` state.

## Configure an Agent

- **Name** identifies the Agent within its Project and external locations.
  Renaming does not change the Agent ID.
- **Avatar** identifies the Agent in the Web UI, Slack, and execution records.
  Mohist updates its presentation immediately and asynchronously synchronizes
  external identities that support updates. An out-of-sync state is visible.
- **Description** helps users select the Agent. It is not execution
  Instructions.
- **Instructions** define the Agent's role, behavior, and stopping conditions.
  They are fixed when an AgentJob starts.
- **Runtime** selects the execution backend and belongs to the Agent. An unset
  Runtime selects `pi`.
- **Model, Reasoning Effort, and Variant** select model behavior on the Agent
  definition. An unset Model uses the Runtime's own default, chosen at dispatch
  and recorded in the AgentJob snapshot as Runtime-chosen. Unset Reasoning
  Effort uses Runtime behavior. An unset Variant selects no variant.
- **Skills** load at AgentJob startup and cannot be added or removed for one
  request.
- **Max concurrent runs** limits launches and follow-ups. Lowering the limit
  does not stop running work; excess work queues.
- **State** controls new delegations. An archived Agent rejects new work while
  existing Sessions remain readable and may continue.

Runtime credentials belong in protected Runtime settings. They do not belong in
Instructions, Agent records, or Agent Connections. Reasoning Effort uses `off`,
`minimal`, `low`, `medium`, `high`, `xhigh`, or `max`; it is never encoded as a
Variant and has no `none` value. OpenCode does not support explicit Reasoning
Effort. Choose Pi or Codex, or leave it unset for OpenCode.

An ordinary launch accepts task text and context references. Context is not Agent
configuration. The Agent definition is fixed when the AgentJob starts, as are
its Skills and Workspace identity. Later Agent edits affect later AgentJobs only.
Follow-ups in an existing Session keep the Session's established configuration.

### Execution Resolution

Execution resolution has exactly three terms:

1. An accepted caller hint, where the entry point allows one. Task-first
   creation accepts Runtime, Model, Reasoning Effort, and Variant hints; other
   launches accept no execution hint.
2. The Agent definition.
3. Runtime behavior for unset fields. An unset Runtime selects `pi`; an unset
   Model uses the Runtime's own default; unset Reasoning Effort uses Runtime
   behavior; an unset Variant selects no variant.

No Project value participates in resolution, and nothing is inherited from
another resource. An explicitly malformed Runtime or Model remains a
configuration gap; no other value hides it. The Server snapshots the resolved
tuple onto every new AgentJob dispatch. A null Model means the Runtime chooses
the model at dispatch, and the AgentJob snapshot records that the Runtime chose
it. A Runner rejects a dispatch whose Runtime is missing or unknown instead of
guessing a backend.

An Agent with an unset Model is ready when its Runtime is usable. An Agent edit
affects later launches only: each AgentJob stores its resolved configuration at
launch, and no edit reinterprets an existing AgentJob, queued Job, or Session
follow-up. A Readiness conclusion confirmed by a completed execution is not
changed by an Agent edit alone.

### Built-in Agents

Built-in Workflow Agents (`mohist/planner`, `mohist/builder`, `mohist/reviewer`)
are complete Mohist-owned definitions with Runtime `pi` and no Model. They
appear in the Project's Agent list and detail reads with their origin:
`built-in` for a built-in definition and `project` for a stored Project Agent.
The Project's effective named Agents are its stored Project Agents plus the
unshadowed built-in Workflow Agents. A Project Agent with the same name shadows
the built-in, is marked as overriding it, and the built-in no longer appears
separately. An archived shadow remains the shadowing entry with its state and
never silently falls back to the built-in. The application-owned `mohist-slack`
manager never appears in Project Agent lists.

**Customize** materializes an override: one Server operation copies the
built-in's Instructions, description, Runtime, and Skills into a new same-name
Project Agent and applies the caller's changes in the same request. Clients
never copy built-in text themselves. The operation fails with a named conflict
when an active same-name Agent already exists; that Agent is edited instead. An
archived same-name Agent is a named repair case.

### Model, Effort, and Variant Selection

Model, Reasoning Effort, and Variant pickers are catalog-backed for the
selected Runtime. When the catalog is unavailable, a syntactically valid value
may be saved and is shown as **not yet verified**. A complete catalog that
proves a model, effort, or variant incompatible rejects the write. An empty
OpenCode catalog is advisory: it never proves a model invalid and never means
that OpenCode is offline. No state silently substitutes another Runtime, model,
effort, or variant.

### Configure and Test in the Web UI

In **Agents**, create or open an Agent and enter its identity and Instructions.
The list shows stored Project Agents and unshadowed built-in Workflow Agents with
their origin; **Customize** on a built-in materializes a same-name Project
Agent. Select a Runtime, then choose only the Model, catalog-backed Reasoning
Effort, Variant, and Skills that it supports. Set a concurrency limit. The page
shows Readiness and each repair gap. When Readiness is `ready`, use **Start session**
to submit a task. You may submit when it is `unknown`, but the task waits for
Runner validation. After a successful launch, open the AgentSession to inspect
replies and send a follow-up.

### Configure and Use in the CLI

```bash
mo agent create --name explorer --description "Explore product needs" --instructions "Clarify the request, identify missing decisions, and produce actionable issues." --runtime opencode --skills mohist,mohist-explore --max-concurrent-runs 1
mo agent view explorer
mo agent launch explorer --prompt "Explore a product design for invoking a Mohist Agent from Slack"
# After response loss, retry with the key printed before launch. Do not create a new launch.
mo agent launch explorer --prompt "Explore a product design for invoking a Mohist Agent from Slack" --idempotency-key <key>
```

`agent view` shows Readiness, Availability, and configuration gaps. `agent
launch` returns AgentJob, AgentSession, first Input, and Turn IDs. Use
`mo session followup` for a continuing Session and `mo session transcript` for
its record. Observe `accepted`, `queued`, and `running`; read the result at
`terminal`; query or retry the original key when state is `unknown`.

## Implementation Gaps

- Built-in Workflow Agents are not yet visible in Agent lists or detail reads,
  and no operation materializes a Project override from a built-in definition.
  Overriding one still requires creating a complete same-name Project Agent.

- The Project-level default execution configuration, its routes, and its
  resolution term remain implemented.
