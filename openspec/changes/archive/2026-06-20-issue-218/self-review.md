# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The proposal framed `Mohist.Server.Infrastructure.JSON` as newly created ("新增门面"), but the design — grounded in actual code (`packages/server/src/Mohist.Server/Infrastructure/JSON.cs` already exists) — correctly treats it as an existing facade being enhanced (add encoder + `Indented` + promote converter). This proposal↔design wording mismatch is a consistency defect.
  Verification: Edited `openspec/changes/issue-218/proposal.md` "What Changes" and "Impact" to say the existing facade is established/enhanced rather than newly added. Re-read proposal and design together; they now agree that the facade pre-exists. No architectural or behavioral change introduced.
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: Design migration step 5 and Open Question 1 propose a long-term enforcement mechanism (source-grep test, Roslyn analyzer, or CI grep) to prevent `new JsonSerializerOptions(` from reappearing outside the facade. T-003 encodes the rule as a one-time grep verification, not a durable automated guard.
  SuggestedAction: During T-003 implementation, decide the enforcement mechanism (lean: a `dotnet test` source-grep test) so the rule survives beyond the initial migration. This is an implementation detail, not a plan-level gap.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-002 relies on the global `Microsoft.AspNetCore.Http.Json.JsonOptions` registration covering `ApiResults.*` / `ApiResponse<T>`. This holds only if those helpers serialize through the framework's `Results.Json` path rather than calling `JsonSerializer.Serialize` with a local options. The design's risk section already calls this out with a grep mitigation.
  SuggestedAction: During T-002, confirm `ApiResults.*` / `ApiResponse.cs` flow through framework serialization (no local options); T-002's acceptance criterion "No response helper overrides the encoder with a locally constructed options instance" already gates this.
  Status: follow-up

## Review Notes

- **Alignment**: Proposal What Changes entries trace 1:1 to the issue's design decisions and all 7 acceptance checkboxes; no issue requirement is missing or misinterpreted (HTTP `ApiResults.*`/`ApiResponse.cs`, SignalR hubs, scattered-options elimination, converter ownership, persistence compat all covered).
- **Completeness**: 2 capabilities in proposal → 2 spec files created (`json-serialization` new with 6 requirements; `http-api` ADDED with 1 requirement). Every requirement has task coverage: json-serialization #1→T-001/T-003, #2→T-001, #3→T-002, #4→T-002, #5→T-001, #6→T-004; http-api→T-002/T-004. Edge cases covered (HTML-escaping safety, backward compat, enum/session round-trip, error responses).
- **Consistency**: Spec capability names match proposal; task `spec` references point to real requirement headers; design's 7 decisions map cleanly onto the 7 spec requirements; naming is uniform (`JSON.Options`, `FailureReason`, hub paths).
- **Feasibility**: T-001 produces the facade+converter that T-002/T-003 consume; DAG verified acyclic; all `dependsOn` reference strictly-lower priorities. No over-fine tasks — no title matches 定义接口/注册DI/创建文件/添加测试 patterns; HTTP+SignalR intentionally kept together (issue Bucket D) to avoid one-line over-splitting; no standalone test task (T-004 is a HITL REVIEW gate with embedded manual checks, not a unit-test task).
- **Dependency completeness**: T-002/T-003 → T-001; T-004 → T-002,T-003. Every non-first task has valid `dependsOn`; T-002 and T-003 are correctly parallel siblings.

<promise>PASS</promise>
