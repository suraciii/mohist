---
status: proposed
---

# Plan Handoff

The default Workflow separates free-form planning from machine execution at a
single contract: the handoff file. The plan Stage's Agent may organize
planning and design material freely under the Workspace `PLANS/` and
`RESEARCH/` directories; the Workflow reads only the handoff file. This
replaces the previous arrangement in which OpenSpec was the Workflow's
implicit protocol: a fixed change directory, fixed file names, three dedicated
Actions (`openspec-tasks`, `openspec-task-prompt`, `openspec-artifacts`), and
an `archive-change` Task that committed plan material into the Repository.

## Contract

`PLANS/issue-<number>.handoff.json`:

```json
{
  "tasks": [
    {
      "id": "T-001",
      "title": "Extract the notification-channel abstraction",
      "goal": "What to implement and why, in a few sentences.",
      "acceptance": ["verifiable criterion"],
      "refs": ["PLANS/issue-5-DESIGN.md#abstraction"]
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
  never read and rendered ordering metadata as prompt text; the contract now
  matches consumption.

### Why no mechanical per-task assertions

Per-task `expect` markers are brittle (a promise-marker parse was already the
fragile part of self-review) and duplicate the Stage's real verification. Hard
checks belong in explicit verify Tasks that run real tests, not in generated
file-existence assertions.

## Named Plan Anchors

The Workflow binds four files as Task artifacts so the approver and later
Stages have stable evidence:

- `PLANS/issue-<number>-PLAN.md` — interpretation, scope, approach; the plan
  approval document.
- `PLANS/issue-<number>-DESIGN.md` — produced when the change involves design
  choices.
- `PLANS/issue-<number>-REVIEW.md` — the Check Stage's review evidence.
- `PLANS/issue-<number>.handoff.json` — the contract above.

Everything else under `PLANS/` and `RESEARCH/` is the Agent's own
organization and is invisible to the Workflow.

## Persistence and Recovery

A WorkflowRun is pinned to one Runner and its Workspace persists across
Stages, so the Build Stage reads the handoff file from the filesystem on the
happy path; no artifact round-trip exists on the main path.

Artifact upload happens once, at the plan Stage's artifact binding, and
serves two purposes: approval evidence and the recovery source. When the
Workspace directory was rebuilt (Runner loss, reclamation), `mohist/task-list`
restores the handoff file from the Run's uploaded artifact record before
loading. This artifact fetch is the only new Runner–Server channel in this
design; other plan material is not restored, and an Agent that needs it reads
the recorded artifacts.

## Review as Approval

The Workflow has exactly one review mechanism: the approval point with its
feedback loop.

- The plan Stage has no self-review Task. A same-session self-verdict
  duplicated the plan approval with the same model and added a second repair
  loop.
- The Check Stage still runs an independent review in its own Session, but it
  produces `REVIEW.md` as evidence. There is no verdict marker, no PASS/FAIL
  gate, and no auto-fix recovery loop; the verdict belongs to the approver. A
  repair requested by the approver goes through the approval feedback Tasks,
  which is now the single repair path.
- Judgment loops carry no budget. Recovery loops are unattended and keep their
  budgets; every approval feedback round is initiated by a deciding actor, so
  the engine imposes no round limit.

## Integrate: Auto-merge

Integrate enables GitHub auto-merge on the approved Pull Request and waits
for `MERGED` via the existing `github-pr-status` Stage Check. Merge timing and
merge-time prerequisites are arbitrated by GitHub, which removes the
`base-moved`, `protection-conflict`, and post-approval `pr-checks-failed`
recovery branches that the synchronous merge required. A Pull Request check
that fails after approval leaves the wait unsatisfied and surfaces as a
blocked Run for intervention. Enabling auto-merge on a Repository that
disallows it is an ordinary Task failure.

`mohist/merge-github-pr` is removed with this change; no consumer remains.

## Prompt Realignment

Built-in prompts follow the same boundary. Removed: `proposal`, `specs`,
`design`, `tasks`, `self-review`, `fix-plan-review`, `auto-fix`. Added:
`plan` (produces the named anchors and the handoff) and `build-task` (base
prompt for generated Tasks). Repurposed: `review` writes evidence without a
verdict marker; `apply-feedback`, `fix-ci`, `fix-pr-checks`, and
`resolve-rebase-conflicts` keep their roles with `PLANS/` paths.

## Web Evidence Surface

The Check approval UI currently parses `review.md` task output
(`ReviewSummary`, `ReviewReportModal`). It is rebound to the recorded
`REVIEW.md` artifact, and the plan approval surface presents `PLAN.md` and the
handoff task list the same way, so each approval point shows the artifacts of
its own Stage.

## Out of Scope

- A Profile-declared automatic reviewer (`reviewer: agent`). Approval is a
  judgment position; who fills it — a person, an external Agent, or
  automation — stays outside the engine. This may be revisited after the
  simplified Workflow has production mileage.
- Restoring `PLANS/` material beyond the handoff file on Workspace rebuild.
