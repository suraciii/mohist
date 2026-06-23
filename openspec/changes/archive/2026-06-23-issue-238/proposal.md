## Why

Model reasoning variants (推理档位) plumbed by #212 never reach a coder session. The runner's `applyRequestedModel` (`packages/runner/src/actions/acp-agent.ts:642`) calls `connection.setSessionConfigOption({ configId: "model" })`, a method opencode does not implement — the SDK silently swallows the call, so neither the model nor the variant is set and opencode falls back to its session default. Five in-flight issues (#168, #190, #219, #221, #222) are running without their configured reasoning effort, an invisible output-quality regression. The fix is urgent because users are configuring variants that are dropped on the floor for every run.

## What Changes

- Compose the requested model id as `provider/model/variant` when a variant is present, `provider/model` otherwise — the slash-separated format opencode's `unstable_setSessionModel` / `parseModelSelection` expects (replacing the never-working `model:variant` composition).
- Replace the no-op `setSessionConfigOption` call **and** its fallback in `applyRequestedModel` with a single `connection.unstable_setSessionModel({ sessionId, modelId })` call. **BREAKING** (internal): the `setSessionConfigOption` code path is removed entirely; the `set_session_config` liveness activity classification is no longer emitted for model application.
- Treat variant delivery as best-effort: if `unstable_setSessionModel` rejects (e.g. variant dropped from the model's variant map after a server-side config change), the runner logs a warning, records `variantDelivered: false`, and continues the run. The variant must never be the failure reason.
- Session-reuse cache keying (`requestedModelMatchesSession` / `cachedModelAllowsReuse`) operates on the full composed id, so changing the variant for the same model starts a fresh opencode session — opencode stores the variant on the session, so reusing across variants would be wrong.
- Add `variantDelivered: boolean` to the `modelDiagnosticContext` log payload; keep the existing `requestedModel` / `requestedModelSource` fields.
- Update the six "reasoning variant delivery" tests in `packages/runner/tests/acp-agent.spec.ts` to assert delivery via `unstable_setSessionModel` (never `setSessionConfigOption`), the composed id, rejection tolerance with `variantDelivered: false`, and session-reuse keying on the composed id.

## Capabilities

### New Capabilities

- `model-reasoning-variants`: Runner-side delivery of per-model reasoning variants to opencode coder sessions — composed `provider/model/variant` id format, single best-effort `unstable_setSessionModel` call, `variantDelivered` diagnostic, session-reuse keying on the composed id, and the invariant that a rejected variant never fails the run.

### Modified Capabilities

<!-- None. No existing spec constrains the apply-model-to-session path: `setSessionConfigOption`, `applyRequestedModel`, and session-reuse model keying are absent from all specs in openspec/specs/. The new `model-reasoning-variants` capability owns the delivery contract from scratch. -->

## Impact

- **Runner code**: `packages/runner/src/actions/acp-agent.ts` — `resolveRequestedModel` (compose `model/variant`), `applyRequestedModel` (single `unstable_setSessionModel`, remove config-option path + best-effort handling), `requestedModelMatchesSession` / `cachedModelAllowsReuse` (key on composed id via `requested.model`), `modelDiagnosticContext` (add `variantDelivered`).
- **Runner tests**: `packages/runner/tests/acp-agent.spec.ts` — the six tests in the "reasoning variant delivery" block.
- **No server changes** — `coderModelVariants` is already plumbed end-to-end (#212).
- **No Web changes** — `VariantPicker` already emits a bare variant string.
- **No ACP protocol changes** — uses the existing `unstable_setSessionModel` opencode already implements.
- **Out of scope**: Web UI ergonomics (#239); server-side variant legal-set validation (#212).
