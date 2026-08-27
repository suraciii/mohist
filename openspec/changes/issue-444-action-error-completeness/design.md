# Design

## Context

Issue #444 established manifest-backed Actions and the rule that the manifest is authoritative for Action-owned business errors. The current Runner already validates result codes against a manifest and maps a non-reserved undeclared code to `unexpected-error`. The remaining contract gap is completeness: the `mohist/opencode` execution path emits `execution-unavailable` for a failed Workflow input handoff, while its manifest does not declare it. The OpenCode runtime also maps its internal `unsupported-execution-configuration` kind to the cross-system category `unsupported_execution_configuration`, but the Action manifest grammar permits only lowercase kebab-case codes.

The current `mohist/pi` path has the shared runtime-unavailable, Session, reporting, and turn failures, plus its own runtime error taxonomy. A bounded source audit also shows concrete omissions: the Skill resolver returns `skill_not_found`, the provider retry diagnostic can produce `provider-quota-exhausted`, and the Pi runtime error mapping can return `incompatible-runtime` and `interrupted` as ActionResult codes. These are included because they are reachable from the current production Action path, not because Pi is merely a peer.

The documentation authority is not limited to the two concrete pages. `docs/actions/README.md` enumerates platform ownership and shared Action semantics, so it must remain consistent with the shared portion of the final manifests and must not present a stale or contradictory owned-code list.

## Goals

- Make the `mohist/opencode` and `mohist/pi` manifests enumerate every statically evidenced non-reserved code they can return at the Action boundary, including the complete existing inventories.
- Preserve the existing normalization authority: declared Action codes pass through, reserved platform codes remain platform-owned, and undeclared codes become `unexpected-error`.
- Define deterministic mappings from runtime or resolver diagnostic vocabulary to Action result vocabulary where spellings differ.
- Keep the inventory bounded to current production Action paths and exclude unrelated Actions or speculative codes.
- Align `docs/actions/opencode.md`, `docs/actions/pi.md`, and shared `docs/actions/README.md` with their actual authority boundaries.

## Non-Goals

- Adding any reserved platform code to an Action manifest.
- Weakening `defineAction` or result validation to permit underscore error codes.
- Renaming cross-system runtime diagnostic categories or historical recorded execution evidence.
- Introducing a global Action error enum, compatibility alias, fallback normalization, or special-case per-action validator.
- Changing Pi behavior without a concrete emitted-code omission.
- Changing Runtime, Server, API, persistence, Workflow recovery semantics, or wire schemas.

## Decisions

### 1. The Action manifests use canonical kebab-case ownership

The final complete `mohist/opencode` Action-owned inventory is:

- `runtime-unavailable`
- `session-workspace-mismatch`
- `session-binding-failed`
- `session-reporting-failed`
- `runtime-session-missing`
- `unavailable-runtime`
- `incompatible-runtime`
- `permission-required`
- `interrupted`
- `generation-drain-timeout`
- `turn-failed`
- `execution-unavailable`
- `skill-not-found`
- `provider-quota-exhausted`
- `unsupported-execution-configuration`

The final complete `mohist/pi` Action-owned inventory is:

- `runtime-unavailable`
- `session-workspace-mismatch`
- `session-binding-failed`
- `session-reporting-failed`
- `runtime-session-missing`
- `unavailable-runtime`
- `turn-failed`
- `skill-not-found`
- `provider-quota-exhausted`
- `incompatible-runtime`
- `interrupted`

OpenCode adds the evidenced `execution-unavailable`, `skill-not-found`, `provider-quota-exhausted`, and `unsupported-execution-configuration` omissions. Pi adds its evidenced `skill-not-found`, `provider-quota-exhausted`, `incompatible-runtime`, and `interrupted` omissions. Both manifests remain subject to the existing lowercase kebab-case validator.

Neither manifest declares `execution_unavailable`, `skill_not_found`, `unsupported_execution_configuration`, or any reserved platform code (`invalid-input`, `unexpected-error`, `timeout`). The inventory is complete for the current production Action paths and does not remove or rename existing valid entries.

The shared intersection that `docs/actions/README.md` must enumerate as shared Agent Action business codes is:

- `runtime-unavailable`
- `session-workspace-mismatch`
- `session-binding-failed`
- `session-reporting-failed`
- `runtime-session-missing`
- `unavailable-runtime`
- `turn-failed`

The shared page also remains authoritative for the reserved platform codes; it must not claim ownership of them for either concrete Action.

### 2. Runtime and resolver vocabulary maps at the Action boundary

The OpenCode runtime may continue to emit internal kind `unsupported-execution-configuration` and diagnostic/recorded category `unsupported_execution_configuration`, because that category is established across AgentJob and execution evidence. Before the `mohist/opencode` ActionResult is normalized, the Action boundary maps that condition to `unsupported-execution-configuration`. The ActionResult and manifest therefore agree, while diagnostic payloads and cross-system records preserve their existing underscore spelling.

The Skill resolver currently reports `skill_not_found` to both Agent Actions. The Action boundary maps it to `skill-not-found`; the diagnostic may retain its source category where that category is exposed. `provider-quota-exhausted` is already a canonical kebab-case cross-layer code and remains unchanged. Pi's `incompatible-runtime` and `interrupted` runtime kinds are already canonical Action codes and pass through after they are declared.

