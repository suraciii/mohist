## Context

The Workflow Definition validator delivered by #432 produces a semantic model in which every task and check carries a `uses` action name, a `with` input map (`Dictionary<string, JsonElement?>`), and a YAML path. The Definition validator deliberately treats `with` as an open structure and owns no Action contract: it never judges whether a `uses` exists or whether a `with` key/required/type is valid. #444 delivered the declarative Action manifest, the serializable catalog (`ActionCatalog` with `Actions[]` and tombstoned `Tombstones[]`), and the Runner's dispatch-time input validation. The Runner already reports its catalog on registration and the Server already retains it (`RunnerInfo.ActionCatalog`, persisted as `LastKnownActionCatalogJson` and hydrated on grain reactivation), but nothing consumes it.

The only consumer of `uses`/`with` on the save path today is `WorkflowActionGuards` (`Workflow/Services/WorkflowActionGuards.cs`), a transitional proxy introduced by #432's D7. It special-cases exactly two inline agents (`mohist/opencode`, `mohist/pi`) and only flags three legacy `with` keys (`agent`, `kind`, `type`) plus a legacy `with.expect` completion-policy shape. It never checks `uses` existence, unknown fields, required inputs, types, or any action other than those two. The Profile save entry `WorkflowProfileYamlParser.Parse(yaml, fallbackId)` merges Definition errors with this guard's `source: action` errors and throws `WorkflowDefinitionValidationException` on any error.

Stakeholders are workflow authors (who want early, actionable feedback), and the dispatch path (which must remain the authoritative fail-closed judge). Risk is medium: the change enters the Profile save main path, but builds entirely on completed infrastructure and degrades gracefully when no catalog is present.

