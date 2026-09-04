# Runtime Integrations

A Runtime integration adapts Mohist execution inputs and Session commands to an
external execution backend. Workflow Actions and AgentJobs use the same
capability. [`../agent-execution.md`](../agent-execution.md) owns Agent and
Session lifecycle rules.

This directory defines Runtime-specific process lifecycle, provider mapping,
physical Session behavior, event projection, state verification, and
compatibility boundaries.

- [OpenCode](opencode.md) defines `OpenCodeRuntime`, its SDK and local Server,
  physical Session lifecycle, Prompt execution, and Session commands.
- [Pi](pi.md) defines `PiRuntime`, its in-process SDK, physical Session-file
  lifecycle, Prompt execution, and Session commands. Pi is an independent peer,
  not an OpenCode extension.
- [Codex](codex.md) defines `CodexRuntime`, its app-server v2 process and
  protocol lifecycle, Thread-backed physical Sessions, Turn execution, and
  Session commands. Codex is an independent peer, not an ACP adapter.

Common boundaries:

- [`../workflow/actions.md`](../workflow/actions.md) defines Workflow Action
  dispatch and input/output contracts.
- [`../../docs/actions/`](../../docs/actions/README.md) defines the user-facing
  Action contracts.

## Model Catalogs

Each Runtime may report models and variants from its configured environment.
Runner registration stores catalogs in `runtimeCatalogs`, keyed by Runtime ID
(`opencode`, `pi`, or `codex`). The Server catalog query accepts a Runtime ID and
aggregates only matching Runner entries. Each ready Runtime reports its latest
catalog. OpenCode, Pi, and Codex discover catalogs independently.

The query returns models and variants for the requested Runtime. A
Profile-backed caller must provide the derived Runtime; it must not use a
historical OpenCode default.

A catalog helps configuration. It is not execution authority. The selected
Action or Agent owns Runtime selection, and the Runtime validates the model when
execution starts. An empty or unavailable catalog never falls back to another
Runtime. A configured model that is absent from the current catalog remains
configured until the user changes or clears it.

Workflow configuration has no Runtime field. An inline task selects a Runtime
through `uses`; a named Mohist Agent selects it through the Agent definition.
`vars.agent` carries Action options such as `model` and `variant` only.

## Adding a Runtime

Add one independent module for each new Runtime. Do not create a common Runtime
interface for hypothetical similarities. A built-in Agent Runtime adds its
module, catalog entry, Agent configuration projection, executor dispatch, and
Session command routing. It does not add a public Workflow Action or change the
Workflow DSL; `mohist/agent` remains the only Agent task binding.
