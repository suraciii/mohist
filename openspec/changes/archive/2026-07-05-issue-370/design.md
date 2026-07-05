## Context

issue-327 split the Session read side into `AgentSessionQuerier` + `AgentActivityFeedAssembler` + `AgentUsageReporter` + `AgentSessionContextRefs`, but the shared logic stayed on the querier as `internal static` members. Today the querier exposes 13 such members and siblings reach back into them ~25 times (`AgentActivityFeedAssembler` 18×, `AgentUsageReporter` 3×, `AgentSessionContextRefs` 4×). Three symptoms follow:

- The split reduced file size, not coupling — editing one static silently perturbs three classes.
- `Labels(...)` is duplicated byte-for-byte at `AgentActivityFeedAssembler.cs:302` and no reviewer noticed.
- The "byte-alignment" invariant between the three consumers lives only in prose comments.

A fourth symptom turned up while reading the code for this design: `WorkflowActivityQuerier.cs:118–124` carries its **own** private copies of `Label(record, key)` and `IssueNumber(record)` — i.e. the same fallback logic exists in a third place the proposal did not call out. Consolidating onto `AgentSessionRecord` removes that duplicate for free.

The 13 internal statics group into four natural buckets:

| Bucket | Members | Natural home |
|--------|---------|--------------|
| DTO projection | `ToUsageDto` (×2 overloads + session overload), `BuildUsageHistoryDto`, `ToEventSummaryDto`, `BuildLineageDto`, `ToProjection` | new `AgentSessionDtoMapper` |
| Record pure reads | `Label`, `IssueNumber` | instance methods on `AgentSessionRecord` |
| Pure forwarder | `Annotation` | inline at callers (`session.Metadata.Annotation(key)`) |
| Transcript reductions | `LoadEventSummariesAsync`, `ReconcileActiveSessionsAsync` | transcript loader region |
| Issue read-side | `LoadIssueTitlesAsync`, `IssueTitle` | Issue read side (`Issue/Services/`) |
| Label filter helper | `Labels(params …)` | single authoritative home (see Decisions) |

Constraints:

- All HTTP responses byte-identical (spec `agent-session-dto-mapping` / `transcript-reductions` / `issue-title-batch-lookup` / `label-filter-builder` all pin this).
- `AgentSessionQuerier` stays an `IScopedService` injected into API routes by concrete type — DI registrations and constructor signatures must not change shape in a way the routes notice.
- No DB schema, migration, runner, web, or CLI impact.
- `AgentSessionRecord` already exposes `Label(string)` reading only the record dictionary; `AgentSessionQuery.ToRecords` constructs every record with `session.Metadata.Labels`, so record-label and metadata-label are the same dictionary in production. The static's `?? session.Metadata.Label(key)` fallback is defensive against synthetic records (tests/fakes). This must be preserved, not optimized away.

Stakeholders: Server Session domain (owner), AgentOps (`Assembler`/`Reporter` consumers), Issue read side (new host for title lookup), Workflow domain (`WorkflowActivityQuerier` benefits from the record accessor consolidation).

## Goals / Non-Goals

**Goals:**

- `AgentSessionQuerier` declares zero `internal static` members; only its real query methods remain.
- DTO projections have one authoritative home called from all three consumers; the byte-alignment invariant becomes "call the same method" instead of a comment.
- `Label` / `IssueNumber` live on the data they operate on (`AgentSessionRecord`), eliminating the querier static **and** the `WorkflowActivityQuerier` private duplicate.
- Transcript reductions sit next to `TranscriptPartLoader`, the loader they already depend on.
- Issue-title batch lookup sits on the Issue read side, called by Session via a `(project, numbers)` boundary.
- Exactly one `Labels(...)` implementation; the assembler's duplicate is deleted.
- All existing specs pass unchanged; new specs assert cross-consumer identity.

**Non-Goals:**

- No change to the transcript-loading design itself (only `LoadEventSummariesAsync` / `ReconcileActiveSessionsAsync` move; `TranscriptPartLoader.LoadAsync` is untouched).
- No change to `AgentSessionRecord` fields or constructor signature (only additive instance methods).
- No change to DTO shapes, HTTP routes, DI lifetimes, or the `IScopedService` boundary.
- No refactoring of `AgentSessionJsonHelper`, `ContextHealthClassifier`, `TranscriptEventSummaryProjector`, or `IssueRowMapper` — they are already correct homes and stay as-is.
- No split of `AgentSessionQuerier` itself beyond removing statics; its query methods stay co-located.

