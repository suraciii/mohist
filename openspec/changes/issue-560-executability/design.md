## Context

`AgentReadinessService` already derives structural gaps from an Agent
definition, compares execution evidence to the current immutable definition,
and gates the direct launcher. Its three display conclusions merge two product
facts: a missing configuration and an execution configuration failure. The
Web and CLI render those conclusions, while Slack ingress independently calls
`AgentReadinessDeriver` on raw config and can therefore miss a matching failed
execution.

## Decision

Keep `AgentReadinessService` as the one evaluation service, but publish an
`AgentExecutabilityResult` on `AgentInfo.Executability`. Its `State` is one of
these values, in precedence order:

1. `not-configured`: one or more structural definition gaps exist.
2. `not-executable`: the latest execution matching the definition failed with
   an execution-configuration category.
3. `executable`: matching execution completed successfully.
4. `unknown`: no matching success or configuration failure exists.

Every gap contains its message, next action, and one fix entry point. The
result includes a pending-launch note for `unknown`. It is calculated on read,
so a definition edit immediately invalidates old matching evidence without a
migration or stored status.

`not-configured` and `not-executable` throw one explicit exception carrying
the result. Routes map them to `agent_not_configured` and
`agent_not_executable`. Direct launch, subagent admission, and Slack ingress
all use the same state; no entry point derives a second verdict from raw
configuration. Existing connection-local `AgentReadiness` remains a setup
diagnostic only and is not an Agent launch decision.

Web and CLI are read-only consumers of `executability`. They do not infer a
state. Each renders Executability and Availability as separate labeled
signals. The composer disables dispatch only for the two blocked states;
unknown explains that the accepted launch awaits Runner verification.

## Alternatives

- Keep `Needs setup` with an error subcode: rejected because callers and users
  still cannot distinguish a missing definition from a runtime rejection.
- Let each launch route inspect config: rejected because it loses evidence
  matching and creates inconsistent admission rules.
- Persist the verdict: rejected because the current definition is the source
  of truth and edits must immediately invalidate historical evidence.
