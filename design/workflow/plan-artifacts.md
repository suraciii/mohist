# Plan Artifacts

The default Workflow separates free-form planning from machine execution without creating a
second planning concept. Everything produced by Plan is a run artifact. Exactly one artifact,
`PLANS/tasks.json`, is machine-readable and expands into Build Tasks.

## Design Drivers

- The Agent needs freedom to organize planning material, while the Workflow needs one stable
  machine input.
- Evidence must remain inspectable without becoming an execution channel.
- Plan and Check need one Approval Point mechanism, not parallel self-review and repair
  protocols.
- A rebuildable Workspace requires explicit recovery for Repository work and an accepted loss
  boundary for local plan material.

## Model

Plan has one execution path and one evidence path:

```text diagram
                      +------------+    +-------+
                  +-->| tasks.json +--->| Build |
+------+          |   +------------+    +-------+
| Plan +----------+
+------+          |   +-----------+     +-------------------+
                  +-->| Artifacts +---->| Approval evidence |
                      +-----------+     +-------------------+


+----------------+    +------------+
| Workspace loss +--->| Rerun Plan |
+----------------+    +------------+
```

`tasks.json` is the machine path. Other artifacts are the evidence path. Workspace loss returns
execution to Plan rather than restoring an artifact into Build.

### The Task List

`PLANS/tasks.json` has this shape:

```json
{
  "tasks": [
    {
      "id": "T-001",
      "title": "Extract the notification-channel abstraction",
      "goal": "What to implement and why, in a few sentences.",
      "acceptance": ["verifiable criterion"],
      "refs": ["PLANS/DESIGN.md#abstraction"]
    }
  ]
}
```

The engine consumes only `id`, `title`, and array order. `mohist/task-list` expands each entry
into one Agent Task in array order through the existing `addTasks` mechanism. The Profile fixes
the execution Action for every generated Task.

The Agent receives `goal`, `acceptance`, and `refs` as prompt material. The Workflow does not
verify acceptance mechanically. Build verify Tasks, Check evidence, and the approver's judgment
provide verification.

The schema has no other fields. It has no per-task `expect`, `uses`, priority, type, mode,
or dependency graph. Ordering metadata is not rendered as prompt text.

The task list is an ordinary artifact file. WorkflowArtifact carries its evidence and audit
role. `addTasks` carries its expansion. The Workspace directory carries its persistence. No
dedicated entity, channel, or lifecycle exists for the task list.

### Why no mechanical per-task assertions

Per-task `expect` markers duplicate Stage verification and make self-review depend on
promise-marker parsing. Hard checks belong in explicit verify Tasks that run real tests, not in
generated file-existence assertions.

### Named Artifacts

The Workflow binds four files as Task artifacts so Approval Points and later Stages have stable
evidence:

- `PLANS/PLAN.md`: interpretation, scope, approach, and the Plan Approval Point document.
- `PLANS/DESIGN.md`: technical decisions and rationale. It always exists. When no separate design is
  needed, it records that conclusion and why.
- `PLANS/REVIEW.md`: Check Stage review evidence.
- `PLANS/tasks.json`: the machine-readable task list.

Everything else under `PLANS/` and `RESEARCH/` remains the Agent's organization. The Workflow
does not consume it.

## Semantics

### Persistence and Recovery

A WorkflowRun remains pinned to one Runner and its Workspace persists across Stages. On the
happy path, Build reads `PLANS/tasks.json` from the Workspace filesystem. No artifact round-trip is used
for execution.

Artifact upload serves evidence and audit only. It is not an execution channel. If the Workspace
directory is lost, unpushed Repository work and Workspace-local plan material are both lost.
Recovery uses `mo run rerun --from-stage plan`, which regenerates both. No artifact fetch or restore channel exists.
See [`../workspaces.md`](../workspaces.md).

### Review at an Approval Point

The Workflow has one review mechanism: an Approval Point with its Approval Feedback sequence.

- Plan has no self-review Task. A same-session self-verdict would duplicate the Plan Approval
  Point and add a second repair loop.
- Check runs an independent review in its own Session and records evidence in `PLANS/REVIEW.md`. It has
  no verdict marker, PASS/FAIL gate, or auto-fix recovery loop. The approver owns the verdict.
- A Request Changes decision uses the configured Feedback Tasks as the single repair path. See
  [`definition.md`](definition.md#approval-feedback).
- Approval Feedback permits unlimited Request Changes cycles. State retains at most 10 feedback
  entries. Unattended recovery loops keep their declared budgets.

### Integrate: Auto-merge

Integrate enables GitHub auto-merge on the approved Pull Request. The registration Action waits
until GitHub reports the merge.

One attempt has a fixed 30-minute absolute deadline covering subject selection, every external
operation, ambiguous-registration reconciliation, and retry delays. An explicit squash subject
wins. Otherwise the Action uses the Pull Request title from its bounded read.

GitHub arbitrates merge timing and merge-time prerequisites. The Workflow therefore has no
`base-moved` or `protection-conflict` recovery branches. A required check that fails after approval is
`pr-checks-failed` and uses the Check recovery. A merge conflict is `conflict` and uses rebase recovery.
Enabling auto-merge on a Repository that disallows it is an ordinary Task failure, `auto-merge-unavailable`.

`retry-safe` permits a later explicit retry. It does not authorize unattended recovery.
Cancellation remains cancellation. The one-shot `github-pr-status` Stage Check with `expect: merged` remains
post-hoc verification.

`mohist/merge-github-pr` is removed. No consumer remains.

### Prompt Realignment

Built-in prompts follow the artifact boundary:

- Removed: `proposal`, `specs`, `design`, `tasks`, `self-review`, `fix-plan-review`, and `auto-fix`.
- Added: `plan`, which produces the named artifacts including the task list, and `build-task`,
  which is the base prompt for generated Tasks.
- Repurposed: `review` writes evidence without a verdict marker. `apply-feedback`, `fix-ci`,
  `fix-pr-checks`, and `resolve-rebase-conflicts` retain their roles with `PLANS/` paths.

### Web Evidence Surface

The Check Approval Point UI reads the recorded `REVIEW.md` artifact rather than Task output. The
Plan Approval Point surface presents `PLAN.md` and the task list. Each Approval Point therefore
shows the artifacts produced by its own Stage.

### Out of Scope

A Profile-declared automatic reviewer such as `reviewer: agent` is outside this contract. An Approval
Point is a judgment position. Whether a person, External Agent, or automation supplies that
judgment remains outside the engine.

### Companion Requirement

Plan and review evidence exists only as uploaded run artifacts. The Web artifact surface exists.
Delegated approvers also need `mo run artifact list/get`, because they previously read plan files from the
Workflow branch.

## Status

The default Workflow uses `tasks.json` as its only machine-readable planning input. Named plan and
review artifacts are uploaded for Approval Point evidence, while Workspace loss is recovered by
rerunning from Plan. Auto-merge and prompt realignment follow the boundaries above.
