# Review

## Findings

### 1. Parent prerequisites are bypassed when starting composite advancement

`IssueGrain.StartWorkAsync` loads the parent's undelivered prerequisites but calls `ThrowIfStartBlocked` before deciding whether the issue has children. However, `StartCompositeAsync` only checks `IsDraft`; it does not receive or validate those prerequisites. As a result, a parent with an unmet external prerequisite can still be marked `InProgress` and fan out work to its children through `StartCompositeAsync` after the start path reaches it. This violates the issue's domain-model requirement that a parent's external prerequisites gate composite advancement.

Location: `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:145-162`, `380-401`.

The parent composite path must enforce the same prerequisite gate as a normal issue before applying `MarkCompositeStarted` or starting any child. Add a regression spec for a parent blocked by an external prerequisite, asserting that the start is rejected and no child workflow is created.

### 2. A reopened child cannot return a Done parent to InProgress

`RecomputeCompositeStatusAsync` correctly calculates `InProgress` for a Done parent with a reopened Backlog child, but `ApplyCompositeTransition` maps that target to `MarkCompositeStarted`. `MarkCompositeStarted` only permits `Backlog -> InProgress` and throws when the parent is already `Done`. Therefore the durable `IssueCompositeChildReopenedHandler` fails its recompute and the parent remains Done, contrary to the required Done-parent child-reopen lifecycle rule.

Location: `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:433-440`, `546-560`; `packages/server/src/Mohist.Server/Issue/Domain/Issue.Transitions.cs:285-297`.

Introduce a distinct aggregate transition for `Done -> InProgress` (emitting `IssueCompositeStatusChanged`), select it from `ApplyCompositeTransition`, and cover reopen of both Done and Cancelled children of a Done parent through the durable event path.

### 3. Composite child selection treats prerequisites outside the sibling set as permanently unsatisfied

`TryStartChildrenAsync` builds `allDoneNumbers` only from children in the current parent snapshot. `IsStartableForComposite` then requires every prerequisite to be in that set. A child whose prerequisite is an already-Done independent issue outside the parent is therefore skipped forever; its prerequisite completion event has no `parent` lineage and cannot trigger another composite recompute. This violates the startability requirement that every prerequisite issue be Done, not merely that it be a Done sibling.

Location: `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:474-521`.

Startability must resolve all prerequisite statuses project-wide, using the same source of truth as direct starts, while retaining sibling fan-out behavior. Add coverage for a child depending on an already-Done non-child issue and for a non-child prerequisite completing after the parent has started.

### 4. Archived children are excluded from parent status aggregation, so archiving a child can leave the parent semantically wrong

Both `StartCompositeAsync` and `RecomputeCompositeStatusAsync` call `LoadCompositeChildrenAsync()` with its default `includeArchived: false`. An archived child is thus removed from the snapshot used to derive the parent status. The specification defines status from the current states of all children and only exempts archived children from the archive cascade's repeated work; it does not say archiving detaches a child. For example, a parent with a Done child and a Cancelled child can be archived after cascading. If it is later unarchived, recompute sees zero non-archived children and leaves the parent status unchanged rather than deriving it from its still-attached children. Partial archival similarly changes terminal-state totals incorrectly.

Location: `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:387`, `424`, `444-471`.

Use a complete attached-child snapshot for aggregation and lifecycle validation; reserve filtering archived children for only the fan-out set. Add a regression spec covering status recompute after archiving/unarchiving an attached child or parent.

<promise>FAIL</promise>
