# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `tasks.json` T-001 and T-002 `output` paths used `packages/server/src/MohistServer/...` (missing dot) for `ConfigService.cs` and `RunnerRoutes.cs`. The real directory is `Mohist.Server` (verified via the codebase), and `proposal.md` Impact already uses the correct form. The typo could mislead an implementer to create a wrong tree.
  Verification: Edited both `output` strings in `tasks.json` to `packages/server/src/Mohist.Server/...`; confirmed no other path strings in the artifacts use the wrong form.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: alignment
  Evidence: `proposal.md` "What Changes" bullet 6 and Impact/Risk bullet both stated the new fault tolerance "mirrors today's `AddJsonStream` + try/catch behavior" / "matching today's fault-tolerance". This is factually wrong: the current `AddMohistConfigFile` does `File.ReadAllText` → `StripJsoncComments` → `AddJsonStream` with NO try/catch (verified at `MohistConfigurationExtensions.cs:19-27`); a malformed `config.jsonc` throws out of `builder.Build()` and aborts startup. The `design.md` (Context line 7, Goals line 28, D4) and the spec already describe fault tolerance as a new behavior, so the proposal was inconsistent with both the verified code and the design/spec.
  Verification: Rewrote the proposal bullet to state fault tolerance is a new behavior (the current path has no try/catch and aborts `builder.Build()`); updated the Risk bullet to "establishes non-fatal reload/load failure handling". Re-read the proposal end-to-end; now consistent with `design.md` Context/Goals/D4 and the spec's "Reload or watcher failure must not block startup" requirement.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: consistency
  Evidence: `design.md` Risks line 131 claimed the only references to `StripJsoncComments` are "the two `ConfigService` call sites (D2) and the `OtelOptions.cs` doc comment (updated to remove the reference)". A repo-wide grep for `StripJsoncComments` returns exactly 4 hits: the definition + 1 caller in `MohistConfigurationExtensions.cs`, and 2 callers in `ConfigService.cs`. There is NO reference in `OtelOptions.cs`. The stale claim could send an implementer looking for a non-existent edit.
  Verification: Removed the "`OtelOptions.cs` doc comment" clause from the risk bullet. Confirmed via `grep -rn "StripJsoncComments" packages/server/src packages/server/tests` that no other references exist.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: feasibility
  Evidence: `design.md` Open Questions leaves two implementation-time decisions open: (a) whether the hot-reload spec lives in a new `Specs/Runner/Config/ConfigHotReloadSpecs.cs` vs. extends `RunnerConfigApiSpecs`, and (b) whether to reuse `MohistIntegrationFixture` vs. a per-test `ConfigHarness`. T-002 correctly defers these to implementation and lists both options; no action needed at plan time.
  SuggestedAction: Resolve during T-002 implementation; prefer the option that minimizes harness code without widening the shared fixture (design leans new spec file + per-test harness).
  Status: follow-up

## Review Notes

Cross-artifact traceability (issue AC ↔ spec ↔ task), all verified:

- AC#1 JSONC native parsing → `config-hot-reload/spec.md` "JSONC is parsed natively without custom comment stripping" → T-001 acceptanceCriteria (JSONC parsing cases).
- AC#2 Hot reload takes effect → `config-hot-reload/spec.md` "Config source reloads on file change" + `options-live-consumption/spec.md` "edited cleanup policy is returned without a server restart" → T-001 (source wiring) + T-002 (live consumption + reload spec).
- AC#3 Dead `reloadOnChange` parameter cleanup → `config-hot-reload/spec.md` "reloadOnChange parameter is honored, not a dead parameter" → T-001 acceptanceCriteria ("ReloadOnChange == true structural assertion").
- AC#4 Redundant `StripJsoncComments` removal → `config-hot-reload/spec.md` "StripJsoncComments is not present on the config-load path" + "ConfigService reads and writes JSONC natively" → T-001 acceptanceCriteria ("zero references and zero definition").
- AC#5 Consumption-point upgrade → `options-live-consumption/spec.md` "Runner config endpoint reads the latest reloaded options per request" → T-002 acceptanceCriteria ("IOptionsSnapshot<CleanupPolicyOptions>").
- AC#6 No regression → `options-live-consumption/spec.md` "Response contract is unchanged" + spec "missing config file is tolerated as optional" → T-001/T-002 acceptanceCriteria.
- AC#7 Testing discipline → design D5 forbids real watcher/wall-clock; T-001/T-002 require deterministic `IConfigurationRoot.Reload()`.
- AC#8 Risk-driven (reload failure non-fatal) → `config-hot-reload/spec.md` "Reload or watcher failure must not block startup" → T-001 acceptanceCriteria ("malformed config.jsonc at startup does NOT throw ... OnLoadException with Ignore=true") + design D4.

Granularity check: two tasks, both cohesive feature slices (source+JSONC+fault-tolerance+ConfigService migration+unit tests in T-001; live consumption+integration reload spec in T-002). No over-fine tasks (no standalone "define interface", "register DI", "create file", "add tests" tasks); tests are integrated into the implementation tasks per the feasibility rule. Dependencies: T-002 `dependsOn: ["T-001"]` points to an existing ID with priority 1 < 2; T-001 has empty `dependsOn` as the first task; no cycles.

<promise>PASS</promise>
