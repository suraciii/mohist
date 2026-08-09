# Runtime Integrations

A Runtime integration adapts execution input and Session requests assembled by
Mohist to an external execution backend. Workflow Action adapters and AgentJob
executors share this capability. See
[`../agent-execution.md`](../agent-execution.md) for Agent and Session ownership
boundaries and invariants.

This directory owns Runtime-specific process lifecycle, SDK or protocol
mapping, physical Session behavior, events, state verification, and
compatibility decisions.

- [OpenCode](opencode.md): `OpenCodeRuntime`, SDK selection, physical Session
  lifecycle, Prompt execution, and Session commands.
- [Pi](pi.md): `PiRuntime`, in-process SDK integration, physical Session
  lifecycle, Prompt execution, and Session commands. Pi is an independent peer
  module, not an OpenCode extension.

Related boundaries:

- [`../workflow/actions.md`](../workflow/actions.md) defines common Workflow
  Action dispatch and input/output contracts.
- [`../../docs/actions/`](../../docs/actions/README.md) defines user-facing
  product contracts for each Action.

Add one independent file for a new Runtime. Do not create a common Runtime
interface in advance for hypothetical similarities.
