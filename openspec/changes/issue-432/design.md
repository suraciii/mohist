## Context

`WorkflowYamlSerializer` is the only Workflow Definition parser today. It deserializes YAML with `IgnoreUnmatchedProperties()` (unknown fields are silently dropped), coerces some invalid values to defaults (e.g. `budget` parses to `0` on failure), and throws `InvalidOperationException` on the first problem — so an author who misspells a field or wrongs a type gets at most one error, and often none. The model (`WorkflowDefinition.cs`) also diverges from its target shape: `title` is forced required, `uses` is nullable, and checks identify with `name`. The parser lives entirely inside `Mohist.Server`; the CLI (`packages/cli`) is a separate project that talks to the server over HTTP and references no server code, so there is no way to validate a Definition offline.

Two prerequisites make the language contract stable enough to enforce authoritatively. #474 separated Profile metadata, Definition, and Variables into distinct assets — a Definition is now purely `approval` + `stages`, and the serializer already rejects the six removed top-level fields as a narrow guard (intentionally leaving broad unknown-field validation to this change). #431 closed the template namespace to exactly ten public roots and unified fail-fast rendering, but explicitly deferred static field/type validation to #432 and save-time Action-contract validation to #446. Stakeholders are workflow authors, project operators, and the server/runner dispatch path. This is high risk: the change replaces the parsing boundary and is consumed simultaneously by the server save path, the CLI, and CI; a mistake in error collection, template-position rules, or the shared model affects every Profile.

The authoritative design contract is [`design/workflow/definition.md`](../../design/workflow/definition.md) (model, rule table, placement, three entry points). This document defines how that contract is implemented.

## Goals / Non-Goals

