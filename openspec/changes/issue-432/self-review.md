# Self-Review — issue-432

Reviewed `proposal.md`, `design.md`, `tasks.json`, and `specs/` against issue #432
and the current codebase. Artifacts are internally coherent and every issue
acceptance criterion maps to a spec requirement and a task. However, the plan
mischaracterizes how custom Profiles are persisted and leaves the load/persistence
path unscoped, which contradicts the design's own stated load behavior on a
high-risk change. **Problems must be fixed before building.**

## What is solid

- All 10 issue acceptance criteria map to a spec requirement and a task; the three
  proposal capabilities each have a spec file and task coverage.
- Single-owner boundaries are consistent across artifacts: Definition validator
  owns structure/type/template-language; Action catalog (`uses`/`with` keys) is
  deferred to #446; template roots reuse #431's closed table; transitional
  inline-agent `with` guards are explicitly labeled Action-catalog territory.
- Factual premises that check out: `WorkflowDefinitionSurrogates.cs` exists
  (design D2's placement claim); built-in `.workflow.yaml` files use check `name`
  and need migration; the docs example already uses check `id` (so the docs golden
  case will pass once the validator supports `id`); no built-in or docs example
  uses `outputs`, so golden cases are safe.
- `tasks.json` is valid JSON; the dependency graph is a DAG with strictly-lower-
  priority deps; every task has test-inclusive acceptance criteria; all
  `passes=false`.

## Problems found

### P1 — MAJOR: Persistence is JSON, not YAML; the load/persistence path is unscoped and the migration plan is factually wrong

Custom Profiles are persisted as **JSON**, not YAML:

- `ProjectWorkflowProfileManager.SerializeProfile` =
  `JsonSerializer.Serialize(profile, WorkflowYamlSerializer.JsonOptions)`
  (`packages/server/src/Mohist.Server/Workflow/Services/ProjectWorkflowProfileManager.cs:551-552`).
- Load is `DeserializeProfile` → `WorkflowYamlSerializer.FromProfileJson(json)`
  (`...ProjectWorkflowProfileManager.cs:554-564`; mirrored in
  `IssueWorkflowProfileManager.cs:178-179`).

This contradicts the plan in two ways:

1. **Migration plan step 8** (`design.md:150`) states persisted Profiles are
   "stored as YAML text and re-parsed on load". They are stored as JSON and
   loaded via JSON model deserialization, not via `Parse(yaml)`.
2. **D8** (`design.md:104`) claims the runtime load path "uses `Parse`" so an
   invalid stored Definition "fails to load with a clear Definition error instead
   of dispatching partially." The actual load path does not call `Parse` — it calls
   `FromProfileJson`, and `DeserializeProfile` **swallows failures and returns
   `null`** (`ProjectWorkflowProfileManager.cs:561-563`). After the model rename
   (`CheckDefinition.Name`→`Id`, `uses` required), a stored JSON Profile carrying
   the old `name` would either fail System.Text.Json binding or silently become
   `null` on load — **not** "a clear Definition error."

Neither `design.md` nor any task scopes the JSON persistence round-trip
(`FromProfileJson` / `SerializeProfile` / `ToJson` / `FromJson`) to the new model,
yet it is a core part of "the server consumes the validator" (T-003) and of the
BREAKING migration. The library's stated contract is `Parse(yaml)`
(`design.md:31,57`); JSON is never addressed. Relatedly,
`CheckDefinitionSurrogate` is keyed on `Name`
(`Workflow/Grains/Surrogates/WorkflowDefinitionSurrogates.cs:113-129`) and must
also migrate to `Id` — implied by T-003's "register surrogates" but not stated.

**Must fix:** correct the storage-format claim in the migration plan; decide and
state whether the library owns only YAML `Parse` or also the JSON round-trip (or
whether persistence switches to storing validated YAML); explicitly scope the
JSON persistence/load migration in a design decision and in T-003's description
and acceptance criteria, including the load failure mode (replace the
try/catch→`null` swallow with a clear Definition error so D8's claim becomes
true) and the `CheckDefinitionSurrogate` `Name`→`Id` migration.

### P2 — MINOR: Error source for the transitional inline-agent `with` guard is unspecified

Design D7 keeps `with.agent`/`with.kind`/`with.type` and legacy `with.expect`
rejection on the save path "as a transitional Action-catalog proxy," and D4 says
every validator error is `source: definition` while `source: action` is reserved
for #446. But neither design nor T-003 assigns a `source` to the transitional
guard's errors. Since the offline CLI (`mo workflow validate`, T-004) runs only
`Parse` and intentionally does not run this guard, an unclassified guard error
creates an ambiguity about whether CLI output equals "the save path's errors."
The `workflow-validate-command` scenario "invalid definition" is fine for pure
Definition errors, but the parity claim needs the guard labeled as non-Definition
(e.g. `source: action`).

**Must fix:** state the transitional guard's error source in design D7/D4 and
T-003, so the CLI/save parity in the `workflow-validate-command` spec is
unambiguous.

### P3 — MINOR: Host-free arch test is referenced but not assigned

Design D1 and the risk list (`design.md:138`) say the no-Orleans/no-ASP.NET
library boundary is "enforced by project refs and an arch test," and the spec
scenario "standalone library carries no host dependency" requires it. T-001's
acceptance criteria assert the project-level constraint but do not require a
test; no task owns the arch test (the repo already has `Mohist.Server.ArchTests`).

**Must fix:** add an arch-test acceptance criterion to T-001 (asserting the
library's dependency graph excludes Orleans/ASP.NET).

### P4 — MINOR: Spec terminology/coverage gap on stage and check identifiers

`workflow-definition-validation` "Identifiers are unique and required fields are
enforced" normatively requires stage identifiers non-empty and unique, and check
ids non-empty and unique, but its scenarios cover only duplicate-task-id,
missing-`uses`, and optional-`title`. It also calls the stage identifier "stage
`name`," while the model/YAML key is `stage` (`StageDefinition.Stage`;
`WorkflowYamlSerializer.ToStage` reads `stage`). An implementer could misread
"stage name" as a `name` key.

**Must fix:** add scenarios for non-empty/unique stage identifier and non-empty/
unique check id, and align the wording to the `stage` key. (T-001's acceptance
criteria already mention stage-name uniqueness, so this is a spec-only gap.)

### P5 — MINOR: Golden-case exclusion criterion does not match the actual docs

The `workflow-validation-golden-cases` spec and issue describe excluding skeleton
snippets that "carry `<...>` placeholders," but `docs/workflow-definition.md`
contains **no** `<...>` tokens. The real exclusion is "the one complete example
block (the fenced `yaml` at roughly lines 183-291) versus the small partial
fenced snippets (roughly lines 14-126)." T-005's acceptance criterion
("selects only the complete example block") is correct, but the spec's `<...>`
wording is inaccurate for this document.

**Must fix:** reword the spec exclusion criterion to match the actual document
(complete example block vs. partial snippets), or note that `<...>` is
illustrative.

## Observations (not blocking)

- The repo's pre-existing `specs/workflow-definition/spec.md` carries a
  spec-ahead task `outputs` array that this validator would reject as an unknown
  field. That is correct for #432 (out of scope) and no built-in or docs example
  uses `outputs`, but a future `outputs` issue must extend the model first.

<promise>FAIL</promise>
