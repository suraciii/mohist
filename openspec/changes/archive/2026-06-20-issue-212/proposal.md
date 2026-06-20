## Why

When Mohist points at a reasoning-capable model, users have no lever to dial reasoning effort up or down in place — they must swap the whole model, so they cannot trade quality for cost at a single configuration surface. Different models support different effort tiers (some none at all), and today no surface reflects a model's real supported tiers, so users can only guess or pick a value the model silently ignores.

## What Changes

- Add an optional **reasoning variant** (推理档位) bound to a model everywhere a model is configurable today: Agent definitions, the issue Coder Model selector (including per-stage), and project + per-stage defaults.
- Model discovery returns each model's supported variant set alongside its id. The legal set is per-model (possibly empty) and is never a fixed enum; UI reads it directly from discovery results with no runtime probing.
- Model selectors expose a variant picker that shows only the variants the selected model actually supports; models with no variants hide the picker. Switching or clearing the model re-derives the legal set and drops a previously stored variant that the new model does not support.
- A selected variant is persisted with its model and round-trips through create/update/show.
- The runner delivers the selected variant to the coder session before prompt execution. Delivery is **best-effort**: an unsupported variant SHALL NOT turn an otherwise-successful run into a failure, and the system SHALL NOT hard-reject a stored variant whose model no longer supports it.
- With no variant set, behavior stays identical to today.

## Capabilities

### New Capabilities

- `model-reasoning-variants`: The contract for a model-bound reasoning variant — what it is and the three governing invariants: (1) **dependency** — a stored variant is only valid for the model it was chosen with, and is not guaranteed after a model change or clear; (2) **non-enumerable legal set** — legal variants are the set returned alongside a model during discovery, differing per model including the empty set; (3) **best-effort delivery** — delivering an unsupported variant to a session must never flip a successful run to failed.

### Modified Capabilities

- `agent-runtime`: Model discovery returns per-model variant sets alongside model ids (extending the lightweight `opencode models` discovery requirement); the runner applies a delivered variant to the coder session and treats an unsupported variant as best-effort.
- `http-api`: Issue model metadata and the opencode/models endpoint carry per-model variant info; issue create/update/show round-trip the selected variant alongside `model` and `stageModels`.
- `local-issue-store`: Persist an issue-level variant alongside `model` and per-stage variants alongside `stageModels`; clearing the model clears the bound variant.
- `web-ui`: Every model selector (issue default, per-stage, project/stage defaults, agent editor) gains a variant picker bound to the selected model; the picker refreshes on model change and is hidden when the model supports no variants.
- `workflow-engine`: The variant is part of the agent config fixed once at issue creation and is read during per-stage agent dispatch alongside the model.

## Impact

- **Runner**: `opencode models` discovery output and the `coderModels` runner-registration payload gain variant data; coder session launch applies the variant to the opencode session on a best-effort basis.
- **Server**: The runner registry and `GET /api/projects/{id}/opencode/models` response gain a per-model variants map alongside the existing `string[]` model list; issue API model metadata gains optional variant fields; per-stage agent dispatch carries the variant. **No database migration** — variants ride on the existing `model`/`stageModels`/`agentConfig` JSON.
- **Web**: All model selectors gain a dependent variant picker whose state must re-derive on model change.
- **Public contracts changed (additive, non-breaking)**: (1) the model-discovery / opencode-models response structure; (2) issue and agent model-metadata fields. Both remain optional, so omitted variants reproduce today's behavior.
