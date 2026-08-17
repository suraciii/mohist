## Why

Starting an Agent today is definition-first. Before any work can run, the user
must author an Agent resource — the create API rejects a request without
`name` and `instructions` — and configure a model, because an Agent without
one is structurally `Needs setup` and launch is blocked. Only then, on a
separate composer page or a second command (`mo agent launch`), can the user
state the task. The task — the only thing the user actually has on day one —
comes last, behind resource and Runtime concepts, and even a one-off
delegation must mint a hand-configured resource before any evidence exists.
The Slack Mohist App already proves the opposite pattern (at most two
questions, defaults for everything else), but Mohist's own Web UI and CLI
cannot do in one step what a Slack conversation can.

## What Changes

- Add a task-first create-and-launch contract at the Server boundary: one
  accepted request carries the task (prompt, optional context references,
  optional identity and execution hints), creates a complete Mohist Agent
  definition, and starts the first AgentJob and AgentSession through the
  existing canonical launch path — not a third execution path.
- Derive unspecified definition parts from defaults at creation: a
  conflict-free name, description, baseline Instructions, and execution
  configuration, so a task-first Agent is launchable immediately (Readiness
  `Ready` or `Unknown`, never `Needs setup` caused only by missing defaults).
- Add a single Project default execution configuration (Runtime, Model,
  optional Variant) with one precedence rule: caller-supplied value, then
  Agent definition, then Project default. With a default configured, a
  missing Model/Variant stops being a structural Readiness gap; without one,
  the gap remains and entry points guide configuration inline instead of
  dead-ending in Agent settings.
- Make model selection on the task-first path a recommendation, not a
  puzzle: the Project default — the owner's choice of what tasks in this
  Project run on — is presented as the labeled recommended execution
  configuration, and every model choice keeps an entry to the full options:
  the Web composer selects from the Project's model catalog through the same
  catalog-backed selection the definition editor uses (never a free-form
  model field), and the CLI's missing-configuration guidance points at
  `mo agent model list`. The catalog carries no per-purpose model metadata,
  so recommendations beyond the labeled Project default (a task-keyed model
  classifier) are out of scope; the recommendation and the full-options
  entry are the commitments.
- A rejected task-first request must not leave a half-created Agent that the
  user must clean up. Replaying the same caller idempotency key returns the
  original outcome, following the existing launch convergence rules.
- Reorient the Web UI session composer to task-first: task and context come
  first, agent selection becomes optional (a new Agent is created for the
  task by default), the user lands in the running session after launch, and
  refinement (name, Instructions, Skills) remains available on the created
  Agent. The Agents empty state starts from a task instead of the editor
  form.
- Add a task-first CLI startup command that creates and launches in one step
  and prints the Agent, AgentJob, AgentSession, Input, and Turn identities
  under the same caller-visible idempotency-key contract as
  `mo agent launch`.
- Keep the definition-first flows (Web editor, `mo agent create`,
  `mo agent install` presets) unchanged for deliberate configuration. No
  breaking API changes; the visible behavior change is that Agents with a
  missing Model/Variant report `Ready`/`Unknown` instead of `Needs setup`
  when a Project default resolves.

## Capabilities

- `agent-task-launch`: The one-request create-and-launch contract: request
  shape and allowed fields, definition creation composed with the canonical
  launch in one accepted operation, rejection and orphan rules, idempotent
  replay, and the response projection (Agent, Job, Session, Input, Turn
  identities).
- `agent-creation-defaults`: How an unspecified Agent definition is derived
  from a task: conflict-free naming, baseline description and Instructions,
  Project default execution configuration resolution and precedence, and the
  resulting Readiness rules for default-resolved and default-missing
  definitions.
- `web-agent-task-composer`: The task-first creation and startup experience
  in the Web UI: composer order and defaults, inline execution configuration
  when no Project default exists — catalog-backed model selection with the
  labeled Project-default recommendation and an entry to the full options —
  launch feedback and navigation into the session, the refine-after-launch
  path, and the Agents empty state.
- `cli-agent-task-launch`: The task-first CLI surface: command arguments,
  identity and idempotency-key output, exit behavior, and JSON output shape.

## Impact

- **Server:** Agent definition and session launch routes
  (`AgentDefinitionRoutes`, `AgentSessionLaunchRoutes`), the agent launcher,
  `AgentReadinessService` structural-gap rules, and new Project default
  execution configuration storage and resolution.
- **Web UI:** the session composer page, the Agents list empty state, and
  the agent entity API client under `packages/web/src`.
- **CLI:** the `mo agent` command group (`MohistCliCommands.Agent.cs`) and
  its output shapes.
- **Docs:** `docs/agent-sessions.md` (Configure an Agent, Launch Entry
  Points), `docs/web-ui.md`, `docs/cli-reference.md`,
  `docs/getting-started.md`.
- **Related work:** composes with the per-execution configuration preview
  (#556) and reasoning-effort execution configuration (#557); direct
  external API projections (#555) are unaffected. Builds on the existing
  launch convergence contract; does not change AgentJob/AgentSession
  semantics, entry-point equivalence, or the definition snapshot rules.
