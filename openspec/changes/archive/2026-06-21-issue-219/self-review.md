# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: Specs and proposal used flat config keys (`Mohist:OtelPort`, `Mohist:OtelDbPath`) while design Decision 6 and tasks.json use nested keys (`Mohist:Otel:Port`, `Mohist:Otel:DbPath`) matching existing codebase conventions (`Mohist:AgentJob`, `Mohist:AttachmentStorage`). This would cause implementation confusion — builders reading specs would use different keys than the OtelOptions class binds.
  Verification: Replaced all occurrences of `Mohist:OtelPort` → `Mohist:Otel:Port` (3 in otel-trace-collection spec, 2 in server-daemon spec, 4 in proposal) and `Mohist:OtelDbPath` → `Mohist:Otel:DbPath` (2 in otel-trace-collection spec). Grep confirms zero stale flat keys remain across all change artifacts.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: `otel-trace-query-api/spec.md` states responses "SHALL 为 JSON 数组" (bare array), but design Decision 8 and tasks.json T-003 specify the standard `ApiResponse<T>` envelope (`{success:true, data:[...]}`) for `/otel/api/*` endpoints. The design acknowledges this as an implementation clarification. The tasks are already aligned with the design. The spec wording is technically more abstract ("returns trace list as JSON") and not wrong per se — the data IS a JSON array, just envelope-wrapped — but a future spec reader might expect a raw array body.
  SuggestedAction: During implementation (T-003), if the team confirms the envelope approach, update the spec requirement text to explicitly mention "ApiResponse 信封包裹" for full precision. No action needed now since design + tasks are authoritative for implementation.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: completeness
  Evidence: Design Open Question #2 asks whether CLI should share server's `OtelDb.cs` / schema DDL constants via a shared project. The design recommends minimal copy. T-004 notes the dependency on T-001's schema contract but doesn't specify whether the DDL is duplicated or shared. If duplicated, there's a maintenance risk of schema drift between server and CLI.
  SuggestedAction: During T-004 implementation, decide whether to extract DDL constants to a shared location or duplicate with a comment pointing to the authoritative source in T-001's `OtelDb.cs`.
  Status: follow-up

## Review Summary

**Alignment**: All 9 issue acceptance criteria map to proposal "What Changes" entries. All issue non-goals are reflected in proposal non-goals. ✅

**Completeness**: All 6 capabilities (3 new, 3 modified) have spec files. All spec requirements have corresponding task coverage. Edge cases covered: port bind failure, protobuf rejection, bad JSON, SQL injection defense, idempotent writes, data isolation, CLI server-down handling, DB-not-found, port isolation. ✅

**Consistency**: Config key naming fixed (item-1). Spec capability names match proposal. Task spec references point to existing requirements. Design decisions align with specs. Tasks reference correct design decisions. ✅

**Feasibility**: 4 tasks are each complete functional slices (infra / ingestion / query API / CLI), not over-granularized technical steps. No tasks named "define interface", "register DI", "create file", etc. No standalone test tasks. Each task includes its own test coverage in acceptance criteria. ✅

**Dependency completeness**: T-001 (priority 1, no deps) → T-002 (priority 2, deps T-001) → T-003 (priority 3, deps T-001) → T-004 (priority 4, deps T-003). All deps point to existing lower-priority tasks. DAG is acyclic. T-002 and T-003 can execute in parallel after T-001. ✅

<promise>PASS</promise>
