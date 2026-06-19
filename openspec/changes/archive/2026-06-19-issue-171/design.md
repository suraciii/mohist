## Context

The Epic board (list + detail) is the triage surface for long-running goals, but its read model and presentation are broken in ways that make it unusable for deciding what to manage now:

- **Ordering**: `EpicQuerier.ListAsync` sorts each status group by `CreatedAt` (`EpicQuerier.cs:26`), so a P0 epic renders below a P2. The frontend renders groups in server order, so the fix belongs server-side.
- **Progress counting bug**: `EpicProgress.Build` derives `blockedIssues`/`activeIssues` by comparing `i.Status == "blocked"/"active"` (`EpicProgress.cs:14-15`). But `Status` is the execution lifecycle (`backlog|in_progress|done|cancelled`); `active`/`blocked` are **`Health`** values (runtime projection in `MohistDefaultWorkflowProjection.RuntimeStatus`). The comparison can never match, so counts are permanently 0.
- **nextIssue**: selected as `linked.FirstOrDefault(!IsCompleted)` — first undelivered by insertion order, ignoring priority, dependencies, and startability. It routinely points at an unstartable issue.
- **Presentation**: description rendered as a `whitespace-pre-wrap` `<p>` (`EpicDetailPage.tsx:385`) so raw `##`/list markers leak; card status text (`EpicListPage.tsx:91-100`) shows "Ready to mark done" regardless of epic status, including on Done/Closed epics; "Current Activity" shows bare counts.

**Critical shared-function constraint**: `EpicProgress.Build` is a pure function reused by `EpicGrain.IsReadyToMarkDoneAsync` (`EpicGrain.cs:133-141`) and `CountUndeliveredAsync`. There are two `LinkedIssueDto` build paths:
1. **Read path** — `EpicQuerier.GetLinkedIssuesAsync` builds from `IssueQuerier.ListAsync` → `IssueInfo`, which carries the full runtime `Health`, `CanStart`, and `Blocker`.
2. **Grain path** — `EpicGrain.BuildLinkedIssueDtosAsync` builds from `db.Issues` rows and computes `Health` via the `IssueStatus`-only overload of `MohistDefaultWorkflowProjection.Health` (no workflow/attention context, no `CanStart`/`Blocker`).

The grain path feeds only mark-done/undelivered counting, which read **delivered counts** only — never active/blocked/nextIssue. This asymmetry is the central design constraint.

See `proposal.md` for motivation and `specs/epic-board/spec.md`, `specs/epic-tracking/spec.md` for requirements.

## Goals / Non-Goals

**Goals:**
- Server-side epic list ordering: priority ascending (P0→P4), then `updatedAt` descending.
- Correct active/blocked progress counting from `Health`; enriched active/blocked DTOs carrying `{id, number, title, health}`.
- `nextIssue` = highest-priority **startable** issue (`CanStart`, no `Blocker`), with a human-readable reason when none is startable.
- List cards show in-progress + next; status text branches by epic status; Done/Closed groups collapse by default.
- Detail "Current Activity" lists concrete in-flight issues with health color + navigation; description rendered via shared `MarkdownReader`.
- Preserve `IsReadyToMarkDone` semantics exactly.

