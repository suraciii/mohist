## Context

An issue's workflow profile selection (`mohist/default` vs `mohist/pr`) decides which workflow definition, status projection, and prompt set the issue uses. Today this selection is **not a persisted fact on the issue**:

- `Domain.Issue` (`Issue.cs`) has no `WorkflowProfileId` field — the aggregate never stores the selection.
- `IssueQuerier.ToInfo` (`IssueQuerier.cs:402`) hardcodes `WorkflowProfileId = IssueWorkflowProfiles.DefaultId`, so every read model derived from the aggregate reports `mohist/default` regardless of any choice.
- The `CreateIssueRequest` DTO declares `WorkflowProfileId` (`IssueRoutes.Dtos.cs:18`) and the CLI already sends it (`MohistCliCommands.Issue.cs:242`), but the POST handler (`IssueRoutes.Crud.cs:66-77`) never passes it to `issueGrain.CreateAsync`, whose signature has no such parameter — the value is silently dropped.
- A separate store, the `IssueWorkflowProfile` table (`IssueWorkflowProfileManager.cs`), holds `SourceTemplateId` (project/system template reference), custom YAML `Template`, `Variables`, and `Prompts`. The workflow-profile endpoint recomputes a `ProfileId` from this template state (`IssueRoutes.Helpers.cs:141`), which can diverge from the hardcoded read model.
- At startup, `IssueGrain.StartWorkflowAsync` (`IssueGrain.cs:152-153`) resolves the definition via `WorkflowProfileManager.LoadTemplateAsync`, which consults the `IssueWorkflowProfile` table and project/system defaults — it never consults an issue-level profile selection. It also always merges prompts from the default profile (`IssueGrain.cs:155`).

Result: a user who selects `mohist/pr` sees `mohist/default` on issue detail / `mo issue show`, possibly `mohist/pr` on the workflow-profile page, and cannot trust which workflow will run. There is also no official update entry to change the selection on a backlog issue.

Constraints:
- The issue body's domain model states the **profile selection** (execution template) and the **runtime profile** (variables/prompts overlay) must not collapse into one field.
- Existing variable/prompt/model overlays must keep working; the consistency fix must not alter overlay semantics.
- Already-running workflows are execution facts and must not be silently re-templated.

Stakeholders: CLI users (`mo`), Web UI users, and any API consumer reading `workflowProfileId`.

## Goals / Non-Goals

**Goals:**
- Make an issue's workflow profile selection a single persisted fact projected identically by every read surface (detail, list, workflow-profile endpoint, `mo issue show`).
- Wire create to persist an explicitly supplied `workflowProfileId`; add an official update path on backlog/ready issues.
- Make startup use the same effective profile the user sees.
- Reject execution-template changes once an issue has started; keep run-scoped variable/prompt overrides unaffected.
- Centralize effective-profile resolution (issue → project default → system default) in one place.

**Non-Goals:**
- Redesigning the `mohist/default` or `mohist/pr` workflow definitions.
- Changing PR publish/merge前置条件 or semantics.
- Folding workflow runtime variables back onto the issue aggregate.
- Supporting mutation of an already-running workflow's definition (reject is sufficient).
- Removing the advanced custom-YAML / project-template override feature on the workflow-profile page.
- Fixing dashboard issue #256.

## Decisions

### D1: Persist `WorkflowProfileId` on the `Domain.Issue` aggregate (single source of truth)

