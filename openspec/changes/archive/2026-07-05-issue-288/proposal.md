## Why

Selecting a model without choosing a reasoning variant silently keeps the previously selected variant. The user expects "no variant picked = use the provider default", but several model-setting paths send a PATCH that omits `variant` (or short-circuits entirely), and the server's merge semantics treat an omitted key as preserve. The stale `agent.variant` then rides the dispatch snapshot into the runner, where `composeRequestedModel` glues it onto the model id (`model-resolution.ts:29`) and ships `model/stale_variant` to the ACP session — a model id that does not exist. The server already implements the correct contract (`null` = delete leaf, omitted = preserve in `VariableBundle.Patch`); the gap is purely that the model-setting business actions do not consistently express the delete intent.

## What Changes

- **Issue default model** (`IssueModelSelector.handleSelect`, `IssueModelSelector.tsx:219`): when a model is picked without a variant, the PATCH now sends `variant: null` alongside `type`/`model` so the server deletes any previously stored `agent.variant`. Today it sends `{ type, model }` with no `variant` key, leaving the old variant behind.
- **Issue stage model** (`IssueModelSelector.handleSetStageModel`, `IssueModelSelector.tsx:272`): same fix at stage scope — the stage-scoped PATCH sends `variant: null`, deleting the stale stage variant. (The local state already drops the variant; only the persisted PATCH was wrong.)
- **Project default model re-click** (`AiSettingsSection.handleSetOpencodeModel`, `AiSettingsSection.tsx:78`): remove the `if (modelId === storedDefaultModel) return` short-circuit so that clicking the already-selected model row still fires the mutation with `variant: null`, clearing a stale default variant instead of no-op'ing.
- **No change** to the generic `PATCH .../workflow-profile/variables` semantics: `null` = delete leaf, omitted = preserve (`VariableBundle.Patch`, `VariableBundle.cs:78`). This fix relies on that contract; it does not alter it.
- **Regression coverage**: assert the null-delete contract holds for model-setting PATCHes across the issue default selector, the issue stage selector, the project-level same-row re-click, and a direct `VariableBundle.Patch` null-leaf case.

## Capabilities

- `model-variant-clearing`: Required behavior that any model-setting action which selects a model without a reasoning variant must clear a previously stored variant for that scope (issue default `agent.variant`, issue stage `agent.variant`, and project default `variant`). Covers the PATCH payload contract (`variant: null` = delete, omitted = preserve), the three previously inconsistent UI paths, and the resulting runner-side composition so a stale variant can never be appended to the model id.

## Impact

- **Web (`packages/web/src/features/select-issue-model/ui/IssueModelSelector.tsx`)**: `handleSelect` and `handleSetStageModel` add `variant: null` to their `patchIssueWorkflowDefinitionVar` / `patchIssueWorkflowStageDefinitionVar` payloads.
- **Web (`packages/web/src/pages/settings/ui/AiSettingsSection.tsx`)**: `handleSetOpencodeModel` drops the early `return` and always calls `setOpencodeModel.mutate({ model, variant: null })` when a model row is chosen.
- **Web tests**: `IssueModelSelector.test.tsx` (the "selects the default variant (no variant)" case currently asserts the buggy `{ type, model }` payload — flips to expect `variant: null`) and `AiSettingsSection.test.tsx` (new case for same-row re-click clearing variant). Stage-model variant-clearing case added.
- **Server (`packages/server/src/Mohist.Server/Workflow/Domain/VariableBundle.cs`)**: no code change; existing `VariableBundleSpecs.cs` already covers null-leaf deletion — confirm/extend to `agent.variant` shape if a gap exists.
- **Runner (`packages/runner/src/actions/acp/model-resolution.ts`)**: no code change; once the variant is gone from the snapshot, `composeRequestedModel` stops appending the suffix. Covered by existing `session-strategies` specs that already exercise the `variant: undefined` path.
- **No API, storage, dependency, or workflow-definition changes.** Non-breaking at the HTTP contract — only the payloads the Web emits change, and they move toward the contract the server already documents.
