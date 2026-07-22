## Context

`WorkflowDefinition` currently carries Profile identity and display fields, top-level and stage Variables, and unrelated top-level fields. `WorkflowYamlSerializer`, `ResolvedTemplate`, the project/issue template managers, and built-in YAML all preserve that mixed shape. `WorkflowProfileManager` then merges embedded values with global, Project, Issue, and WorkflowRun layers on every dispatch.

This change separates the three ownership domains defined in the proposal and specs: Profile metadata selects and describes a Definition, Definition describes workflow behavior, and Project, Issue, and WorkflowRun own Variables. The main stakeholders are workflow authors, project operators configuring Variables, and the server/runner dispatch path. This is high risk because a missed selection or live-variable path can either make a Profile unloadable or dispatch a task with a missing value.

## Goals / Non-Goals

**Goals:**
- Represent Profile metadata and pure Definition behavior with separate domain and persistence shapes.
- Use the same Profile-to-Definition resolution path for Project templates, Issue overrides, built-ins, startup, and later stage loads.
- Remove Definition and global-config participation from effective Variable resolution while preserving Project, Issue, Run, and stage precedence.
- Seed only the built-in values required for existing workflows: Issue `agent: {}` and WorkflowRun `archive: ""`.
- Preserve task-level `setVars` as a Workflow language construct that writes WorkflowRun Variables.

**Non-Goals:**
- Add a Project-scoped Profile collection, Settings UI, or a broad API redesign.
- Define a general unknown-field/type/template-location validator; issue #432 owns that work.
- Change template expression namespaces, interpolation behavior, or Variable resource routes.
- Preserve embedded Definition Variables through a compatibility read path.

## Decisions

### Model Profiles as an envelope around a pure Definition

Introduce a `WorkflowProfile` value that contains `id`, `name`, `description`, and `WorkflowDefinition`. `WorkflowDefinition` retains only `Approval` and `Stages`; `StageDefinition` no longer has Variables. `WorkflowStructure` and stage-loading APIs consume the Definition behavior, while selection and API response assembly retain the Profile envelope.

Persist custom project and issue Profiles as the envelope while retaining the existing storage keys (`ProjectId` + template ID, or `ProjectId` + issue number). The project template key remains the Profile identity; issue custom Profile metadata is persisted with its custom Profile rather than synthesized from Definition fields. Built-ins are composed from catalog metadata plus pure `.workflow.yaml` Definitions.

Alternative considered: keep metadata fields on `WorkflowDefinition` and simply stop using them at runtime. Rejected because serializers and direct reads would continue to present those fields as Definition language, leaving the contract ambiguous.

### Make one Definition serializer the language boundary

Refactor `WorkflowYamlSerializer` and JSON persistence conversion to read/write only `approval` and `stages`. It will explicitly reject the known removed fields `id`, `name`, `description`, `variables`, `defaults`, and top-level `artifacts`, and reject `stage.variables`. Task-level artifacts and `setVars` remain valid parts of a stage/task.

The serializer will not become a general unknown-field validator in this change. It handles the removed, known ambiguity directly and leaves broader language validation to #432.

Alternative considered: silently ignore removed fields. Rejected because a workflow author would believe a value still affects execution, and the breaking surface would be invisible.

### Resolve a Profile once per lookup and remove embedded variables

Replace `ResolvedTemplate` with a resolved Profile result containing the selected Profile identity and its pure Definition. The existing selection cascade remains: Issue custom Profile, Issue project-template reference, Project default, then enabled built-in Profile. `LoadStructureAsync`, `LoadStageSpecsAsync`, and approval loading all use that resolver, so later stage entries continue to observe Definition updates through the same boundary.

Remove `EmbeddedVariables`, `ExtractEmbeddedVariables`, Definition variable fields, and the template load performed only to resolve Variables. `ResolveEffectiveVariableBundleAsync` loads Project, Issue, and WorkflowRun `VariableBundle`s, deep-merges them in that order, and applies selected-stage overlays using the existing bundle mechanics. `ConfigService.GetVariables()` is removed from this path because global configuration is not a Variables resource in the target model.

Alternative considered: keep `ResolvedTemplate.EmbeddedVariables` empty as a compatibility placeholder. Rejected because it retains a second variable-source contract and invites reintroduction of Definition-owned Variables.

### Initialize defaults in their owning resources, idempotently

`IssueVariableBuilder.BuildContextBundle` will add `agent: {}` to the Issue bundle. The existing deep patch preserves explicit Issue values. A narrow `WorkflowRunProfileManager` initialization operation will ensure `archive: ""` exists when a WorkflowRun is created, using a merge that never overwrites an existing Run value. Call it from the WorkflowRun creation path before work can be dispatched.

Task completion already carries `setVars` into workflow follow-up handling. That path will continue to patch `WorkflowRunProfile` only. Variable resolution remains live, so a retry or re-entered stage reads the persisted Run value rather than a task or Definition snapshot.

Alternative considered: put both defaults into built-in Definition YAML. Rejected because it recreates the forbidden ownership boundary and does not cover custom Profiles using the same workflow tasks.

## Risks / Trade-offs

- [Persisted custom Definitions can contain embedded Variables with no unambiguous owner] -> Migration identifies these records and requires their values to be moved explicitly to Project, Issue, or WorkflowRun Variables; it never silently broadens their scope.
- [Profile-envelope migration changes persisted JSON consumed by old binaries] -> Deploy as a single version and retain a database backup; no dual-read compatibility path is added.
- [Removing global Variables changes an implicit source] -> Focused specs verify only Project, Issue, and Run contribute effective values, including selected-stage overlays.
- [Run initialization races with first dispatch or overwrites user input] -> Make `archive` initialization idempotent and execute it before the run becomes dispatchable; merge only when the key is absent.
- [Built-in content loses behavior during extraction] -> Lock catalog names, descriptions, default selection, and ordered stages with resource parsing and profile-resolution specs.

## Migration Plan

1. Add the Profile envelope/domain conversion and pure Definition serializer, then update catalog, project-template, issue-custom-template, API, and resolver call sites together.
2. Convert persisted custom Profile records in one migration: retain each storage identity as Profile metadata and write the pure Definition into the envelope. Records with embedded Variables stop migration with a diagnostic naming the Profile and affected paths; operators move those values to their intended resource before retrying.
3. Move built-in descriptions to the catalog, remove their YAML variables, and seed `agent` and `archive` through the Issue and WorkflowRun initialization paths.
4. Add high-risk tests for all Profile sources, pure parse/serialize behavior, rejected removed fields, scoped Variable precedence, default initialization, and `setVars` visibility after retry or stage re-entry.
5. Deploy the server and built-in assets atomically. Roll back by restoring the pre-migration database backup and the prior server/assets release; do not run old binaries against migrated Profile envelopes.

## Open Questions

None. Broader unknown-field validation remains intentionally deferred to #432.
