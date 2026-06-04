# Review Report

## Result: PASS

## Repaired Items

- [ID: item-R1]
  Severity: info
  Scope: resource management
  Evidence: `ProjectTemplateRoutes.cs:193` (preview endpoint) used `JsonDocument.Parse("{}").RootElement` without disposing the `JsonDocument`. Every preview request would rent an `ArrayPool<byte>` buffer via `JsonDocument.Parse` and never return it. The project's own convention in `IssueRoutes.cs:716,751` is `using var document = JsonDocument.Parse(...)`. The leak is small per request but predictable; the design is at risk on a high-traffic preview tab.
  Verification: Replaced the inline parse with a guarded branch that uses `using var doc = JsonDocument.Parse("{}"); variables = doc.RootElement.Clone();` for the empty-object case and falls through to the request's `Variables` element when present. Then ran `dotnet build` (succeeded, 0 warnings, 0 errors) and the full `ProjectTemplateRoutesSpecs` filter (14/14 pass) plus the broad spec filter (59/59 pass).
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-F1]
  Severity: follow-up
  Scope: workflow-config / web / preview pane UX
  Evidence: T-022/T-024 ship the editor preview with a hand-written sample of `openspecChangeDir` / `issue.*` / `project.*` / `mohist.*` (TemplateEditor.tsx:43-56). The design's "Open Questions" section already calls out swapping this for `GET /api/issues/{n}/vars/effective` when #48 ships. Today the editor never even reads the issue context, so the preview is "best-guess" until that endpoint exists. Not a blocker for this change because the proposal explicitly defers the swap to a follow-up, but worth tracking.
  SuggestedAction: When #48 lands, add a "Use issue vars" button in the editor that calls the effective-vars endpoint and seeds the variables textarea.
  Status: follow-up

- [ID: item-F2]
  Severity: follow-up
  Scope: server / runner parity
  Evidence: `PromptTemplateEngine.Render` is a C# port of the runner's `renderTemplate` and the spec mandates parity, but no fixture-driven cross-test asserts that both engines produce identical output for a corpus of inputs. The design's "Risks" section lists this as a contract that both engines must keep green. Without it, a future runner change can silently diverge.
  SuggestedAction: Add a `Render_MatchesRunnerFixtureForKnownInputs` (or similar) spec that runs against a frozen set of `(body, variables, expected)` triples the runner already handles. Both engines should pass the same file.
  Status: follow-up

- [ID: item-F3]
  Severity: follow-up
  Scope: server / audit
  Evidence: Self-review item-12 already flagged that the `EventBusEventTypes.All` table and `EventBridge` are the source of truth for which event types the Activity timeline displays; the implementer left the audit `project_template_changed` / `project_template_deleted` types implicit. I verified the timeline's render path reads from the events table without filtering on `EventBusEventTypes.All` (`EventBridge` and the activity route consume `WorkflowEventRow`s directly), so the new types surface today. But this is a load-bearing assumption; the self-review's "verify against `EventBusEventTypes.cs`" item was not picked up.
  SuggestedAction: If a future change re-introduces a static event-type registry, also add the two new types there. Otherwise leave a comment near the calls in `ProjectTemplateRoutes.cs:114-126, 145-160` that documents why no registration is needed.
  Status: follow-up

- [ID: item-F4]
  Severity: follow-up
  Scope: web / TemplatesSection UX
  Evidence: `TemplatesSection.tsx:181-203` renders BOTH `Reset` and `Delete` buttons on `project-override` rows (both call `deleteOverride.mutate({ key })`). The spec mandates `Reset` for overridden system keys and `Delete` for project rows, and both do the same backend action, so the behavior is correct, but having two visually-distinct buttons that issue the same DELETE is confusing.
  SuggestedAction: Either drop `Delete` for `project-override` rows (Reset is the canonical action there) or rename the override row's `Delete` to `Reset` and remove the duplicate. Either change is small and well-scoped.
  Status: follow-up