The authoritative design contract is [`design/workflow/actions.md`](../../design/workflow/actions.md) §"校验时机与 catalog 消费" (status gap #1). This document defines how that contract is implemented.

## Goals / Non-Goals

**Goals:**
- Consume the retained Runner Action catalog during Profile save to reject unknown `uses`, unknown `with` fields, missing required inputs, and constant-value type mismatches, with field- and reason-level diagnostics.
- Distinguish tombstoned (removed) Actions from unknown ones, surfacing tombstone guidance.
- Validate template-expression inputs by field name only; defer value-type checking to the Runner.
- Skip cleanly when no catalog is available and report that Action-contract validation was not performed, without rejecting the save.
- Merge Action-contract errors with Definition errors through the shared YAML-path rule and the existing `ValidationSource.Action` label, with no Definition or template-namespace rule duplicated.
- Replace the transitional `WorkflowActionGuards` with the catalog-backed check.

**Non-Goals:**
- Multi-Runner catalog merging; the single-Runner model is used and divergent catalogs across runners are out of scope.
- Making `mo run validate` connect to the Server or replicate the catalog; it stays Definition-language only.
- Changing Action manifest input/output/error definitions, or Runner catalog reporting and dispatch-time validation (#444).
- Validating built-in profiles against the catalog at load time; built-ins load via `WorkflowProfileCatalog` (Definition-only, validated by #432's CI golden cases) and no Runner is registered at startup.
- Adding catalog golden cases for built-in profiles in CI (a possible follow-up, not required by the acceptance criteria).

## Decisions

### D1: The catalog check is a pure server-side validator; the Definition library stays host-free

**Decision:** Implement the catalog check as a pure function `ActionContractValidator.Validate(WorkflowDefinition definition, ActionCatalog catalog) -> IReadOnlyList<ValidationError>` (each error `Source = ValidationSource.Action`) in `Mohist.Server`, consuming the #432 model and the #444 catalog records (`Mohist.Server.Runner.Grains.ActionCatalog`). It lives alongside the model it walks and the catalog type it consults. `WorkflowProfileYamlParser.Parse` gains an `ActionCatalog?` parameter; when non-null it delegates to this validator and merges its errors with Definition errors exactly as it merges `WorkflowActionGuards` errors today (thrown together on any error). The standalone `Mohist.Workflow.Definition` library gains no knowledge of catalogs — it already exports `ValidationSource.Action` for the save path to use.

**Rationale:** The catalog types are server-side Orleans records; pulling them into the dependency-free library would either couple the library to Orleans (rejected by #432 D1) or force a parallel catalog abstraction in the library. A pure server-side function over the already-validated model keeps one merge point (the save entry), one error type, and one catalog type. Keeping the check a pure function makes it trivially unit-testable without grains or HTTP.

**Alternatives considered:**
- *Move the check into `Mohist.Workflow.Definition` with a catalog abstraction.* Rejected: it drags host concerns into the CLI-shared library and duplicates the catalog shape; the library's only Action-related export should remain `ValidationSource`.
- *Validate in the Runner.* Rejected: the whole point is early save-time feedback on the Server, before any dispatch.

### D2: A scoped catalog source hides the grain call from the save managers

**Decision:** Introduce `IActionCatalogSource.GetCatalogAsync() -> ActionCatalog?` (a scoped DI service) backed by `RunnerRegistryCatalogSource`, which resolves `IGrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).ListRunnersAsync()` and selects a catalog (D3). The save managers (`ProjectWorkflowProfileManager`, `IssueWorkflowProfileManager`) inject it and resolve the catalog once per save before calling `WorkflowProfileYamlParser.Parse(yaml, fallbackId, catalog)`. This mirrors the established pattern of injecting `IGrainFactory` into scoped services (`RunnerStatusService`, `WorkflowArtifactUploadService`).

**Rationale:** The managers are already the async, DI-backed orchestration point for save and are the natural place to resolve the catalog, while `WorkflowProfileYamlParser.Parse` stays synchronous and remains the single error-merge point. An interface lets spec tests inject a fake catalog source (per the no-real-dependency testing rule) instead of touching real grains.

**Alternatives considered:**
- *Inject `IGrainFactory` directly into the managers.* Rejected: it couples the managers to Orleans and makes faking harder; a thin interface is the codebase's existing fake seam.
- *Make `WorkflowProfileYamlParser.Parse` async and resolve the catalog internally.* Rejected: it fuses a host concern into a parsing entry point and forces async into every caller, including the merge point that #432 deliberately kept synchronous.

### D3: Single-Runner catalog selection — most recently registered catalog wins

**Decision:** `RunnerRegistryCatalogSource` picks the catalog of the most recently registered runner (max `RegisteredAt`) that reports a non-null `ActionCatalog`. If no runner has reported a catalog, it returns null and the save skips the Action-contract check (D4). When two or more runners carry divergent catalogs, the selection is still deterministic (latest wins) but divergence is a known limitation explicitly deferred (multi-Runner merging is a non-goal).

**Rationale:** Built-in Actions are compiled into every runner, so catalogs agree in the common case; divergence only arises across runner versions or future custom Actions. "Most recently registered" is deterministic and biases toward the freshest catalog. A null result (no runner yet) is the expected pre-registration state and must not block saving.

**Alternatives considered:**
- *Union all runners' catalogs.* Rejected: it is the multi-Runner merge strategy explicitly out of scope, and a union would mask genuinely unknown `uses` as known.
- *Require exactly one runner or fail.* Rejected: it would reject saves in the normal no-runner-yet state, violating the "do not mis-reject" criterion.

### D4: No catalog means skip, not reject; the save outcome carries the notice

**Decision:** When `IActionCatalogSource.GetCatalogAsync()` returns null, the save runs only Definition validation and succeeds (subject to Definition errors); `ActionContractValidator` is not invoked. The save managers surface an Action-validation status to the route handlers, and the success response of the template create/update endpoints (project template create, project template update, issue template update) carries an additive field indicating that Action-contract validation was not performed (e.g. `actionValidation: { performed: false }`; performed saves omit it or report `performed: true`). The exact response field name is an implementation detail; the contract is that the caller can tell validation was skipped.

**Rationale:** The criterion requires the save to both succeed and explicitly state it skipped Action-contract validation. The managers are the layer that knows whether a catalog was resolved, so they own the notice; the additive response field is backward-compatible. Definition errors still throw `WorkflowDefinitionValidationException` unchanged.

**Alternatives considered:**
- *Return a notice from `WorkflowProfileYamlParser.Parse`.* Rejected: `Parse` only sees the catalog passed to it, not whether one *could* be resolved; the skip decision belongs to the manager that owns the catalog fetch.
- *Log-only notice.* Rejected: the criterion requires the *outcome* to state it, which must reach the API caller, not just a server log.

### D5: Template-expression inputs are detected by a shared library helper

**Decision:** Expose a small public helper in `Mohist.Workflow.Definition` (e.g. `TemplateTokens.Contains(JsonElement?)`) that walks a `with` value and reports whether any string contains a `${{ }}` token, using the same token grammar already owned by the library (`WorkflowDefinitionRules.TemplateTokenRegex`, #432/#431). The catalog validator uses it to apply the field-name-only rule: for a declared input whose value contains a template, it validates only the field name and skips value-type checking; for an unknown field it rejects regardless of whether the value is a template.

**Rationale:** The token grammar must have one owner or the validator and the Definition rules drift. The library already owns the grammar privately; exposing a `Contains` helper keeps it singular without exposing validation internals. This lets the catalog check ask exactly the question it needs ("is this value a template?") without re-implementing expression parsing.

**Alternatives considered:**
- *Duplicate the regex in the server catalog validator.* Rejected: it creates a second source of truth that can diverge from the Definition validator.
- *Treat every non-primitive value as a potential template.* Rejected: only string values can carry `${{ }}`; a precise detector avoids both false skips and false type assertions.

### D6: Exact-JSON-kind type check mirrors the Runner; null is absent

**Decision:** For a declared input supplied with a constant (non-template) value, the validator checks the value's JSON kind against the catalog's declared `Types[]` using the same exact-kind rule as the Runner's `validateActionInput`: `string`, finite `number`, `boolean`, non-null `object` (not array), or `array`, with no coercion, stringification, or numeric/boolean parsing. An explicit `null` matches no kind and is treated as absent for optional inputs (and as a missing-required failure for required inputs without a default). The engine-reserved `working-directory` key is exempt from the unknown-field check, matching the Runner. The mapping is new server (C#) code, mirrored semantically — not shared — with the Runner (TypeScript); both are test-covered.

**Rationale:** Save-time and dispatch-time must agree on what "type matches" means so a Profile that passes save is not spuriously failed at dispatch, and vice-versa. Mirroring the established exact-kind rule (rather than inventing a save-specific one) keeps the two consistent. The two implementations cannot share code (different languages), so a mirrored, tested rule is the faithful option.

**Alternatives considered:**
- *Share a single kind-mapping definition across server and runner.* Rejected: they are different languages in different packages; a shared definition is not feasible without a code-generation step that adds complexity for no correctness gain.
- *Looser save-time types (accept anything that might coerce).* Rejected: it would let Profiles pass save that dispatch then rejects, recreating the late-failure problem this change removes.

### D7: The transitional guard is deleted; all Actions get full input checking

**Decision:** Delete `WorkflowActionGuards.cs` and its dedicated tests. The legacy `with.agent` / `with.kind` / `with.type` keys and the legacy `with.expect` completion-policy shape become ordinary "unknown input field" rejections for their resolved Action (none of those keys are declared inputs), so they are subsumed by the catalog check. Crucially, every Action now receives full `uses`/input checking, not just the two previously special-cased inline agents. The catalog check also judges every task/check position — stage tasks, stage checks, approval-feedback tasks, and tasks nested in recovery handlers — where the transitional guard covered only the first two plus approval-feedback tasks.

**Rationale:** #432 D7 kept the guard only as a bridge to #446; with the real catalog check in place it is redundant and its narrow scope is a regression risk. Folding legacy keys into the general unknown-field rule preserves their rejection under one owner instead of two.

**Alternatives considered:**
- *Keep the transitional guard alongside the catalog check.* Rejected: it duplicates Action-contract ownership for two agents and would shadow the catalog's judgment for them.

### D8: Built-in loading, runtime load, and the CLI are unchanged

**Decision:** `WorkflowProfileCatalog.LoadProfile` (built-in profiles) continues to call `WorkflowDefinitionParser.Parse` directly with no catalog — no runner is registered at startup, so a catalog check there would always skip. Runtime load (`WorkflowProfilePersistence.Deserialize`) continues to run the model-level `WorkflowDefinitionValidator.Validate` (Definition rules only). `mo run validate` continues to call `WorkflowDefinitionParser.Parse` directly with no Server connection. None of these gain a catalog dependency.

**Rationale:** The catalog is a runtime-resolved Runner artifact; it is meaningless at startup load and intentionally absent from the offline CLI (non-goal). Confining the catalog check to the user-initiated save entry matches where a runner is plausibly present.

## Risks / Trade-offs

- **[Save now rejects Profiles that previously saved silently]** -> A Profile with an unknown `uses` or a bad `with` that passed before will now be rejected at save. Mitigation: this is the intended behavior and the whole point of the change; built-in profiles are already valid against the catalog (migrated by #432/#444), and user profiles that fail get actionable, field-level errors. When no catalog is present the save still succeeds (D4), so a freshly started Server before any runner registers is unaffected.
- **[Catalog staleness — the retained catalog may lag the dispatching runner]** -> The catalog used at save may differ from the runner that eventually executes the Profile (different version, or a runner registered between save and dispatch). Mitigation: dispatch-time validation remains authoritative and fail-closed (AC7); save-time is explicitly advisory early feedback. The single-Runner latest-catalog selection (D3) biases toward freshness.
- **[Two language-mirrored type rules can drift]** -> The C# save-time kind check and the TypeScript dispatch-time check are independent implementations. Mitigation: D6 pins both to one documented exact-kind rule; shared spec scenarios (numeric-string-for-number, object-for-string, union-kind, optional-null) are exercised on both sides.
- **[Multi-Runner divergence masked as single truth]** -> With two runners carrying different catalogs, latest-wins may reject a `uses` that the other runner actually provides. Mitigation: D3 keeps selection deterministic and divergence is an explicit non-goal; the dispatch boundary catches genuine mismatches. A follow-up can add multi-Runner merging if needed.
- **[New save-time dependency on a grain call]** -> Every Profile save now resolves the registry grain. Mitigation: the call is one read per save (`ListRunnersAsync`), already used by many request paths; the catalog source is faked in tests so no real grain is touched.

## Migration Plan

1. Add `IActionCatalogSource` + `RunnerRegistryCatalogSource` (scoped, `IGrainFactory`-backed) and register it in DI.
2. Implement the pure `ActionContractValidator.Validate(definition, catalog)` covering: unknown `uses`, tombstone-vs-unknown, unknown `with` fields (minus `working-directory`), missing required, exact-kind constant-value check (D6), and the field-name-only template rule.
3. Expose the `TemplateTokens.Contains(JsonElement?)` helper from `Mohist.Workflow.Definition` (single grammar owner) for D5.
4. Add the `ActionCatalog?` parameter to `WorkflowProfileYamlParser.Parse`; replace the `WorkflowActionGuards` call with `ActionContractValidator` when a catalog is supplied; delete `WorkflowActionGuards.cs` and convert its tests.
5. Wire the save managers to inject `IActionCatalogSource`, resolve the catalog once per save, pass it to `Parse`, and surface the Action-validation status to the route handlers.
6. Add the additive `actionValidation` notice to the project-template create/update and issue-template update success responses.
7. Add spec tests (server `SpecTests`) covering every catalog-check scenario (unknown/tombstoned `uses`, unknown field, missing required, type mismatch, template-name-only, engine-reserved key, no-catalog skip + notice, recovery/approval positions judged, merged error sources).
8. Update the `design/workflow/actions.md` status gap to reflect that Profile save-time Action-contract checking is now implemented.
9. Deploy server atomically. **Rollback:** revert the release; persisted Profiles are unaffected (no storage format change — the catalog check is read-only over the Definition model and the retained catalog). The transitional guard's removal is recovered by the revert; no data migration is involved.

## Open Questions

- **Skip-notice response shape:** D4 commits to surfacing the skip in the success response but leaves the field name open (`actionValidation.performed` vs a top-level boolean). Lean toward a small nested object so it can later carry richer status without another breaking change; confirm against the Web UI's existing response consumption during implementation.
- **Catalog-check coverage for built-ins in CI:** Whether to add a CI test that validates the built-in profiles against a fixture catalog (catching built-in drift against shipped Actions earlier than a user save). Not required by the acceptance criteria; defer unless the integrate stage calls for it.
- **Divergent-runner observability:** Whether to log a warning when two registered runners carry divergent catalogs, to surface the multi-Runner limitation before someone relies on it. Lean toward a single warning log line; confirm it does not add noise in the common single-runner case.
