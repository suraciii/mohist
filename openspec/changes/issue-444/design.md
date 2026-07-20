## Context

The [proposal](proposal.md) defines two deliverables: manifest-backed Action registration/catalog publication and authoritative dispatch-time input validation. The [`action-manifests`](specs/action-manifests/spec.md) and [`action-input-validation`](specs/action-input-validation/spec.md) capability specs make the Runner's local manifest collection the execution authority, require task and check validation after template rendering, and preserve structured errors for recovery.

Today `packages/runner/src/actions/registry.ts` stores a mutable case-insensitive `Map<string, ActionHandler>` and separately registers 17 built-in handlers. Each handler reads `ActionContext.with` through permissive helpers that stringify objects, parse numeric strings, parse boolean-like strings, and ignore unknown keys. `WorkExecutor` and `executeCheckDispatch` resolve and invoke bare handlers; task recovery runs only after an Action result, so current resolution and input failures bypass recovery. Removed Action `mohist/acp-agent` and OpenCode output projection are hard-coded name sets in the executor.

Runner registration currently publishes runtime capabilities and model catalogs through `RunnerRegistration`. The Server maps register and heartbeat payloads into `RunnerInfo`, persists the last value in `RunnerWorksState.LastKnownInfo`, and mirrors online runners into `RunnerRegistryGrain`. There is no Action catalog field or Profile consumer yet.

This change crosses the Workflow/Runner published-language boundary but keeps execution authority in the Runner: the selected Runner validates against the same definitions it executes, while the Server only retains the reported catalog for a later Profile-validation change. Action authors and workflow authors are the primary stakeholders; Runner registration and Workflow recovery are affected integration surfaces.

## Goals / Non-Goals

**Goals:**

- Make one immutable Action definition the source for runtime resolution, input metadata, output metadata, business error metadata, and execution.
- Validate and default rendered top-level `with` values for tasks and individual checks before Action execution.
- Return field-specific `invalid-input` errors without coercion and make task validation failures available to existing recovery matching.
- Derive and publish a serializable Action catalog, including removed-Action tombstones, through Runner registration state.
- Migrate all built-in Actions and preserve valid built-in profile behavior.
- Reject malformed definitions, duplicate names, reserved input declarations, reserved business error declarations, and undeclared business error results at deterministic boundaries.

**Non-Goals:**

- Profile save/update validation or catalog consensus across multiple Runners.
- External plugins, versioned `uses`, composite Actions, or runtime Action installation.
- Replacing the broad `ActionContext`, removing implicit Variable reads, or injecting capabilities from manifests.
- Replacing the existing `mohist/opencode` name-based promise projection in this issue.
- Adding nested object/array schemas or runtime validation of declared successful output fields.
- Changing Workflow completion, check aggregation, or recovery budget semantics.

## Decisions

### 1. `defineAction` returns one immutable executable definition

Add an Action definition module under `packages/runner/src/actions/` with these conceptual types:

```ts
type ActionInputType = "string" | "number" | "boolean" | "object" | "array"

type ActionDefinition<M extends ActionManifest> = Readonly<{
  manifest: M
  run: (context: ValidatedActionContext<M>) => Promise<ActionResult>
}>

type ActionTombstone = Readonly<{ name: string; guidance: string }>
```

`defineAction` validates and freezes the manifest, verifies static defaults, and returns the manifest paired with its function. `ValidatedActionContext` keeps the current `ActionContext` fields and `rawWith`, but narrows `with` to the inferred, validated input shape. This gives authors typed input without narrowing server/runtime/Variable capabilities in this issue.

Canonical names match `^[a-z0-9]+(?:-[a-z0-9]+)*/[a-z0-9]+(?:-[a-z0-9]+)*$`. `working-directory` is rejected as a manifest input. Input/output entries carry type and optional description; errors are a kebab-case code-to-description map. Descriptions are catalog metadata, not runtime behavior.

Alternative considered: `defineAction(manifest, run)`. It separates serializable data from code more visibly, but weakens the single-object authoring experience and TypeScript contextual inference between `inputs` and `run`.

Alternative considered: keep `register(name, handler)` and add a parallel manifest map. This preserves test setup but recreates the current dual authority and permits catalog/runtime drift, so it is rejected.

### 2. The registry is constructed from definitions and tombstones

Replace mutable production registration with `new ActionRegistry(definitions, tombstones)`. Construction canonicalizes lookup keys, rejects case-insensitive duplicates and executable/tombstone collisions, and builds:

- a discriminated resolver result: executable definition, tombstone, or unknown;
- a deterministic catalog projection sorted by canonical name;
- no public bare-handler registration path.

`createDefaultRegistry()` supplies all 17 built-in definitions and the `mohist/acp-agent` tombstone. Tests create small definitions with `defineAction` rather than bypassing the production contract. Case-insensitive dispatch remains supported, and handlers no longer branch on the original `context.uses` casing.

