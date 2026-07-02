## Context

Mohist ships two built-in system workflow profiles — `mohist/local` and `mohist/github-pr` — materialized through two catalog paths: `SystemTemplateInfo` (from `ProjectWorkflowProfileManager.BuildSystemTemplates()`) and `WorkflowProfileDescription` (from `IssueWorkflowProfileRegistry.ListDescribed()`, served at `GET /api/workflow-profiles` via `SystemRoutes.cs:37`).

Two coupled drift problems live in this single subsystem:

1. **Description has two sources of truth.** `mohist/local` resolves its `Description` from the parsed `mohist-local.workflow.yaml` (`MohistWorkflow.Definition.Description`, with an empty-value fallback in `MohistLocalIssueWorkflowProfile.ResolveDescription()`). `mohist/github-pr` instead reads a C# string constant compiled into the binary (`MohistGithubPrIssueWorkflowProfile.GithubPrDescription`), even though `mohist-github-pr.workflow.yaml` *also* carries a `description` that is parsed into `WorkflowDefinition.Description` but never displayed. The two texts have drifted in wording, and editing the GitHub-PR copy forces a recompile. A third inline copy of the fallback logic lives in `BuildSystemTemplates()`.

2. **`SuitableFor` is dead surface.** The structured `IReadOnlyList<string> SuitableFor` tag list on the profile model has zero production consumers: `IssueWorkflowProfileRegistry.Matches()` is uncalled (confirmed — the only `.Matches(` call sites in the server are unrelated regex operations in `PromptTemplateEngine` and `PromptReferenceScanner`), `SuitableForMatcher.cs` is dead code, and Web/runner/DB never read it. It is not persisted. Its only consumers are the `mo workflow list --described` output and the `mohist-create-issue` skill's tag-matching recommendation, which is meaningless when both built-ins are general-purpose defaults. The natural-language `description` already states applicability, making the structured list redundant and a drift magnet.

Constraints: this is labeled `risk=low`, `domain=workflow`, a pure refactor with no persisted state and no external API consumer for the removed field. The `SuitableFor` name also exists on a **separate** concept — `IssueTemplateRegistry` / the issue-template model (`IssueTemplateRegistry.cs:273`) — which is entirely unrelated to workflow profiles and is explicitly out of scope.

## Goals / Non-Goals

**Goals:**
- Make the workflow YAML `description` field the single source of every system profile's user-facing description, read identically by both built-in profiles and both catalog materialization paths.
- Delete the compiled-in `GithubPrDescription` constant and all references to it.
- Remove the `SuitableFor` structured field and its matcher/match plumbing entirely from the workflow-profile model and its consumers (DTO, registry, CLI, skill).
- Keep the change covered by repointed/rewritten specs; `dotnet build Mohist.sln` and affected specs pass.

**Non-Goals:**
- Changing the description *wording* (already corrected manually in a prior step).
- Changing `WorkflowYamlSerializer` parsing logic.
- Any DB migration or runner/Web change (`SuitableFor` was never persisted or read there).
- Touching `IssueTemplate.SuitableFor` / `IssueTemplateRegistry` — a different domain concept that happens to share the name.
- Replacing `SuitableFor` with a new applicability mechanism; the natural-language `description` is the sole applicability description.

## Decisions

### Decision 1: One shared description resolver, not three inline copies

**Choice.** Introduce a single pure function that resolves a `WorkflowDefinition.Description` to a display string — applying the empty/whitespace fallback (`"No description provided"`) and trimming trailing whitespace — and call it from (a) `MohistLocalIssueWorkflowProfile.Description`, (b) `MohistGithubPrIssueWorkflowProfile.Description`, and (c) `BuildSystemTemplates()` for both profiles. The github-pr profile switches from the constant to `MohistWorkflow.GithubPrWorkflowDefinition.Description`, mirroring local's use of `MohistWorkflow.Definition.Description`.

**Rationale.** The current code triplicates the fallback rule (local `ResolveDescription()`, the github-pr constant path, and the inline copy in `BuildSystemTemplates()`). The spec requires the two catalog paths to *agree* and to read "identically"; a single resolver is the only way to make that an invariant rather than a convention that re-drifts — which is the exact bug being fixed.

**Trailing-whitespace normalization.** The local resolver currently returns the YAML value verbatim (no trim); the github-pr constant applied `.TrimEnd()`. The spec mandates "with trailing whitespace trimmed" for both. YAML block scalars (`|`) emit a trailing newline, so trimming is the correct, consistent behavior. The unified resolver applies `.TrimEnd()`, which is a minor behavior normalization for `mohist/local` (cosmetic only — removes a trailing newline from the displayed text).

**Placement alternatives considered:**
- *Static method on `MohistWorkflow`* (e.g. `ResolveDescription(WorkflowDefinition)`) — co-located with the definitions and the placeholder; no new type. **Recommended.**
- *Dedicated `WorkflowDescriptionResolver` static class* — cleaner single-responsibility and unit-testable in isolation, at the cost of one more file.
- *Keep three inline copies* — rejected; reintroduces the drift the change exists to kill.

### Decision 2: Remove `SuitableFor` from the model entirely, not deprecate it

**Choice.** Hard-delete `IIssueWorkflowProfile.SuitableFor`, the `MohistIssueWorkflowProfileBase` abstract member, both built-in overrides, the `WorkflowProfileDescription.SuitableFor` DTO member, `IssueWorkflowProfileRegistry.Matches()`, and the whole `SuitableForMatcher.cs` file. The `suitableFor` JSON field leaves the `/api/workflow-profiles` response with no replacement.