These are explicit mappings, not validator exceptions. A future code path returning `unsupported_execution_configuration` or `skill_not_found` directly as an ActionResult without the mapping is undeclared and must normalize to `unexpected-error`.

### 3. Standard normalization remains generic

`normalizeActionResult` continues to accept a returned error code only when it is one of the reserved platform codes or appears in the selected manifest's error declarations. Every code in the complete inventory above passes through unchanged for its declaring Action. A truly undeclared code still becomes `unexpected-error` with the original human-readable message, and recovery sees the normalized structured code.

No global list is added to the normalizer and no Action-specific exception is added to `defineAction`. The manifest remains the one source of Action-owned result authority.

### 4. Documentation mirrors each authority boundary

`docs/actions/opencode.md` lists exactly the complete OpenCode inventory above. `docs/actions/pi.md` lists exactly the complete Pi inventory above. `docs/actions/README.md` lists the shared intersection above and retains the platform-owned `invalid-input`, `unexpected-error`, and `timeout` semantics. No page presents an underscore category as a manifest-owned ActionResult code.

The OpenCode page states separately that runtime diagnostics and recorded execution evidence may carry `unsupported_execution_configuration`; that underscore category is not an Action manifest code. Both concrete pages identify `skill_not_found` as the source resolver category where exposed, while showing `skill-not-found` as the ActionResult/recovery code. The OpenCode page must not claim that Pi has the OpenCode-only unsupported-configuration result, and the shared page must not contradict either concrete manifest.

### 5. The inventory is limited to evidenced production paths

The future implementation must audit `mohist/opencode` and `mohist/pi` Action implementations, their Agent-turn capability projections, runtime/resolver error mappings, manifests, documentation, and focused tests. The bounded inventory is:

- OpenCode capability input handoff: `execution-unavailable` → `execution-unavailable`.
- OpenCode unsupported reasoning effort: `unsupported_execution_configuration` diagnostic/recorded category → `unsupported-execution-configuration` ActionResult.
- OpenCode and Pi Skill resolution: `skill_not_found` resolver category → `skill-not-found` ActionResult.
- OpenCode and Pi provider exhaustion diagnostic: `provider-quota-exhausted` → `provider-quota-exhausted` ActionResult.
- Pi runtime failure mapping: `incompatible-runtime` → `incompatible-runtime` ActionResult.
- Pi runtime interruption mapping: `interrupted` → `interrupted` ActionResult.
- Existing shared and runtime-specific entries listed in the complete inventories above remain declared.

No code outside these current production Action paths is added merely for parity. Shared codes already present in each manifest are not duplicated or renamed.

## Verification

Future implementation tests MUST:

1. Assert the complete OpenCode and Pi manifest arrays exactly match the inventories above, including all existing entries and the newly evidenced entries; neither manifest contains source underscore spellings or reserved platform codes.
2. Feed every newly declared code and every existing inventory code through `normalizeActionResult` with its selected manifest and assert the code is preserved.
3. Exercise the OpenCode unsupported-reasoning-effort path and assert the ActionResult uses the kebab-case code while its runtime diagnostic/recorded evidence retains `unsupported_execution_configuration`.
4. Exercise Skill resolution for both Actions and assert the ActionResult uses `skill-not-found` while the source resolver category remains `skill_not_found` where exposed.
5. Feed a synthetic truly undeclared code through normalization and assert `unexpected-error` for both manifests.
6. Verify `docs/actions/opencode.md`, `docs/actions/pi.md`, and `docs/actions/README.md` name exactly their authority-owned codes, preserve platform ownership, and distinguish diagnostic vocabulary.

Use fakes for runtime/connection dependencies. Do not add network, process, database, or system-service dependencies. Run these exact focused commands:

- `npm run test:run -w packages/runner -- tests/opencode-action-turn.spec.ts src/actions/pi.test.ts tests/action-registry.test.ts tests/define-action.test.ts`
- `npm run typecheck -w packages/runner`
- `npm run typecheck:tests -w packages/runner`
- `npm run docs:check`
- `npm run archtest`
- `npm run test:fast`
- `npm run verify`

## Risks and Trade-offs

- Existing consumers may inspect underscore categories on runtime diagnostics or execution evidence. Keeping those source vocabularies unchanged avoids a cross-system compatibility break while ActionResults become manifest-valid.
- A future projection could bypass an explicit mapping. Tests at runtime/resolver and ActionResult boundaries plus generic undeclared-code normalization prevent silent drift.
- The complete inventory could grow if a new current production path is discovered. Only a statically evidenced path may extend it; unrelated Action parity remains out of scope.

## Migration Plan

1. Add the evidenced OpenCode and Pi manifest entries and explicit runtime/resolver-to-Action mappings.
2. Update the two concrete Action pages and shared Action Contracts documentation to match the complete authority inventories.
3. Add focused normalization and mapping/audit tests using the exact commands above.
4. Run docs and architecture checks, fast tests, and the full `npm run verify` gate.
5. Confirm no reserved platform or global error contract changes appear and no unrelated Action is included.

Rollback is a source revert of the manifest, mapping, documentation, and tests. No persisted data or API migration is required.

## Open Questions

None. The Action boundary uses kebab-case; established underscore categories remain diagnostic and recorded-execution vocabulary only.
