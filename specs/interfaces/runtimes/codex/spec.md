# Codex

## Codex Runtime

Select `codex` as the Agent Runtime to execute the same AgentJob and
AgentSession product through Codex. Workflow, Web, CLI, Slack, event routing,
and mentions keep their existing entry points. A Workflow still uses only
`mohist/agent`; there is no user-selectable `mohist/codex` Action.

Codex Models and Reasoning Effort values come from the Codex catalog reported
by ready Runners. Codex exposes no Variant in v1, so Variant is unavailable for
this Runtime. Mohist never substitutes an OpenCode or Pi model when the Codex
catalog is empty or unavailable.

Codex runs unattended with the Runner's execution authority. Mohist does not
turn a Codex tool permission request into a Workflow Approval Point and does not
leave an AgentJob waiting for an interactive Codex answer. An unexpected
permission or user-input request ends the current execution with
`permission-required` while preserving the AgentSession and its binding.

Runner-managed Codex configuration, authentication, and physical conversation
history are isolated from a person's interactive Codex installation. Readiness
distinguishes a missing CLI, missing authentication, incompatible app-server
protocol, and catalog or Model gaps from Runner Availability. Credentials and
provider payloads are never stored in the Agent definition or transcript.

The Codex physical conversation may survive a Runner restart, but the execution
that was active in the lost Runner generation does not. Mohist closes that old
execution as `runner-lost`; it never adopts the old Codex Turn or reconstructs
its result from history. A later independently accepted Input may reuse the same
AgentSession and physical conversation, but it starts a new AgentTurn. If the
old Input might have reached Codex, Mohist never replays it automatically.

## Implementation Gaps

- Codex Runtime selection, catalog discovery, app-server lifecycle, execution,
  and Session commands are specified but not implemented.
