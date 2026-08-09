---
status: converged
---

# Agent Supervision Preset

The supervision preset turns "the owner delegates production-line operation to an Agent" into
one command. It installs a supervision Agent and two routing rules in the Project. The Agent
takes responsibility for approval decisions and terminal failure handling; the owner steps in
only when the Agent stops.

This document defines the preset content, installation semantics, and Agent behavior policy.
Routing-table evaluation, Agent launch, and the AgentSession model are defined in
[`event-routing.md`](event-routing.md) and [`agent-execution.md`](agent-execution.md).

## Model

The preset is not a new domain resource. It is a set of text resources shipped with the CLI.
Installation produces an ordinary Mohist Agent and ordinary RoutingRules. Once installed, the
resources are detached from the preset: users may modify them with `mo agent edit` and
`mo routing rule edit`. A later `install` neither writes over them nor tracks drift.

The preset contains:

| Resource | Name | Content |
|---|---|---|
| Agent | `supervisor` | Identity instructions (see [Preset policy and sources](#preset-policy-and-sources)); no AgentConfig, Skills, or concurrency override |
| RoutingRule | `supervisor-approval` | Matches approval-request events; response policy in [Preset policy and sources](#preset-policy-and-sources) |
| RoutingRule | `supervisor-failure` | Matches terminal run-failure events; response policy in [Preset policy and sources](#preset-policy-and-sources) |

The two rule match expressions are:

```text literal
event.type == "com.mohist.workflow.stage.approval-requested"
event.type == "com.mohist.workflow.run.failed"
```

The rules have no Issue filter: supervision covers the whole Project. They do not set
`Continue`, so the response is exclusive. `run.failed` is a terminal event; supervision does not
run while automatic recovery emits `run.retrying`.

## Semantics

### Installation

```bash
mo agent install supervisor
```

`install` accepts a built-in preset name. The only current name is `supervisor`; an unknown name
is rejected with a list of available presets. Installation performs these steps in order. Each
step is idempotent by name: an existing resource is skipped and reported.

1. Create the `supervisor` Agent. If an Agent with that name already exists, reuse it without
   changing it.
2. Append `supervisor-approval` and then `supervisor-failure` to the routing table. Appending
   makes them fallback rules: existing targeted rules remain above them and match first. Skip
   either rule when a rule with the same name already exists.

Installation does not move existing rules, overwrite instructions on an existing Agent, change
notification settings, or write a skill stub into the repository.

### Preflight checks

Installation only checks prerequisites; it does not repair them. A failed check does not prevent
installation, but the output must report it clearly:

- Check whether the Agent can discover the `mohist` skill stub
  (`.agents/skills/mohist`) in the default repository workspace. If it is missing, tell the user
  to run `mo skill install --path <repo>`.
- Supervision relies on the owner retaining default notifications for approval requests,
  failures, and completions. If notifications are disabled, explain that the owner can discover
  an Agent handoff only by checking proactively.

### Escalation model

The preset adds neither an `escalate` command nor a new event type. Escalation combines four
existing mechanisms:

1. **Notifications remain enabled.** Approval requests and failure events already notify the
   owner. A notification says that something happened on the production line; it does not by
   itself mean that the owner must act.
2. **A `[supervisor]` comment carries the escalation.** The Agent writes one comment beginning
   with `[supervisor]` for every intervention. When it stops, the comment states the root-cause
   conclusion, actions already attempted, and the exact decision needed from the owner. The
   owner reads the comment from the notification and can continue from there.
3. **Stopping is escalation.** The strongest escalation signal is that the Agent takes no further
   action. The approval remains pending or the run remains failed, and the owner takes over using
   the normal command surface (`approve` / `reject` / `retry` / `rerun`).
4. **Agent failure surfaces too.** When the response cannot start or fails while running, the
   default notification path includes `agent.job.failed`. The state "the owner thinks it is being
   handled, but it is not" must never be silent. See [`event-response.md`](event-response.md).

### Behavior principles

Preset text supplies identity, goal, boundaries, and a memory protocol. It leaves how to review,
how to repair, and when to stop to the Agent's judgment. This is deliberate: whether an approval
should pass and whether another repair is worthwhile are contextual decisions. Encoding them as
a decision tree would reduce the Agent to a rule engine and prevent useful rework loops.

- **Goal:** keep the production line from waiting for a person. Resolve work at the Agent level
  whenever possible instead of passing it to the owner.
- **Memory:** each trigger creates an independent AgentJob and a new AgentSession. Issue comments
  are the only memory between executions. Before acting, read `[supervisor]` comments on the
  Issue. Write one for every intervention, recording what was assessed, what was done, and why.
- **Escalate by judgment, not a counter:** the Agent recognizes from its own record when repeated
  intervention on the same problem has produced no new progress, then stops and escalates.
  Repetition is a signal for detecting a loop, not a mechanical threshold. Loop prevention
  therefore depends on Agent judgment and owner observation through notifications; the system
  imposes no retry count.
- **Do not guess:** when a decision depends on product direction, external constraints, or
  missing information, write a comment explaining the uncertainty and leave it to the owner.
- **Delegation boundary:** the Agent owns "is this done correctly?"; the owner owns "should this
  be done?" The Agent only proposes terminal decisions such as abandoning an Issue (`close`),
  stopping the whole run (`stop`), or changing the Issue objective. It does not execute them.
  Identity instructions and auditing enforce this boundary; the system does not hard-code it.
- **Action surface:** use the same `mo` command surface and Issue workspace as a person. There is
  no privileged channel and no enumerated action allowlist. The only boundary is not changing
  Issues, configuration, or code unrelated to the current event.

The default topology uses one Agent. Rule prompts carry the differences between the two response
types, while identity and Issue memory remain shared. Users may customize the topology by using
separate Agents for approval and repair and changing the Agent references in the rules; no new
mechanism is required. In that topology, each Agent needs a distinguishable marker and each set
of identity instructions must preserve the rule "read all supervision comments on the Issue
first." Otherwise, neither side can see the rework loop between approval and repair.

### Preset policy and sources

The preset wording is executable configuration owned by the CLI resources. The design document
does not duplicate that wording because a prose copy can drift and a documentation translation
must not silently change Agent behavior. Installation copies these resources verbatim:

- [identity instructions](../packages/cli/Mohist.Cli/presets/supervisor/instructions.md);
- [approval response prompt](../packages/cli/Mohist.Cli/presets/supervisor/approval.md);
- [failure response prompt](../packages/cli/Mohist.Cli/presets/supervisor/failure.md).

The [preset manifest](../packages/cli/Mohist.Cli/presets/manifest.json) binds those resources to
the Agent and routing rules. `{{event.*}}` placeholders belong to RoutingRule runtime syntax; the
CLI preserves them for response-time rendering.

The three resources separate stable identity from event-specific work. The identity instructions
carry the authority boundary and memory protocol once, so approval and failure responses cannot
silently develop different identities. Each rule prompt supplies only the event context and the
decision required for that trigger.

The identity policy protects four boundaries:

- Issue comments beginning with `[supervisor]` are durable memory across otherwise independent
  executions and the handoff surface when the Agent stops.
- Comments and approval decisions identify `supervisor` as their author so history can answer who
  acted and Agent-authored mentions cannot recursively launch another Agent.
- The Agent owns correctness decisions and repair attempts. Product direction, objective changes,
  Issue closure, and stopping the whole run remain owner decisions.
- A mention is one bounded task. Continued attention requires Issue watch; the preset does not
  pretend that an Agent process remains continuously active.

The approval prompt asks the Agent to approve correct work, reject work that needs a specific
change, or stop and hand a product-direction decision to the owner. The failure prompt runs only
after automatic recovery is exhausted; it asks the Agent to use prior supervision comments,
repair and retry when new progress is likely, or stop with a root-cause handoff when it is not.
Both prompts require one `[supervisor]` comment so the next execution and the owner share the same
reasoning trail.

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

Repeated installation after the user has edited the identity instructions does not overwrite
them:

```text literal
$ mo agent install supervisor
exists, skipped: agent supervisor
exists, skipped: routing rule supervisor-approval
exists, skipped: routing rule supervisor-failure
```

## Status

Implemented: `mo agent install supervisor` idempotently creates the preset Agent and two
tail-position routing rules by name; `mo issue watch` supports watching and muting; Agent response
failures (`agent.job.failed`) enter the inbox and Hermes notifications; approval decisions record
the actor (`--author` -> `decidedBy`).

Agent Skills are pinned into each execution definition. The preset adds no Skills override;
discovery of the `mohist` skill still depends on the stub file in the execution workspace, so
installation only checks and reports it and cannot decide to modify the user's repository.
