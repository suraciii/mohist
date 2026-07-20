## Context

The proposal identifies two coupled failures in the runner. Model discovery currently uses `client.v2.model.list()` inside `OpenCodeRuntime`; that OpenCode catalog path does not derive reasoning variants, so the runner reports an empty `coderModelVariants` map even though `opencode models --verbose` reports variants. The runtime also treats catalog loading as part of readiness, so a configuration-hint failure can stop all work claiming despite a healthy OpenCode server.

Today `OpenCodeRuntime` owns `CatalogClient`, `RuntimeModelCatalog`, `catalog()`, and `refreshCatalog()`. `RunnerHost` projects that runtime-owned catalog into its registration and calls `refreshCatalog()` from the existing rediscovery timer. Server aggregation and all Web selectors already handle populated variant maps correctly; their contracts and implementation do not need to change.

The normative behavior is defined by the three capability specs under `specs/`. This design must also reconcile `design/runtimes/opencode.md`, which currently states that the SDK v2 catalog belongs to `OpenCodeRuntime` and is a readiness prerequisite. That repository design document is the architecture authority and must be updated as part of implementation.

Constraints are:

- Keep `coderModels` and `coderModelVariants` registration/heartbeat fields unchanged.
- Keep the 30-minute default, 60-second minimum, configurable rediscovery timer and change-triggered heartbeat.
- Do not add persisted state, dependencies, a fallback discovery source, or a model-legality check in Mohist.
- Default tests must use fake processes and fake time; they cannot invoke OpenCode, the network, the filesystem, or wall-clock waits.

## Goals / Non-Goals

**Goals:**

- Restore model IDs and provider-defined reasoning variants from complete `opencode models --verbose` output.
- Make model discovery an independent, best-effort runner-host concern at startup and on the existing periodic timer.
- Make OpenCode runtime readiness depend on execution health rather than catalog availability.
- Preserve last-known non-empty catalog state across failed or empty rediscovery and immediately report real changes.
- Remove the obsolete SDK catalog surface and its test wiring rather than retain two discovery paths.

**Non-Goals:**

- Change Server aggregation, API response shapes, Web selector behavior, or model/variant persistence.
- Modify OpenCode, derive variants in Mohist, or guide users to declare variants manually.
- Validate whether a configured model or variant can execute; OpenCode remains authoritative at turn time.
- Add caching, a new refresh trigger, a new heartbeat channel, or persistent catalog storage.
- Generalize model discovery for runtimes other than OpenCode.

## Decisions

### D1. Restore a standalone CLI discovery module

Add `packages/runner/src/runtime/opencode-models.ts` as the single owner of command selection, command execution, verbose-output parsing, failure normalization, and catalog set equality. Its Mohist-owned result remains a small shape:

```ts
interface DiscoveredOpencodeModels {
  models: string[]
  variants: Record<string, string[]>
}
```

The module selects `MOHIST_AGENT_MODELS_COMMAND`, then `MOHIST_AGENT_COMMAND`, then `opencode`, and always supplies `models --verbose`. Every invocation executes the command; no timestamp or cached result is stored.

The production adapter uses `spawnSync` with the existing external-process policy check, an abort signal, a bounded timeout, UTF-8 decoding, and a buffer large enough for the observed catalog. Synchronous collection is intentional: it drains stdout before returning and avoids the previously observed race where process completion exposed only part of a roughly 49 KB response. A replaceable command-runner seam lets tests provide stdout or failures without starting a process.

Alternatives considered: keep the SDK v2 catalog, rejected because it is the source of the missing variants; merge SDK models with CLI variants, rejected because it creates two authorities and ambiguous mismatch behavior; use user-authored `opencode.json` variants, rejected because it moves discovery burden to users; use the former asynchronous callback path, rejected because it has already truncated buffered stdout in production.

### D2. Parse model blocks structurally and tolerate local metadata damage

The parser recognizes a model header with the same lexical rule used for runtime model identifiers: after trimming, `^([^/\s]+)/(\S+)$`. The first capture is the provider and the entire second capture is the model ID, including additional slashes; the full trimmed header is retained in the catalog. Nonmatching lines outside metadata are ignored, so warnings and malformed output cannot become model IDs. A sequence of valid headers without metadata remains an accepted flat-list form.

