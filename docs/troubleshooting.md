# Troubleshooting and Recovery

Use this guide when an Issue stops advancing. It maps each symptom to a cause
and an operator action while preserving the Issue history.

## Product Commitments

- An operator can inspect health, status, and the blocking reason before taking
  recovery action.
- Retry, rerun, pause, resume, stop, and close keep the Issue history and
  explain their different effects.
- A recovery command never silently creates a second execution path or discards
  recorded evidence.
- Each common failure identifies what to inspect and which action is safe.
- Unknown failures provide one complete diagnostic path for creating a useful
  follow-up Issue.

## Inspect State First

```bash
mo issue view <number>
```

You can also open Issue details in the Web UI. Inspect three fields: `health`
is `blocked`, `cancelled`, or `done`; `status` is `in-progress`, `done`, or
`cancelled`; `blockedReason` gives the cause when health is `blocked`.

## Health Actions

See [Workflow Health](the-workflow.md#health) for the meaning of each value.
Each value maps to an operator action:

- `active`: wait.
- `attention`: approve or request changes when available.
- `paused`: resume.
- `blocked`: use a recovery action below.
- `cancelled`: reopen when necessary.
- `done`: accept or archive.

## Recovery Commands

- An automated Check failed: `mo run retry --issue <n>` retries the current
  failure point.
- Runner crashed and current work failed: `mo run retry --issue <n>` retries
  after Runner recovers.
- Repeat the current stage and discard its artifacts:
  `mo run rerun --issue <n>` starts the current stage again as a new attempt.
- Repeat from a selected stage: `mo run rerun --issue <n> --from-stage <stage>`
  discards output from the target stage and later stages, then reruns.
- The current stage is stuck: `mo run pause --issue <n>` pauses current
  execution so it can resume later.
- The Issue is paused and work should continue: `mo run resume --issue <n>`
  continues the paused run.
- Work must not continue: `mo run stop --issue <n> --yes` stops the run
  permanently; it cannot resume.
- The Workflow stopped, but the work was delivered another way:
  `mo issue done <n>` enters Done and retains Workflow history.
- Abandon the Issue: `mo issue close <n>` enters the cancelled terminal state.

Every recovery command preserves Issue history. State and artifacts remain
unless the Issue is closed and archived.

A retry restores the complete automatic recovery budget. Approval Feedback is
not automatic recovery and has no Request Changes limit.

## Common Failure Modes

### 1. Plan Does Not Produce `PLAN.md`

**Symptom:** Plan is blocked and `PLANS/PLAN.md` does not exist.

**Cause:**

- OpenCode is missing or its path is wrong.
- Model API configuration or rate limiting prevents execution.
- The Issue body is too ambiguous for a stable plan.

**Action:** Check:

```bash
mo issue logs <n>              # Read the specific error
mo session list --issue <n>   # Inspect the Agent execution
```

**Action:**

- Confirm that `opencode --help` works.
- Inspect model configuration under Settings > OpenCode in the Web UI.
- Make the Issue body more specific, then retry.

### 2. Build Does Not Produce Code

**Symptom:** A Build task fails repeatedly.

Cause:

- The Repository is too large for the Agent context.
- The test suite itself fails or is unstable.
- Task definitions conflict.

**Action:** Check:

```bash
mo session list --issue <n>
```

**Action:**

- Edit the task list in `PLANS/tasks.json` and remove a blocked task.
- Request Changes at the Plan Approval Point so its Feedback Tasks revise the
  output without rerunning the original Plan Tasks.
- Split the Issue into smaller child Issues.

### 3. Check Review Reports Findings

**Symptom:** The Check review evidence reports must-fix findings.

**Cause:** A separate Agent session found a problem while reviewing the
diff. The review is evidence, not a verdict. The Workflow does not repair
findings on its own.

**Action:** Choose one:

- Request Changes so the configured Feedback Tasks repair the findings and the
  Stage Checks run again.
- Approve the current output when the finding is not material.

### 4. Integrate Has a Merge Conflict

**Symptom:** Integrate is blocked with a conflict.

**Cause:** The base branch advanced while the Issue was running, because
another Issue merged or a user pushed manually.

**Action:** First try automatic rebase:

```bash
mo issue rebase <n>
```

If it also conflicts:

1. Run `mo workspace view issue-<n> --json home` to find the actual Runner and
   Workspace path.
2. In that reported path, resolve conflicts manually.
3. Run `git add` and `git rebase --continue`.
4. Resume with `mo run resume --issue <n>`.

### 5. Runner Is Unavailable

**Symptom:** A Workflow waits for a long time, or the Issue becomes blocked
with `runner-lost`.

**Cause:** Runner is stopped or disconnected. Executing work fails explicitly
with `runner-lost`; it is not replayed automatically.

**Action:** Verify Runner state:

```bash
mo runner status
```

A waiting Workflow continues automatically when Runner returns. For a blocked
Workflow, retry after Runner recovers. Completed stages and history remain.

### 6. AgentSession Produces No Output

**Symptom:** The Issue shows running but produces no output for more than ten
minutes.

**Cause:** Agent execution or the Runner response is stalled.

**Action:** Check:

```bash
mo session list --issue <n>   # Inspect the latest AgentSession entry
mo server logs                # Inspect the Server application log tail
```

Fix:

```bash
mo run pause --issue <n>     # Pause
mo run resume --issue <n>    # Continue from the pause point
# Or
mo run retry --issue <n>     # Retry the failure point
```

### 7. Drift Warning

**Symptom:** Issue details show a "Base Drift Detected" panel.

**Cause:** The base branch advanced while the Issue was running. Execution has
not failed, but Integrate can fail later.

**Action:** The panel shows one of four decisions:

- `needs-attention`: drift must be handled; rebase now.
- `defer`: Mohist can handle it automatically later; wait.
- `suggest`: handling is recommended; decide from current context.
- `enqueue`: handling is queued; wait.

Rebase explicitly with:

```bash
mo issue rebase <n>
```

Some drift resolves automatically and can be left alone.

## Failure Signals

**Symptom:** You do not know where a failure is visible.

**Cause:** Mohist reports failures through several operator surfaces.

**Action:** Check the **Needs attention** banner above the Web UI board, the
blocked indicator on the Issue card, the error panel on Issue details, or a
Hermes notification for an Approval Point, failure, or completion. See
[Hermes Notifications](hermes-notifications.md).

## Repeated Failure Pattern

**Symptom:** Several Issues fail at the same stage or on the same type of work.

**Cause:** The shared cause may be an ambiguous Issue body or input template,
a slow or unstable test, an unclear module boundary, or an unsuitable Workflow
Profile. Repeated retries do not repair a shared cause.

**Action:** Write a specific Issue body. Keep one change in each Issue. Avoid
changing the base branch during Agent work. Check capacity with `mo runner
status`; work above capacity waits instead of failing. Commit and push important
Workspace changes so they survive Runner loss or cleanup. Repair the shared
cause before increasing concurrency. See [Write an Effective Issue Body](issues.md#write-an-effective-issue-body).

## Unknown Failure

**Symptom:** The documented entries do not explain why an Issue stopped.

**Cause:** The available state, event, session, and Server logs do not identify
a known failure mode.

**Action:** Collect:

```bash
mo issue logs <n>
mo issue events <n>
mo session list --issue <n>
mo server logs

# Then inspect error-level entries on the Web UI Logs page.
```

Create a Mohist defect Issue with the Issue number, `health`, `status`,
`blockedReason`, relevant log excerpts, and reproduction steps.

Implementation source: recovery spans the Issue and Workflow domains, including
health and blocked-state handling.

## Implementation Gaps

Provider-specific runtime errors may not match a named entry in this guide. Use
[Unknown Failure](#unknown-failure) when the symptom, cause, or recovery action
is not yet documented.