## Why

The Integrate delivery — the work that lands an issue's changes on the base branch — is one opaque `integrate:merge` task today. Conflict resolution, the expensive agent-driven work of reconciling the issue branch with the base branch, runs *inside* that task but is presented as if it were tracked work, so the task list is not truthful: conflict resolution cannot be seen, retried, or budgeted on its own. Because the whole delivery (prepare, resolve conflicts, land, publish) is one task, a failure also hides *what kind* of failure occurred — the user cannot tell whether the base branch simply moved, a real conflict needs attention, or a transient issue is to blame — so the right next action is not obvious and a failed attempt can leave the workspace in a messy state. Splitting delivery into real, observable tasks makes every task-list entry a genuine unit of work and makes failures honest and recoverable.

## What Changes

- **Split the single `integrate:merge` task into two visible tasks:**
  - A **prepare** task (`integrate:prepare`): bring the issue branch up to date with the latest base branch (rebase-based reconciliation) and resolve any conflicts.
  - A **publish** task (`integrate:publish`): land the issue's changes as a single commit on the base branch and push it to the remote.
  Each task has an obvious success and failure meaning, instead of one opaque task doing everything.
- **Make conflict resolution a first-class, visible task.** Resolving conflicts between the issue branch and the base branch happens only in the prepare task — visible in the task list and retryable on its own. It is no longer a hidden sub-step loop buried inside the merge action.
- **Classify delivery failures into actionable kinds** so the failure tells the user the *kind* of problem and the obvious next action: safe to just retry, the base branch moved so the branch needs preparing again, or a conflict needs attention.
- **A failed delivery attempt leaves the workspace clean.** Neither task leaves a half-finished merge/rebase or dirty tree behind on failure.
- **Keep the publish step cheap under contention.** When the base branch moves between prepare and publish, the publish step re-attempts cheaply (fast-forward / re-land); it must not silently repeat expensive conflict-resolution work in a loop.
- **Single owner for pushing to the remote.** Only the publish task pushes to the remote; prepare never does.
- **No change to the user-visible outcome.** An issue's changes still land on the base branch as a single commit and are pushed to the remote. This is a re-shaping of *how* delivery is tracked and reported, not a change to what users configure or what lands.

## Capabilities

### New Capabilities
- `merge-delivery`: The contract for what the Integrate delivery is responsible for and how its failures are distinguished. Today no spec governs this — delivery lives as un-spec'd runner behavior in the `mohist/merge` action and is referenced only indirectly by `workflow-definition`/`workflow-run`. This capability defines (a) the prepare responsibility (rebase the issue branch onto the latest base branch and resolve conflicts as the single place conflict resolution ever happens during delivery), (b) the publish responsibility (land the already-prepared change as one commit on the base branch and push to the remote, cheaply re-attempting when the base moves), and (c) the distinguishable, actionable delivery failure kinds a user can tell apart and act on. Becomes `specs/merge-delivery/spec.md`.

### Modified Capabilities
- `workflow-definition`: The default Integrate stage definition changes from a single `integrate:merge` task to an ordered `integrate:prepare` task followed by an `integrate:publish` task, with publish as the single owner for pushing to the remote. The existing `integrate:spec-sync` → `integrate:archive-change` ordering is preserved ahead of delivery.
- `workflow-run`: REQ-WR-005 and its scenarios enumerate `integrate:merge` as the delivery task and record merge delivery metadata plus a post-merge freeze point. These SHALL be updated to reflect the `integrate:prepare` + `integrate:publish` task split and their distinct delivery facts (e.g. prepared-at base, published commit, push ownership), while preserving post-merge freeze semantics.

## Impact

- **Workflow definition** (`packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml`): the Integrate `tasks` block replaces the single `mohist/merge` task with a `mohist/prepare` (rebase + first-class conflict resolution) task and a `mohist/publish` (land-as-one-commit + push) task, in that order, after `integrate:archive-change`. The Integrate `health` check and `lockBehavior: sequential` / `project-integration` resource ownership are unchanged.
- **Runner actions** (`packages/runner/src/actions/registry.ts`, `packages/runner/src/actions/rebase.ts`): the monolithic `mergeAction` (which inlines conflict resolution in a retry loop and performs the squash commit) is split. Prepare reuses the existing rebase + conflict-resolution machinery so conflict resolution is the task's visible purpose; publish performs the cheap land-as-one-commit and remote push, re-attempting cheaply when the base moved and never re-running conflict resolution. The `mohist/merge` registration is replaced by `mohist/prepare` and `mohist/publish`.
- **Failure reporting (CLI and Web UI):** delivery failures surface the actionable failure kind (retry-safe / base-moved-needs-reprepare / conflict-needs-attention) rather than one opaque merge failure, across the existing task/log/evidence surfaces.
- **Workflow-run evidence** (`packages/server/...`, `workflow-run` spec): Integrate StageRun seeding and delivery/freeze facts reflect prepare + publish.
- **No user-visible configuration change:** issues still land on the base branch as a single commit and are pushed to the remote; the Check-stage `merge-ready` squash-mergeability preflight (in `workflow-engine`) is unchanged in meaning.