## Decisions

### D1. New `AgentSessionDtoMapper` as a pure static type

**Choice.** Add `internal static class AgentSessionDtoMapper` in `Sessions/Services/`. Move all five DTO projections there: `ToUsageDto(AgentSession)`, `ToUsageDto(AgentUsageSummary)`, `ToUsageDto(AgentUsageSummary, history)`, `BuildUsageHistoryDto(AgentSession)`, `ToEventSummaryDto(summary?)`, `BuildLineageDto(AgentSession)`, `ToProjection(sessionId, part)`.

**Rationale.** Every member is a pure function — no DB, no clock, no DI. `IssueRowMapper` and `WorkflowStatusMapper` already set the `static *Mapper` precedent in this codebase. Static keeps the three consumers (querier, assembler, generic-summary path) invocation-shaped identical to today (`AgentSessionDtoMapper.ToUsageDto(s)` is a one-token swap for `AgentSessionQuerier.ToUsageDto(s)`), so the diff stays mechanical and reviewable. `internal` because the only out-of-assembly caller is the test assembly via `InternalsVisibleTo`, matching the existing visibility of the statics being moved.

**Alternatives considered.**
- *Instance `AgentSessionDtoMapper` resolved from DI.* Rejected: no dependencies to inject, so it would just add a constructor parameter to three classes for zero behavioral gain and force a DI registration update.
- *Methods on the DTOs themselves (e.g. `AgentUsageDto.From(session)`).* Rejected: the DTOs live in `Sessions/AgentSessionReadModels.cs` and are also returned by AgentOps consumers; pushing domain-session knowledge onto them inverts the dependency direction.
- *Spread projections across multiple mapper types (one per DTO).* Rejected: the byte-alignment invariant is the whole point — one home is what makes "call the same method" enforce the invariant a comment currently can't.

### D2. `Label` and `IssueNumber` become instance methods on `AgentSessionRecord`

**Choice.** Add `LabelWithFallback(string key)` (or similar — see Open Questions) and `IssueNumber()` instance methods on `AgentSessionRecord`. The fallback method resolves `Labels[key] ?? Session.Metadata.Label(key)`, preserving today's record-first-then-metadata order; `IssueNumber()` parses the fallback-resolved issue-number label and returns 0 on absent/non-numeric. Remove `AgentSessionQuerier.Label` and `AgentSessionQuerier.IssueNumber`.

**Rationale.** The spec (`agent-session-record-accessors`) pins this directly. The method operates on the record's own state — record is the natural home. Bonus: `WorkflowActivityQuerier.cs:118–124` carries private duplicates of both; those collapse to `record.LabelWithFallback(key)` / `record.IssueNumber()` for free, removing a third copy the proposal didn't enumerate.

**On the fallback semantics.** `AgentSessionQuery.ToRecords` constructs every record with `session.Metadata.Labels`, so in production `Labels` and `Session.Metadata.Labels` are the same dictionary and the fallback is a no-op. It is kept as a defensive read against synthetic records (tests/fakes that build `AgentSessionRecord` directly with a hand-crafted label dictionary). The spec's three scenarios (record-precedence, metadata-fallback, absent-returns-null) fix this behavior; existing record-only callers (e.g. `RunnerRoutes.cs:362–363`) observe the same value because the production dictionaries are identical.

**Alternatives considered.**
- *Make existing `Label(string)` itself do the fallback, drop the "withFallback" suffix.* See Open Questions — this changes the method's contract for `RunnerRoutes` and any future caller that wanted record-only reads. Currently only `RunnerRoutes` calls `record.Label(...)` directly and it gets the same answer either way (production dictionaries coincide), so this is viable but trades a quiet contract change for a cleaner name.
- *Leave a `Label` record-only read and add `ResolveLabel` for the fallback.* Cleaner separation but two methods where one would do; the record-only version has no production caller that needs the distinction.

### D3. Remove the `Annotation` forwarder; callers use `session.Metadata.Annotation(key)` directly

