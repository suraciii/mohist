## Context

Today a model is configured in three places — Agent definitions (`agentConfig.model`), issue Coder Model + per-stage overrides (`IssueInfo.Model` / `StageModels`), and project + per-stage defaults — and the runner applies it to an opencode coder session via `applyRequestedModel` (`packages/runner/src/actions/acp-agent.ts:642`), which calls `connection.setSessionConfigOption({ configId: "model", value })`. There is no lever to dial reasoning effort without swapping the model.

The available models come from a single discovery path: the runner runs `opencode models` (`packages/runner/src/runtime/opencode-models.ts`), reports the resulting `string[]` as `coderModels` during registration (`host.ts:234`), the server aggregates it in `RunnerRegistryGrain`, and the web reads it from `GET /api/projects/{id}/opencode/models` → `{ models: string[] }`.

**Confirmed discovery structure.** `opencode models --verbose` emits `<provider/model>\n{ <json metadata> }` pairs. The JSON has a top-level `variants` map, e.g.:

```json
"variants": {
  "low":    { "reasoningEffort": "low" },
  "medium": { "reasoningEffort": "medium" },
  "high":   { "reasoningEffort": "high" },
  "max":    { "reasoningEffort": "max" }
}
```

Models without variants report `"variants": {}`. This `variants` map is exactly the per-model legal tier set the issue calls for ("模型发现时随模型一并返回其 variants"). OpenCode selects a variant natively via the `provider/model:<variant>` model-id syntax.

Constraints: no DB migration (variants ride existing JSON columns); behavior with no variant set must be byte-identical to today; the codebase is pre-1.0 with no version-compat obligation (AGENTS.md), though the proposal committed to additive behavior.

## Goals / Non-Goals

**Goals:**
- Surface a model-bound optional reasoning variant on every existing model-config surface.
- Derive the legal variant set per model from the existing discovery mechanism (no new runtime probing).
- Persist the selected variant with its model and round-trip it through create/update/show.
- Deliver the variant to the coder session before prompt execution, best-effort.
- Keep "no variant set" indistinguishable from today.

**Non-Goals:**
- No global default variant; no mid-run variant switching.
- No model/provider management (keys, adding models).
- No hard validation that rejects a stored variant whose model no longer supports it (best-effort only).
- No surfacing of variant body keys beyond the selectable variant *name* (e.g. cost/limit metadata stays internal).

## Decisions

### D1. Discovery switches to `opencode models --verbose` and parses the `variants` map
`discoverOpencodeModels` (`opencode-models.ts`) SHALL run `opencode models --verbose` and parse the `id\n{json}` stream, extracting each model's top-level `variants` object and projecting it to a flat `string[]` of variant names (the keys). Parsing SHALL be defensive: a model whose JSON is missing `variants` or fails to parse SHALL be treated as having an empty variant set, never as a discovery failure (preserves the existing "discovery failure → report error" semantics only for command/transport failures). The 30-minute discovery cache (`agent-runtime` spec) is reused unchanged.

- *Alternative*: keep the plain `opencode models` command and add a second `--verbose` call only when a selector opens. Rejected — doubles process spawns and breaks "selector reads discovery directly, no runtime probing" (model-reasoning-variants spec).
- *Alternative*: add a Mohist-side static variant map keyed by model family. Rejected — duplicates opencode's source of truth and drifts.

### D2. The variant is stored as a *name*, in a separate optional field bound to its model
The user-facing tier is the variant **name** (e.g. `"high"`). The variant *body* (`{reasoningEffort: ...}`) lives only in discovery results and is never persisted. Storage is a parallel optional field next to each model field, so "clear the model ⇒ clear the variant" is explicit and atomic:

- Issue: `modelVariant: string?` beside `model`; `stageModelVariants: Dictionary<string,string>?` beside `stageModels` (`IssueInfo.cs`, `IssueReadModel.cs`, `IssueRoutes.Dtos.cs`).
- Agent: a new `variant` key inside the opaque `agentConfig` dict (`BuildAgentConfig`, `MohistIssueWorkflowProfileBase.cs:86`). Because `agentConfig` is opaque metadata (agent-definitions spec), this needs no agent-definitions spec change.

All variant fields ride the existing nullable JSON columns — **no schema migration**.

- *Alternative*: encode the variant into the model string (`"provider/model:high"`). Rejected for storage — it obscures clear-on-model-clear semantics, complicates `provider/model` format validation (http-api spec), and forces every model-comparing site to strip suffixes. The suffix is used *only at delivery* (see D5).
- *Alternative*: nest `{model, variant}` objects inside `stageModels`. Rejected — breaks the existing `Dictionary<string,string>` shape needlessly when a parallel map is additive.

### D3. Transport is additive: keep `string[]`, add a parallel variants map
The runner registration payload and `/api/projects/{id}/opencode/models` response SHALL keep the existing `coderModels` / `models: string[]` unchanged and add a parallel variants map (`coderModelVariants: Record<modelId, string[]>` on registration; `modelVariants: Record<modelId, string[]>` on the endpoint). A model with no variants is absent from the map (or mapped to `[]`). This is strictly field-additive: the existing `coderModels: string[]` consumers — notably the runner-status Web view (`RunnerStatusRow.coderModels`, `RunnerList.tsx`) and the runner-status server DTO — keep working unchanged, and the http-api spec's "same shape" backward-compatibility scenario holds literally.

