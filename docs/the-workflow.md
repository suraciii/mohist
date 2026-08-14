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

Each stage stores its artifacts under
`openspec/changes/issue-<number>/`. These artifacts provide evidence for
later decisions and audits. See the complete state machine near the end of this
document.

## Draft

Draft keeps a mutable requirement separate from execution. This lets people
clarify intent and resolve prerequisites before Mohist consumes execution
capacity. In this state:

- The Workflow has not started.
- The Inline Agent has not started.
- You may edit the title, body, labels, and priority.
- You may add prerequisites, such as "wait for #N to finish before starting."

Run:

```bash
mo issue edit <number> --ready   # Move the requirement to Backlog.
mo issue start <number>          # Start the Workflow and enter Plan.
```

## Plan

The Inline Agent interprets the requirements and plans the implementation. This
is the least expensive stage in which to find a wrong direction. During a
manual approval, focus on `proposal.md` and `tasks.json`.

Plan produces five artifacts in order:

| Artifact | Contents |
|---|---|
| `proposal.md` | The interpretation, scope, motivation, and proposed solution |
| `specs/` | Specific capability-spec changes at the user-story level |
| `design.md` | Technical design decisions, including the selected option and its rationale when alternatives exist |
| `tasks.json` | The ordered Build steps and their acceptance conditions |
| `self-review.md` | The Inline Agent's review of its plan, including considerations, tradeoffs, and concerns |

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
Following `tasks.json` one item at a time, checking each increment, and recording
separate commits localizes failures and makes recovery understandable. Work
remains isolated on the Issue branch until Integrate.

### After Build

By default, the Workflow enters Check automatically. To require approval after
Build, set Build's `requiresApproval` field to `true` in the Workflow Profile.
Declare `approval.feedback.tasks` when rejected approvals should create
follow-up work; built-in Profiles include this loop.

## Check

Check prevents Build's claim of completion from being its only evidence. It
runs the complete test suite, reviews the diff, and records the conclusion and
findings in `review.md`. A problem returns to Build before the shared base branch
is involved.

### After Check

The Workflow stops at an approval point and waits for an approve or reject
decision:

```bash
mo run approve --issue <number>   # Enter Integrate.
mo run reject --issue <number> --message "Describe the required changes"  # Return to Build.
```

For a manual decision, read `review.md`.

## Integrate

Integrate isolates shared-branch risk from implementation work. Another person
or Issue may have advanced the base branch while Build and Check ran, so this
stage checks drift, rebases when needed, merges the Issue branch, and pushes
when configured. Conflicts remain an Integrate concern instead of leaking into
earlier stages.

### Integrate Failure

The most common causes are:

- A merge conflict because the branches have diverged too far for the rebase.
- A push failure caused by permissions or the network.

The Issue becomes blocked and waits for intervention. See
[Troubleshooting](troubleshooting.md).

## Done

Done records that both implementation and shared-branch integration succeeded.
It preserves the evidence needed for later audit while removing the Issue from
active execution. In this state:

- The code is on the base branch.
- All artifacts are archived under
  `openspec/changes/issue-<number>/`.
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
for capacity, executing, waiting for a decision, or stopped for recovery:

| Health | Meaning |
|---|---|
| `active` | The Workflow is assigned and executing or advancing normally |
| `queued` | The Workflow has started but is waiting for Runner assignment |
| `attention` | An approval decision is required before execution can continue |
| `paused` | Execution was stopped explicitly and remains resumable |
| `blocked` | The Workflow cannot continue without intervention |
| `cancelled` | The Issue was cancelled and will not run again |
| `done` | The Issue is complete |

The Web UI shows health as a colored dot on each Issue card.

## When Is Action Required?

Action is required in four situations:

1. Plan is complete and needs an approve or reject decision.
2. Check is complete and needs an approve or reject decision.
3. The Issue is blocked and needs a retry, rerun, or stop decision.
4. The Runner was lost and current work failed with `runner-lost`.

The owner, a script, or a Mohist Agent may perform these actions. The Workflow
only consumes the approval action and its result.

## Customize the Workflow

Change the default Workflow when it does not fit the project:

- To require approval after Build, change the Profile's `requiresApproval`
  value. A Profile with any Approval Stage must declare non-empty
  `approval.feedback.tasks`.
- To skip Check, remove that Stage from a custom Profile.
- To add a Stage such as Deploy, extend the Profile YAML.

See [Workflow Profile](workflow-profiles.md).