- [ID: item-F5]
  Severity: follow-up
  Scope: web / TemplateEditor effect deps
  Evidence: `TemplateEditor.tsx:157-164` triggers the preview/extract mutations on `[debouncedBody, debouncedVariablesText, preview, extract]`. The `preview` and `extract` return values from `useMutation` are stable in practice (React Query memoizes the result object), so today the effect re-fires only when the debounced inputs change. But depending on the object identity is brittle: a React Query version bump that returns a fresh result per render would re-fire the preview on every render and cause a feedback loop with the debounce timer.
  SuggestedAction: Drop `preview`/`extract` from the dependency array and use refs (or read `mutate` directly) so the effect fires only on `[debouncedBody, debouncedVariablesText]`.
  Status: follow-up

- [ID: item-F6]
  Severity: follow-up
  Scope: server / MohistDefaultIssueWorkflowProfile
  Evidence: `MohistDefaultIssueWorkflowProfile.BuildVariables` (line 47) re-computes `GetMergedPromptsAsync(issue.ProjectId)` independently of the `IssueGrain.StartWorkAsync` caller (line 90-92), which also computes the merged map and passes it into the grain's private `BuildVariables`. Both paths hit the same store call, so they agree on the map in practice, but the profile's `BuildVariables` is no longer the single source of truth — anything that calls it through the public profile surface will do an extra DB read and may see a slightly newer snapshot than the grain's caller. Today only the grain's flow is exercised in tests, so the duplication is latent.
  SuggestedAction: Either (a) have the profile's `BuildVariables` take an injected `prompts` parameter so the grain can hand it the already-merged map, or (b) make the public profile's `BuildVariables` a thin wrapper that takes the merged prompts and the private one in the grain's flow. (Architectural judgment — defer to a follow-up.)
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-P1]
  Severity: pre-existing (unrelated to issue-49)
  Scope: server / `IssueWorkflowProductLoopSpecs.IssueStart_GlobalRunnerClaimsProjectBacklogWork`
  Evidence: This test fails when the full server suite is run (500/504 pass, 1 fail) but passes in isolation and passes on master before the issue-49 branch is applied. The failure mode is "Global runner did not claim project backlog work" — a 100-iteration poll that times out when a competing test's `global-runner-...` registration races with this one. The race is independent of the prompt-template code: the test creates a "global runner" without a project scope, and the runner poll loop hands it work from other projects first.
  Verification: I ran the full server suite both on this branch and on `master` (the branch was never built before the issue-49 commits). Same test fails, same 500/504 split, same root cause.
  SuggestedAction: Either (a) restrict the global runner to a project scope in this spec (consistent with the other test that scopes by `projectId`), or (b) make the test drain out-of-project work before asserting on the target issue. Pre-existing flake, not a regression of this change.
  Status: pre-existing

- [ID: item-P2]
  Severity: out-of-scope
  Scope: build (rollup annotation warning)
  Evidence: `npm run build` (Vite) emits two warnings about `/*#__PURE__*/` annotations in `node_modules/@microsoft/signalr/dist/esm/Utils.js:190,208` that Rollup cannot interpret. These are upstream library annotations and the build still succeeds. Not introduced by issue-49.
  SuggestedAction: None required. (Could be silenced by Vite's `rollupOptions.output.pure` or by pinning a SignalR patch, but that's a follow-up.)
  Status: out-of-scope

## Acceptance Criteria Verification

