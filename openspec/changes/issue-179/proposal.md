## Why

Closing an epic today destroys the record of which issues it contained: `EpicGrain`'s close path removes every `EpicIssueRow`, so the membership history is lost. Closing should mean "stop advancing this epic", not "forget its scope". At the same time, a closed/done epic currently holds a permanent lock on its issues via the cross-aggregate uniqueness check (`DUPLICATE_EPIC_MEMBERSHIP`), so an issue that belonged to a finished epic can never be re-homed into a new active epic. Both behaviours make epic membership needlessly destructive and rigid.

## What Changes

- **Close becomes non-destructive**: closing an epic retains all its `EpicIssueRow` links. Close only marks the epic terminal and emits `EpicClosed`; it SHALL NOT unlink issues. **BREAKING** (behavioural): the post-close membership set changes from empty to preserved.
- **Uniqueness invariant refined** to "an issue belongs to **at most one non-terminal (active) epic**" — i.e. at most one `idle`/`running`/`paused` epic. Existing membership in a terminal epic (`done`/`closed`) SHALL NOT count as a conflict and SHALL NOT block linking the issue to a new `idle`/`running`/`paused` epic.
- `LinkIssueAsync`'s duplicate check becomes epic-status-aware: it queries the existing link's owning epic and rejects only when that owner is non-terminal.
- Explicit `unlink` (remove a single link) remains available and unchanged.
- `primaryEpic` projection points at an issue's non-terminal epic membership; an issue with only terminal-epic memberships projects no `primaryEpic`.
- Epic progress/history remains readable after close because the links (and therefore the issue set) are retained.

## Capabilities

### New Capabilities
- `epic-issue-membership`: Governs the issue↔epic membership relationship — linking an issue to an epic, explicit unlink, the cross-aggregate uniqueness invariant (at most one non-terminal epic per issue), link retention across the epic's close transition, and the `primaryEpic` projection rule (issue's active epic membership, or none).

### Modified Capabilities
- `epic-lifecycle`: The Close requirement gains an explicit non-destructive clause — closing an epic transitions it to `closed` and halts advancement, but SHALL retain its linked-issue membership (history is preserved). No other lifecycle transition changes.

## Impact

- **Server / Epic domain** (`packages/server/src/Mohist.Server/Epic/`):
  - `EpicGrain.ApplyPendingEvents` (the `RemoveAllLinkedIssues` side-effect tied to `EpicClosed`) is removed; close no longer touches `EpicIssueRow`.
  - `EpicGrain.LinkIssueAsync` duplicate check joins the existing link to its owning `EpicRow.Status`; `DUPLICATE_EPIC_MEMBERSHIP` is raised only when the existing owner is non-terminal (`idle`/`running`/`paused`). A terminal owner does not block re-linking.
  - `UnlinkIssueAsync` unchanged in behaviour (single-link removal still available).
- **Server / Issue read-model** (`packages/server/src/Mohist.Server/Issue/Services/`):
  - `IssueQuerier` `primaryEpic` projection (`IssueQuerier.cs:1258-1281`) must select the issue's non-terminal epic membership; with only terminal memberships, `primaryEpic` is left null.
- **Epic progress / history**: `EpicProgress`, list, and detail reads continue to work because links persist after close — verify no code path assumed links were empty post-close.
- **DB schema change (index relaxation)**: the unique index `IX_EpicIssues_ProjectId_IssueId` (`MohistDbContext.cs:221`, `IsUnique()`) is relaxed to non-unique via an EF Core migration, so a retained terminal-epic membership and a new non-terminal membership can coexist for the same issue. Existing terminal epics have empty link sets today (pre-change behaviour deleted them), which remains valid; the relaxation only enables the new re-homing write path. The "at most one non-terminal epic per issue" invariant is then enforced in application code (`LinkIssueAsync`). See `design.md` D2.
- **Tests** (Fake-based, per project rules): (a) close keeps links, (b) link to a new active epic while held by a terminal epic succeeds, (c) link to a second non-terminal epic still raises `DUPLICATE_EPIC_MEMBERSHIP`, (d) unlink single link still works, (e) progress/history readable after close, (f) `primaryEpic` reflects non-terminal membership.
- **Web/CLI**: No contract change required; UI surfaces the preserved links on closed epics naturally. No Done/reopen semantics touched (out of scope).
