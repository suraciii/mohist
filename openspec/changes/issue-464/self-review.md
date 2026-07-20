# Self Review Report

## Result: FAIL

## Blocking Items

- [ID: item-1]
  Severity: high
  Scope: consistency
  Evidence: `specs/runner-model-discovery/spec.md:17-22` requires the first periodic rediscovery exactly one full interval after startup discovery. `design.md:63-69` orders startup discovery before `connectRunner`, but starts periodic timers only after connection; `design.md:73` explicitly preserves the existing timer registration. Connection retries and startup convergence can take arbitrary time, so that design can fire later than the spec permits. `tasks.json:37` repeats the spec timing but does not resolve the contradictory implementation order.
  SuggestedAction: Choose one clock origin and align the spec, design, and T-002. If the existing lifecycle is intended, define the first fire as one interval after timer registration/run-loop startup. If discovery completion is authoritative, move timer scheduling immediately after startup discovery and design cleanup for failures or delays before connection.
  Status: open

- [ID: item-2]
  Severity: high
  Scope: specification
  Evidence: `specs/opencode-model-catalog/spec.md:69-80` requires output with no parseable model entries to produce an empty result, while `:37-48` and `design.md:53-57` refer only to an "identifiable model" or "model identifier line" without defining what makes a line identifiable or parseable. The planned line-oriented parser can therefore treat arbitrary warnings, diagnostics, or malformed metadata lines as model IDs and still claim compliance. `tasks.json:14-15` asks for an unparseable-output test but supplies no grammar from which its expected result can be derived.
  SuggestedAction: Define the model-entry grammar and recovery boundary. At minimum, state how a `provider/modelID` header is recognized, whether flat non-verbose lists remain accepted, how blank lines delimit entries, and which line is consumed after an unbalanced or malformed JSON block; then make parser scenarios and T-001 tests use concrete examples.
  Status: open

- [ID: item-3]
  Severity: medium
  Scope: test coverage
  Evidence: Issue acceptance explicitly includes the Agent editor model selector. `tasks.json:41` claims existing tests cover create/agent model selection, but `packages/web/src/widgets/agent-profile-editor/ui/AgentProfileEditor.test.tsx:31-36` always returns `modelVariants: {}`. Its only variant assertions concern a pre-existing `agentConfig`; it never renders an Agent editor variant chip, selects one, persists it, reopens it as active, or clears it. Shared `ModelSelect` tests prove the generic component, not the Agent editor's query and state wiring at `AgentProfileEditor.tsx:212-225`.
  SuggestedAction: Change T-002 from merely rerunning existing tests to adding or updating an AgentProfileEditor regression test with populated `modelVariants`. Require chip rendering, model-plus-variant persistence, active state on reopen, model-only selection, and clear behavior through the editor surface.
  Status: open

- [ID: item-4]
  Severity: medium
  Scope: regression verification
  Evidence: Complete stdout capture is a central issue requirement (`specs/opencode-model-catalog/spec.md:55-67`). `design.md:49` places the test seam at the command-runner boundary, above the production `spawnSync` adapter, while `tasks.json:13` proposes feeding a large, already-complete string through that seam without starting a process. Such a test validates only parser capacity; it remains green if the production adapter later switches back to a truncating asynchronous implementation or resolves before stdout drains.
  SuggestedAction: Add a deterministic verification of the production command boundary without invoking a real process. Move or add a seam around the synchronous executor/result, assert the production adapter selects the synchronous path with the required timeout/buffer/encoding options, and pass its returned full stdout into parsing. Keep the large-payload parser test as separate coverage rather than presenting it as proof of pipe draining.
  Status: open

## Coverage Summary

- The proposal's three capabilities have matching spec directories and each is referenced by `tasks.json`.
- All requirements have four-hash WHEN/THEN scenarios, and the issue's runtime-health, refresh, registration-contract, and non-goal boundaries are otherwise represented.
- `tasks.json` is valid JSON. T-002 consumes T-001, its dependency points to a strictly lower priority, and the two-task graph is acyclic.
- The atomic T-002 boundary correctly keeps host switchover and runtime catalog removal together, avoiding a broken intermediate runtime API.
- The four open items above leave behavior or verification ambiguous for timing, parser failure semantics, one required selector entry point, and the known stdout-truncation regression.

<promise>FAIL</promise>
