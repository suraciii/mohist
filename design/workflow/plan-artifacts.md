# Plan Artifacts

The default Workflow separates free-form planning from machine execution
without inventing a new concept for it. Everything the plan Stage produces is
a run artifact; exactly one of those artifacts, `tasks.json`, is
machine-readable and expands into the Build Stage's tasks.

## The Task List

`PLANS/tasks.json`:

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

- Machine-consumed: `id`, `title`, and array order. `mohist/task-list` expands
  each entry into one Agent Task in array order through the existing
  `addTasks` mechanism; the execution Action is fixed by the Profile for every
  generated Task.
- Prompt-rendered: `goal`, `acceptance`, `refs`. The Workflow never verifies
  acceptance mechanically. Verification lives in the Build Stage's explicit
  verify Tasks, the Check Stage's evidence, and the approver's judgment.
- No other fields. There is no per-task `expect`, `uses`, priority, type,
  mode, or dependency graph. A previous schema carried eight fields the engine
  never read and rendered ordering metadata as prompt text; the schema now
  matches consumption.

The task list is an ordinary artifact file that the engine happens to read.
Its evidence and audit role is carried by the WorkflowArtifact entity; its
expansion is carried by `addTasks`; its persistence is the Workspace
directory. No dedicated entity, channel, or lifecycle exists for it.

### Why no mechanical per-task assertions

Per-task `expect` markers are brittle (a promise-marker parse was already the
fragile part of self-review) and duplicate the Stage's real verification. Hard
checks belong in explicit verify Tasks that run real tests, not in generated
file-existence assertions.

## Named Artifacts

The Workflow binds four files as Task artifacts so the approver and later
Stages have stable evidence:

- `PLANS/PLAN.md` — interpretation, scope, approach; the Plan Approval Point
  document.
- `PLANS/DESIGN.md` — technical decisions and rationale. The file always
  exists; when no separate design is needed, it records that conclusion and
  why.
- `PLANS/REVIEW.md` — the Check Stage's review evidence.
- `PLANS/tasks.json` — the task list above.

Everything else under `PLANS/` and `RESEARCH/` is the Agent's own
organization and is invisible to the Workflow.

## Persistence and Recovery

A WorkflowRun is pinned to one Runner and its Workspace persists across
Stages, so the Build Stage reads the task list from the filesystem on the
happy path; no artifact round-trip exists anywhere.

Artifact upload serves exactly one purpose: evidence and audit. It is not an
execution channel. A lost Workspace directory is an accepted loss (see
[`../workspaces.md`](../workspaces.md)): unpushed Repository work and
Workspace-local plan material are both gone, and the recovery is the existing
`mo run rerun --from-stage plan`, which regenerates both. No artifact fetch
or restore channel exists.

## Review at an Approval Point

The Workflow has exactly one review mechanism: the Approval Point with its
Approval Feedback sequence.

- The plan Stage has no self-review Task. A same-session self-verdict
  duplicated the Plan Approval Point with the same model and added a second
  repair loop.
- The Check Stage still runs an independent review in its own Session, but it
  produces `REVIEW.md` as evidence. There is no verdict marker, no PASS/FAIL
  gate, and no auto-fix recovery loop; the verdict belongs to the approver. A
  repair requested by the approver goes through the configured Feedback Tasks,
  which are the single repair path. See
  [`definition.md`](definition.md#approval-feedback) for the authoritative
  sequence and ownership rules.
- Approval Feedback supports unlimited Request Changes cycles. State retains
  only a bounded window of up to 10 feedback entries. Recovery loops are
  unattended and keep their declared budgets.

## Integrate: Auto-merge

Integrate enables GitHub auto-merge on the approved Pull Request. The
registration Action then waits until GitHub performs the merge. One attempt
has a fixed 30-minute absolute deadline covering every external operation and
retry delay, including subject selection and ambiguous-registration
reconciliation. An explicit squash subject wins; otherwise the Action uses the
PR title returned by the bounded PR read. Merge timing and merge-time
prerequisites are arbitrated by GitHub, which removes the
`base-moved` and `protection-conflict` recovery branches that the synchronous
merge required. A required check that fails after Approve is classified
`pr-checks-failed` and repaired through the same declared recovery the Check
Stage uses; a merge conflict is classified `conflict` and follows the rebase
recovery. Enabling auto-merge on a Repository that disallows it is an ordinary
Task failure (`auto-merge-unavailable`). `retry-safe` makes a later explicit
retry valid but does not trigger unattended recovery; cancellation remains
cancellation. The one-shot `github-pr-status` Stage Check with `expect: merged`
remains as post-hoc verification.

`mohist/merge-github-pr` is removed with this change; no consumer remains.

## Prompt Realignment

Built-in prompts follow the same boundary. Removed: `proposal`, `specs`,
`design`, `tasks`, `self-review`, `fix-plan-review`, `auto-fix`. Added:
`plan` (produces the named artifacts, including the task list) and
`build-task` (base prompt for generated Tasks). Repurposed: `review` writes
evidence without a verdict marker; `apply-feedback`, `fix-ci`,
`fix-pr-checks`, and `resolve-rebase-conflicts` keep their roles with `PLANS/`
paths.

## Web Evidence Surface

The Check Approval Point UI currently parses `review.md` task output
(`ReviewSummary`, `ReviewReportModal`). It is rebound to the recorded
`REVIEW.md` artifact, and the Plan Approval Point surface presents `PLAN.md`
and the task list the same way, so each Approval Point shows the artifacts of
its own Stage.

## Out of Scope

- A Profile-declared automatic reviewer (`reviewer: agent`). An Approval Point
  is a judgment position; who fills it — a person, an external Agent, or
  automation — stays outside the engine. This may be revisited after the
  simplified Workflow has production mileage.

## Companion Requirement

Plan and review evidence lives only as uploaded run artifacts. The Web
artifact surface already exists; a CLI read path (`mo run artifact list/get`)
is a companion requirement, because delegated approvers work through the CLI
and previously read plan files from the Workflow branch.
