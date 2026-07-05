## Context

Model selection in Mohist flows through three layers (project default → issue override → issue stage override), each stored as a `vars.agent = { type, model, variant? }` leaf inside a `VariableBundle`. The server merges these layers via deep-merge PATCH semantics documented in `VariableBundle.cs:78`: a JSON `null` value at a leaf **deletes** that key, while a key that is **omitted** is preserved. The resolved `agent` then rides the dispatch snapshot into the runner, where `composeRequestedModel` (`packages/runner/src/actions/acp/model-resolution.ts:25`) appends the variant suffix as `model/variant`.

The bug: several model-setting UI paths express "model chosen, no variant" by sending `{ type, model }` (omitting `variant`) or by short-circuiting entirely. Because omission = preserve, a previously stored variant survives and `composeRequestedModel` ships a non-existent `model/stale_variant` id to the ACP session. The merge contract is correct; only the Web payloads fail to express the delete intent consistently.

Verified inconsistent paths (2026-07-04):

| Path | Current behavior | Symptom |
|---|---|---|
| `IssueModelSelector.handleSelect` (`IssueModelSelector.tsx:219`) | sends `{ type, model }`, no `variant` key | stale issue `agent.variant` survives |
| `IssueModelSelector.handleSetStageModel` (`IssueModelSelector.tsx:272`) | sends stage-scoped `{ type, model }`, no `variant` | stale stage `agent.variant` survives (local state already drops it) |
| `AiSettingsSection.handleSetOpencodeModel` (`AiSettingsSection.tsx:78`) | `if (modelId === storedDefaultModel) return` short-circuits | clicking the already-selected row cannot clear a stale default variant |

Correct path (reference implementation): `AiSettingsSection` already issues `{ model, variant: null }` for the project default; this fix propagates that same pattern to the three stragglers.

Stakeholders: Web UI ( sole emitter of these PATCHes ), Server ( merge contract — unchanged ), Runner ( consumer — unchanged ). The server already has spec coverage for null-leaf deletion; the runner already covers the `variant: undefined` composition path. The fix is Web-local.

## Goals / Non-Goals

**Goals:**
- Make every "model selected without variant" UI path express the delete intent explicitly via `variant: null`, so a stale variant can never survive the merge and never reach the runner.
- Cover three specific paths: issue default model, issue stage model, and project-default same-row re-click.
- Preserve the generic `PATCH .../workflow-profile/variables` contract (`null` = delete leaf, omitted = preserve) unchanged.
- Add regression coverage that locks the null-delete payload for each fixed path plus the runner composition behavior, so the bug cannot silently reappear.

**Non-Goals:**
- Do **not** alter the server's `VariableBundle.Patch` / `DeepMerge` merge semantics — they are the contract this fix relies on.
- Do **not** change the runner's `composeRequestedModel` — it already does the right thing once the variant is absent from the snapshot.
- Do **not** introduce a new API, storage shape, workflow-definition change, or migration.
- Do **not** refactor the broader model-selection UI architecture; the change is point-wise on the three handlers' PATCH payloads.

## Decisions

### Decision 1: Express "no variant" as an explicit `variant: null` in the PATCH payload

`IssueModelSelector.handleSelect` and `handleSetStageModel` will add `variant: null` to the value object they pass to `patchIssueWorkflowDefinitionVar` / `patchIssueWorkflowStageDefinitionVar`. `AiSettingsSection.handleSetOpencodeModel` will drop the early-return guard so the same `variant: null` payload always fires on a model-row click.

- **Rationale:** `null` is the contract's delete marker. It is idempotent (deleting an absent key is a no-op), so it is safe to send unconditionally and removes the need for the UI to know whether a variant is currently stored.
- **Alternatives considered:**
  - *Send the full `agent` object with `variant` set to empty string `""`.* Rejected — the contract treats `""` as a string value, not a delete; it would store `variant: ""`, which the runner trims but the bundle still carries. `null` matches the documented intent precisely.
  - *Read the current `agent.variant` and conditionally include `variant: null` only when one exists.* Rejected — adds a read-before-write race and extra state plumbing for zero correctness benefit since the delete is idempotent.
  - *Change the merge semantics so omission = delete.* Rejected — omission-as-preserve is load-bearing for partial PATCHes across the codebase; changing it would break every other caller and is far out of scope for a UI payload bug.

