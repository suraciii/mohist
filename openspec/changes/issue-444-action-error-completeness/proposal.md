# Action Manifest Error Completeness

## Why

The Action manifest is the authority used by standard result normalization. The current `mohist/opencode` production path can return `execution-unavailable` when Workflow input reporting cannot be handed to the execution capability, and it can surface the established OpenCode unsupported-configuration category. A bounded audit also finds Skill-resolution and provider-quota failures reachable from the OpenCode and Pi Agent Action paths, plus Pi runtime interruption and incompatibility results. Because these outcomes are not all represented as Action-owned manifest errors, normalization can change real failures into `unexpected-error` and recovery can lose the intended category.

The OpenCode runtime and cross-system execution evidence already use two vocabularies for the unsupported reasoning-effort case. The runtime diagnostic and recorded failure category remain `unsupported_execution_configuration`, while the Action contract must use the repository's canonical kebab-case error code. The Skill resolver similarly reports `skill_not_found`, while the Action contract uses canonical kebab-case. The boundary must make these mappings explicit rather than weakening manifest validation.

The documentation authority spans the two concrete Action pages and the shared Action Contracts page. `docs/actions/README.md` currently enumerates platform ownership and shared Action semantics, so its shared-code section must be kept consistent with the final manifest intersection rather than treated as optional context.

## What Changes

- Declare every statically evidenced Action-owned failure code reachable from the current `mohist/opencode` and `mohist/pi` production Action paths, preserving their complete existing inventories while adding the evidenced omissions.
- For OpenCode, add exact `execution-unavailable`, `unsupported-execution-configuration`, `skill-not-found`, and `provider-quota-exhausted` codes.
- For Pi, add exact `skill-not-found`, `provider-quota-exhausted`, `incompatible-runtime`, and `interrupted` codes.
- Map underscore runtime or resolver categories to canonical lowercase kebab-case Action codes at the Action boundary where the vocabularies differ.
- Preserve declared Action-owned codes through standard result normalization and recovery matching.
- Require `docs/actions/opencode.md`, `docs/actions/pi.md`, and shared `docs/actions/README.md` to describe their respective manifest/platform semantics and the source diagnostic vocabulary.
- Keep complete evidenced OpenCode/Pi inventories and distinguish kebab Action codes from underscore diagnostic or recorded-execution categories.
- Preserve `unexpected-error` normalization for a truly undeclared Action code. Keep reserved platform codes out of Action manifests; add no global validator exception.

## Capability

- `action-error-completeness`: manifest-authoritative ownership, normalization, and documentation of concrete Action error codes.

## Impact

- **Runner Action contract:** the `mohist/opencode` and `mohist/pi` manifests plus their runtime-to-Action projections are the implementation boundary.
- **Result validation:** declared codes remain stable; undeclared non-platform codes still normalize to `unexpected-error`.
- **Documentation:** the two concrete Agent Action pages mirror their full manifests, while the shared Action Contracts page remains authoritative for platform codes and shared manifest-owned semantics.
- **Evidence boundary:** only codes reachable from the current production Action paths are included; unrelated Actions and speculative parity are excluded.
- **Other runtimes and APIs:** no global error enum, reserved-code change, backend protocol change, or Server schema change.