**Choice.** Delete `AgentSessionQuerier.Annotation`. The three call sites in `AgentSessionQuerier` (`ToSummaryDto`, `BuildSessionMetadataDtoAsync` ×2) become `s.Metadata.Annotation(key)`.

**Rationale.** The static is `=> session.Metadata.Annotation(key)` — a pure forwarder with zero added logic. The spec (`agent-session-record-accessors`, third requirement) explicitly calls this out. Removing it makes the querier surface smaller and removes a false implication that the querier does something special with annotations.

**Alternatives considered.** None — the spec mandates removal.

### D4. Transcript reductions move next to `TranscriptPartLoader`

**Choice.** Move `LoadEventSummariesAsync` and `ReconcileActiveSessionsAsync` (with their private helpers `LoadWorkflowRunsForReconciliationAsync`, `DeserializeWorkflowRun`, `IsSessionAssociatedWithRun`, `IsActiveSession`) into the transcript loader region. Concretely: a new `internal static class TranscriptReductions` in `Sessions/Services/` peer to `TranscriptPartLoader.cs`, or instance methods on a peer type — see Open Questions. Both `AgentSessionQuerier` and `AgentActivityFeedAssembler` call the new home.

**Rationale.** Both reductions already depend on `TranscriptPartLoader.LoadAsync` and on the `TranscriptEventProjection` shape (which itself moves to the mapper per D1 — `ToProjection` is a DTO projection). Co-locating them with the loader tightens the "transcript read region" boundary issue-327 T-002 established and gets the assembler's dependency off the querier entirely.

**On the reconciliation helper dependencies.** `ReconcileActiveSessionsAsync` reads `WorkflowRun` state from `db.WorkflowRuns` and uses `Label(record, WorkflowRunId)` / `Label(record, WorkId)`. After D2 those become `record.LabelWithFallback(...)`. The `WorkflowRun` deserialization is workflow-domain shaped but the *filter* (single-runner assignment + running-task match) is a session-read concern deciding which sessions to surface; the reduction stays in the Session/transcript region and consumes `WorkflowRun` as a read model, exactly as today.

**Alternatives considered.**
- *Put them on `TranscriptPartLoader` itself.* Rejected: the loader is intentionally a thin raw-materials loader ("returns the raw materials — loaded rows, dictionaries, parts list — and lets each caller impose its own ordering"); bolting summarization + workflow-run reconciliation onto it would break that contract.
- *Put reconciliation on the Workflow read side.* Rejected: the decision "is this session active for surfacing" is a Session read concern that *consumes* a `WorkflowRun` read model; inverting it would push session-list logic into Workflow.
- *Instance type with DI.* Rejected for the same reason as D1: the reductions take `MohistDbContext` as a parameter, not via constructor; making them instance would add a DI registration for no behavioral gain.

### D5. Issue-title batch lookup moves to the Issue read side

**Choice.** Add `internal static class IssueTitleLookup` (or a method on an existing Issue read-side type — see Open Questions) in `Issue/Services/` exposing `LoadTitlesAsync(db, projectId, issueNumbers, ct) -> Dictionary<int, string>` and `Resolve(titles, issueNumber) -> string`. Session-side callers (`AgentSessionQuerier.ListCurrentAsync`, `AgentActivityFeedAssembler`) call it directly with the `(project, numbers)` tuple.

**Rationale.** The lookup reads `db.Issues` + `IssueRowMapper.ByNumber` — both are Issue-domain data. Today it sits on the querier only because the assembler needed a way to call it without its own DB context; now both callers can invoke the Issue-side capability with the same `(db, project, numbers)` signature. Matches the architecture rule "跨域只读报告组装 → 把跨域查询放到拥有那份数据的域".

**Alternatives considered.**
- *Method on `IssueQuerier`.* Rejected: `IssueQuerier` is a rich read service with project/profile/enrichment dependencies; the title lookup needs only `(db, projectId, numbers)`. Adding it there would force the assembler and querier to take a full `IssueQuerier` dependency for one batch read.
- *Method on `IssueRowMapper`.* Tempting (the lookup already calls `IssueRowMapper.ByNumber`), but `IssueRowMapper` is intentionally a pure row→domain mapper with no DB access. Adding `IQueryable`/`ToListAsync` to it breaks that contract.

### D6. `Labels(...)` collapses to a single shared helper

