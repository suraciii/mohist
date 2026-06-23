## Context

Issue #212 wired reasoning-variant *discovery* end-to-end (`opencode models --verbose` → runner `coderModelVariants` → server → Web `VariantPicker`). The *delivery* path is broken: the runner's `applyRequestedModel` (`packages/runner/src/actions/acp-agent.ts:642`) calls `connection.setSessionConfigOption({ configId: "model" })`, which opencode does not implement — the SDK silently swallows the call, so neither model nor variant is set and opencode falls back to its session default. The earlier `model:variant` composition (colon) was also wrong; opencode's `parseModelSelection` splits solely on `/`.

Investigation against opencode's `agent.ts` confirmed:
- The only model-setting method opencode implements is `unstable_setSessionModel`.
- `parseModelSelection(modelId, providers)` splits on `/` and treats the last segment as a variant iff it matches a key in the model's `variants` map.
- The parsed variant is stored on the session and forwarded into `sdk.session.prompt({ variant })` at prompt time → provider-side `reasoningEffort` / `thinking.budgetTokens`.

**How the variant reaches the runner today.** The server has no strongly-typed `Variant` property; the chosen variant travels as an opaque JSON field inside the workflow profile's `vars.agent` (and stage overlay `stages.<stage>.vars.agent`). `WorkflowDispatchBuilder.ResolveDispatchWith` (`WorkflowDispatchBuilder.cs:154-161`) copies `effectiveVarsJson.agent` into the dispatched `with.agent` via the generic deep-merge (`DeepMergeSkippingNulls`, `WorkflowDispatchHelpers.cs:32`). The variant therefore already arrives at the runner as `context.with.agent.variant` — the runner's `resolveAgentConfig` just never reads it. This is why the issue scope is runner-only.

Current state of the four touch points (all in `packages/runner/src/actions/acp-agent.ts`):
- `AgentConfig` (346-353) and `RequestedModel` (355-358): no `variant` field.
- `resolveAgentConfig` (572-593): reads `model` from `agent`/`with`; no `variant` read.
- `resolveRequestedModel` (634-640): returns bare `model`; never composes a variant.
- `applyRequestedModel` (642-663): two-tier — primary `setSessionConfigOption` (no-op), fallback `unstable_setSessionModel`.
- `requestedModelMatchesSession` (740-744) / `cachedModelAllowsReuse` (746-752): string-compare on `requested.model`. At line 699 `requestedModel = resolveRequestedModel(...).model`, and `manager.set(..., { model: requestedModel })` at 718/723 caches the same string — so reuse keying already follows whatever `.model` carries.

## Goals / Non-Goals

**Goals:**
- Deliver the user-configured reasoning variant to the opencode coder session via the slash-separated `provider/model/variant` id, using the one method opencode actually implements.
- Make delivery best-effort: a rejected variant never fails a run, and the outcome is observable in diagnostics (`variantDelivered`, `requestedVariant`).
- Ensure changing the variant for the same model starts a fresh session (opencode pins the variant on the session).
- Achieve the above with runner-only changes; no server, Web, CLI, or ACP-protocol changes.

**Non-Goals:**
- Web `VariantPicker` ergonomics (3-level cascade, inline chips) — #239.
- Server-side variant legal-set validation — #212.
- Changing how the server stores or projects `stageModelVariants` (opaque JSON today; stays opaque).
- Distinguishing *why* `unstable_setSessionModel` rejected (unknown variant vs. unknown model vs. transient). All rejections are treated uniformly as best-effort.

## Decisions

### D1: Read the variant from `with.agent.variant` (mirror `model`); add it to `AgentConfig` and `RequestedModel`

Add `variant?: string` to `AgentConfig` (346) and read it in `resolveAgentConfig` (572) from both the `agent` sub-object and the top-level `with` — exactly mirroring how `model` is resolved today (`stringInput(agent, "variant")` / `stringInput(with_, "variant")`). Add `variant?: string` to `RequestedModel` (355) for diagnostics.

The variant is already present in `context.with.agent.variant` thanks to the server's generic vars deep-merge (`WorkflowDispatchBuilder.cs:154-161`), so no server work is needed.

**Alternatives considered:**
- *Compose server-side and ship `provider/model/variant` as `agent.model`.* Rejected: (a) issue scope is runner-only; (b) the runner owns the ACP delivery format and the server should not learn opencode's separator convention; (c) keeping `variant` separate preserves the `requestedVariant` diagnostic and lets the runner decide formatting.
- *Read variant only from `with.variant` (top-level).* Rejected: `model` already checks `agent.model` first, then `with.model`; variant must mirror that precedence or stage-overlay variants (`stages.<stage>.vars.agent.variant`) would be ignored when a top-level `with.model` is also set.

### D2: Compose the slash-separated id inside `resolveRequestedModel`, not at the ACP call site

`resolveRequestedModel` (634) builds `requested.model` as `${model}/${variant}` when a non-empty trimmed variant is present, else bare `${model}`. Empty/whitespace variant is treated as absent (no trailing slash). The composed value flows everywhere `requested.model` is read: the ACP call, the session-reuse cache (`manager.set` at 718/723 stores `requestedModel` from line 699), and `modelDiagnosticContext`.

**Why here:** the reuse-keying helpers (740-752) and the cache (718/723) compare on the resolved `.model` string. Composing at the call site would leave them keyed on the bare model and silently reuse sessions across variants — exactly the bug the spec forbids. Composing at the source makes correct reuse keying free.

