# Issue Management

An Issue is the main unit of work in the Mohist execution layer. This document
covers every operation from creation through closure. Users usually ask an
external Agent to perform these operations through the Mohist Skill and `mo`.
You can also use the same CLI directly or work manually in the Web UI as a
fallback surface. See [The Workflow](the-workflow.md) for the work and
artifacts inside each Workflow Stage. See [Planning with Epics](epics.md) to organize
multiple Issues into a milestone.

## Create an Issue

### CLI

```bash
# Minimal form; a new Issue is a Draft by default.
mo issue create "Add search feature"

# Include a body
mo issue create "Fix login bug" --body "Users can't login on Safari"

# Use a file for a long body
mo issue create "Refactor auth module" --body-file ./issue-body.md

# Read from stdin
cat ./my-issue.md | mo issue create "My issue" --body-file -

# Set priority and labels
mo issue create "Critical fix" --priority p0 --label kind=bug

# Select a Workflow Profile
mo issue create "Implement search" --workflow-profile mohist/local

# Select a model
mo issue create "Complex refactor" --model claude-sonnet-4

# Select the target repository in a multi-repository Project.
# Without --repo, Mohist uses the default repository.
mo issue create "server: add subscription API" --repo server

# Create a child of another Issue
mo issue create "web: add subscription management" --parent 42 --repo web

# Skip Draft only when the requirement is ready to execute.
mo issue create "Document search API" --body-file ./issue-body.md --ready
```

Draft protects incomplete requirements from consuming execution capacity. Mark
an Issue ready only after its body and dependencies are usable.

Without `--workflow-profile`, the Issue inherits the Project's default Profile.
You may update the selection before the Workflow starts or later. An active
Workflow continues to use the Profile selected when it started. A new
selection applies to the next run. Clearing an explicit selection restores
inheritance from the Project default.

### Web UI Fallback

Select **New Issue** in the upper-right corner of the board. Enter the title,
body, priority, and labels.

### Target Repository and Child Issues

- Each Issue has one **target repository**. Its branch, diff, and Integrate work
  all occur in that repository. The target must not change after the Workflow
  starts. See [Repositories](repositories.md).
- When one requirement spans multiple repositories, split it into child Issues.
  The parent tracks the complete requirement, and each child runs its own
  Workflow. See [Composite Issues and Sub-issues](sub-issues.md).

### Write an Effective Issue Body

Body quality determines Plan quality, and Plan quality determines the outcome
of the Issue. Five minutes spent on a clear body can prevent much more time
spent correcting the Plan.

An effective body contains:

```markdown
## Background
Why is this change needed? What problem occurred?

## Goal
What must be true after this Issue is complete?

## Non-goals
What is explicitly outside the scope?

## Acceptance criteria
What verifiable conditions define completion?
```

Do not use a body with too little context:

```text literal
Add search
```

The Inline Agent cannot determine what to search, which fields to include,
whether to highlight matches, or whether to paginate. It can produce a Plan
that solves the wrong problem.

Use a specific body instead:

```markdown
## Background
The task list on the home page has more than 100 entries. Users cannot find old
tasks.

## Goal
Add a search field above the list. Filter in real time by a partial title match.

## Non-goals
- Do not search descriptions.
- Do not add advanced filters.
- Do not change the backend API.

## Acceptance criteria
- Entering "foo" shows only tasks whose title contains "foo".
- Matching is case-insensitive.
- An empty input shows every task.
- Rendering a list of 200 tasks has no visible delay.
```

## View an Issue

In an external Agent, ask directly for an Issue's progress, blockers, and
pending actions. To use the CLI directly:

```bash
# List all Issues in the current Project
mo issue list

# Filter by Stage
mo issue list --stage plan

# List archived Issues
mo issue list --archived

# List all Issues, including archived Issues
mo issue list --all

# View details
mo issue view 42
```

For a complete visual view or manual takeover, select an Issue card in the Web
UI. The details page shows:

- The current Stage, health, and approval state.
- The complete body and comments.
- The Workflow timeline.
- The branch bar with the current branch state.
- A diff and commit summary.
- The latest Plan and Check artifacts.
- Actions such as Start, Approve, Reject, Stop, and Retry.
- AgentSessions that contain the Workflow execution conversations.

## Start an Issue

```bash
mo issue edit 42 --ready
mo issue start 42
```

After the Issue starts:

1. Mohist creates or reuses the named Workspace `issue-42`.
2. The Workflow enters Plan and waits for Runner capacity when necessary.
3. The Inline Agent starts when a Runner claims the work.

The following conditions must be true:

- The Issue is in `backlog`.
- The Issue is marked ready rather than Draft.
- Its prerequisites and Repository binding permit a start.

Runner presence and free slots affect dispatch, not the Start decision. A
started Workflow waits safely until eligible capacity is available. Use
`mo runner status` to inspect that wait.

## Respond to an Approval Point

After Plan or Check, the Issue enters `awaiting approval`. The Workflow is
stopped at an approval point and waits for an approve or reject decision:

