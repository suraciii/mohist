# Review Report

## Result: PASS

Post-repair candidate snapshot satisfies the issue acceptance criteria. Evidence: issue default model row selection sends `variant: null` in `packages/web/src/features/select-issue-model/ui/IssueModelSelector.tsx:223`; stage model row selection sends stage-scoped `variant: null` in `packages/web/src/features/select-issue-model/ui/IssueModelSelector.tsx:277`; project default same-row clicks no longer short-circuit and always mutate with `variant: null` in `packages/web/src/pages/settings/ui/AiSettingsSection.tsx:78`; generic `VariableBundle.Patch` null-delete / omitted-preserve semantics remain unchanged in `packages/server/src/Mohist.Server/Workflow/Domain/VariableBundle.cs:78` and `packages/server/src/Mohist.Server/Workflow/Domain/VariableBundle.cs:150`, with regression coverage added in `packages/server/tests/Mohist.Server.Tests/Specs/Foundation/VariableBundleSpecs.cs:721`, `packages/server/tests/Mohist.Server.Tests/Specs/Foundation/VariableBundleSpecs.cs:759`, and `packages/server/tests/Mohist.Server.Tests/Specs/Foundation/VariableBundleSpecs.cs:796`.

Verification run for the candidate-specific surface passed: `npm run typecheck -w packages/web`; `npm run test:run -w packages/web` (4416 passed, 1 skipped); `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter FullyQualifiedName~VariableBundleSpecs` (36 passed); `npm test -w packages/runner -- tests/acp/session-strategies.spec.ts` (48 passed). Broad `npm test` still fails on one pre-existing Epic CLI documentation drift, reported below.

## Repaired Items

None.

## Blocking Items

None.

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: warning
  Scope: root test suite / `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Docs/EpicDocumentationSpecs.cs:73`
  Evidence: Broad `npm test` fails before workspace JS tests run because `EpicDocumentationSpecs.SurfaceDocs_StayAlignedWithEpicCliAndWebUiCopy` expects the substring `mo epic create <title> [options]` in `docs/cli-reference.md`. This is outside the issue-288 diff and is also recorded as pre-existing in `openspec/changes/issue-288/progress.txt:14` and `openspec/changes/issue-288/progress.txt:38`.
  SuggestedAction: Fix the Epic CLI documentation drift in a separate docs-sync change, then rerun full `npm test`.
  Status: pre-existing

- [ID: item-2]
  Severity: warning
  Scope: `packages/web/src/widgets/agent-profile-editor/ui/AgentProfileEditor.tsx:200`
  Evidence: Separate from issue #288, Agent Profile Editor uses the same `ModelSelect` but handles model-row selection with `onChange={(m) => setModel(m)}` while the saved payload still uses the existing `variant` state at `packages/web/src/widgets/agent-profile-editor/ui/AgentProfileEditor.tsx:74`. Editing an agent that already has a variant, then selecting a model row without a variant chip, can preserve and save the stale variant. This path is not one of the issue-level, stage-level, or project-level acceptance criteria reviewed for this candidate.
  SuggestedAction: In a separate issue, clear `variant` when the Agent Profile Editor model row changes and add a focused regression test.
  Status: out-of-scope

<promise>PASS</promise>
