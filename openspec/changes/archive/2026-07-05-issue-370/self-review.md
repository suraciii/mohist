# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: T-005's doc-reference file list was inaccurate. It enumerated five files (`AgentSessionContextRefs.cs`, `AgentSessionContextRefsSpecs.cs`, `GenericAgentSessionSummarySpecs.cs`, `AgentActivityFeedAssemblerSpecs.cs`, `AgentSessionReadModels.cs`) as needing `<see cref>` repointing, but a `rg "see cref"` sweep confirms none of them reference any of the 13 removed statics — they reference members that are NOT being removed (`BuildAgentSessionListContextRefs`, `BuildGenericSessionSummaryContextRefs` are `private static`; `GetGenericSessionSummaryAsync` is a real query method). The only file that actually contains `<see cref>` to removed statics is `AgentActivityFeedAssembler.cs:22-23` (`ReconcileActiveSessionsAsync`, `LoadEventSummariesAsync`), which is not in T-005's list but is covered by T-003's assembler re-targeting. As written, T-005's acceptance criterion was vacuously satisfied and the real repointing location was mislabeled. Updated T-005's `description` and acceptance criterion in `tasks.json` to reference the correct file (`AgentActivityFeedAssembler.cs`) and explicitly note that the other listed files reference non-removed members and need no repointing.
  Verification: `python3 -c "import json; json.load(open('openspec/changes/issue-370/tasks.json'))"` confirms the file remains valid JSON after the edit; the T-005 acceptance criterion now points at the actual location of removed-static `<see cref>` references and the verification gates (zero-statics `rg` + `npm test`) are unchanged.
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: alignment
  Evidence: The issue body states "14 个 internal static 成员" but the codebase exposes exactly 13 (`rg "internal static" AgentSessionQuerier.cs`). The proposal correctly enumerates 13 and aligns to the codebase, so this is a minor count drift in the issue itself, not in the plan artifacts. Separately, the design's problem framing cites ~25 sibling calls (assembler 18×); the actual counts are 29 total (assembler 22×, reporter 3×, context-refs 4×). These are soft "approximate" figures that do not affect the solution.
  SuggestedAction: No artifact change required. If desired, the implementation PR can note the real counts in its summary; the issue's "14" can be left as-is since the proposal already reflects reality.
  Status: follow-up

## Review Notes

Traceability and completeness were verified end-to-end:

- **Capabilities ↔ specs ↔ tasks**: all five proposal capabilities (`agent-session-dto-mapping`, `agent-session-record-accessors`, `transcript-reductions`, `issue-title-batch-lookup`, `label-filter-builder`) have matching spec directories and are covered by tasks. `label-filter-builder` is co-implemented with `agent-session-dto-mapping` in T-001, consistent with design D6 (authoritative `Labels` lives on `AgentSessionDtoMapper`).
- **Static-removal partition**: the 13 internal statics are partitioned without gaps or overlaps — T-001 removes 6 (ToUsageDto, BuildUsageHistoryDto, ToEventSummaryDto, BuildLineageDto, ToProjection, Labels), T-002 removes 3 (Label, IssueNumber, Annotation), T-003 removes 2 (LoadEventSummariesAsync, ReconcileActiveSessionsAsync), T-004 removes 2 (LoadIssueTitlesAsync, IssueTitle). 6+3+2+2 = 13.
- **Acceptance criteria coverage**: every issue AC is pinned by spec scenarios — AC1 (zero statics) by each spec's "Core query class exposes no X statics" scenario; AC2 by `agent-session-dto-mapping` cross-consumer identity scenarios; AC3 by `agent-session-record-accessors` precedence/fallback/absent scenarios; AC4 by `label-filter-builder` single-implementation scenarios; AC5 (behavior unchanged, tests green) by byte-identity scenarios plus per-task `npm test` gates.
- **Dependency graph**: DAG with no cycles. T-001 (priority 1, dependsOn []), T-002 (priority 2, dependsOn []), T-004 (priority 2, dependsOn []) are logically independent; T-003 (priority 3) correctly depends on T-001 + T-002 because `ReconcileActiveSessionsAsync` consumes `AgentSessionDtoMapper.ToProjection` and the record label-with-fallback; T-005 (priority 4, REVIEW) depends on T-001..T-004 as the integration gate. All `dependsOn` entries point to existing IDs with lower priority.
- **Granularity**: every task is a complete feature slice (new home + consumer re-target + static removal + tests in one task). No task is a pure code-move, rename, DI-registration, or standalone "add tests" task; tests are embedded in each WRITE task. T-005 is an appropriately-scoped REVIEW gate.
- **Verified codebase claims**: 13 statics confirmed; duplicate `Labels` at `AgentActivityFeedAssembler.cs:302` confirmed; third `Label`/`IssueNumber` duplicate at `WorkflowActivityQuerier.cs:118-124` confirmed (design's bonus find); 8 static call sites in `AgentSessionRecoveryDomainSpecs.cs` confirmed (BuildLineageDto ×3, BuildUsageHistoryDto ×3, ToUsageDto ×2); existing `AgentSessionRecord.Label(string)` is record-only (line 166), so design Open-Question-1 lean (a) — making `Label(key)` do the fallback — is a real contract widen that the spec's three scenarios pin regardless of name; `RunnerRoutes.cs:362-363` calls `record.Label(...)` directly and is covered by the existing `AgentSessionContextAssociationApiSpecs` since production records share the metadata label dictionary.

<promise>PASS</promise>