**Alternatives considered:**
- *Compose inside `applyRequestedModel`.* Rejected per above (reuse keying would break).
- *Add a separate `composedId` field.* Rejected: redundant; `requested.model` is already the canonical delivery id.

### D3: Replace the two-tier call with a single `unstable_setSessionModel`

Rewrite `applyRequestedModel` (642-663) to a single `connection.unstable_setSessionModel({ sessionId, modelId: requested.model })` call, ordered before the prompt. Remove the `setSessionConfigOption` primary path and its `unstable_setSessionModel` fallback entirely. On the single call's success, emit `classifyAcpLivenessActivity({ kind: "protocol_response", response: "set_session_model" })`. Drop the `set_session_config` liveness classification for model application (it logged success of a no-op).

**Rationale:** opencode does not implement `setSessionConfigOption` at all — the "primary" was a silent no-op and the "fallback" was the only working path. Keeping the dead branch adds confusion and a misleading activity log.

**Alternatives considered:**
- *Keep `setSessionConfigOption` as a no harm primary.* Rejected: it is harmful — it logs false success and obscures the real delivery path.

### D4: Best-effort error handling — `variantDelivered` boolean threaded into diagnostics

Wrap the single `unstable_setSessionModel` call in try/catch. Compute a local `variantDelivered: boolean` (`true` on resolve, `false` on reject). On reject: `console.warn` with the diagnostic context and continue (no rethrow, no task failure). Thread `variantDelivered` into `modelDiagnosticContext` by adding a parameter (the function is pure and currently takes only `context` + `requested`). Also add `requestedVariant: requested.variant ?? null` to the context for provider-side correlation; preserve existing `requestedModel` / `requestedModelSource`.

For the early-return "no model configured" path (643-645), no ACP call is made, so `variantDelivered` is **omitted** from that log line (no delivery claim either way).

**Alternatives considered:**
- *Mutate `requested.variantDelivered`.* Rejected: `RequestedModel` is a resolution result, not a delivery outcome; mixing them muddies the type.
- *Retry the variant-less id on rejection.* Rejected: out of scope; spec only requires the run survives. A retry would also mask the rejection from diagnostics.

### D5: Session-reuse keying — no helper change required

`requestedModelMatchesSession` (740) and `cachedModelAllowsReuse` (746) already string-compare `requested.model` against `session.model` / `cached.model`. Because D2 makes `requested.model` carry the full composed id, and because line 699 + 718/723 store the same composed id in the cache, switching variant (`.../max` → `.../high`) naturally produces a mismatch → fresh session. No edits to these helpers or their call sites.

This is the load-bearing invariant of the design: **composition must happen at the resolution source (D2) precisely so D5 stays a no-op.**

## Risks / Trade-offs

- [`unstable_setSessionModel` is an opencode "unstable" API and may change] → Mitigation: it is today the *only* model-setting method opencode implements; the acceptance criterion's manual smoke (#190 plan stage with `variant: max`, checking `reasoning_effort=max` in opencode logs) pins the contract. If opencode renames it, the single call site is the only place to update.
- [Removing `setSessionConfigOption` changes liveness activity output that existing tests assert] → Mitigation: the affected tests (acp-agent.spec.ts lines 35-70, 272-321) are the ones the issue explicitly scopes for rewrite; they map 1:1 to the new spec scenarios. Test churn is bounded and intentional.
- [Variant string with unexpected casing/whitespace] → Mitigation: trim before composition; treat empty as absent (spec scenario "Empty variant is treated as absent"). Do not lowercase — variant keys are provider-specific and case-sensitive in opencode's `variants` map.
- [Variant valid at config time but disabled server-side before the run] → Mitigation: this is exactly the best-effort path (D4); `variantDelivered: false` is recorded and the run continues on the session-default model.
- [Silent fallback to session-default model looks like success to the user] → Mitigation: `variantDelivered: false` + `requestedVariant` in diagnostics make the drop observable. Surfacing this in the Web UI is #239 (out of scope here).

## Migration Plan

Single-PR, runner-only change. No data migration, no API contract change (server/Web/CLI untouched), no schema change.

- **Deploy:** merge; the next runner restart picks up the new delivery path. Existing in-flight sessions continue on whatever model they were started with (the change applies at session establishment).
- **Rollback:** revert the commit. Behavior returns to today's silent no-op (`setSessionConfigOption` swallowed), which is no worse than the pre-fix state — variants were already not being delivered.
- **Verification:** `npm run typecheck -w packages/runner` and `npm test -w packages/runner` must pass; plus the acceptance-criteria smoke (#190 plan stage with `variant: max`).

## Open Questions

- **OQ-1:** Does opencode's `unstable_setSessionModel` distinguish "unknown variant" from "unknown model" in its rejection, or return a generic error? D4 treats all rejections uniformly (sufficient for the spec). Confirm during the #190 smoke; if a typed error exists, a follow-up could classify it for richer diagnostics. Not blocking.
- **OQ-2:** Should the "no model configured" early-return log emit `variantDelivered`? D4 omits it (no call made). Review to confirm omission is acceptable vs. explicitly `null`.
- **OQ-3:** The variant reaches the runner as `with.agent.variant` via the server's opaque JSON deep-merge. If a future server change introduces strongly-typed variant handling, the runner's read path (D1) is unaffected — it only reads JSON. Confirm at review that no server-side `agent.variant` sanitization is planned that would strip the field before dispatch.
