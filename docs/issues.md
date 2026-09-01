# Issue Management

An Issue is Mohist's main unit of work. This document explains how to create,
prepare, start, inspect, advance, recover, and close an Issue. Use the same
operations through an External Agent and the Mohist Skill, the `mo` CLI, or
the Web UI fallback.

## Product Commitments

- Every Issue has one Project-scoped identity and one target Repository.
- A Draft Issue does not consume execution capacity.
- A ready Issue starts only when its prerequisites and Repository binding allow it.
- A WorkflowRun uses the complete Definition that it bound when it started.
- An Issue without a Workflow bypasses the production line and can be completed manually.
- Approval decisions use authenticated ownership. A display name never changes attribution.
- Pause preserves a recoverable Workflow. Stop is permanent. Close cancels the Issue and can be reopened.
- Manual completion preserves Workflow history and cannot override a parent Issue's derived state.

## Create an Issue

### CLI

```bash
# Minimal form; a new Issue is a Draft by default.
mo issue create "Add search feature"

# Skip Draft only when the requirement is ready to execute.
mo issue create "Document search API" --body-file ./issue-body.md --ready
```

The command accepts a body inline, from a file, or from stdin. It can also set
priority, labels, a Workflow Profile, a model, a target Repository, and a
parent Issue. See [CLI Reference](cli-reference.md#issue) for all options.

Draft protects an incomplete requirement from consuming execution capacity.
Mark an Issue ready only when its body and prerequisites are usable.

Without `--workflow-profile`, the Issue inherits the Project default Profile.
Use `--no-workflow`, which conflicts with `--workflow-profile`, for work
outside the Mohist production line. You may change the selection until the
Issue starts. A running WorkflowRun keeps the complete Definition bound at its
start. A new selection or Profile edit affects only a future Run. Clearing an
explicit selection restores inheritance from the Project default.

### Web UI Fallback

Select **New Issue** in the upper-right corner of the board. Enter the title,
body, priority, and labels.

### Target Repository and Child Issues

- Each Issue has one target Repository. Its branch, diff, and Integrate work
  occur there. The target cannot change after the Workflow starts. See
  [Repositories](repositories.md).
- When one requirement spans multiple Repositories, split it into child Issues.
  The parent tracks the complete requirement, and each child runs its own
  Workflow. See [Composite Issues and Child Issues](composite-issues.md).

### Write an Effective Issue Body

Body quality affects Plan quality. An effective body states the problem, the
goal, the boundaries, and the checks that prove completion:

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

The Plan stage's Mohist Agent cannot infer what to search, which fields to
include, or how to handle highlighting and pagination. It may produce a Plan
for the wrong problem.

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

Ask an External Agent for an Issue's progress, blockers, and pending actions.
Use `mo issue list` for the list and `mo issue view 42` for one Issue. Filters
such as Stage, priority, and archived state are in
[CLI Reference](cli-reference.md#issue).

For a complete visual view or manual takeover, select an Issue card in the Web
UI. The details page shows:

- Current Stage, health, and Approval Point state.
- The complete body and comments.
- GitHub mirror number, link, and sync health when the target Repository is
  connected. See [GitHub](github.md).
- Workflow timeline, branch state, diff, and commit summary.
- Latest Plan and Check artifacts.
- Available actions such as Start, Approve, Request Changes, Stop, and Retry.
- AgentSessions that contain Workflow execution conversations.

## Start an Issue

```bash
mo issue edit 42 --ready
mo issue start 42
```

An Issue starts only from `backlog`, when it is ready rather than Draft, and
when its prerequisites and Repository binding permit a start. A Workflow
Profile creates or reuses the named `issue-42` Workspace and enters Plan. An
Issue created with `--no-workflow` moves directly to in progress. Complete it
with `mo issue done` or `mo issue close`. When its target Repository is
connected to GitHub, the linked GitHub Issue can drive the same lifecycle. See
[GitHub](github.md#linked-pairs).

## Respond to an Approval Point

After Plan or Check, the Issue enters `awaiting approval`. The Workflow waits
for Approve or Request Changes:

```bash
mo run approve --issue 42     # Approve and enter the next Stage.
mo run request-changes --issue 42 --message "Missing error handling in the plan"
```

Decision and comment ownership come from the authenticated identity. Mohist
does not accept a self-declared identity. `--display-name` is only a display
alias. Request Changes requires `--message` or `-m` and is available only when
the bound Definition declares Feedback Tasks. The approver may be a person or
automation. See [Core Concepts: Approval Point](concepts.md#approval-point).

For longer context, add a comment first and make the short message refer to it:

```bash
mo issue comment create 42 --display-name "Ada" --body "Changes needed: add error handling to the plan"
mo run request-changes --issue 42 -m "See comment: missing error handling"
```

## Comments

```bash
# Add a comment. The authenticated identity is the author.
# --display-name is only a display alias.
mo issue comment create 42 --display-name "Ada" --body "Looks good but check edge cases"

# The CLI does not currently delete comments. Use the Web UI or API.
```

The comment area is at the bottom of the Issue details page. During Plan, the
stage's Mohist Agent reads comments as additional context.

## Prerequisites

To require #10 to finish before #11 starts:

```bash
mo issue prereq add 11 10    # Make #11 wait for #10.
mo issue prereq remove 11 10 # Remove the dependency.
```

Mohist checks each prerequisite before starting an Issue and rejects the start
when a prerequisite is incomplete. The Issue details page has an **Add
Prerequisite** section.

## Mark an Issue Done Manually

When work was completed outside the Workflow, explicitly mark the Issue Done:

```bash
mo issue done 42
```

This command applies to an in-progress Issue without child Issues whose
Workflow has stopped permanently or completed, including an Issue with no
Workflow. A failed Workflow can still be retried. First use
`mo run stop --issue 42 --yes` to end it explicitly, then mark the Issue Done.
The command does not stop the Workflow or reset its AgentSession.

Marking an already Done Issue succeeds without creating a second completion
record. Manual completion preserves the original stopped or completed Workflow
history and counts toward delivered Epic progress. A parent Issue's Done state
is derived from its child Issues and cannot be overridden manually.

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

Use `pause` to preserve recovery and resume later. It ends the current AgentTurn
and puts the Workflow in a paused state. Use `stop` to end the WorkflowRun
permanently. Use `done` when work was delivered outside the Workflow. Use
`close` when the Issue will not be done; it moves the Issue to `cancelled` and
can be reopened. Use `reopen` to return a closed Issue to `backlog`.

## Recover from Failure

Recovery is a core Mohist capability. See
[Troubleshooting](troubleshooting.md) for the situation-to-recovery mapping.

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

Before an Issue starts, edit its title, body, priority, and labels:

```bash
mo issue edit 42 --title "New title" --priority p1
```

See [CLI Reference](cli-reference.md) for all edit options. Edit the body of an
active Issue with care because its Stage's Mohist Agent may already be working
from the previous body.

## Complete CLI Reference

See the Issue section in [CLI Reference](cli-reference.md) for the authoritative
`mo issue` command surface. This document contains scenario-based examples.
Use `mo issue <command> --help` for all options.

## Implementation Gaps

The CLI cannot delete comments. Users must use the Web UI or API for that
operation.

---

Implementation source: `packages/server/src/Mohist.Server/Issue/`,
`Api/IssueRoutes.*`, and the CLI under `packages/cli/`.
