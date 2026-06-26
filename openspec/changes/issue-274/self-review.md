# Self Review Report

## Result: PASS

## Repaired Items

_None._ The plan artifacts are internally consistent and accurately grounded in
the codebase; no safe, in-scope repair was required.

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: consistency
  Evidence: Proposal "What Changes" #1 cites `ProjectId`/`Number`/`Status`/`IsArchived`
  as the stored-computed-column precedent. In `MohistDbContext.cs` only `ProjectId`
  (line 233), `Number` (235), and `WorkflowRunId` (239) are `stored: true`; `Status`
  (237) and `IsArchived` (241) are virtual (no `stored:` flag). The design (Decision 1)
  and `tasks.json` T-001 are precise — they mandate `stored: true` and explicitly point
  at the `ProjectId`/`Number`/`WorkflowRunId` precedent, so implementation correctness
  is unaffected. The proposal's looser phrasing describes the shared
  `json_extract`/`COALESCE`-from-`State` family rather than the storage flag, so it is
  defensible but could be tightened for precision.
  SuggestedAction: In `proposal.md` "What Changes" #1, name the stored precedent as
  `ProjectId`/`Number`/`WorkflowRunId` (or qualify "stored") to match the design and
  task notes exactly. Cosmetic; not required for correctness.
  Status: follow-up

## Review Notes

Cross-checked every codebase claim in the artifacts against source:

- N+1 site confirmed: `EpicQuerier.ListAsync` loops epics (`EpicQuerier.cs:29-30`) and
  `GetLinkedIssuesAsync` re-runs `_issuesQuery.ListAsync(projectId, all: true)` per epic
  (`EpicQuerier.cs:98`) — matches issue root cause and proposal/design.
- `EpicProgress.Build` (`EpicProgress.cs:12`) reads only `Status`, `Health`, `Priority`,
  `CanStart`, `StartBlocker`, `Number`, `Id`, `Title` — confirms the four derived columns
  (`Title`/`Priority`/`IsDraft`/`PrerequisiteNumbersJson`) are sufficient and that
  `Stage`/`ExternalPrerequisites`/`PrerequisiteNumbers` can be defaulted on the list path
  without affecting progress output.
- Frontend claim confirmed: `EpicListPage.tsx:60` reads `progress.activeIssues[0]` only,
  never `blockedIssues` — justifies the list-path Health approximation.
- Stored-computed-column precedent confirmed (`MohistDbContext.cs:232-239`); the four
  proposed columns follow the identical `COALESCE(camel, Pascal)` form with `stored: true`.
- `EpicProgress.IsCompleted` treats `"done"`/`"completed"` as delivered
  (`EpicProgress.cs:40`); spec/task wording ("done or completed") aligns. `cancelled` is
  excluded from delivery and next-issue selection — matches spec scenarios and T-002 ACs.
- Reuse targets exist: `EpicGrain.BuildLinkedIssueDtosAsync` (`EpicGrain.cs:445-481`),
  `EpicQuerier.BuildExternalPrerequisites`, `EpicQuerierExternalPrerequisitesSpecs`, and
  `EpicAutoDoneHandlerSpecs` (constructs `EpicQuerier` with null `IssueQuerier`) — all
  referenced accurately by T-002 notes.

Coverage matrix (issue requirement → spec requirement → task):

| Issue requirement | Spec requirement | Task |
|---|---|---|
| 4 stored computed columns | "Issue derived columns mirror the State JSON" | T-001 |
| Single aggregate SQL, no N+1 | "Epic list query issues a single aggregate SQL" | T-002 |
| Drop WorkflowRuns.State/Comments/Attachments/agent-config | "Epic list path avoids full issue enrichment" | T-002 |
| nextIssue/CanStart from new columns | "Epic list next-issue and CanStart correctness is preserved" | T-002 |
| Exact progress preserved | "Epic list progress correctness is preserved" | T-002 |
| Health approximated (list) / exact (detail) | "Epic list Health is approximated..." | T-002 |
| DTO/frontend unchanged | (preserved by construction; EpicProgress.Build untouched) | T-002 |

No issue requirement is missing or misinterpreted; every spec requirement has a task;
every task has correct `dependsOn` (T-001 → [], T-002 → [T-001]) with strictly lower
priority and no cycle. Task granularity is appropriate — each task is one cohesive
feature slice (data layer; query layer) with tests embedded as acceptance criteria
rather than split into micro-tasks; no "define interface"/"register DI"/standalone
"add tests" tasks exist.

<promise>PASS</promise>