Alternative considered: keep tombstone checks in `WorkExecutor`. That preserves current behavior with less migration, but catalog publication and check execution could diverge from task execution. A discriminated registry result keeps all name resolution in one authority.

### 3. A pure validator owns top-level input rules and defaults

Add one pure `validateActionInput(manifest, renderedWith)` function. It returns either a new validated input object or an `ActionError` with code `invalid-input`. It does not mutate the rendered object or manifest defaults.

Validation order is deterministic:

1. Exclude `working-directory` from Action-owned input and reject other unknown top-level keys.
2. Reject omitted required fields.
3. Reject supplied values whose exact JSON kind differs from the declaration. `null` is supplied and mismatches every supported type; omission means the property is absent.
4. Copy valid supplied values and clone static defaults for absent defaulted fields.

Diagnostics sort candidate field names before selecting the first error and use stable forms such as `Action 'core/process' received unknown input 'commmand'` and `Action 'core/script' input 'timeout' must be number, received string`.

Object and array declarations validate only the container. Conditional rules such as `expect` or legacy `contains`, `message` or `messageFrom` when squashing, nested OpenCode options, and context-dependent fallbacks remain in the Action implementation. Inputs currently obtainable from implicit Variables must remain optional in manifests until the later single-input-channel change; otherwise this issue would remove those fallbacks before the Action runs.

Alternative considered: wrap each handler with validation inside the registry. That prevents direct unvalidated calls, but current callers would create the working directory and perform task branch checks first, and validation failures would not naturally enter task recovery. Explicit validation at the executor boundary preserves the required ordering and error flow.

### 4. Tasks and checks share validation but preserve their host semantics

For tasks, `WorkExecutor.executeOne` will:

1. resolve executable/tombstone/unknown;
2. resolve variables and render `with`/`expect` as today;
3. validate and default rendered `with`;
4. on validation failure, build a normal failed `WorkItemResult` and call `tryRecovery` before returning;
5. resolve `working-directory`, run branch checks, and invoke the definition with validated `context.with` while preserving `rawWith`;
6. validate the returned error code and continue through output, completion, recovery, and postcondition handling.

Workspace preparation remains before Action resolution because moving that cross-cutting lifecycle boundary is outside this issue. Input validation still occurs before Action-owned execution, work-directory creation, and branch probes.

For checks, `runOneCheck` follows the same resolve-render-validate-invoke sequence. Validation failures are stored as row-level `error: { code: "invalid-input", ... }`. The aggregate check result remains `check-failed` with all rows in output; this satisfies individual-check error retention without changing stage-check verdict semantics. Tombstones use the same guidance for tasks and checks.

Alternative considered: change the aggregate check error to `invalid-input` when any row has invalid input. That loses the existing multi-check aggregate contract and makes one row's error overwrite peers, so row-level structured retention is preferred.

### 5. The Action boundary enforces error ownership

Maintain a constant reserved set containing `invalid-input`, `unexpected-error`, and `timeout`; manifests cannot declare those codes. After an execution function returns, the boundary accepts either a reserved platform code or a business code declared by that manifest. An undeclared business code is a contract violation and is normalized to `unexpected-error` before recovery.

Native exceptions from the execution function and malformed Action results are also normalized to `unexpected-error` at this boundary and enter the same task recovery path. Broader executor failures outside the Action boundary retain their existing executor-specific codes and behavior.

Existing built-ins may still return a reserved code for a platform-defined condition, such as semantic invalid input or command timeout. Ownership means these codes are absent from the Action business catalog; it does not require capability/context refactoring in this issue.

Alternative considered: permit arbitrary returned codes and use manifests only as documentation. That would leave recovery contracts unverifiable and allow implementation/catalog drift, defeating the manifest as authority.

### 6. Catalogs are typed registration data, not opaque JSON

Project a pure-data `ActionCatalog` from the registry with executable entries and tombstones. Execution functions, inferred TypeScript types, and private handler output are excluded. Output declarations describe the public Action output after executor projection; for `mohist/opencode`, this is `promise`, not the handler's private debug payload.

Add `actionCatalog` to TypeScript `RunnerRegistration`, initial registration, heartbeat state, and host test fakes. `RunnerHost` must retain the same registry instance passed to `WorkExecutor` and call `registry.catalog()` when building registration state.

Add matching C# catalog records and an optional `ActionCatalog` field to `RunnerRegisterRequest`, `RunnerHeartbeatRequest`, and `RunnerInfo`. Register and heartbeat mapping pass the field through. Existing `RunnerGrain` persistence and registry mirroring then retain it automatically in `LastKnownInfo`; no Workflow service consumes it yet.

Alternative considered: send an opaque JSON blob. That reduces C# types now but moves schema validation and parsing into every future consumer and weakens Orleans persistence contracts.

