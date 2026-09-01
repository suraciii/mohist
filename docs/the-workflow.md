# The Workflow

The default Mohist Workflow separates five decisions with different costs and failure
boundaries. Plan tests direction before code changes. Build creates an isolated increment. Check
creates independent evidence. Integrate alone changes the shared base branch. Approval Points
and recovery therefore stay focused on one uncertain boundary. See [Issue Management](issues.md) for Issue
operations and [Workflow Profile](workflow-profiles.md) for custom stages, tasks, and approval policies.

Every Stage stores artifacts as evidence for later decisions and audits. See [Plan Artifacts](../design/workflow/plan-artifacts.md) for the
artifact contract.

## Product Commitments

- Draft keeps mutable requirements separate from execution.
- Plan produces a human-readable plan, technical design, and ordered task list before Build
  starts.
- Build changes only the Issue branch and expands the approved task list in order.
- Check produces independent review evidence. An approver, not the reviewing Agent, decides
  whether work continues.
- Integrate is the only Stage that changes the shared base branch or completes Pull Request
  delivery.
- A failure blocks the Workflow at the smallest recovery boundary. Retry, resume, and rerun use
  the current Run's declared rules.

## Draft

Draft keeps a mutable requirement separate from execution. The Workflow has not started, no
Stage Agent has started, and you may edit the title, body, labels, and priority. You may add
prerequisites, such as waiting for another Issue to finish.

```bash
mo issue edit <number> --ready   # Move the requirement to Backlog.
mo issue start <number>          # Start the Workflow and enter Plan.
```

## Plan

Plan interprets the requirement and proposes implementation work. It is the least expensive
Stage for finding a wrong direction. During approval, read `PLAN.md` and the task list.

Plan artifacts live under `PLANS/` in the Issue Workspace, outside the Repository checkout.
They do not enter the Pull Request or Repository history. Plan produces:

- `PLANS/PLAN.md`: interpretation, scope, motivation, and approach. It is the primary Plan Approval
  Point document.
- `PLANS/DESIGN.md`: technical decisions and rationale. It always exists and explains when no separate
  design is needed.
- `PLANS/tasks.json`: an ordered machine-readable task list. Each entry has a goal, acceptance criteria,
  and references to plan material.

The Agent may organize other material under `PLANS/` and `RESEARCH/`. The Workflow consumes only
the task list. See [Workspace](workspaces.md#layout) for the layout and [Plan Artifacts](../design/workflow/plan-artifacts.md) for persistence and recovery.

### After Plan

The Workflow stops at an Approval Point and waits for Approve or Request Changes:

```bash
mo run approve --issue <number>   # Approve the plan and enter Build.
mo run request-changes --issue <number> --message "Describe the required changes"
```

Request Changes is available only when the bound Definition declares Feedback Tasks. An
authorized actor may approve or request changes. See [Core Concepts: Approval Point](concepts.md#approval-point) for the feedback sequence and
ownership rules.

## Build

Build turns the approved plan into small, reviewable changes. It expands the approved task list
into one Agent Task per entry and executes entries in order. Verification after each increment
and separate commits localize failures. Work remains isolated on the Issue branch until
Integrate.

The built-in Profiles enter Check automatically and have no Build Approval Point. A custom
Profile may require approval after Build and use the same configured Feedback Tasks. See
[Workflow Definition Reference](workflow-definition.md) for the fields.

## Check

Check prevents Build's completion claim from being the only evidence. A separate Agent Session
reviews the diff and records findings in `PLANS/REVIEW.md`. The Stage also confirms external checks
where they exist.

The review is evidence, not a verdict. The approver owns the Approve or Request Changes
decision. A problem returns to configured Feedback Tasks before the shared base branch is
involved.

### After Check

The Workflow stops at an Approval Point and waits for Approve or Request Changes:

```bash
mo run approve --issue <number>   # Enter Integrate.
mo run request-changes --issue <number> --message "Describe the required changes"
```

Request Changes runs the Feedback Tasks bound to the WorkflowRun. Read the review report and
diff before a manual decision. See [Core Concepts: Approval Point](concepts.md#approval-point) for the complete sequence.

## Integrate

Integrate isolates shared-branch risk from implementation work. The Pull Request Profile enables
auto-merge on the approved Pull Request and waits for GitHub to report it merged. GitHub
arbitrates merge timing and merge-time prerequisites.

### Integrate Failure

Common causes are:

- The Repository does not allow auto-merge.
- The token lacks permission.
- A required Pull Request check fails after approval.

The Issue becomes blocked and waits for intervention. See [Troubleshooting](troubleshooting.md).

## Done

Done records successful implementation and shared-branch integration. Plan and review artifacts
remain inspectable as run artifacts. The Issue leaves active execution, and you may archive it:

```bash
mo issue archive <number>
```

## Complete State Machine

```text diagram
              +-------+
              | Draft |
              +---+---+
                  |
                  vready
             +---------+
             | Backlog |
             +----+----+
                  |
                  vstart
              +------+
              | Plan +---------------+
              +--+-+-+               |
                 | |^  changes       |
                 | ++                |
                 |                   |
                 vapprove            |
             +-------+               |
             | Build +---------------+
             +---+---+               |
               +-+                   |
               vauto                 |
           +-------+                 |
           | Check +-----------------+
           +---++--+                 |
               || ^  changes         |
               |+-+                  |
            +--+                     |
            vapprove                 |
      +-----------+                  |
      | Integrate |                  |
      +-----+-----+                  |
      +-----+------+                 |
      vauto        vfail             |
  +------+    +---------+       fail |
  | Done |    | Blocked |<-----------+
  +---+--+    +---------+
      |
      varchive
+----------+
| Archived |
+----------+
```

Draft moves to Backlog when the requirement is marked ready. Backlog enters Plan when started.
Approving Plan enters Build. Build enters Check automatically. Approving Check enters Integrate.
Integrate enters Done automatically. Done moves to Archived when the Issue is archived.

Request Changes runs the declared Feedback Tasks and checks, then returns to the same Approval
Point. A failure in Plan, Build, Check, or Integrate enters Blocked. Use `mo run retry`, `mo run resume`,
or `mo run rerun` as appropriate. These commands return execution to the appropriate point in the
Workflow.

## Health

`health` describes execution health separately from the Workflow Stage. One Stage may be
queued, executing, waiting for a decision, or stopped for recovery.

- `active`: the Workflow is assigned and executing or advancing normally.
- `queued`: the Workflow has started and waits for Runner assignment.
- `attention`: an Approval Point decision is required.
- `paused`: execution was stopped explicitly and remains resumable.
- `blocked`: the Workflow cannot continue without intervention.
- `cancelled`: the Issue was cancelled and will not run again.
- `done`: the Issue is complete.

The Web UI shows health as a colored dot on each Issue card.

## When Is Action Required?

Action is required when:

1. Plan needs an Approve or Request Changes decision.
2. Check needs an Approve or Request Changes decision.
3. The Issue is blocked and needs a retry, rerun, or stop decision.
4. The Runner was lost and current work failed.

The owner, a script, or a Mohist Agent may perform these actions. WorkflowRun owns the decision
and resulting state.

## Customize the Workflow

Create a custom Profile when the default Workflow does not fit the Project. A custom Profile may
require an Approval Point after Build, skip Check, or add a Stage such as Deploy. [Workflow Definition Reference](workflow-definition.md)
defines Profile fields and validation. See [Workflow Profile](workflow-profiles.md) for Profile selection.
