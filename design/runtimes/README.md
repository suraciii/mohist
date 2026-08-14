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
- [`../workflow/profile.md`](../workflow/profile.md#agent-runtime-projection)
  defines the read-side Runtime projection used by Workflow model selectors.
- [`../../docs/actions/`](../../docs/actions/README.md) defines user-facing
  product contracts for each Action.

## Model Catalogs

Each Runtime may report the models and variants available in its own configured
environment. Runner registration carries those catalogs in `runtimeCatalogs`,
keyed by the existing Runtime ID (`opencode` or `pi`). The Server model-catalog
query accepts that Runtime ID and aggregates only matching Runner entries.
Runner registration includes the latest catalog of each ready Runtime. Pi loads
its catalog before becoming ready as defined in [`pi.md`](pi.md); the host then
projects `PiRuntime.catalog()` into `runtimeCatalogs.pi`. OpenCode and Pi discover
their catalogs independently.

The current read contract remains:

```text literal
GET /api/projects/{projectRef}/opencode/models?runtime={runtimeId}
```

It returns `{ models, modelVariants }`. Profile-backed callers must provide the
derived Runtime explicitly; they must not rely on the endpoint's historical
OpenCode default. Renaming this route is unrelated to correcting model
selection and is outside this change.

The catalog is a configuration aid, not execution authority. The selected
Action or Agent still owns Runtime selection, and the Runtime validates the
model when execution starts. An empty or unavailable Pi catalog must not fall
back to OpenCode, and an unavailable OpenCode catalog must not fall back to Pi.
A configured model that is absent from the current catalog remains configured
until the user explicitly changes or clears it.

Workflow configuration does not add a Runtime field. An inline task selects its
Runtime through `uses`; a named Mohist Agent selects its Runtime through the
Agent definition. `vars.agent` continues to carry only Action options such as
`model` and `variant`.

Add one independent file for a new Runtime. Do not create a common Runtime
interface in advance for hypothetical similarities. A new built-in Agent
Runtime adds its Runtime module, `mohist/<runtime-id>` Action, catalog entry, and
the corresponding Profile projection mapping. It does not change the Workflow
DSL or existing Action manifests.
