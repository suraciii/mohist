# Agent Supervision Preset

The supervision preset turns owner delegation of production-line operation into
one command. It installs one supervision Agent and two Project routing rules.
The Agent handles Approval Point decisions and terminal failure repair; the
owner takes over when the Agent stops.

This document defines preset content, installation, and behavior policy.
Routing evaluation, Agent launch, and AgentSession semantics live in
[`event-routing.md`](event-routing.md) and [`agent-execution.md`](agent-execution.md).

## Design Drivers

- Installation creates ordinary Agent and RoutingRule resources, not a new
  supervision resource or execution lifecycle.
- Installation is idempotent by resource name and never overwrites user edits.
- Approval requests and terminal run failures are the only triggers. Automatic
  recovery remains separate from supervision.
- The Agent owns correctness and repair decisions. Product direction, Issue
  closure, and stopping the whole Run remain owner decisions.
- Each trigger gets an independent AgentJob and AgentSession. Issue comments
  provide the durable handoff memory between executions.
- Stopping is escalation. The Agent never hides an unresolved Approval Point or
  failed Run behind a successful-looking response.

## Model

The preset is a set of text resources shipped with the CLI, not a domain
resource. Installation produces an ordinary Mohist Agent and ordinary
RoutingRules. After installation, users may edit those resources with
`mo agent edit` and `mo routing rule edit`; later installation does not track
drift or write over them.

The preset contains one Agent and two rules:

- `supervisor`: an Agent with identity instructions and no AgentConfig, Skills,
  or concurrency override.
- `supervisor-approval`: a fallback rule for approval-request events.
- `supervisor-failure`: a fallback rule for terminal run-failure events.

The rules match:

```text literal
event.type == "com.mohist.workflow.stage.approval-requested"
event.type == "com.mohist.workflow.run.failed"
```

Neither rule has an Issue filter, so supervision covers the Project. Neither
sets `Continue`, so the response is exclusive. `run.failed` is terminal;
supervision does not run while automatic recovery emits `run.retrying`.

## Semantics

### Installation

```bash
mo agent install supervisor
```

`install` accepts a built-in preset name. The only current name is `supervisor`;
an unknown name is rejected with the available presets. Installation performs
these steps in order, skipping and reporting an existing resource:

1. Create or reuse the `supervisor` Agent without changing an existing Agent.
2. Append `supervisor-approval`, then `supervisor-failure`, to the routing
   table. Their tail position makes them fallback rules, so existing targeted
   rules match first. Skip a rule when its name already exists.

Installation does not move existing rules, overwrite Agent instructions, change
notification settings, or write a skill stub into the repository.

### Preflight checks

Preflight checks report prerequisites but do not repair them. A failed check does
not prevent installation:

- Check whether the Agent can discover `.agents/skills/mohist` in the default
  repository Workspace. If missing, tell the owner to run
  `mo skill install --path <repo>`.
- Check whether default notifications for approval requests, failures, and
  completions remain enabled. If disabled, explain that the owner must inspect
  proactively to discover a handoff.

### Escalation model

The preset adds no `escalate` command and no event type. Escalation combines
existing mechanisms:

1. Existing notifications report approval requests and failure events. A
   notification says that something happened; it does not itself require the
   owner to act.
2. The Agent writes one comment beginning with `[supervisor]` for every
   intervention. When it stops, that comment states the root-cause conclusion,
   attempted actions, and exact decision needed from the owner.
3. The Agent stops its response to hand off. It does not execute `mo run stop`.
   The Approval Point remains pending or the Run remains failed, and the owner
   uses `approve`, `request-changes`, `retry`, or `rerun`.
4. A response that cannot start or fails while running follows the default
   `agent.job.failed` notification path. The owner must not believe the Agent
   is handling work when it is not. See [`event-response.md`](event-response.md).

### Behavior principles

Preset text supplies identity, goal, boundaries, and the memory protocol. The
Agent decides how to review, repair, and stop because Approval versus Request
Changes and another repair attempt depend on context. A fixed decision tree
would turn the Agent into a rule engine.