Add a nullable `WorkflowProfileId` property to `Domain.Issue` (persisted in the issue's serialized state, same as the other JSON-serialized issue fields). `null` means "no issue-level selection" → inherit default. This is the single source of truth for the profile **selection**.

**Rationale:** Every read model is derived from the issue aggregate via `IssueQuerier.ToInfo`. Putting the fact there makes consistency automatic and removes the hardcoded default at `IssueQuerier.cs:402`. It also matches the issue body's domain model, which treats the profile selection as an issue-level fact distinct from the runtime overlay.

**Alternative considered — reuse `IssueWorkflowProfile.SourceTemplateId` as the source of truth.** Rejected: that table conflates the selection with variables/prompts/custom-YAML (the runtime overlay the issue body says must stay separate), its `SourceTemplateId` primarily references *project* templates, and recomputing a `ProfileId` from template state is exactly the divergence we are fixing (`IssueRoutes.Helpers.cs:141`). Keeping the selection on the aggregate and the overlay in `IssueWorkflowProfile` gives a clean separation.

**Alternative considered — a new dedicated column/table for the selection.** Rejected: the issue aggregate already owns scalar issue-level facts (priority, risk, repository ref); a profile selection is the same shape and belongs there. A separate store would reintroduce a second source to keep in sync.

### D2: Centralize effective-profile resolution in one resolver

Introduce a single `EffectiveWorkflowProfileResolver` (or method on `IssueWorkflowProfileRegistry`) that resolves: issue-level `WorkflowProfileId` (if non-null and exists) → project default template id → system default (`mohist/default`). Every read path (`IssueQuerier.ToInfo`, the workflow-profile endpoint response, list) and the startup path call this same resolver so they cannot diverge. `ToInfo` stops hardcoding the default and instead projects the resolved value.

**Rationale:** The bug is fundamentally multiple read surfaces inventing values independently. One resolver called by all surfaces is the structural fix.

### D3: Wire create and update through the aggregate

- **Create:** extend `IIssueGrain.CreateAsync` / `IssueGrain.CreateAsync` with a `workflowProfileId` parameter; pass `req.WorkflowProfileId` from the POST handler. Validate existence via `IssueWorkflowProfileRegistry.Exists` (400 on unknown).
- **Update:** add `WorkflowProfileId` to `UpdateIssueData` and `UpdateFullAsync`, using the same raw-presence-aware three-state semantics already used for `labels`/`isDraft`/`attachmentIds` (absent = unchanged, present-null = clear → inherit default, present-value = replace). Extend `UpdateIssueRequest.BindAsync` to capture `workflowProfileId` presence off the raw body.

**Rationale:** Reuses the established PATCH contract documented in the `http-api` spec rather than inventing a parallel update mechanism.

### D4: Startup resolves the definition from the effective profile

In `IssueGrain.StartWorkflowAsync`, resolve the effective profile via D2 and take its definition from `_profiles.Get(effectiveProfileId).Definition` as the base. The advanced custom-YAML / project-template override in `IssueWorkflowProfile` (resolved by `WorkflowProfileManager.LoadTemplateAsync`) continues to take precedence **when present** — it is an explicit override on top of the selected profile. Merge prompts from the selected profile (not unconditionally the default profile as at `IssueGrain.cs:155-158`).

**Rationale:** Guarantees the running workflow uses the profile the user sees, while preserving the existing escape hatch for advanced users who upload custom YAML.

### D5: Reject selection changes on started issues; leave runtime overlays alone

In the update path, when the issue has an `ActiveWorkflowRunId` and a `workflowProfileId` key is present in the PATCH body, reject with a clear error (409) naming the reason. Variable/prompt endpoints (`/workflow-profile/variables`, `/workflow-profile/prompts`) are untouched and remain valid run-scoped runtime overrides — they mutate `IssueWorkflowProfile`, not `issue.WorkflowProfileId`.

**Rationale:** Satisfies the "started issue is an execution fact" invariant without disabling legitimate runtime tuning. This also keeps the `IssueWorkflowProfile` custom-template PUT path available but, per D4, it overrides runtime execution rather than silently rewriting the displayed selection.

### D6: Align the workflow-profile endpoint response with the aggregate fact

`BuildIssueWorkflowProfileResponseAsync` reports `ProfileId` from the issue's effective profile (D2), not from recomputing `template?.Id ?? SourceTemplateId ?? "mohist/default"`. The response continues to surface `Yaml`, `HasCustomTemplate`, and `TemplateSource` for the advanced override. Setting the template via `PUT /workflow-profile/template` remains an advanced override and does **not** rewrite `issue.WorkflowProfileId`; the profile selection is changed only via create/PATCH.

**Rationale:** Removes the second divergent `ProfileId` computation while keeping the advanced YAML editing feature intact.

### D7: CLI and Web consume the unified fact

- **CLI:** `mo issue create --workflow-profile` already sends `workflowProfileId` (now persisted via D3). Add `mo issue update --workflow-profile <id>` (and a clear-on-null) hitting PATCH. `mo issue show` already renders the read model field — it will now be correct. Surface server started-issue rejections as non-zero exits with the server message.
- **Web:** the create dialog already binds `workflowProfileId`; the issue detail and `IssueWorkflowProfileEditor` read the effective profile from the issue read model / unified workflow-profile response. Add a control on the issue detail to change the selection on backlog issues, disabled with an explanatory message when the issue has started.

## Risks / Trade-offs

- `[Existing issues have no stored WorkflowProfileId]` → On read, a missing/null field resolves via D2 to the inherited default, so pre-existing issues display exactly as before (default). No data migration of historical issues is required; the field is additive and null-safe.
- `[Startup now honors a profile that previously was ignored]` → An issue whose selection was silently dropped previously would now actually run the PR workflow when started. This is the intended fix, but changes runtime behavior for issues created with `--workflow-profile mohist/pr` that were never started. Mitigation: this only affects issues that have not yet started (started issues are guarded by D5), and it makes behavior match what the user already requested.
- `[Two write surfaces could diverge: PATCH workflowProfileId vs PUT workflow-profile/template]` → D6 clarifies roles: PATCH changes the selection (the displayed fact); PUT template is an advanced execution override. Document this in the workflow-profile response fields so the Web UI can render them distinctly. Trade-off: advanced users must understand the distinction.
- `[Custom-YAML override can make the running definition differ from the selected profile id]` → Accepted and surfaced: the workflow-profile response exposes `HasCustomTemplate`/`TemplateSource` so surfaces can show "custom override active". The selection remains the displayed profile id.
- `[Project default template vs system profile selection interaction]` → The resolver (D2) defines a clear precedence (issue selection → project default → system default). Documented and tested.

## Migration Plan

1. Add `WorkflowProfileId` (nullable) to `Domain.Issue` with `init` setter and a `ReplaceWorkflowProfile` method that records any needed event. No DB schema migration (issue state is JSON-serialized in `Issues.State`); old rows deserialize with a null selection and resolve to the default.
2. Implement the centralized resolver (D2) and rewire `IssueQuerier.ToInfo`, the workflow-profile response builder, and `StartWorkflowAsync` to use it.
3. Wire create/update (D3) and the started-issue guard (D5).
4. Add CLI `--workflow-profile` to `update` and verify `show` rendering; update Web bindings (D7).
5. Add regression tests (see spec scenarios): create-as-PR round-trip, default→PR switch with read-model agreement, started-issue rejection, startup template selection, variable-overlay preservation.

**Rollback:** Revert the code change. Because the new field is additive/null-safe and no schema migration was applied, reverting restores prior behavior (issues again display default and startup ignores the selection). No data cleanup is required.

## Open Questions

- **O1:** Should `PUT /workflow-profile/template` (custom YAML / project template reference) also update `issue.WorkflowProfileId` when the referenced template id corresponds to a known system profile (e.g. `mohist/pr`)? Proposed answer: no — keep the two concerns separate per D6, and let the selection be the single displayed fact while the template override remains an execution-layer override surfaced via `HasCustomTemplate`. Confirm during implementation.
- **O2:** For the Web issue-detail profile control, should changing the profile be a primary control or live behind an "advanced" disclosure (since most users inherit the default)? Decide during Web implementation based on the existing detail-page layout.