After a valid header, blank lines are skipped. A next nonblank line beginning with `{` starts metadata collection; the parser balances braces while accounting for strings and escapes, then uses `JSON.parse` and takes `Object.keys(metadata.variants)` only when `variants` is an object. Missing, empty, non-object, balanced-invalid, or unbalanced metadata yields no variants for the already-recognized model. Recovery after balanced-invalid JSON starts after the balanced block; recovery after an unbalanced block scans for the next line independently matching the header regex and does not consume it. Command failure, abort, non-zero exit, or output with no valid headers is normalized to `{ models: [], variants: {} }` and logged.

Alternatives considered: regular expressions over JSON, rejected because nested objects, braces in strings, and multiline output make it fragile; require every metadata block to parse before returning any models, rejected because one provider's malformed metadata would hide unrelated valid providers; silently reuse an earlier result inside the module, rejected because freshness and failure-state policy belong to the host lifecycle.

### D3. Move catalog ownership fully to RunnerHost

`RunnerHost` becomes the only runtime-process owner of the current `coderModels` and `coderModelVariants` snapshot. Startup ordering is:

1. Load the workspace registry.
2. Initialize the shared `OpenCodeRuntime` server/client/event lifecycle.
3. Run one best-effort CLI model discovery and assign its result, including an empty initial result.
4. Connect and register using the host-owned snapshot.
5. Run startup convergence.
6. Register the periodic timers.
7. Enter the worker loop.

The initial discovery runs regardless of whether runtime startup succeeded, and discovery failure does not change runtime readiness. `registrationState()` reads the two host fields directly; it no longer projects a runtime catalog.

Periodic rediscovery calls the same discovery module without checking `runtime.ready()`. Empty results preserve the current snapshot. Non-empty results are compared as sets, including variant-map keys and each model's variant set. A content change replaces both fields before attempting one immediate heartbeat; ordering-only differences do nothing. The existing timer registration point remains after startup convergence and immediately before the worker loop; the first fire is one full interval after that registration, not after startup discovery. Exception containment, the 30-minute default, 60-second floor, and shutdown cleanup remain in `RunnerHost`.

Alternatives considered: keep `catalog()` on `OpenCodeRuntime` but implement it with the CLI, rejected because execution lifecycle would still own an unrelated best-effort hint and callers could reintroduce readiness coupling; create a new long-lived catalog service, rejected because one host, one timer, and two in-memory fields do not justify another lifecycle abstraction; discover on every heartbeat, rejected because it couples process cost to transport cadence and discards the existing refresh contract.

### D4. Narrow OpenCodeRuntime readiness to execution health

Remove `catalogFactory` from `OpenCodeRuntimeDeps`; remove catalog state, `catalog()`, `refreshCatalog()`, `RuntimeModelCatalog`, and `RuntimeModelDescriptor`; remove `catalog.ts` and its default-factory wiring. `OpenCodeRuntime.start()` succeeds after the shared server starts, health succeeds, and the global event subscription is established. Spawn and health failures retain the existing `server-spawn-failed` and `health-failed` diagnostics. `catalog-load-failed` and `catalog-refresh-failed` cease to be runtime diagnostics.

Server disconnect and heartbeat-failure handling remains unchanged: readiness flips false, in-flight operations fail without replay, and the runtime rebuilds its server/client/event lifecycle. Recovery requires server startup and health only. Runner work claiming continues to use `runtime.ready()`, which now expresses execution availability rather than discovery availability.

Alternatives considered: keep catalog failure as a warning inside `OpenCodeRuntime`, rejected because it preserves hidden state and a misleading API; remove readiness gating entirely, rejected because server spawn, health, and process-loss failures still make execution impossible; let the host combine runtime and discovery readiness, rejected because the specs explicitly make discovery non-blocking.

### D5. Preserve external contracts and update the architecture authority