**Choice.** Put the one authoritative `Labels(params (string Key, string? Value)[])` builder on the new `AgentSessionDtoMapper` (it is a pure projection helper and the mapper is already the single home sibling code reaches for). Delete the duplicate at `AgentActivityFeedAssembler.cs:302–311`. `AgentUsageReporter` and `AgentSessionQuerier` swap their `AgentSessionQuerier.Labels(...)` calls for `AgentSessionDtoMapper.Labels(...)`.

**Rationale.** The spec (`label-filter-builder`) pins "exactly one authoritative implementation". The mapper is already the natural sibling-facing surface; parking `Labels` there avoids inventing a third type for one five-line helper.

**Alternatives considered.**
- *Dedicated `LabelFilterBuilder` static type.* Clean but a whole type for one method is overkill; the mapper already exists and is the natural sibling-facing surface.
- *Extension method on `(string, string?)[]`.* Rejected: discovery cost is high and the call sites are few.

### D7. Test migration: re-target only, no spec rewrite

**Choice.** The four direct-static call sites in `AgentSessionRecoveryDomainSpecs.cs` (`BuildLineageDto` ×3, `BuildUsageHistoryDto` ×3, `ToUsageDto` ×2) swap `AgentSessionQuerier.X` → `AgentSessionDtoMapper.X`. No assertion text changes. Add **one** new spec per consumer pair asserting identical output: usage DTO from querier vs. assembler for the same `AgentSession`; event-summary DTO from querier vs. assembler; lineage from metadata path vs. generic-summary path. These codify the byte-alignment invariant that used to live in comments.

**Rationale.** The existing specs already prove byte-identity against fixed inputs; the move is a rename, not a behavior change. The new specs make the *cross-consumer* invariant — the actual point of this refactor — explicit and enforced.

**Alternatives considered.**
- *Leave the existing statics as thin forwarders to the mapper.* Rejected: the acceptance criterion "internal static 成员数降为零" rules this out, and forwarders would re-create exactly the coupling the refactor removes.

## Risks / Trade-offs

