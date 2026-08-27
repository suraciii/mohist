# Design

## Context

Issue #444 established manifest-backed Actions and the rule that the manifest is authoritative for Action-owned business errors. The current Runner already validates result codes against a manifest and maps a non-reserved undeclared code to `unexpected-error`. The remaining contract gap is completeness: the `mohist/opencode` execution path emits `execution-unavailable` for a failed Workflow input handoff, while the manifest does not declare it. The OpenCode runtime also maps its internal `unsupported-execution-configuration` kind to the cross-system category `unsupported_execution_configuration`, but the Action manifest grammar permits only lowercase kebab-case codes.

The current `mohist/pi` path has the shared runtime-unavailable, Session, reporting, and turn failures, plus its own runtime error taxonomy. A bounded source audit also shows concrete omissions: the skill resolver returns `skill_not_found`, the provider retry diagnostic can produce `provider-quota-exhausted`, and the Pi runtime error mapping can return `incompatible-runtime` and `interrupted` as ActionResult codes. These are included because they are reachable from the current production Action path, not because Pi is merely a peer.

## Goals

- Make the `mohist/opencode` and `mohist/pi` manifests enumerate every statically evidenced non-reserved code they can return at the Action boundary.
- Preserve the existing normalization authority: declared Action codes pass through, reserved platform codes remain platform-owned, and undeclared codes become `unexpected-error`.
- Define deterministic mappings from runtime or resolver diagnostic vocabulary to Action result vocabulary where spellings differ.
- Keep the inventory bounded to current production Action paths and exclude unrelated Actions or speculative codes.
- Align both concrete Agent Action documentation pages with their manifests without altering product scope.

## Non-Goals

- Adding any reserved platform code to an Action manifest.
- Weakening `defineAction` or result validation to permit underscore error codes.
- Renaming cross-system runtime diagnostic categories or historical recorded execution evidence.
- Introducing a global Action error enum, compatibility alias, fallback normalization, or special-case per-action validator.
- Changing Pi behavior without a concrete emitted-code omission.
- Changing Runtime, Server, API, persistence, Workflow recovery semantics, or wire schemas.

## Decisions

### 1. The Action manifests use canonical kebab-case ownership

`mohist/opencode` declares these newly evidenced Action-owned errors alongside its already declared concrete codes: `execution-unavailable`, `skill-not-found`, `provider-quota-exhausted`, and `unsupported-execution-configuration`. `mohist/pi` declares these newly evidenced omissions: `skill-not-found`, `provider-quota-exhausted`, `incompatible-runtime`, and `interrupted`. Both manifests remain subject to the existing lowercase kebab-case validator.

The manifests do not declare `execution_unavailable`, `skill_not_found`, `unsupported_execution_configuration`, or any reserved platform code. The exact inventory is derived from the Action boundary, including the production capability path and runtime projection. Existing declared codes remain unchanged. The inventory excludes internal diagnostic-only labels that cannot appear as an ActionResult error code.

### 2. Runtime and resolver vocabulary maps at the Action boundary

The OpenCode runtime may continue to emit internal kind `unsupported-execution-configuration` and diagnostic/recorded category `unsupported_execution_configuration`, because that category is established across AgentJob and execution evidence. Before the `mohist/opencode` ActionResult is normalized, the Action boundary maps that condition to `unsupported-execution-configuration`. The ActionResult and manifest therefore agree, while diagnostic payloads and cross-system records preserve their existing underscore spelling.

The skill resolver currently reports `skill_not_found` to both Agent Actions. The Action boundary maps it to `skill-not-found`; the diagnostic may retain its source category where that category is exposed. `provider-quota-exhausted` is already a canonical kebab-case cross-layer code and remains unchanged. Pi's `incompatible-runtime` and `interrupted` runtime kinds are already canonical Action codes and pass through after they are declared.

These are explicit mappings, not validator exceptions. A future code path returning `unsupported_execution_configuration` or `skill_not_found` directly as an ActionResult without the mapping is undeclared and must normalize to `unexpected-error`.

### 3. Standard normalization remains generic

`normalizeActionResult` continues to accept a returned error code only when it is one of the reserved platform codes or appears in the selected manifest's error declarations. Declared `execution-unavailable`, `skill-not-found`, `provider-quota-exhausted`, `unsupported-execution-configuration`, `incompatible-runtime`, and `interrupted` codes pass through unchanged for the Action that declares them. A truly undeclared code still becomes `unexpected-error` with the original human-readable message, and recovery sees the normalized structured code.