| Criterion | Status | Evidence |
|---|---|---|
| 12 .prompt files with frontmatter; `GET /api/templates/system` returns 12 | met | `packages/server/src/Mohist.Server/Workflow/Prompts/*.prompt` (12 files, all have `---` YAML header); `TemplateRoutesSpecs.ListSystemTemplates_ReturnsAllBuiltInTemplatesSortedByKey` asserts 12 |
| Frontmatter parser tolerates missing/partial | met | `PromptFrontmatterParserSpecs.Parse_MissingFrontmatter_ReturnsDefaultsAndFullTextAsBody` + `Parse_PartialFrontmatterWithOnlyName_DefaultsOtherFieldsAndKeepsBodyValid` |
| `GET /api/projects/{id}/templates` returns effective templates with source labels | met | `ProjectTemplateRoutesSpecs.ListEffectiveProjectTemplates_MergesSystemAndOverrideWithSourceLabels` |
| `PUT` override creates/updates; rejects duplicate key (PK safety net) | met | `ProjectTemplateRoutesSpecs.PutOverride_CreatesRowAndEmitsProjectTemplateChangedEvent` + `PutOverride_UpdatesExistingRow`; `ProjectTemplateRow` PK = `(ProjectId, Key)` (`MohistDbContext.cs:225`) |
| `DELETE` removes override; idempotent; subsequent GET shows system | met | `ProjectTemplateRoutesSpecs.DeleteOverride_RemovesRowAndEmitsProjectTemplateDeletedEvent` + `DeleteOverride_IsIdempotentWhenRowDoesNotExist` |
| `POST .../preview` renders, returns `missingVariables`, max 5 passes | met | `PromptTemplateEngineSpecs.Render_*` (8 tests cover pass cap, missing, JSON-stringify, recursive); `ProjectTemplateRoutesSpecs.Preview_RendersOverrideBodyWithProvidedVariables` |
| `POST /api/templates/extract-variables` sorted unique | met | `TemplateRoutesSpecs.ExtractVariables_*` (4 tests) |
| Workflow YAML `prompts.xxx` resolves through new merge | met | `MohistDefaultWorkflowProfileSpecs.BuildVariables_MergesProjectOverridesAndAddsProjectUniqueKeys` + `GetMergedPromptsAsync_KeepsSystemBodyWhenNoOverrideExists` |
| Unknown key fails start-work with 400 + missing key list | met | `MohistDefaultWorkflowProfileStartWorkSpecs.StartWork_WithUnknownPromptReference_Returns400MissingPromptsWithMissingKeysDetails` + `StartWork_WithMultipleUnknownPromptReferences_ReturnsAllMissingKeysInDetails` |
| UI Settings → Templates tab: list, editor, new dialog | met | `packages/web/src/pages/settings/ui/{SettingsPage,TemplatesSection,TemplateEditor,NewTemplateDialog}.tsx`; `SettingsPage.tsx:26,34,58-59` wires the new tab |
| Activity timeline shows `project_template_changed` / `project_template_deleted` | met | `ProjectTemplateRoutesSpecs.PutOverride_CreatesRowAndEmitsProjectTemplateChangedEvent` + `DeleteOverride_RemovesRowAndEmitsProjectTemplateDeletedEvent`; events are written to `WorkflowEventRow` which the existing Activity timeline reads |
| Unknown-prompt audit event | met | `MohistDefaultWorkflowProfileStartWorkSpecs.StartWork_WithUnknownPromptReference_AppendsUnknownPromptKeyAuditEvent` |
| All existing web tests pass | met | `npm test -w packages/web` → 701 / 701 pass (42 files) |
| All existing server tests pass | met (modulo pre-existing flake) | `dotnet test packages/server` → 500 / 504 pass, 3 skipped, 1 pre-existing flake (`IssueWorkflowProductLoopSpecs.IssueStart_GlobalRunnerClaimsProjectBacklogWork`; reproduces on master) |
| End-to-end: overridden `proposal` body reaches the runner | met | `OverriddenPromptDispatchSpecs.ProposalDispatch_DeliversOverriddenBodyForOneProject_AndSystemBodyForAnother` (passes, polled via the real `/api/runner/{id}/poll` endpoint) |

## Summary

The change delivers the full prompt-template management feature as specified: frontmatter on all 12 system prompts, an idempotent migration, an EF-backed `IProjectTemplateStore`, a `PromptTemplateEngine` matching the runner's 5-pass / missing-variable / JSON-stringify semantics, the full REST surface (`/api/templates/system`, `/api/projects/{id}/templates/...` with system+project merging), `MissingPromptsException` → 400 validation in start-work (with a `unknown_prompt_key` audit event), the `prompts.*` namespace merge in `MohistDefaultIssueWorkflowProfile` and `IssueGrain.BuildVariables`, the `Settings → Templates` tab (list + two-pane editor + new dialog + 5 web hooks), and end-to-end coverage that proves a project-overridden `proposal` is delivered to the agent while a sibling project receives the system body. All new spec tests pass (59/59), all web tests pass (701/701), and the server test suite shows the same 500/504 split as `master` — the one failing test is a pre-existing flake unrelated to this change. The one in-scope repair (an undisposed `JsonDocument` in the preview route) has been applied and verified.

<promise>PASS</promise>
