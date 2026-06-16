# Review Report

## Result: PASS

## Summary

Issue 102 implements structured explore-to-issue handoff via YAML frontmatter on issue body files. The change spans five deliverables (T-001..T-005): a nullable `risk` field on the Issue model (server + DB migration + web types), CLI frontmatter parsing for `mo issue create --body-file`, a `mo workflow list --described` command backed by `GET /api/workflow-profiles`, Web UI frontmatter detection in the create-issue dialog, and updated `mohist-explore` skill guidance.

Build: `dotnet build Mohist.sln` — 0 errors, 0 warnings.
Server tests (filtered to touched areas): 95/95 pass.
Web tests (3 new/updated files): 24/24 pass.
Web typecheck (`tsc -b --noEmit`): clean.

The implementation is well-structured, faithfully follows the design decisions (D1–D6), and covers the spec scenarios with meaningful tests. The frontmatter parsers (C# and TypeScript) are line-scanning, dependency-free, BOM/CRLF-tolerant, and handle block scalars, quoted values, partial frontmatter, and malformed input gracefully.

## Repaired Items

No repairs were made. All findings below are either out of repair-policy scope or are reported as unresolved/follow-up.

## Blocking Items

None.

## Non-Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Api/IssueRoutes.Crud.cs:49`, `packages/server/src/Mohist.Server/Issue/Grains/IIssueGrain.cs:9`, `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:164`
  Evidence: The `workflowProfileId` parsed from frontmatter and sent by both the CLI (`MohistCliCommands.Issue.cs:165`) and Web UI (`CreateIssueDialog.tsx:244`) is accepted by `CreateIssueRequest.WorkflowProfileId` but never persisted or used by the server. `IssueRoutes.Crud.cs:49` calls `issueGrain.CreateAsync(...)` without passing `req.WorkflowProfileId`; `IIssueGrain.CreateAsync` has no such parameter; and `IssueQuerier.ToInfo` hardcodes `WorkflowProfileId = IssueWorkflowProfiles.DefaultId` at line 164. This means the issue's stated acceptance criterion "至少跑通一次完整链路：explore 对话 → 结构化 issue → 自动选择 workflow → start" cannot fully succeed end-to-end for any non-default recommendation: the explore skill recommends a profile, the CLI parses and sends it, but the server silently drops it and always reports `mohist/default`. This is a **pre-existing** gap (the DTO field existed before this branch) and the design doc (D1) asserts the plumbing already works, which is only true at the DTO level. Fixing it requires product behavior changes (new domain field, grain signature change, migration, query path) which are disallowed by the repair policy. [disallowed:reason=product behavior change + architectural judgment]
  SuggestedAction: Open a follow-up issue to plumb `workflowProfileId` through `IssueGrain.CreateAsync` → `Issue` domain → `IssueRow` → `IssueQuerier.ToInfo`, including a migration and server-side validation. Until then, the frontmatter recommendation is advisory-only (visible in CLI output and Web UI) but does not influence the runtime workflow selection.
  Verification: `curl -X POST /api/projects/<id>/issues -d '{"title":"T","workflowProfileId":"feature-flow"}'` then `GET` the issue and observe `workflowProfileId` is `mohist/default`.
  Status: unresolved

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Api/IssueApiSpecs.cs:92-100`
  Evidence: The test `CreateIssue_WithWorkflowProfileId_RoundTripsProfileId` creates an issue with `workflowProfileId = "mohist/default"` and asserts the response contains `"mohist/default"`. This passes **only** because `IssueQuerier.ToInfo` unconditionally returns `IssueWorkflowProfiles.DefaultId` — not because the server persisted the client's value. If the test used any non-default profile (e.g., `"feature-flow"`), it would fail, exposing item-1. The test gives a false sense that `workflowProfileId` round-trips correctly.
  SuggestedAction: Either (a) change the test to use a non-default profile ID to expose the gap, or (b) add a comment documenting that this test only verifies the hardcoded default and will need updating when item-1 is resolved.
  Verification: Change the requested `workflowProfileId` to `"feature-flow"` in the test and observe the assertion fails.
  Status: unresolved

- [ID: item-3]
  Severity: minor
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260616062153_AddIssueRiskColumn.cs:14-20`
  Evidence: The migration named `AddIssueRiskColumn` performs **two** unrelated schema changes in addition to adding the `Risk` column: it drops `CompletedAt` and `UpdatedAt` from the `AgentSessions` table (lines 14–20). These columns were removed from the `AgentSessionRow` C# model by commit `2c4889bc` ("refactor: remove dead fields from session data models") without an accompanying migration. The pending model drift was auto-detected by EF Core and bundled into this migration. The `Down` method re-adds those columns (lines 37–48), which would re-introduce columns that don't match the current model — making the migration's reverse non-idempotent. If a operator needs to roll back just the risk column, they cannot do so without also affecting `AgentSessions`.
  SuggestedAction: In future, generate a separate migration immediately after any model-only refactor commit so migrations stay single-purpose. For the current migration, no action is needed (forward migration is correct), but note the coupling for rollback planning.
  Verification: `dotnet ef migrations script --idempotent` shows the `AgentSessions` column drops inside the `AddIssueRiskColumn` migration block.
  Status: unresolved

- [ID: item-4]
  Severity: minor
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Workflow.cs:34-37`
  Evidence: When `--described` is combined with `--output json`, the `--output` flag is silently ignored. The code unconditionally calls `api.PrintWorkflowProfilesDescribedAsync()` which always renders human-readable text. The rest of the CLI respects `--output` for JSON/table modes; this is the only command where `--output` is dropped.
  SuggestedAction: Either document that `--described` implies human-readable output (and reject `--output` when combined), or honor `--output json` by emitting the raw JSON array from `/api/workflow-profiles`.
  Verification: `mo workflow list --described --output json` produces human-readable text, not JSON.
  Status: unresolved

- [ID: item-5]
  Severity: minor
  Scope: `packages/web/src/features/create-issue/ui/CreateIssueDialog.tsx:236-246`
  Evidence: The Web UI sends the body **with** frontmatter to the server (line 238: `body: body || undefined`), while the CLI strips the frontmatter block before sending (`MohistCliCommands.Issue.cs:203`: `return (ok.Body, workflow, risk)`). Issues created via Web UI will have the raw `---\nrecommended_workflow: ...\n---\n` block visible in the body, while CLI-created issues will not. The self-review-t004.md acknowledges this as a deliberate minimal change, but the inconsistency means the same frontmatter-annotated file produces different issue body content depending on which client is used.
  SuggestedAction: Strip the frontmatter block from the body before sending in the Web UI, mirroring the CLI. Alternatively, document the divergence as intentional.
  Verification: Create an issue via Web UI with frontmatter body and observe the issue detail shows the `---` block; repeat via CLI and observe it does not.
  Status: unresolved

- [ID: item-6]
  Severity: minor
  Scope: `packages/cli/Mohist.Cli/FrontmatterParser.cs:106-142`, `packages/web/src/features/create-issue/lib/frontmatter.ts:57-88`
  Evidence: `ReadBlock` sets the block indentation from the **first** content line and only breaks out of the block when `leading == 0`. If a subsequent line has **less** indentation than the first (but still > 0), it is consumed and stripped by the first line's indent count, garbling the content. Standard YAML treats a line with less indentation than the block scalar's indent as the end of the block. Example: `recommended_workflow_reason: |\n    Line one\n  Not part of block` — the second line (2-space indent) would have 4 characters stripped, corrupting it. This only affects malformed YAML with inconsistent indentation and is acceptable for the "simple line-scanning" design (D2), but it is a correctness deviation.
  SuggestedAction: Change the break condition from `if (leading == 0) break;` to `if (indent >= 0 && leading < indent) break;` in both parsers. Low risk since valid YAML won't trigger this path.
  Verification: Parse `---\nrecommended_workflow_reason: |\n    a\n  b\n---\n` and observe the value is garbled (`"a\n"` with `b` truncated to empty, rather than block termination).
  Status: unresolved

- [ID: item-7]
  Severity: minor
  Scope: `packages/server/src/Mohist.Server/Api/IssueRoutes.Crud.cs:28-52`
  Evidence: `CreateIssueRequest.Risk` is passed directly to `issueGrain.CreateAsync` without API-level validation. If the client sends an invalid risk value (e.g., `"extreme"`), `IssueRisk.From()` throws `ArgumentException` which surfaces as an unhandled 500 Internal Server Error rather than a 400 Bad Request. The CLI (`MohistCliCommands.Issue.cs:117`) also does not validate `--risk` before sending. The test `CreateIssue_WithInvalidRisk_Throws` confirms the domain throws, but there is no HTTP-level test verifying the status code.
  SuggestedAction: Validate `risk` in `IssueRoutes.Crud.cs` before calling the grain, returning `ApiResults.BadRequest("risk must be one of: low, medium, high")` for invalid values.
  Verification: `curl -X POST .../issues -d '{"title":"T","risk":"extreme"}'` returns 500 instead of 400.
  Status: unresolved

## Follow-up Items

- [ID: item-8]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Api/IssueRoutes.Dtos.cs:18-26`, `packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs:259-323`
  Evidence: `UpdateIssueRequest` does not include `Risk` or `WorkflowProfileId`, and the `mo issue update` command has no `--risk` flag and does not parse frontmatter from `--body-file`. Once an issue is created, neither risk nor the workflow recommendation can be changed via CLI or PATCH API. The spec for this issue only covers creation-time frontmatter, so this is not a gap in the current scope, but it limits the usefulness of risk as a first-class field.
  SuggestedAction: Consider adding `Risk` to `UpdateIssueRequest` and a `--risk` flag to `mo issue update` in a follow-up.
  Status: follow-up

- [ID: item-9]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Workflow.cs:47-49`
  Evidence: `mo workflow list` (without `--described`) calls `/api/workflow-templates/system` while `--described` calls `/api/workflow-profiles`. Two different endpoint names (`workflow-templates` vs `workflow-profiles`) serve the same conceptual domain. This is pre-existing naming divergence, not introduced by this change, but the new `--described` path adds a second endpoint that widens the surface.
  SuggestedAction: Consider consolidating to a single endpoint with optional richness query parameter in a future refactor.
  Status: follow-up

- [ID: item-10]
  Severity: follow-up
  Scope: `packages/web/src/features/create-issue/ui/CreateIssueDialog.tsx:228-232`
  Evidence: Once the risk selector is touched (either from frontmatter pre-fill acceptance or manual click), `riskTouched` is set to `true` and never reset while the dialog is open. If the user subsequently edits the body to change the frontmatter `risk` value, the selector does not update. The same applies to `workflowTouched` (line 222-226). This is intentional for manual overrides but means editing frontmatter in-place won't refresh the selectors after any prior interaction. Minor UX consideration.
  SuggestedAction: Consider resetting `riskTouched`/`workflowTouched` when the frontmatter-derived recommendation changes identity (not just value), so body edits can refresh the selectors until the user explicitly overrides.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-11]
  Severity: info
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs:173-218`
  Evidence: `ApplyFrontmatter` is called for **all** body sources (`--body`, `--body-file`, `--body-stdin`), not just `--body-file`. The spec (`specs/cli-interface/spec.md`) scopes frontmatter parsing to `--body-file` only. The broader behavior is arguably more consistent but technically exceeds the spec. Not a problem — just an observation that the implementation is more permissive than required.
  SuggestedAction: Optionally update the spec to document that frontmatter is parsed from any body source, or narrow the implementation to `--body-file` only.
  Status: out-of-scope

- [ID: item-12]
  Severity: info
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs:213-215`
  Evidence: The "no frontmatter found" warning text in the code is `"warning: no frontmatter found in body file. Consider including recommended_workflow and risk."` (lowercase 'n' after the `warning: ` prefix). The spec scenario (`specs/issue-body-frontmatter/spec.md:46`) quotes it as `"No frontmatter found in body file..."` (capital 'N'). Stylistic mismatch only; the functional behavior matches.
  SuggestedAction: None required. Optionally align the casing if the spec wording is considered normative.
  Status: pre-existing

## Verification Summary

### Acceptance Criteria Mapping

| Issue Acceptance Criterion | Status | Evidence |
|---|---|---|
| `mo issue create --body-file` parses YAML frontmatter | ✅ | `FrontmatterParser.cs`, tested by `FrontmatterParserSpecs.cs` (11 tests) and `IssueCliBodyInputSpecs.cs` (`IssueCreate_BodyFileWithFrontmatter_AutoFillsWorkflowAndRiskAndStripsBlock`) |
| Missing frontmatter → warning, not blocking | ✅ | `MohistCliCommands.Issue.cs:213-216`, tested by `IssueCreate_BodyFileWithoutFrontmatter_EmitsWarningButSucceeds` |
| Malformed frontmatter → warning, body sent | ✅ | `MohistCliCommands.Issue.cs:204-211`, tested by `IssueCreate_MalformedFrontmatter_EmitsWarningButSendsFullBody` |
| CLI flags override frontmatter | ✅ | `MohistCliCommands.Issue.cs:185-201`, tested by `IssueCreate_ExplicitWorkflowProfileOverridesFrontmatter` and `IssueCreate_ExplicitRiskFlagOverridesFrontmatterRisk` |
| `--risk` CLI flag | ✅ | `MohistCliCommands.Issue.cs:117`, tested by `IssueCreate_RiskFlag_SentInCreateRequest` |
| Risk persisted server-side | ✅ | `IssueRisk.cs`, `Issue.Transitions.cs:16`, `IssueGrain.cs:335-356`, `IssueStore.cs:32-37`, migration `AddIssueRiskColumn`, tested by `CreateIssue_WithRisk_PersistsAndReturnsIt`, `CreateIssue_WithoutRisk_ReturnsNull`, `ReadModel_IncludesRisk_AfterCreate`, `State_RoundTripsRisk` |
| `GET /api/workflow-profiles` returns id/displayName/description/suitableFor | ✅ | `SystemRoutes.cs:18-19`, `IssueWorkflowProfileRegistry.ListDescribed()`, tested by `WorkflowProfilesEndpoint_ReturnsIdDisplayNameDescriptionAndSuitableFor` |
| `mo workflow list --described` human-readable output | ✅ | `MohistCliApi.cs:154-190`, tested by `WorkflowList_Described_RoutesToWorkflowProfilesEndpoint`, `WorkflowList_Described_FormatsHumanReadableOutput`, `WorkflowList_Described_EmptySuitableFor_ShowsNotSpecified` |
| `mo workflow list` (no flag) preserves existing behavior | ✅ | tested by `WorkflowList_WithoutDescribed_RoutesToExistingEndpoint` |
| Explore skill produces frontmatter + structured sections | ✅ | `skill-data/mohist-explore/SKILL.md`, tested by `ExploreSkillContentSpecs` (12 tests) |
| Skill calls `mo workflow list --described` | ✅ | SKILL.md line 40, tested by `PackagedExploreSkill_InstructsWorkflowDiscoveryCommand` |
| Skill documents matching logic + default fallback | ✅ | SKILL.md lines 54-71, tested by `PackagedExploreSkill_DocumentsSuitableForMatchingLogic`, `PackagedExploreSkill_DocumentsDefaultFallback` |
| Skill documents user confirmation | ✅ | SKILL.md lines 108-120, tested by `PackagedExploreSkill_DocumentsUserConfirmationStep` |
| Reference template file exists | ✅ | `references/issue-body-template.md`, tested by `PackagedExploreSkill_ProvidesReferenceTemplateFile` |
| Web UI shows recommendation panel | ✅ | `CreateIssueDialog.tsx:314-337`, tested by `CreateIssueDialog.test.tsx` ("shows recommendation panel...") |
| Web UI pre-fills workflow selector | ✅ | `CreateIssueDialog.tsx:222-226`, tested by "shows recommendation panel and pre-fills workflow selector" |
| Web UI one-click submit with recommendation | ✅ | tested by "one-click submit with recommendation creates issue with recommended workflow and risk" |
| Web UI manual override | ✅ | tested by "manually changing the workflow selector overrides the frontmatter recommendation" |
| Web UI risk pre-fill | ✅ | tested by "pre-fills risk selector from frontmatter" |
| Web UI no panel without frontmatter | ✅ | tested by "does not show recommendation panel when body has no frontmatter" |
| Web UI malformed fallback | ✅ | tested by "silently ignores malformed frontmatter and falls back to defaults" |
| `createIssue()` sends workflowProfileId and risk | ✅ | `client.ts:21`, tested by `create-issue-api-client.test.ts` (5 tests) |
| End-to-end: explore → structured issue → auto-select workflow → start | ⚠️ Partial | CLI parsing, skill production, Web UI, and risk persistence all work. However, `workflowProfileId` is silently dropped by the server (item-1), so non-default workflow auto-selection does not influence runtime. This is a pre-existing server-side gap. |

<promise>PASS</promise>