- Behavior stays additive: a model absent from the variants map (or mapped to `[]`) is presented and dispatched exactly as today.
- *Alternative*: evolve `models` to `Array<{ id, variants: string[] }>` (structured items). Rejected — it changes the element shape already consumed by the runner-status Web view and server DTO, ripples into unrelated work, and contradicts the http-api spec's same-shape contract. Revisit only if model items later need to carry more than variants.

### D4. Variant flows through the issue-creation-fixed agent config like the model
Per the workflow-engine spec, effective agent config is fixed once at issue creation by the `Variables` merge; per-stage dispatch reads `Variables.stages[stage].vars.agent`. The variant SHALL ride the same path: the issue/agent variant is written into the opaque agent config during `BuildVariables`, and per-stage dispatch carries it alongside the model. Runtime dispatch SHALL NOT re-resolve variants. Recovery sessions inherit the same pre-merged value.

### D5. Delivery composes `provider/model:<variant>` at the single existing injection point
The runner's `resolveAgentConfig` (`acp-agent.ts:572`) SHALL additionally read a `variant` from the `agent`/`with` config, and `resolveRequestedModel` (`acp-agent.ts:634`) SHALL compose the effective model id as `${model}:${variant}` when a variant is present. `applyRequestedModel` (`acp-agent.ts:642`) then sets that id via the existing `setSessionConfigOption` call — **no new session-config path**.

This satisfies best-effort (model-reasoning-variants invariant #3) almost for free: `applyRequestedModel` already wraps the set call in try/catch and logs a warning on failure, so an unsupported/ignored variant cannot flip a successful run to failed.

Session-reuse correctness: the cached-session match (`requestedModelMatchesSession`, `cachedModelAllowsReuse`, `acp-agent.ts:711-743`) compares model strings; because the variant is composed into that string, a variant change naturally selects a fresh session rather than reusing one with a different effort. The cache key/match SHALL compare the full composed id.

- *Alternative*: set the variant body as a separate option (`reasoningEffort`) after setting the model. Rejected — adds a second config call per session start, requires maintaining the option-key mapping, and offers no benefit over opencode's native `:variant` syntax.
- *Spike needed*: confirm `provider/model:<variant>` selection across all providers opencode exposes (confirmed for `reasoningEffort`-bearing variants; the build should verify edge providers early).

### D6. UI variant picker is fully driven by discovery, bound to the selected model
Every model selector (issue default + per-stage, project + per-stage defaults, agent editor) gains a variant picker that reads the selected model's entry from the `modelVariants` map on the `/opencode/models` response. Rules (web-ui spec): present only listed variants; hide when `variants: []`; on model change/clear, re-derive and drop a stored variant the new model does not support; on reopen, show the stored variant as selected. No selector issues a runtime probe.

## Risks / Trade-offs

- [opencode `--verbose` output / `variants` schema drift] → Defensive parse: missing/unparseable `variants` ⇒ empty set ⇒ today's behavior. Discovery command failure keeps existing error-reporting semantics.
- [Stored variant name becomes stale after a model change or opencode rename] → Accepted by design: dependency invariant (model-reasoning-variants) means a stale name is dropped from selection and delivered best-effort, never hard-rejected. No data repair required.
- [Composing `:variant` into the model id breaks session reuse] → Mitigated by keying the reuse cache on the full composed id (D5). Trade-off: switching variant spins a new session rather than mutating a live one — acceptable and within Non-Goals (no mid-run switching).
- [`--verbose` is heavier and may hit models.dev] → Reuses the existing 30-minute cache; only the runner runs discovery; on network failure, variants degrade to empty (today's behavior) while model ids still resolve when possible.
- [Endpoint consumer surface] → D3 keeps `models: string[]` unchanged and adds a parallel variants map, so existing consumers (runner-status view/DTO) are untouched; a client that ignores the map behaves as today.

## Migration Plan

No database migration. Deploy in order, each layer degrading gracefully to today's behavior if a neighbor hasn't shipped:

1. **Runner**: switch discovery to `--verbose`, report `coderModels: string[]` (unchanged) plus the `coderModelVariants` map; read+compose variant in `resolveAgentConfig`/`resolveRequestedModel`. A runner talking to an older server simply isn't asked for variants.
2. **Server**: accept the `coderModelVariants` map, serve `/opencode/models` with a `modelVariants` map alongside the unchanged `models[]`; accept/persist/round-trip `modelVariant` + `stageModelVariants`; thread variant into `BuildVariables`. An older web ignores the new fields.
3. **Web**: render the bound variant picker; send variant fields on create/update.

**Rollback**: revert the three layers. Any persisted variant fields are ignored by old code and silently dropped on next model clear; runs behave exactly as today. No data cleanup step is required.

## Open Questions

- Confirm `provider/model:<variant>` is honored uniformly across all opencode-exposed providers (early build spike; D5).
- Final field naming: `variant` (matches the discovery key) vs `reasoningVariant`/`effort`. Leaning `variant` for consistency with discovery; confirm during build.
- Whether variant bodies carrying keys other than `reasoningEffort` (e.g. future `budget`/`temperature` caps) should ever be surfaced. Out of scope here; the name-only model (D2) leaves room to extend later without a storage change.