- **Goal:** keep the production line from waiting for a person. Resolve work at
  Agent level when possible.
- **Memory:** each trigger creates an independent AgentJob and AgentSession.
  Before acting, read all `[supervisor]` comments on the Issue. Write one for
  every intervention, recording what was assessed, done, and why. Comments are
  the only memory between executions.
- **Escalation:** recognize a no-progress loop from the supervision record and
  stop by judgment. Repetition is a signal, not a mechanical threshold; the
  system imposes no retry count.
- **Uncertainty:** when product direction, external constraints, or missing
  information matter, explain the uncertainty in a comment and leave the
  decision to the owner.
- **Delegation:** the Agent owns "is this done correctly?" The owner owns
  "should this be done?" The Agent may propose closing an Issue, stopping a
  Run, or changing its objective, but it never executes those terminal product
  decisions. Identity instructions and audit enforce this boundary; the system
  does not hard-code it.
- **Action surface:** use the same `mo` command surface and Issue Workspace as a
  person. There is no privileged channel or enumerated allowlist. The Agent
  must not change Issues, configuration, or unrelated code.

The default topology uses one Agent. Rule prompts distinguish approval and
repair while identity and Issue memory remain shared. Users may use separate
Agents by changing rule references. Each customized Agent needs a distinct
marker and must preserve the rule to read all supervision comments first, or
the Agents cannot see the rework loop between Approval Feedback and repair.

### Preset policy and sources

Preset wording is executable CLI configuration. This design does not duplicate
it, because a prose copy can drift. Installation copies these resources
verbatim:

- [identity instructions](../packages/go/mohist-cli/presets/supervisor/instructions.md);
- [approval response prompt](../packages/go/mohist-cli/presets/supervisor/approval.md);
- [failure response prompt](../packages/go/mohist-cli/presets/supervisor/failure.md).

The [preset manifest](../packages/go/mohist-cli/presets/manifest.json) binds
resources to the Agent and rules. `{{event.*}}` placeholders belong to
RoutingRule runtime syntax; the CLI preserves them for response-time
rendering.

The identity resource owns the shared authority and memory policy. Each rule
prompt supplies only event context and the decision required for that trigger.
The policy preserves these boundaries:

- `[supervisor]` comments are durable memory and the handoff surface.
- Comments and approval decisions identify `supervisor` as author, so history
  records who acted and Agent-authored mentions cannot recursively launch an
  Agent.
- The Agent owns correctness decisions and repair attempts. Product direction,
  objective changes, Issue closure, and stopping the whole Run remain owner
  decisions.
- A mention is one bounded task. Continued attention requires Issue watch; the
  preset does not pretend that an Agent process remains continuously active.

The approval prompt asks the Agent to approve correct work, select Request
Changes with specific Approval Feedback, or stop with a product-direction
handoff. The failure prompt runs only after automatic recovery is exhausted; it
asks the Agent to use prior comments, repair and retry when new progress is
likely, or stop with a root-cause handoff. Both prompts require one
`[supervisor]` comment.

## Examples

First installation in a new Project:

```text literal
$ mo agent install supervisor
created agent: supervisor
created routing rule: supervisor-approval (position 1)
created routing rule: supervisor-failure (position 2)
warning: .agents/skills/mohist not found in repository 'web-app';
         run `mo skill install --path web-app` so the agent can discover the mo command surface
```

Repeated installation after the owner edits identity instructions does not
overwrite them:

```text literal
$ mo agent install supervisor
exists, skipped: agent supervisor
exists, skipped: routing rule supervisor-approval
exists, skipped: routing rule supervisor-failure
```

## Status

Implemented: `mo agent install supervisor` idempotently creates the preset Agent
and two tail-position routing rules by name. `mo issue watch` supports watching
and muting. Agent response failures (`agent.job.failed`) enter the inbox and
Hermes notifications. Authenticated identity owns approval and comment
attribution; `--display-name` is a presentation alias.

Agent Skills are pinned into each execution definition. The preset adds no
Skills override. Discovery of the `mohist` Skill still depends on the stub in
the execution Workspace, so installation reports a missing stub but does not
modify the user's repository.