- **[Risk: `LabelWithFallback` naming bikeshedding / contract surprise]** → Mitigation: pick a single name up front (see Open Questions) and document the fallback semantics on the method XML doc; the three spec scenarios pin the behavior regardless of name.
- **[Risk: hidden third/fourth duplicate of `Label` / `Labels` outside the Sessions folder]** → Mitigation: a `rg "record.Label\(|Session.Metadata.Label\b|private static.*Labels\("` sweep is part of the implementation checklist; this design already found one (`WorkflowActivityQuerier`).
- **[Risk: behavioral drift in `RunnerRoutes.cs:362–363` if record-label semantics change]** → Mitigation: production records are constructed with `session.Metadata.Labels`, so record-only and fallback reads coincide; the implementation must verify these two call sites observe identical values via the existing `AgentSessionContextAssociationApiSpecs`.
- **[Risk: the "byte-identical" invariant is hard to assert exhaustively]** → Mitigation: existing spec suite already pins outputs for token counts, null-vs-false flags, lineage timestamps, and null-when-empty history; new cross-consumer identity specs add the equality dimension. No new external dependencies.
- **[Trade-off: `internal static` over instance/DI]** → Accepted: these are pure functions or db-parameterized reductions; static matches the `IssueRowMapper` precedent and keeps the diff a one-token rename at each call site. Cost: slightly weaker testability for the reductions (they take `MohistDbContext` as a parameter, which already gives fakes full control via the in-memory test DB factory).
- **[Trade-off: one mapper type holds seven methods]** → Accepted: they share the "session → DTO projection" concern and the byte-alignment invariant is *among them* (usage DTO must agree with event-summary DTO's session shape); splitting would re-create the comment-only invariant.
- **[Risk: `ReconcileActiveSessionsAsync` is the most coupled reduction (workflow-run reads + session filtering)]** → Mitigation: it moves as a unit with its private helpers; no helper signature changes; the existing `AgentSessionRecoveryDomainSpecs` / `AgentActivityFeedAssemblerSpecs` exercise both branches (active-non-matching, unassigned-provisional, non-active passthrough).

## Migration Plan

This is an internal-surface-only refactor with no external contract change, so deployment is a single build-and-restart — no schema migration, no config, no runner/web/CLI coordination.

**Step order (each step leaves the build green):**

1. **Additive only.** Introduce `AgentSessionDtoMapper` (with `Labels`), `TranscriptReductions` (or chosen home), `IssueTitleLookup` (or chosen home), and the new `AgentSessionRecord` instance methods. Don't remove anything yet. Build + run the spec suite — it must be green with zero test edits.
2. **Re-target sibling consumers.** Swap `AgentSessionQuerier.X` → new homes in `AgentActivityFeedAssembler`, `AgentUsageReporter`, `AgentSessionContextRefs`, and `WorkflowActivityQuerier`. Delete the assembler's private `Labels` duplicate. Build + spec green.
3. **Re-target the querier itself.** Its private helpers (`ToWorkflowDto`, `ToSummaryDto`, `BuildSessionMetadataDtoAsync`, `ListCurrentAsync`, etc.) now call the mapper / record methods / `IssueTitleLookup` / `TranscriptReductions`. Inline `Annotation` at the three call sites. Build + spec green.
4. **Delete the statics.** Remove all 13 `internal static` members from `AgentSessionQuerier`. Verify the file compiles and no caller references them (`rg "AgentSessionQuerier\.(Build|Load|To|Issue|Label|Annotation|Reconcile|Labels)" packages/server` should return only doc-comment `<see>` references, which are then re-pointed).
5. **Migrate tests.** Re-target the 8 static calls in `AgentSessionRecoveryDomainSpecs`. Add the three cross-consumer identity specs (usage, event-summary, lineage).
6. **Doc cleanup.** Repoint `<see cref="AgentSessionQuerier.BuildLineageDto"/>`-style references in `AgentSessionContextRefs.cs`, `AgentSessionContextRefsSpecs.cs`, `GenericAgentSessionSummarySpecs.cs`, `AgentActivityFeedAssemblerSpecs.cs`, and `AgentSessionReadModels.cs` to the new homes.

**Verification gates:**

- `npm test` (server) — full spec suite green, including the byte-identity specs already covering usage / event-summary / lineage / history.
- `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` — unchanged inputs/outputs, must stay green.
- `rg "internal static.*AgentSessionQuerier|AgentSessionQuerier\.(Build|Load|To|Issue|Label|Annotation|Reconcile|Labels)\b" packages/server/src` — returns zero non-doc hits.

**Rollback.** Revert to the commit before step 4 (the statics still existed through step 3 as forwarders, so the safest rollback point is the pre-refactor commit). No data or state to roll back.

## Open Questions

1. **`Label` method name on `AgentSessionRecord`.** The record already has a record-only `Label(string)`; the static being moved adds the metadata fallback. Three options:
   - (a) Rename existing to `RecordLabel(key)`, make `Label(key)` do the fallback. Cleanest call-site text; quiet contract change for `RunnerRoutes.cs:362–363` (same answer in production, but the contract widens).
   - (b) Add `LabelWithFallback(key)` alongside the existing `Label(key)`. No contract change; call-site text is clumsier; future readers must learn which to call.
   - (c) Add `ResolveLabel(key)` for the fallback, keep `Label(key)` record-only. Same trade as (b), different name.

   Lean: **(a)** — the production dictionaries coincide so `RunnerRoutes` observes no change, the name reads cleanest at the ~17 call sites, and the spec's three scenarios pin the behavior regardless of name. Confirm with the implementation PR.

2. **Home for `LoadEventSummariesAsync` / `ReconcileActiveSessionsAsync`.** New `internal static class TranscriptReductions` (peer to `TranscriptPartLoader`), or methods on a new instance type, or folded into `TranscriptPartLoader` despite its "raw materials only" contract? Lean: new `TranscriptReductions` static — matches D4's "don't break the loader's contract" and keeps the reductions testable via their existing `MohistDbContext` parameter.

3. **Home for the issue-title lookup on the Issue side.** New `internal static class IssueTitleLookup`, or a method on an existing Issue read-side type? `IssueQuerier` is too heavy (full enrich surface), `IssueRowMapper` is too pure (no DB). Lean: new `IssueTitleLookup` static — narrow, testable, matches the `IssueRowMapper` precedent for "pure-ish Issue-side helper that takes a `db`".