Alternative considered: publish only a digest and expose a second catalog endpoint. This reduces heartbeat payload size but introduces synchronization and failure modes before any catalog consumer exists. The small built-in catalog is sent with existing full registration state; deterministic ordering permits a digest to be added later without changing semantics.

### 7. Built-ins migrate atomically and tests own each behavior matrix

Define each built-in beside its implementation and replace local coercive reads with direct reads from validated `context.with` where the manifest fully expresses the rule. Keep Action-level checks for non-empty strings, conditional relationships, nested object semantics, and implicit Variable fallback. Static defaults such as booleans can move to manifests; environment-dependent defaults such as the platform shell and context-dependent repository fallbacks remain Action semantics and are not falsely advertised as static catalog defaults.

The migration includes all Actions returned by `createDefaultRegistry`; partial migration is not permitted. A profile traversal test must inspect executable task/check/recovery/approval-feedback `uses` while excluding nested prompt-loader `uses`. Existing built-in profile flow specs remain the regression boundary.

Focused Runner unit tests own definition validation, exact-kind input validation, default cloning, error-code enforcement, and catalog projection. Runner specs own task recovery, check rows, tombstones, and built-in profile traversal. Host tests assert registration/heartbeat catalog payloads. Server API/grain specs assert wire binding and retention across heartbeat repair and activation using in-memory test infrastructure.

## Risks / Trade-offs

- [Strict types expose previously accepted coercions such as `force: "true"` or numeric strings] -> Treat this as the intended breaking behavior; update built-in profiles to native YAML types and add explicit regression cases for rejected coercion.
- [A built-in manifest omits a currently accepted key, causing a valid profile to fail] -> Audit every handler read and shipped profile field, then lock completeness with catalog/profile traversal and full built-in profile specs.
- [Implicit Variable fallbacks conflict with manifest requiredness] -> Mark fallback-backed inputs optional for this issue and retain handler resolution; move them to required only with the later single-input-channel change.
- [Static defaults cannot represent OS- or context-dependent behavior] -> Declare only serializable static defaults; leave dynamic choices in Action semantics and avoid misleading catalog values.
- [Validation failures could bypass recovery through an early return] -> Route task validation errors through one helper that calls `tryRecovery`; cover matched and unmatched `invalid-input` cases.
- [Catalog and execution registry could diverge] -> Generate both once from the same immutable registry instance and remove mutable/bare production registration.
- [Catalog data increases every heartbeat payload] -> Keep the schema compact and omit descriptions only if measurement shows a problem; do not add a second synchronization protocol preemptively.
- [Persisted `RunnerInfo` lacks the new field during rollout] -> Make the catalog field nullable in Server state and repopulate it on the next Runner registration/heartbeat; no business-state migration is required.
- [Action exceptions currently surface as `runner-failed`] -> Narrow the normalization change to the Action invocation/result boundary and regression-test unrelated workspace/executor failure codes.
- [Large test migration from `registry.register` obscures product changes] -> Provide a concise test-definition factory and migrate each test to explicit inputs/errors rather than retaining a bypass API.

## Migration Plan

1. Add Runner manifest/catalog types, `defineAction`, registry construction checks, pure input validation, and focused unit tests without changing default registration.
2. Convert test-only registries to explicit definitions so later removal of `register(name, handler)` has no hidden callers.
3. Define and audit all 17 built-in manifests, add the `mohist/acp-agent` tombstone, switch `createDefaultRegistry()` atomically, and remove hard-coded handler/tombstone lists.
4. Integrate resolution, validation/defaulting, result-code enforcement, and exception normalization into task and check execution. Add task recovery and check-row specs.
5. Add the catalog to Runner registration state and typed Server DTOs/state; verify register, heartbeat repair, persistence reactivation, and host payloads.
6. Run built-in profile traversal/full-flow regressions, Runner typecheck/tests, and Server tests. No database migration or Web/CLI change is required.
7. Deploy Server and Runner together and restart the Runner so registration immediately populates the catalog. Existing `RunnerInfo` without a catalog remains readable until that registration.

Rollback reverts both binaries. The Server may retain catalog data in last-known Runner state, but older code does not consume it; the next old-Runner registration replaces the state. Workflow definitions and TaskRun persistence require no rollback migration. Rolling back also restores permissive input handling, so tasks rejected only by the new validator can be retried after rollback.

## Open Questions

1. **How does `mohist/opencode.prompt` satisfy the single-type manifest rule?** Current contracts and shipped profiles accept both a string and a prompt-loader/structured object, while the input spec requires exactly one type. The recommended resolution is to amend the manifest input model/spec to allow an explicit finite union (`string | object`) because moving prompt-loader execution into the generic validator would add Action-specific engine behavior. Implementation must not silently exempt this field.