```bash
mo run approve --issue 42     # Approve and enter the next Stage.
mo run reject --issue 42 --message "Missing error handling in proposal"  # Reject and repeat the current Stage.
```

Approval and comment ownership comes from the authenticated identity. Mohist
does not need or accept a self-declared identity. `--display-name` is an
optional display alias and does not change ownership. The `reject` action must
include a reason in `--message` or `-m`. The approver can be a person or
automation. See [Core Concepts: Approval](concepts.md#approval). For longer context, add a
comment first and make the short rejection message refer to it:

```bash
mo issue comment create 42 --display-name "Ada" --body "Reject because: missing error handling in proposal"
mo run reject --issue 42 -m "See comment: missing error handling"
```

## Comments

```bash
# Add a comment. The authenticated identity is the author.
# --display-name is only a display alias.
mo issue comment create 42 --display-name "Ada" --body "Looks good but check edge cases"

# The CLI does not currently delete comments. Use the Web UI or API.
```

The comment area is at the bottom of the Issue details page in the Web UI.

Comments are a lightweight collaboration channel between you and the Inline
Agent. During Plan, the Inline Agent reads them as additional context.

## Prerequisites

To require #10 to finish before #11 starts:

```bash
mo issue prereq add 11 10    # Make #11 wait for #10.
mo issue prereq remove 11 10 # Remove the dependency.
```

When an Issue has a prerequisite, Mohist checks that it is complete before
starting the Issue. Mohist rejects the start when the prerequisite is not
complete.

The Issue details page has an **Add Prerequisite** section.

## Mark an Issue Done Manually

When work was completed outside the Workflow, explicitly mark the Issue Done:

```bash
mo issue done 42
```

This command applies only to an in-progress Issue without child Issues whose
Workflow has either stopped permanently or completed. A failed Workflow can
still be retried. First use `mo run stop --issue 42 --yes` to end it explicitly,
then mark the Issue Done. The command does not stop the Workflow or reset its
Session for you.

Marking an Issue that is already Done succeeds without creating a second
completion record. Manual completion preserves the original stopped or
completed Workflow history and counts toward delivered Epic progress in the
same way as normal Workflow completion. A parent Issue's Done state is still
derived from its child Issues and must not be overridden manually.

## Pause, Stop, Complete, or Close

```bash
# Recoverable pause: end the current turn, preserve the AgentSession, and resume later.
mo run pause --issue 42

# Permanent stop: terminal and cannot be resumed.
mo run stop --issue 42 --yes

# Manual completion: the Workflow ended, but the work was delivered another way.
mo issue done 42

# Close completely: move the Issue to the cancelled terminal state.
mo issue close 42

# Reopen: reactivate a closed Issue.
mo issue reopen 42
```

| Operation | Use | Result |
|---|---|---|
| `pause` | Stop temporarily, interrupt a stuck Inline Agent, or preserve a recovery path | Ends the current turn and puts the Workflow in a paused state that supports `resume` |
| `stop` | End this Workflow permanently | Stops the Workflow Run permanently. It is terminal and cannot resume |
| `done` | Work was completed and delivered outside the Workflow | Moves the Issue to Done and preserves the Workflow history |
| `close` | The Issue will not be done | Moves the Issue to the `cancelled` terminal state. It can be reopened |
| `reopen` | Reverse an accidental closure or do the work later | Returns the Issue to `backlog` |

## Recover from Failure

Recovery is a core Mohist capability. See [Troubleshooting](troubleshooting.md)
for details. The following table is a quick reference:

| Situation | Command |
|---|---|
| The Issue is blocked. Retry the current Stage | `mo run retry --issue 42` |
| The Issue is paused. Continue the current Workflow | `mo run resume --issue 42` |
| The Workflow stopped, but the work was delivered another way | `mo issue done 42` |
| Repeat the current Stage and discard its artifacts | `mo run rerun --issue 42` |
| Repeat from a selected Stage and discard that Stage's later artifacts | `mo run rerun --issue 42 --from-stage build` |
| The base branch moved. Rebase the Issue branch | `mo issue rebase 42` |

## Archive an Issue

An Issue remains in the board's Done column after completion. Archive it to
remove it from the board:

```bash
mo issue archive 42
mo issue restore 42       # Restore an archived Issue.
mo issue list --archived  # List archived Issues.
```

The Web UI has an Archive page.

## Edit an Issue

You may freely edit an Issue before it starts:

```bash
mo issue edit 42 --title "New title"
mo issue edit 42 --body-file ./new-body.md
mo issue edit 42 --priority p1
mo issue edit 42 --label kind=bug --label area=web
```

Edit the body of an active Issue with care. The Inline Agent is already working
from the previous body.

## Complete CLI Reference

See the Issue section in [CLI Reference](cli-reference.md) for the authoritative
`mo issue` command surface. This document contains only scenario-based examples.
Use `mo issue <command> --help` for all options.

---

Implementation source: `packages/server/src/Mohist.Server/Issue/`, `Api/IssueRoutes.*`, and the
CLI under `packages/cli/`.
