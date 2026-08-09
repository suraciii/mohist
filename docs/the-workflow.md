# The Workflow

The default Mohist workflow has five stages. You need to understand what each
stage does, what it produces, and when it stops so that you know where approval
and recovery actions occur. See [Issue Management](issues.md) for all issue
operations, including create, start, approve, and recover. See
[Workflow Profile](workflow-profiles.md) for custom stages, tasks, and approval
policies.

Each stage stores its artifacts under
`openspec/changes/<issue-number>-<slug>/`. These artifacts provide evidence for
later decisions and audits. See the complete state machine near the end of this
document.

## Draft

Draft is the initial state of a new Issue. In this state:

- The Workflow has not started.
- The Inline Agent has not started.
- You may edit the title, body, labels, and priority.
- You may add prerequisites, such as "wait for #N to finish before starting."

Run:

```bash
mo issue start <number>   # Start the Workflow and enter Plan.
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

The Inline Agent implements the steps in `tasks.json`.

### What Build Does

- Works in the Issue-specific worktree on the `mo/issue-<number>` branch.
- Executes the tasks in `tasks.json` one at a time.
- Runs tests or lint checks after each task.
- Retries or adjusts failed tasks automatically.
- Creates one commit for each task.

### After Build

By default, the Workflow enters Check automatically. To require approval after
Build, set Build's `requiresApproval` field to `true` in the Workflow Profile.

## Check

The Inline Agent reviews the Build output. This stage acts as an internal code
review.

### What Check Does

- Runs the complete test suite.
- Reviews the Inline Agent's own diff.
- Produces `review.md` with the conclusion, findings, and recommended fixes.
- May start another Build pass when it finds a problem.

### After Check

The Workflow stops at an approval point and waits for an approve or reject
decision:

```bash
mo run approve --issue <number>   # Enter Integrate.
mo run reject --issue <number> --message "Describe the required changes"  # Return to Build.
```

For a manual decision, read `review.md`.

## Integrate

Integrate merges the `mo/issue-<number>` branch into the base branch.

### What Integrate Does

- Checks whether the base branch has moved because another person or Issue
  advanced it.
- Attempts a rebase when the base branch has moved. This can cause conflicts.
- Merges into the base branch.
- Pushes to the remote when configured.

### Integrate Failure

The most common causes are:

- A merge conflict because the branches have diverged too far for the rebase.
- A push failure caused by permissions or the network.

The Issue becomes blocked and waits for intervention. See
[Troubleshooting](troubleshooting.md).

## Done

Done is the terminal state for a completed Issue. In this state:

- The code is on the base branch.
- All artifacts are archived under
  `openspec/changes/<number>-<slug>/`.
- You may archive the Issue to remove it from the board.

```bash
mo issue archive <number>
```

## Complete State Machine

```text
Draft --start--> Plan --approve--> Build --automatic--> Check
                   |                                  |
                   +--reject--> Plan                  +--reject--> Build

Check --approve--> Integrate --automatic--> Done --archive--> Archived

Any stage --failure--> Blocked
Blocked --retry/resume/rerun--> Workflow execution
```

After a failure in any stage, use `mo run retry`, `mo run resume`, or
`mo run rerun` as appropriate.

## Health

In addition to its Workflow stage, an Issue has a `health` field that describes
execution health:

| Health | Meaning |
|---|---|
| `active` | The Workflow is running or waiting for the system to continue automatically |
| `paused` | Execution was stopped manually or is waiting for an approval decision |
| `blocked` | The Workflow cannot continue without intervention |
| `cancelled` | The Issue was cancelled and will not run again |
| `done` | The Issue is complete |

The Web UI shows health as a colored dot on each Issue card.

## When Is Action Required?

Action is required in four situations:

1. Plan is complete and needs an approve or reject decision.
2. Check is complete and needs an approve or reject decision.
3. The Issue is blocked and needs a retry, rerun, or stop decision.
4. The Runner is unavailable and automatic recovery has failed.

The owner, a script, or a Mohist Agent may perform these actions. The Workflow
only consumes the approval action and its result.

## Customize the Workflow

Change the default Workflow when it does not fit the project:

- To require approval after Build, change the Profile's `requiresApproval`
  value.
- To skip Check, remove that Stage from a custom Profile.
- To add a Stage such as Deploy, extend the Profile YAML.

See [Workflow Profile](workflow-profiles.md).