No Server, CLI, or Web production path changes. Registration and heartbeat continue to send `coderModels` and `coderModelVariants`; existing Server union aggregation and project model-list responses feed the existing chips and persistence behavior.

Implementation must update `design/runtimes/opencode.md` in the same change: remove SDK catalog operations from the call table, remove catalog ownership from the deep-module boundary and readiness sequence, describe host-owned CLI discovery, and remove CLI parsing from the list of retired behavior. Comments in `core/types.ts`, `host.ts`, and the runtime public surface must be aligned so no source still presents catalog loading as a readiness invariant.

Alternative considered: leave the repository design document as historical text, rejected because it is explicitly architecture authority and would instruct future changes to restore the defect.

### D6. Replace catalog-coupled tests with focused fakes

Restore unit coverage for command precedence, every-call execution, complete large stdout, multiline JSON, variant-key extraction, malformed metadata recovery, empty/failure normalization, and order-insensitive equality through the injected command runner. Host specs use fake timers and fake discovery results to cover initial registration, runtime-independent periodic execution, changed/unchanged heartbeats, retained state on empty/failure, and timer cleanup.

Runtime tests remove catalog fixtures and assert that health success is sufficient for readiness, discovery failure cannot affect readiness, and rebuild recovery does not request a catalog. Shared OpenCode test support drops `RuntimeModelCatalog` and `catalogFactory` setup. Existing Web selector tests remain the regression proof for chips, active selection, persistence, model-only selection, and clearing; no duplicate Web implementation is added.

Alternative considered: retain old catalog fixtures as inert compatibility helpers, rejected because they would keep the removed boundary visible and make future tests depend on behavior that no longer exists.

## Risks / Trade-offs

- `[Synchronous discovery blocks the Node event loop for the command duration] ->` Keep execution infrequent, retain a short bounded command timeout and output cap, and never run discovery on heartbeat cadence. This accepts a bounded pause in exchange for complete stdout.
- `[OpenCode changes the human-readable verbose output format] ->` Keep parsing isolated, structurally parse JSON blocks, tolerate per-model metadata failures, log empty discovery, preserve the last non-empty periodic snapshot, and retry next interval.
- `[The selected CLI binary and SDK-spawned server expose different provider sets] ->` Preserve the explicit command override precedence, treat the catalog only as a configuration hint, and leave execution validity to the serving OpenCode runtime.
- `[An empty result can also mean every provider was intentionally removed] ->` Preserve the established safety rule that empty periodic results do not erase the registered catalog; a later non-empty discovery converges state. The catalog may be temporarily stale but cannot disable execution.
- `[Removing catalog types touches many runtime test fixtures] ->` Remove the types and fixtures in one change and use focused runtime-health and host-discovery fakes; do not add compatibility aliases.
- `[Architecture documentation currently states the opposite readiness rule] ->` Update `design/runtimes/opencode.md` before or with code so repository design and implementation converge atomically.

## Migration Plan

1. Update the model-catalog and readiness sections of `design/runtimes/opencode.md` to the target ownership and lifecycle.
2. Restore the standalone CLI discovery module and its isolated parser/command tests.
3. Remove catalog state and APIs from `OpenCodeRuntime`, its factory, public types, and shared test fixtures; update readiness and rebuild tests.
4. Wire startup and periodic discovery into `RunnerHost`, switch registration to host-owned state, and update host specs.
5. Run runner typecheck and tests. Verify no default test invokes a real process, filesystem, network, or wall clock.
6. Deploy the runner normally. Its first registration after restart replaces the server's runner catalog; no server restart, database migration, persisted-data rewrite, or cache invalidation is required.
7. Verify the project model endpoint contains CLI-reported variants and that all existing selector entries render and persist a selected variant.

Rollback is a runner-code rollback only. Reinstalling the prior runner restores SDK catalog discovery and its readiness coupling; registration/heartbeat and persisted data remain compatible, so no data rollback is needed. The known missing-variant and catalog-gated-readiness defects return under rollback.

## Open Questions

None. The CLI source, command precedence, failure policy, refresh cadence, readiness boundary, and unchanged external contracts are fixed by the proposal and capability specs.
