# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `design.md` decision D5 grouped `TerminalFact.FromTranscript` and `TranscriptEventSummaryProjector` among the "literal `\"session_closed\"` comparisons" to be replaced. Codebase verification shows these two sites already reference the `TranscriptPartTypes.SessionClosed` *constant* (not a literal) — `AgentSessionQuerier.cs:1578` and `TranscriptEventSummaryProjector.cs:21`. Only `ReadTerminalStateAsync:445`, `SessionTranscriptBuilder.cs:77`, and `AgentSessionSummaryBuilder.cs:79,114` carry real literals. The spec (`session-transcript-event-naming/spec.md`) and task T-001 already describe the correct end state; only the design prose was imprecise, which could send an implementer hunting for a non-existent literal.
  Verification: Refined D5 to split literal sites (need edits) from constant-reference sites (pick up the dot token automatically when the constant flips). The spec and task descriptions were already correct and were not changed. No behavioral or architectural change.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: `design.md` Open Questions defer two micro-decisions to implementation time — final service names (`AgentUsageReporter` / `AgentActivityFeedAssembler` vs the `*Querier`/`*Service` convention) and placement of shared DTO mappers (`ToUsageDto`/`BuildUsageHistoryDto` stay `internal static` on the core querier vs move to a shared mapper). Both preserve behavior either way.
  SuggestedAction: Confirm names and mapper placement at the start of T-003/T-004 implementation; update the capability/spec headings if the final names diverge from the proposal.
  Status: follow-up

## Review Notes

Spot-checks performed against the live codebase (`packages/server/src/Mohist.Server/Sessions/`) to ground the plan:

- `AgentSessionQuerier.cs` is exactly **1635 lines** (matches proposal/design).
- `ToAgentSessionDto` (`AgentSessionQuerier.cs:1274`) and `AgentSessionDto` (`AgentSessionReadModels.cs:40`) have **zero external callers** — dead-code claim confirmed. The substring-overlapping `RunnerAgentSessionDto` test record is a distinct type (already called out in T-005 AC).
- Dual-spelling matcher lives at `AgentSessionQuerier.cs:445`; the only other literal `"session_closed"` type-comparisons are `SessionTranscriptBuilder.cs:77` and `AgentSessionSummaryBuilder.cs:79,114`. `TerminalFact` and `TranscriptEventSummaryProjector` use the constant.
- All five transcript-load duplication sites exist (`LoadLatestEventsAsync:1106`, `LoadEventSummariesAsync:1132`, `LoadTerminalFactsAsync:1422`, `GetGenericSessionSummaryAsync:511`, `BuildSessionMetadataDtoAsync:583`).
- Both context-ref builders exist (`BuildAgentSessionListContextRefs:296`, `BuildGenericSessionSummaryContextRefs:566`).
- `_workflowQuerier` is used at exactly one site (`AgentSessionQuerier.cs:736`, inside `BuildTaskProgressMapAsync`) — validates design D2's ctor-drop claim.
- All four literal-asserting spec files named in the migration plan exist and reference `session_closed`.
- `MigratedServicesRegistrationSpecs` exists; the direct-construction site `GenericAgentSessionSummarySpecs.cs:210` already passes `null!` for the `_workflowQuerier` arg (validates the risk note).

### Criteria summary

- **alignment** — Every issue AC maps to a proposal "What Changes" entry, a spec requirement, and a task. All Non-Goals (lineage fallback, storage model, label key, `cancelled`→`stopped`, followup/cancel relocation) are respected.
- **completeness** — Three capabilities ↔ three specs ↔ five tasks, fully cross-referenced. Edge cases (direct-construction spec, historical rows, missed literals, DTO-mapper placement) are captured in design risks / open questions.
- **consistency** — Capability ↔ spec ↔ task mapping is uniform; the single D5 wording imprecision was repaired (item-1).
- **feasibility** — Linear dependency chain `T-001 → T-002 → T-003 → T-004 → T-005`, each `dependsOn` pointing to a lower-priority existing ID, no cycles. Granularity is appropriate: each task is a complete feature slice (event-vocabulary unification, helper consolidation, service extraction ×2, dead-code deletion). No micro-tasks ("define interface", standalone "register DI", standalone "add tests"); tests are integrated into each task's acceptance criteria.
- **dependency_completeness** — Every non-first task has `dependsOn`; all entries resolve to existing IDs with lower priority.

<promise>PASS</promise>
