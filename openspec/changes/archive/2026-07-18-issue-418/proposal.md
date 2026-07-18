## Why

When one requirement spans multiple codebases there is no way today to track it as a single whole while splitting execution per codebase — you create several unrelated issues and remember in your head that they belong together. A one-level parent-child (sub-issue) relationship lets one parent issue carry the overall requirement while each child is a normal, independently executable issue (own repo, workflow, approval gates, prerequisites), with the relationship enforced by the system rather than the user. The product (`docs/sub-issues.md`) and design (`design/issue-breakdown.md`) specs are already accepted; this change closes the implementation gap for the relationship itself — composite advancement and status aggregation are explicitly later issues.

## What Changes

- **New `--parent <number>` flag on `mo issue create`** that creates a child issue. The child is a complete normal issue with a back-reference to its parent; the parent/child link is visible on both issues' detail output.
- **New `--parent <number>` / `--parent none` on `mo issue update`** that attaches an existing backlog issue as a child or detaches it. Once all children are detached the parent reverts to a normal issue that can start itself.
- **Parent is a derived fact** ("has ≥1 child"), not a new entity type; single-direction child→parent reference, one level deep.
- **Single-level constraint**: a child cannot itself have children — attempting to attach one is rejected.
- **Priority inheritance**: a child created without `--priority` inherits its parent's priority.
- **New `--parent <number>` filter on `mo issue list`** that lists only that parent's children.
- **Start refusal on parents**: an issue that has children cannot `mo issue start` directly (its delivery is the sum of its children's). Composite advancement is deferred to a later issue, so for now starting a parent is rejected outright with a typed blocker.
- **Split / attach guards**: an issue already in a workflow or in a terminal state cannot be split or attached as a child; an issue that already has children cannot be attached as someone else's child; a terminal parent cannot gain new children.
- **Epic isolation**:
  - A child issue cannot be linked to an Epic — **BREAKING** for `mo epic link`, which today succeeds for any non-closed epic and will now reject sub-issues.
  - An issue already in an Epic cannot be attached as a child (unlink from the Epic first). The parent issue remains a normal issue from the Epic's perspective.
- **Out of scope**: composite advancement / status aggregation, auto-split suggestions, multi-level trees (all explicitly deferred).

## Capabilities

- `issue-parent-child`: The sub-issue relationship on the Issue domain — establishing it (`--parent` on create and update), removing it (`--parent none`), the single-level constraint, priority inheritance on child creation, listing children (`mo issue list --parent`), the derived "is a parent" fact, and every validation tied to the relationship: refusing to start a parent that has children; refusing to split or attach an in-workflow or terminal issue; refusing to attach a has-children issue as a child; refusing to add children to a terminal parent; and the bidirectional isolation between sub-issues and Epic membership (child cannot link to Epic; Epic member cannot become a child).

## Impact

- **Server — Issue domain (`packages/server/src/Mohist.Server/Issue/Domain/`)**:
  - `Issue.cs` / `Issue.Transitions.cs` — add `ParentIssueNumber?` state + `AssignParent`/`RemoveParent` transitions enforcing single-level, in-workflow/terminal, has-children, and terminal-parent guards.
  - `Issue.Transitions.cs:160` `StartBlocker` + `IssueStartBlocker.cs` — add a new blocker variant for "has children, cannot start directly"; surfaced via `IssueRoutes.Lifecycle.cs:33-51`'s existing blocker→code mapping.
  - `IssuePriority.cs` — read path used by create for inheritance.
  - `Issue/Domain/Events/IssueEvent.cs` — add an `IssueParentChanged` event for cross-aggregate coordination (same shape as `IssueEpicChanged`).
- **Server — Issue grain & querier**:
  - `Issue/Grains/IssueGrain.cs` — parent lookups for the start guard and for create-time inheritance; emit `IssueParentChanged`.
  - `Issue/Services/IssueQuerier.cs` — `--parent` list filter; project `ParentIssueRef` + child summary onto `IssueReadModel` (alongside `PrimaryEpic`).
  - `Infrastructure/Data/Issue/IssueRow.cs` — add `ParentIssueNumber` projection column (mirrors `EpicNumber`); a backfill migration paralleling `20260715000000_BacklogIssueEpicAffiliation.cs`.
- **Server — Issue API (`packages/server/src/Mohist.Server/Api/IssueRoutes.*.cs`)**:
  - `IssueRoutes.Dtos.cs` — add `ParentIssueNumber` to `CreateIssueRequest` and `UpdateIssueRequest` (three-state via existing `Fields` set).
  - `IssueRoutes.Crud.cs` — list query param `parent`; create/update wiring.
  - `IssueRoutes.Lifecycle.cs:17` start route — new blocker code for the parent-has-children refusal.
- **Server — Epic API (`packages/server/src/Mohist.Server/Api/EpicRoutes.cs`)**: `EpicRoutes.cs:55` link path gains the "target issue is a sub-issue" rejection (per `design/issue-breakdown.md:42` the check lives at the Issue-side link entry).
- **CLI (`packages/cli/Mohist.Cli/MohistCliCommands.Issue*.cs`)**:
  - `Issue.CrudWrites.cs` — `--parent` on `create` and `update` (with `none` sentinel); priority inheritance is server-side.
  - `Issue.CrudReads.cs` — `--parent` filter on `list`.
- **Web**: detail/board surfaces already render `PrimaryEpic`; a minimal `ParentIssueRef`/child-count rendering is in scope only to satisfy "details can see the relationship". Board-card progress badges and composite-status UI are out of scope (later issue).
- **Specs**: introduces `openspec/specs/issue-parent-child/` (the Issue domain's first structural-relationship spec; the existing `openspec/specs/issue-board/` is kanban-UI only and untouched).
- **Dependencies**: none.
- **Risk**: medium — the Issue domain gains a structural relationship and new validations across create/update/start/epic-link; mitigated by the accepted design spec, the single-direction one-level model, and by reusing the existing `IssueStartBlocker` + domain-exception + event patterns.