**Goals:**
- One authoritative `Parse(yaml) → Definition | Error[]` validator owning every Definition-language rule, living in a standalone library shared by the server save path, the CLI, and CI.
- Collect all errors in one pass with YAML paths and domain-language messages; reject unknown fields; report type errors instead of coercing defaults.
- Enforce the full structural/type/template-position rule table from `design/workflow/definition.md`, including the tightened model shape (`uses` required, `title` optional, check `id`).
- Keep `with` an open structure (object + recursive template validation, no key/type interpretation) and preserve the single-owner boundary against the Action catalog (#446).
- Deliver `mo workflow validate --file <path|->` as an offline command and CI golden cases for built-ins plus the docs example.

**Non-Goals:**
- Validate whether an action `uses` exists or whether a `with` value satisfies an Action manifest — that is #446.
- Export a JSON Schema.
- Validate runtime-inserted recovery or control tasks; those are constructed from already-validated subtrees or by their constructors.
- Prove a referenced task succeeds or produces a specific output field at runtime — runtime value presence is owned by template rendering (#431) and Action output.
- Validate scattered documentation fragments or syntax skeletons carrying `<...>` placeholders.
- Migrate the whole `mo` command tree; only the `workflow validate` leaf command is in scope.
- Re-do the Profile/Definition/Variables asset boundary (#474) or the template namespace closure (#431).

## Decisions

### D1: A standalone shared library is the validation boundary

**Decision:** Create `Mohist.Workflow.Definition` as a new project containing the semantic model, parser, validator, and error type, with no Orleans and no ASP.NET dependency. Both `Mohist.Server` and `Mohist.Cli` reference it. The public root table (the ten roots #431 locked) lives in this library; the server `PromptTemplateEngine` references that constant so there is exactly one owner of the root allowlist. The single `Parse(yaml)` entry point is the only place Definition YAML is interpreted.

**Rationale:** Three consumers (save path, CLI, CI) must run identical rules. The CLI must work offline, so it cannot call the server. A shared library is the only structure that gives one owner without dragging host concerns into the CLI. The current `WorkflowYamlSerializer` is fused to the server and is the thing being superseded.

**Alternatives considered:**
- *Keep parsing in the server and have the CLI validate via an HTTP endpoint.* Rejected: the issue requires offline validation with no server connection.
- *Duplicate the parser/rules in the CLI.* Rejected: it duplicates the rule set and guarantees drift — exactly the "each re-parses an approximate structure" failure the proposal removes.
- *Put the library inside the server project and reference the server from the CLI.* Rejected: it transitively pulls Orleans and ASP.NET into the CLI.

### D2: The canonical model is the library's; the server registers Orleans surrogates

**Decision:** The library's plain record types (`WorkflowDefinition`, `StageDefinition`, `TaskDefinition`, `CheckDefinition`, `RecoveryDefinition`, …) are the canonical semantic model in the tightened target shape: `Uses` is non-nullable and required, `Title` is nullable and optional, and `CheckDefinition` identifies by `Id` (not `Name`). Server grain and persistence code consumes these library types directly. Because the library must stay free of Orleans attributes, the server registers `IExternalSerializer` surrogates for the library records — following the existing `WorkflowDefinitionSurrogates` pattern — so grains can serialize them. The surrogate is a serialization shadow, not a second domain model.

**Rationale:** "One model and one error type, no one re-parsing an approximate structure" requires a single in-memory contract. Keeping a parallel server-side domain model would re-introduce the split. Surrogates decouple serialization from the domain without duplicating it.

**Alternatives considered:**
- *Keep a server-side shadow model mapped from the library model.* Rejected: it splits the contract and invites the two shapes to drift; the narrow `WorkflowStructure`/`StageStructure` projections the grain already uses remain as narrow read-only views, not a parallel model.
- *Put `[GenerateSerializer]` on the library records.* Rejected: it couples the library to Orleans.

### D3: Two-phase parsing — tolerant YAML load, then a collecting semantic walk

**Decision:** Parsing is two phases. Phase 1 loads YAML text into a node tree (`YamlDotNet` `YamlNode` / `YamlDocument`), which is tolerant of structure and preserves line/column. A YAML-syntax failure in phase 1 is a single fatal error. Phase 2 is a validating reader that walks the node tree, checking unknown fields, types, required fields, and template expressions, appending `ValidationError`s with YAML paths, and building the `Definition` from the validated nodes. The walk continues across siblings after a malformed node (it skips an unrecoverable subtree but keeps validating the rest), so a Definition with several problems yields several errors.

**Rationale:** Full single-pass error collection is an explicit acceptance criterion. Typed deserialization with `IgnoreUnmatchedProperties` either drops unknown fields silently or throws on the first, and cannot recover to report later errors. Walking a position-preserving node tree is what lets the validator emit paths like `stages[1].tasks[0].recovery.handlers[0]` and keep going.

**Alternatives considered:**
- *Configure YamlDotNet typed deserialization to reject unknown fields.* Rejected: it throws on the first unknown field and cannot collect all errors; it also silently coerces types via the target type system, defeating the "no silent defaults" goal.
- *Keep the current `Dictionary<object, object?>` walk but collect instead of throw.* Rejected in favor of the node tree: the node tree preserves line numbers (a bonus for messages) and handles nested arrays/maps more uniformly than the loose dictionary walk.

### D4: Error model — YAML path + domain message + source

**Decision:** Every error is a `ValidationError(Path, Message, Source)`. `Path` uses object keys for mappings (`approval.feedback.tasks`) and `[i]` for array indices, built as the walk descends. `Message` is written in domain language (it names the construct and the rule) and never contains exception stack traces, type names, or source paths. `Source` is `definition` for every error this validator emits; the save path's combined result also carries `action`-sourced errors from #446, so the two are distinguishable. Errors are returned deterministically (sorted by path).

**Rationale:** The acceptance criteria require YAML-path-located, domain-language messages and require Definition errors and Action errors to be distinguishable by source with one shared path rule. A dedicated error type (rather than reusing exceptions) is what enables collecting many at once.

**Alternatives considered:**
- *Throw a rich exception per error.* Rejected: exceptions abort the walk and cannot represent "many problems at once."
- *Embed a structured error code.* Deferred as an implementation detail; the domain message is the contract.

### D5: Template static validation reuses the shared root table and a context flag

**Decision:** The validator extracts every `${{ }}` expression from string-typed values across the Definition — including recursively inside `with` and `expect` — using the same token grammar #431 locked, and checks each root against the public table constant owned by the library (D1). As the walk descends it carries a position context — *ordinary stage task*, *recovery-handler task*, or *approval-feedback task* — and enforces: `failure.*` only inside recovery-handler tasks; `work.approvalFeedback.*` only inside approval-feedback tasks. The library does not duplicate the root list; `PromptTemplateEngine.AllowedRoots` is rewritten to reference the library constant.

**Rationale:** Position rules are Definition-language rules, so they belong to the one validator. The root table must have one owner or the validator and engine drift apart. Carrying a single context flag during the existing walk avoids a second pass.

**Alternatives considered:**
- *Validate templates in a separate post-parse pass over the model.* Rejected: it splits position knowledge from the walk that already knows whether a task is a recovery/feedback task, and risks losing the YAML path.
- *Copy the ten roots into the validator.* Rejected: it creates a second source of truth that can diverge from the engine.

### D6: `tasks.<id>` references resolve only to strictly-earlier execution positions

**Decision:** The validator builds an ordered declaration list of all task ids by execution order (stages in document order, tasks within a stage in order; approval-feedback tasks execute after their stage; recovery-handler tasks execute after their owning task). For each `tasks.<id>` reference it records the referencing task's execution position and requires the referenced id to be declared at a strictly-earlier position. A reference to the enclosing task's own id (self) or to any task that can only execute later (forward) is rejected. The validator does not assert the referenced task will succeed or that the referenced output field will exist — that remains a runtime concern owned by #431.

**Rationale:** The acceptance criteria require rejecting self/forward references at save time while explicitly not guaranteeing runtime output presence. "Strictly-earlier execution position" is the faithful static analog of "can execute before the reference position," and it is computable without runtime facts.

**Alternatives considered:**
- *Only check that the id is declared anywhere.* Rejected: it permits self/forward references, which the issue explicitly forbids.
- *Model full runtime reachability (branches, recovery retries).* Rejected: it requires runtime facts and over-promises; static declaration order is the correct save-time boundary.

### D7: `with` is open — validate shape and templates, defer keys to #446

**Decision:** The validator requires `with` to be absent or a JSON object (YAML mapping), recurses into all string values inside it to validate `${{ }}` expressions (D5/D6), and otherwise treats `with` as opaque. It never reports an unknown `with` key, never checks a key's required-ness, and never checks a value's type against any Action contract. A regression test asserts the validator accepts arbitrary `with` keys so the Action-key rule cannot accidentally migrate in.

The existing legacy inline-agent input guards in `WorkflowYamlSerializer` (`with.agent`, `with.kind`, `with.type`, legacy `with.expect` shape for `mohist/opencode` / `mohist/pi`) are Action-contract concerns, not Definition-language rules. They remain on the save path as a transitional Action-catalog proxy until #446 delivers the real catalog check; the Definition validator does not own them.

**Rationale:** "Each rule has exactly one owner" and "`with` key/type validation belongs to the Action catalog." Keeping the transitional guard prevents a regression window before #446, while explicitly labeling it as Action-catalog territory preserves the single-owner boundary.

**Alternatives considered:**
- *Move the legacy `with` guards into the Definition validator.* Rejected: they are Action-contract rules tied to specific `uses` values; placing them in the Definition validator duplicates Action ownership.
- *Drop the legacy guards now and rely on #446 later.* Rejected: #446 is not delivered; dropping them would let legacy shapes pass with no replacement (the same call #431 D6 made).

### D8: Save path and built-in catalog consume the validator; runtime load uses the same parser

**Decision:** The Profile save managers (`ProjectWorkflowProfileManager`, `IssueWorkflowProfileManager`) and the built-in `WorkflowProfileCatalog.LoadProfile` call `Parse` instead of `WorkflowYamlSerializer`. On success they continue with the validated model (and, for the save path, hand it to the Action catalog boundary for #446). On failure the save API returns `BadRequest` carrying the full, path-sorted `ValidationError` list with `source: definition`. The runtime load path (per-stage `LoadStageSpecsAsync` / approval loading) also uses `Parse`, so a Definition that became invalid after tightening fails to load with a clear error rather than silently dispatching wrong behavior.

**Rationale:** The validator's three declared entry points (save API, CLI, CI) all need the save path to return the full list. Letting the runtime load path re-parse through a different code path would recreate the "each re-parses an approximate structure" problem, so it shares `Parse` too.

**Alternatives considered:**
- *Validate only at save time; let load be lenient.* Rejected: it splits the rule owner between save and load and lets a now-invalid stored Profile dispatch silently.
- *Have the save path call a server-side wrapper that re-checks Definition rules.* Rejected: it duplicates the validator's rules.

### D9: `mo workflow validate --file <path|->` is an offline leaf command

**Decision:** Add a `validate` subcommand to the existing `workflow` command group. It takes `--file <path>` or `--file -` (stdin), reads the Definition text, and calls the library's `Parse` directly — no HTTP, no Project resolution, no server. On success it prints a clear valid message and exits `0`. On failure it prints each error's path and message and exits non-zero. A 2-source `--file` reader is added (file path or `-` for stdin), modeled on the existing `BodyInputResolver`/`ExpandAtFileAsync` patterns but narrower.

**Rationale:** The acceptance criteria require an offline command that returns the same Definition errors as the save path. Calling the shared library directly (the CLI references it per D1) guarantees identical errors with no server round-trip.

**Alternatives considered:**
- *Implement validate as a server endpoint the CLI calls.* Rejected: the issue requires no server connection.
- *Reuse the 3-source `BodyInputResolver`.* Rejected: it models mutually-exclusive `--body`/`--body-file`/`--body-stdin` for issue bodies; `validate` has a single `--file` option where `-` means stdin, which is a cleaner surface for this command.

### D10: CI golden cases are locked by tests, with a negative case and explicit exclusions

**Decision:** Add tests (in the server test project, so they run under `npm test`) that run `Parse` over (1) every built-in `.workflow.yaml` and (2) the complete fenced example extracted from `docs/workflow-definition.md`, asserting success. A negative test injects an unknown field into a golden-case Definition and asserts `Parse` fails with the expected path/message. The docs-example extractor selects only the complete example block; syntax skeletons containing `<...>` placeholders and scattered fragments are explicitly excluded and never passed to `Parse`, so they cannot produce false positives.

**Rationale:** The acceptance criteria require built-ins and the docs example to pass, an injected unknown field to fail with the same error, and skeletons/fragments to be excluded. Tests lock the syntax↔validator contract and surface regressions in the default `npm test` run, which is preferable to a standalone CI script.

**Alternatives considered:**
- *A standalone CI shell step invoking `mo workflow validate`.* Rejected: it runs outside `npm test`, gives weaker assertions, and adds a CI-only failure mode; a test is locked and runs everywhere.

## Risks / Trade-offs

- **[BREAKING: tightened model rejects previously-accepted custom Profiles]** -> `uses` becomes required, `title` becomes optional (safe), and check `name` becomes `id`. Stored custom Profiles using check `name` or omitting `uses` will fail to save and, because load uses the same parser (D8), fail to load at runtime. Mitigation: built-ins are migrated in the same change; operators fix custom Profiles guided by the precise path/message errors; there is no silent acceptance. Rollback restores the prior release.
- **[Runtime load failure on a now-invalid stored Profile]** -> A custom Profile valid before the change can break an in-flight workflow at stage load. Mitigation: fail fast with a clear Definition error (consistent with removing silent acceptance); document the migration; the failure is at load, not mid-dispatch of a half-parsed task.
- **[Two-phase tolerant walk produces false positives/negatives]** -> A malformed subtree could be mis-handled. Mitigation: phase 1 fails fast on YAML syntax; phase 2 skips only unrecoverable subtrees and keeps validating siblings; the full rule table is covered by tests.
- **[Template position ordering has edge cases across recovery/feedback scopes]** -> Mis-modeling feedback/recovery execution order could reject valid references or accept forward ones. Mitigation: D6 defines strictly-earlier execution position explicitly; tests cover self, forward, cross-stage-earlier, recovery-task, and feedback-task references.
- **[Root table drift between validator and engine]** -> Copying the ten roots would let them diverge. Mitigation: D1/D5 make the library the single owner and rewrite `PromptTemplateEngine` to reference it.
- **[Library drags host concerns into the CLI]** -> Mitigation: the library has no Orleans/ASP.NET `PackageReference`; enforced by project refs and an arch test.
- **[Orleans serialization of library records]** -> Plain records have no `[GenerateSerializer]`. Mitigation: D2 registers server-side surrogates following the existing pattern; a round-trip test verifies grain serialization.

## Migration Plan

1. Create `Mohist.Workflow.Definition` with the tightened model, `Parse` validator, `ValidationError` type, and the shared public root table; add it to the solution.
2. Migrate both built-in `.workflow.yaml` files to the target shape (check `name`→`id`, ensure `uses` present; `title` is already present and remains valid as an optional field).
3. Rewrite `PromptTemplateEngine.AllowedRoots` to reference the library root table (single owner).
4. Wire the server save managers, built-in catalog, and runtime load path to `Parse`; register Orleans surrogates for the library records; remove the superseded `WorkflowYamlSerializer` unknown-field/type paths (keep the transitional inline-agent `with` guard on the save path per D7).
5. Add the `mo workflow validate --file <path|->` command to the CLI (reference the library; 2-source `--file` reader).
6. Add CI golden-case tests (built-ins + docs example pass; unknown-field injection fails) and the skeleton/fragment exclusion.
7. Update `docs/workflow-definition.md` implementation-gap section and `design/workflow/definition.md` status to reflect the enforced rules.
8. Deploy server + built-ins + CLI atomically. **Rollback:** revert the release and restore prior built-in assets. Persisted custom Profiles are stored as YAML text and re-parsed on load; Profiles rejected by the tightened rules must be fixed by the operator using the reported errors (no automatic data migration, consistent with the issue scope).

## Open Questions

- **Load-path failure mode:** When a stored Definition is now invalid, should load fail the stage (fail fast) or fail the whole WorkflowRun? Leaning toward failing the stage load with a clear Definition error so the run surfaces a precise diagnostic; to confirm against the grain's stage-init flow during implementation.
- **Docs-example extraction source:** Read the fenced example straight from `docs/workflow-definition.md` at test time, or commit a mirrored fixture the docs link to? Leaning toward reading the doc directly so the syntax and the golden case cannot drift; the extractor must select only the complete example block.