### Decision 2: Keep the fix Web-local; no server/runner code change

- **Rationale:** The root cause is that emitters fail to express the documented delete intent. Server and runner already implement their halves correctly. Touching them would expand blast radius without addressing the cause.
- **Alternatives considered:** *Add a server-side guard that strips `agent.variant` whenever `agent.model` changes in a single PATCH.* Rejected — it would couple the generic merge to agent-specific business rules, violating the merge contract's domain-neutrality, and would not fix the project-default path (which goes through a different endpoint).

### Decision 3: Remove the `AiSettingsSection` same-row short-circuit rather than special-casing it

`handleSetOpencodeModel` currently `return`s when `modelId === storedDefaultModel`, presumably as a no-op optimization. Drop the guard entirely so the `variant: null` mutation always fires.

- **Rationale:** The mutation is idempotent (model unchanged, variant null-deleted), so the extra call is cheap and correct. Keeping a guard would require a second code path that sends `variant: null` only when a variant exists — more branches for the same outcome.
- **Alternatives considered:** *Keep the guard but expand it to fire when either model or variant differs.* Rejected — it reintroduces the exact bug class (a future state where the guard misjudges "no change needed").

### Decision 4: Regression coverage placed with the components it exercises

- **Web component tests** (`IssueModelSelector.test.tsx`, `AiSettingsSection.test.tsx`): assert the emitted PATCH payload carries `variant: null` for (a) issue default no-variant select, (b) issue stage no-variant select, (c) project same-row re-click. The existing issue-default test that currently asserts the buggy `{ type, model }` payload flips to expect `variant: null`.
- **Server `VariableBundleSpecs.cs`:** confirm/extend an `agent.variant` null-leaf deletion case so the contract the Web now depends on is locked at the merge layer.
- **Runner `session-strategies` specs:** already cover `variant: undefined` composition; confirm rather than add, per the proposal.
- **Rationale:** tests live next to the unit they constrain — UI payload shape in Web, merge contract in Server, composition in Runner. No new cross-cutting test harness is warranted.

## Risks / Trade-offs

- **[Idempotent PATCH now fires on every same-row click]** → minor extra network call. Mitigation: the mutation is cheap and idempotent; TanStack Query invalidation is already part of the flow. Trade-off accepted over a brittle conditional.
- **[A stale `agent.variant` already persisted for existing issues is not retroactively cleaned]** → existing issues that already carry a stale variant before this fix lands will only be cleaned when the user next touches the model selector. Mitigation: this matches the existing product behavior (no background migration of user-editable settings), and any subsequent model selection will repair it. A one-shot migration is explicitly a Non-Goal.
- **[Future model-setting paths could re-introduce the bug if they omit `variant`]** → add a regression test for the payload contract so a regression is caught; rely on the capability spec (`model-variant-clearing`) as the durable rule a future path must satisfy.
- **[Test that currently asserts the buggy payload must flip]** → trivial, but flagged here so the implementer does not treat the old assertion as a constraint to preserve.

## Migration Plan

- **Deploy:** Web-only change shipped in the next frontend build. No server/runner redeploy required, no schema migration, no workflow-definition change.
- **Order:** land the three handler fixes + their Web tests together in one PR; the server/runner confirmation tests can land in the same PR (extend `VariableBundleSpecs.cs`) or be verified to already pass.
- **Rollback:** revert the Web PR. The server's merge contract is unchanged, so a rollback simply restores the pre-fix (buggy) UI behavior — no data inconsistency, no orphaned keys. Any `variant: null` PATCHes that did land before rollback produce a clean delete and remain correct.
- **Verification post-deploy:** for an issue known to carry a stale variant, re-select the model without a variant and confirm the resolved model id sent to ACP no longer carries the suffix (observable in runner logs via `modelDiagnosticContext`).

## Open Questions

- Should the runner additionally log a warning when the resolved model id contains a `/`-segment that does not match any known variant, as a defense-in-depth signal? Out of scope for this issue (runner is intentionally unchanged), but worth a follow-up to make a future regression observable from the runner side.
- Is there a product desire to surface "a stale variant was cleared" in the UI so the user understands why the model id changed? Currently silent; acceptable given variant-clearing is the expected default behavior, but flagged for product review.