No global list is added to the normalizer and no Action-specific exception is added to `defineAction`. The manifest remains the one source of Action-owned result authority.

### 4. Documentation mirrors the actual boundary

`docs/actions/opencode.md` and `docs/actions/pi.md` list all Action-owned error codes from their manifests. The OpenCode page states separately that runtime diagnostics and recorded execution evidence may carry `unsupported_execution_configuration`; that underscore category is not an Action manifest code. The pages may identify `skill_not_found` as the source resolver category, but they must show `skill-not-found` as the ActionResult/recovery code. Reserved platform codes remain described by the shared Action Contracts document and are not repeated as Action-owned errors.

The OpenCode page must not claim that Pi has the OpenCode-only unsupported configuration result. The shared documentation remains authoritative for codes shared by both Actions.

### 5. The inventory is limited to evidenced production paths

The future implementation must audit `mohist/opencode` and `mohist/pi` Action implementations, their agent-turn capability projections, runtime/resolver error mappings, and focused tests. The audit inventory is limited to the following newly evidenced paths:

- OpenCode capability input handoff: `execution-unavailable` → `execution-unavailable`.
- OpenCode unsupported reasoning effort: `unsupported_execution_configuration` diagnostic/recorded category → `unsupported-execution-configuration` ActionResult.
- OpenCode and Pi Skill resolution: `skill_not_found` resolver category → `skill-not-found` ActionResult.
- OpenCode and Pi provider exhaustion diagnostic: `provider-quota-exhausted` → `provider-quota-exhausted` ActionResult.
- Pi runtime failure mapping: `incompatible-runtime` → `incompatible-runtime` ActionResult.
- Pi runtime interruption mapping: `interrupted` → `interrupted` ActionResult.

No code outside these current production Action paths is added merely for parity. Shared codes already present in each manifest are not duplicated or renamed.

## Verification

Future implementation tests MUST:

1. Assert the OpenCode manifest contains `execution-unavailable`, `skill-not-found`, `provider-quota-exhausted`, and `unsupported-execution-configuration`, while the Pi manifest contains `skill-not-found`, `provider-quota-exhausted`, `incompatible-runtime`, and `interrupted`; neither manifest contains source underscore spellings or reserved platform codes.
2. Feed declared `execution-unavailable`, `skill-not-found`, `provider-quota-exhausted`, `incompatible-runtime`, and `interrupted` results through `normalizeActionResult` with their selected manifests and assert each code is preserved.
3. Feed a declared `unsupported-execution-configuration` result through normalization and assert the code is preserved.
4. Exercise the OpenCode unsupported-reasoning-effort path and assert the ActionResult uses the kebab-case code while its runtime diagnostic retains `unsupported_execution_configuration`.
5. Exercise Skill resolution for both Actions and assert the ActionResult uses `skill-not-found` while the source resolver category remains `skill_not_found` where exposed.
6. Feed a synthetic truly undeclared code through normalization and assert `unexpected-error` for both manifests.
7. Verify `docs/actions/opencode.md` and `docs/actions/pi.md` name exactly their manifest-owned Action errors and distinguish any diagnostic vocabulary.

Use fakes for runtime/connection dependencies. Do not add network, process, database, or system-service dependencies. Run focused Runner result-validation/OpenCode action tests, `npm run docs:check`, `npm run archtest`, `npm run test:fast`, and `npm run verify`.

## Risks and Trade-offs

- Existing consumers may inspect underscore categories on runtime diagnostics or execution evidence. Keeping those source vocabularies unchanged avoids a cross-system compatibility break while ActionResults become manifest-valid.
- A future projection could bypass an explicit mapping. Focused tests at runtime/resolver and ActionResult boundaries plus generic undeclared-code normalization prevent silent drift.
- The inventory could grow if a new current production path is discovered. Only a statically evidenced path may extend it; unrelated Action parity remains out of scope.

## Migration Plan

1. Add the evidenced OpenCode and Pi manifest entries and explicit runtime/resolver-to-Action mappings.
2. Update both concrete Agent Action documentation pages and add focused normalization/mapping/audit tests.
3. Run the focused Runner checks, docs and architecture checks, fast tests, and full `npm run verify` gate.
4. Confirm only Runner Action contract/projection tests and the two concrete Action documentation pages are changed; no reserved platform or global error contract changes appear.

Rollback is a source revert of the manifest, mapping, documentation, and tests. No persisted data or API migration is required.

## Open Questions

None. The Action boundary uses kebab-case; the established underscore category remains diagnostic and recorded-execution vocabulary only.