**Non-Goals:**
- Epic paused/blocked lifecycle states (issue #173).
- Transitive dependency / critical-path ordering for `nextIssue` (only direct `Blocker`).
- Changing issue-side `Health`/`CanStart` computation (consume only).
- List search/filter/pagination; Create/Edit epic Markdown input hints.
- Including `attention`/`paused`/`queued` healths in active/blocked counts.

## Decisions

### Decision 1: Keep `EpicProgress.Build` a single shared pure function; carry startability optionally on `LinkedIssueDto`

Extend `LinkedIssueDto` with `bool CanStart` and `IssueStartBlockerDto? StartBlocker`, **nullable/defaulted**. The read path populates them from `IssueInfo`; the grain path leaves them default (null/false → treated as not-startable). `ReadyToMarkDone` continues to equal `linked.Count > 0 && all delivered`, independent of nextIssue/counting.

- *Rationale*: one function = one source of truth for progress math; the grain path needs none of the new fields because mark-done reads only delivered counts. Optional fields avoid forcing the grain path to load prerequisite data just to compute a value it discards.
- *Alternative considered*: split into `BuildForRead` / `BuildForMarkDone`. Rejected — duplicates the delivered/total math and invites drift on the exact `ReadyToMarkDone` invariant this change must protect.
- *Alternative considered*: have the grain path also load `CanStart`/`Blocker`. Rejected — extra DB/prereq work for a value mark-done never reads.

### Decision 2: Count active/blocked strictly from `Health`

`blockedIssues` ← `health == "blocked"`; `activeIssues` ← `health == "active"`. Both as enriched `EpicProgressIssueDto(Id, Number, Title, Health)` entries. `attention`/`paused`/`queued`/`done`/`cancelled` are intentionally excluded from both sets.

- *Rationale*: the field names map directly to the two `Health` values the UI already colors; broadening would inflate "active" with attention-needed work. Correctness holds only on the read path (full runtime `Health`); the grain path's IssueStatus-only `Health` never yields "blocked", but mark-done does not read these sets.

### Decision 3: `nextIssue` selection and fallback

Selection: from linked issues where `!IsCompleted && CanStart && StartBlocker is null`, pick the highest priority (P0→P4); tiebreak by `Number` ascending for determinism. If the set is empty and undelivered work remains, set `NextIssue = null` and populate a new `NextIssueReason` (string?) derived from the highest-priority non-startable issue's `StartBlocker` (`Draft` → e.g. "Still a draft"; `WaitingFor` → "Waiting on #{Number}"). When all delivered, `NextIssueReason` is null and `ReadyToMarkDone` is true.

- *Rationale*: priority + startability matches what a user can actually advance; the reason gives actionable context instead of a silent null. Stable tiebreak prevents non-deterministic UI order.
- *Alternative considered*: a polymorphic `NextIssue` union (issue | reason). Rejected — a sibling `NextIssueReason` string is simpler to serialize/deserialize and keeps `NextIssue`'s shape stable.

### Decision 4: Enriched active/blocked entries as a dedicated DTO

Introduce `EpicProgressIssueDto(string Id, int Number, string Title, string Health)` for `BlockedIssues`/`ActiveIssues` (replacing `IReadOnlyList<string>`).

- *Alternative considered*: reuse `EpicNextIssueDto`. Rejected — next-issue vs in-flight have different semantics; a named type keeps the contract legible.

### Decision 5: Server-owned list ordering

In `EpicQuerier.ListAsync`, replace `OrderBy(CreatedAt)` with: priority ordinal (map `p0`=0 … `p4`=4, unknown→9) ascending, then `UpdatedAt` descending. The frontend keeps rendering groups in the received order and performs no re-sort.

- *Rationale*: single source of truth; the frontend already consumes array order. Matches the spec's "consumers render in server-supplied order".
- *Alternative considered*: sort client-side. Rejected — duplicates logic and diverges from the read-model contract.

### Decision 6: Frontend presentation reuses existing primitives

- Group collapse: local component state; Active expanded, Done/Closed collapsed by default.
- Card status text: branch on `epic.status` (Active → in-progress/next/ready; Done → completion phrase; Closed → closed phrase). "Ready to mark done" only when `status === Active`.
- Card in-progress + next: in-progress from `progress.activeIssues[0]` (or "N in progress"); next from `progress.nextIssue`/`nextIssueReason`.
- Detail "Current Activity": list `activeIssues` and `blockedIssues` entries with health color (`StatusBadge`-style mapping already present in `EpicDetailPage.tsx:66`) and `<Link>` to the issue.
- Description: replace the `<p whitespace-pre-wrap>` with `<MarkdownReader content={epic.description} />` (imported from `@/shared/ui`, same as `IssueDetailPage.tsx:21`). No new component.

## Risks / Trade-offs

- **[EpicProgress.Build shared with mark-done]** → Mitigation: `ReadyToMarkDone` stays pure delivered-count; add a regression test asserting mark-done is unchanged when the highest-priority undelivered issue is not startable. Guard with the existing `EpicLifecycleSpecs` mark-done scenarios.
- **[Grain path lacks `CanStart`/runtime `Health`]** → Mitigation: new `LinkedIssueDto` fields are optional; grain path omits them; nextIssue is unused there. Document that active/blocked correctness is a read-path property only.
- **[JSON contract change for `BlockedIssues`/`ActiveIssues` (`string[]` → object[])]** → Mitigation: server and web ship together; no external consumers. Update the private DTO mirrors in `EpicLifecycleSpecs.cs:305-307` and any `EpicApiSpecs` assertions.
- **[`attention`/`paused` excluded from counts]** → Trade-off: accepted; matches field semantics. Users with attention-needed issues won't see them under "active" — they remain visible in the linked-issues list.
- **[nextIssue tiebreak chosen as Number ascending]** → Trade-off: stable but not "most recent"; deterministic UI preferred over recency heuristics.
- **[Health counting depends on runtime projection availability]** → Mitigation: read path already populates full `Health`; no extra computation added.

## Migration Plan

- **No data/schema migration** — all changes are read-model + presentation; no persisted shape changes.
- **Deploy order**: server and web are coupled by the `EpicProgress` JSON contract, so deploy them together (the normal monorepo release). There is no partial-deploy window that matters because the old frontend reads `.length` on the old `string[]` — a server-only deploy first would break the counts until the web ships.
- **Rollback**: revert both server and web commits; no data cleanup required.
- **Test updates**: update `EpicLifecycleSpecs.cs` and `EpicApiSpecs.cs` DTO mirrors; add cases for priority ordering, Health-based counting, startable nextIssue, and the nextIssue reason fallback; add a mark-done regression test.

## Open Questions

- Confirm the equal-priority tiebreak for `nextIssue` (proposed: `Number` ascending) vs. `updatedAt` — picking Number for determinism since issue rows always have it.
- Confirm desired wording for Done/Closed card phrases and the `NextIssueReason` strings (e.g. "Waiting on #N" vs. "Blocked by #N") during UI review.
