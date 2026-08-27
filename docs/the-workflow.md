# The Workflow

The default Mohist Workflow separates five kinds of decisions that have
different costs and failure boundaries. Plan tests the direction before code is
changed. Build creates an isolated increment. Check creates evidence in a
separate verification pass. Integrate alone changes the shared base branch.
This separation keeps approval and recovery focused on the smallest uncertain
boundary. See [Issue Management](issues.md) for all Issue operations, including
create, start, approve, and recover. See
[Workflow Profile](workflow-profiles.md) for custom stages, tasks, and approval
policies.

Each stage stores its artifacts as evidence for later decisions and audits. See
the complete state machine near the end of this document.

## Draft

Draft keeps a mutable requirement separate from execution. This lets people
clarify intent and resolve prerequisites before Mohist consumes execution
capacity. In this state:

- The Workflow has not started.
- No stage Agent has started.
- You may edit the title, body, labels, and priority.
- You may add prerequisites, such as "wait for #N to finish before starting."

Run:

```bash
mo issue edit <number> --ready   # Move the requirement to Backlog.
mo issue start <number>          # Start the Workflow and enter Plan.
```

## Plan

The Plan stage's Mohist Agent interprets the requirements and plans the implementation. This
is the least expensive stage in which to find a wrong direction. During a
manual approval, focus on the plan document and the task list.

Plan artifacts live in the Issue Workspace under `PLANS/`, outside the
Repository checkout, so they never appear in the Pull Request or the
Repository history. See [Workspace](workspaces.md#layout) for the layout.
Plan produces:

- `PLANS/PLAN.md`: the interpretation, scope, motivation, and
  proposed approach. This is the primary approval document.
- `PLANS/DESIGN.md`, when the change involves design choices:
  the technical design, including the selected option and its rationale.
- `PLANS/tasks.json`: the machine-readable task list,
  an ordered list whose entries each carry a goal, acceptance criteria,
  and references to plan material.

The Agent organizes any additional planning material freely under `PLANS/` and
`RESEARCH/`; the Workflow consumes only the task list.

This stage usually takes 5-20 minutes. The duration depends on the clarity of
the Issue body, repository complexity, and model speed.

### After Plan

The Workflow stops at an approval point and waits for an approve or reject
decision:

```bash
mo run approve --issue <number>   # Approve the plan and enter Build.
mo run reject --issue <number> --message "Describe the required changes"  # Reject and run Plan again.
```

The approver may be any authorized actor. See
[Core Concepts: Approval](concepts.md#approval).

## Build

Build turns the approved plan into small, reviewable changes. Keeping Build
after plan approval avoids spending execution time on a rejected direction.
The Workflow expands the approved task list into one Agent task per
entry, executed in order; checking each increment and recording separate
commits localizes failures and makes recovery understandable. Work remains
isolated on the Issue branch until Integrate.

### After Build

By default, the Workflow enters Check automatically. A Workflow Profile can
require approval after Build and can turn a rejected approval into follow-up
work; built-in Profiles include this loop. See
[Workflow Definition Reference](workflow-definition.md) for the exact fields.

## Check

Check prevents Build's claim of completion from being its only evidence. It
runs an independent verification pass: a separate Agent session reviews the
diff and records the findings in `PLANS/REVIEW.md`, and external checks are
confirmed where they exist. The review is
evidence, not a verdict: the approve or reject decision belongs to the
approver. A problem returns to Build before the shared base branch
is involved.

### After Check

The Workflow stops at an approval point and waits for an approve or reject
decision:

```bash
mo run approve --issue <number>   # Enter Integrate.
mo run reject --issue <number> --message "Describe the required changes"  # Return to Build.
```

For a manual decision, read the review report and the diff.

## Integrate

Integrate isolates shared-branch risk from implementation work. For a Pull
Request Workflow, this stage enables auto-merge on the approved Pull Request
and waits until GitHub reports it merged. Merge timing and merge-time
prerequisites are arbitrated by GitHub, so there is no merge-moment race for
the Workflow to recover from.

### Integrate Failure

The most common causes are:

- The Repository does not allow auto-merge, or the token lacks permission.
- A required Pull Request check failed after approval.

The Issue becomes blocked and waits for intervention. See
[Troubleshooting](troubleshooting.md).

## Done

Done records that both implementation and shared-branch integration succeeded.
It preserves the evidence needed for later audit while removing the Issue from
active execution. In this state:

- The code is on the base branch.
- Plan and review artifacts are recorded as run artifacts and remain
  inspectable from the Issue.
- You may archive the Issue to remove it from the board.

```bash
mo issue archive <number>
```

## Complete State Machine

```text diagram
Draft --mark ready--> Backlog --start--> Plan
Plan --approve--> Build --automatic--> Check
Plan --reject--> Plan
Check --approve--> Integrate --automatic--> Done
Check --reject--> Build

Done --archive--> Archived

Any stage --failure--> Blocked
Blocked --retry/resume/rerun--> Workflow execution
```

After a failure in any stage, use `mo run retry`, `mo run resume`, or
`mo run rerun` as appropriate.

## Health

In addition to its Workflow stage, an Issue has a `health` field that describes
execution health. These facts stay separate because one Stage can be waiting
for capacity, executing, waiting for a decision, or stopped for recovery.
Health is `active` when the Workflow is assigned and executing or advancing
normally, `queued` when the Workflow has started but is waiting for Runner
assignment, `attention` when an approval decision is required before execution
can continue, `paused` when execution was stopped explicitly and remains
resumable, `blocked` when the Workflow cannot continue without intervention,
`cancelled` when the Issue was cancelled and will not run again, and `done`
when the Issue is complete.

The Web UI shows health as a colored dot on each Issue card.

## When Is Action Required?

Action is required in four situations:

1. Plan is complete and needs an approve or reject decision.
2. Check is complete and needs an approve or reject decision.
3. The Issue is blocked and needs a retry, rerun, or stop decision.
4. The Runner was lost and current work failed.

The owner, a script, or a Mohist Agent may perform these actions. The Workflow
only consumes the approval action and its result.

## Customize the Workflow

Change the default Workflow when it does not fit the project. A custom Profile
can require approval after Build, skip Check, or add a Stage such as Deploy.
[Workflow Definition Reference](workflow-definition.md) defines the Profile
fields and their validation rules. See [Workflow Profile](workflow-profiles.md)
for Profile selection.
