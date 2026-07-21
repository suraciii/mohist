## Why

Action authors currently have to repeat hidden registration and input-parsing conventions, while misspelled or malformed `with` fields are often silently ignored. A declaration-backed Action contract is needed now so new Actions have one source of truth and invalid workflow input fails before execution.

## What Changes

- Define each Action through one declarative manifest containing its name, typed inputs, defaults and required fields, output fields, business error codes, and execution function.
- Build Action registration and the serializable Action catalog from the manifest collection, including tombstones that distinguish removed Actions from names that never existed.
- Migrate every built-in Action to manifests so no built-in can be registered outside the catalog, while preserving existing built-in workflow behavior.
- **BREAKING** Validate rendered `with` input at dispatch for tasks and checks: unknown fields, missing required fields, and type mismatches fail with platform error code `invalid-input` and identify the offending field instead of being ignored or coerced.
- Apply manifest defaults to omitted inputs before invoking the Action implementation.
- Keep platform error codes (`invalid-input`, `unexpected-error`, and `timeout`) separate from each Action's declared business error catalog; recovery remains able to match either kind of error code.
- Publish the manifest-derived catalog with Runner registration data for later consumers, without adding Profile save-time validation in this change.
- Preserve the existing Action execution capability surface and implicit Variable reads for now; capability-based host narrowing and single-channel inputs remain follow-up work.

## Capabilities

- `action-manifests`: Declarative Action contracts, manifest-derived registration and catalog publication, complete built-in coverage, removed-Action tombstones, and separation of platform and Action-owned error codes.
- `action-input-validation`: Authoritative dispatch-time validation and defaulting of rendered `with` input for tasks and checks, including actionable `invalid-input` failures before Action execution.

## Impact

- Runner Action definitions, registry/catalog construction, task and check execution paths, input helpers, removed-Action handling, and built-in Action tests under `packages/runner/`.
- Runner registration contracts and the matching Server API/Runner state DTOs must carry the serializable Action catalog; this change does not yet consume it for Profile validation.
- Built-in workflow profiles and their full-flow regressions are affected because all referenced Actions move to manifest-backed registration and stricter input contracts.
- Workflow recovery continues using structured error codes, but its matchable catalog expands from implicit implementation behavior to declared Action errors plus reserved platform errors.
- No external Action loading, versioned `uses` syntax, composite Actions, new runtime dependencies, Profile save-time validation, or reduced Action host capabilities are introduced.