**Rationale.** The project is pre-1.0 / actively developing with no version-compatibility concern (per AGENTS.md), the field has no production consumer and no persisted state, and there is no deprecation policy to honor. A deprecation shim would preserve exactly the dead, drift-prone surface this issue removes. This is the only externally visible API delta and it is breaking-by-design.

**Alternatives considered:**
- *Keep `SuitableFor` but stop populating it* — rejected; leaves dead model surface and a still-serializable field that implies a contract.
- *Soft-deprecate then remove later* — rejected; no consumers to migrate, so a two-step removal is pure overhead.

### Decision 3: `ListDescribed()` and `WorkflowProfileDescription` slim to three fields

**Choice.** `WorkflowProfileDescription` becomes `(string Id, string DisplayName, string Description)`; `ListDescribed()` drops the `SuitableFor` projection. This is the DTO serialized at `/api/workflow-profiles`, so the response shape change flows from Decision 2.

**Rationale.** Keeps the described surface to exactly the fields that have a consumer (`mo workflow list --described` shows id, name, description; the skill reads the same).

### Decision 4: CLI `--described` shows description only

**Choice.** In `MohistCliApi.RenderWorkflowProfilesDescribed`, remove the `suitableFor` JSON parse and both the `Suitable for: <tags>` and `Suitable for: (not specified)` output branches; emit only `id — displayName` and the description. Update the `--described` option help text in `MohistCliCommands.Workflow.cs` to drop the "suitable_for context" wording.

**Rationale.** With the field gone from the response, the parse branch becomes dead and the `(not specified)` placeholder meaningless. Keeping the output to id/name/description matches the new API contract and the skill's needs.

### Decision 5: Skill selects default/operator choice, not tag matching

**Choice.** Rewrite the `mohist-create-issue` skill docs (`SKILL.md` + `references/issue-templates.md`) so workflow selection picks the default profile or an operator-chosen enabled profile id (still sourced from `mo workflow list --described`), and `recommended_workflow_reason` explains the choice in natural language rather than citing matched tags. Remove all `suitable_for` parsing/scoring guidance and the `(not specified)` handling.

**Rationale.** With two general-purpose defaults and no tag data, tag-based scoring is both unavailable and meaningless. The default-or-operator-choice rule is what the skill already falls back to; this makes it the only rule.

## Risks / Trade-offs

- **[Breaking API field removal] -> Mitigation:** `suitableFor` leaves `/api/workflow-profiles`. No production consumer reads it (Web does not render it, runner ignores it, DB does not persist it). The CLI and skill — the only consumers — are updated in the same change. Documented as breaking-by-design in the proposal.
- **[Behavior change: local description now trims trailing whitespace] -> Mitigation:** Cosmetic only (strips a trailing newline from YAML block-scalar output). No assertion in the existing suite pins the trailing whitespace; repointed specs assert the trimmed value. Called out explicitly so it is not a surprise.
- **[Name collision with `IssueTemplate.SuitableFor`] -> Mitigation:** Explicitly out of scope; the grep boundary confirms `IssueTemplateRegistry`/`IssueTemplate` is a separate model. The design touches only `Issue/Services/WorkflowProfiles/` and the registry/DTO within it.
- **[Skill recommendation quality may drop without tag scoring] -> Mitigation:** Acceptable and intended — with two general-purpose profiles there is nothing meaningful to score; default-or-operator-choice is the correct behavior, and the operator can still override via the existing confirmation flow.
- **[Stale `SuitableFor` references left behind] -> Mitigation:** A repo-wide grep for `SuitableFor`/`suitable_for` is the verification gate (excluding the known `IssueTemplate` model and any test fakes), plus `TreatWarningsAsErrors` build and the repointed specs.

## Migration Plan

No data migration — `SuitableFor` was never persisted, and no runner/Web/runtime state references it. Deployment is a single atomic server + CLI + skill-data release (server and CLI ship together in this repo).

1. Server: introduce the shared resolver; repoint github-pr `Description` and `BuildSystemTemplates()`; delete the constant; remove `SuitableFor` from interface/base/both overrides/DTO/`ListDescribed`/`Matches()`; delete `SuitableForMatcher.cs`.
2. CLI: strip `suitableFor` from `RenderWorkflowProfilesDescribed`; update `--described` help text.
3. Skill-data: rewrite `mohist-create-issue` selection guidance.
4. Tests: repoint description specs to the YAML source; delete/rewrite `SuitableFor`-positive specs (profile metadata, `ListDescribed` includes SuitableFor, gh-cli tag mention, `--described` empty-suitableFor, API-endpoint SuitableFor assertions); drop `SuitableFor` from test DTO records.
5. Verify: `dotnet build Mohist.sln`, server specs, CLI specs; grep confirms no stray `SuitableFor`/`suitable_for` outside the `IssueTemplate` model.

**Rollback:** Revert the commit. Since nothing was persisted and no consumer depends on the removed field, rollback restores the prior constant + tag surface with no data reconciliation. (Rollback is not expected to be needed — the removed field has no production consumer.)

## Open Questions

- **Resolver placement.** `MohistWorkflow.ResolveDescription(WorkflowDefinition)` (recommended, no new type) vs. a dedicated `WorkflowDescriptionResolver` static class. Functionally identical; a style call left to implementation.
- **`recommended_workflow_reason` wording guidance depth.** How prescriptive the rewritten skill docs should be about the reason sentence now that tag citation is gone — minimal ("state the default/fallback rationale") is assumed, but final copy is settled at implementation.
