# Troubleshooting and Recovery

Use this guide when an Issue stops advancing.

## Inspect State First

```bash
mo issue view <number>
```

You can also open Issue details in the Web UI. Inspect three fields:

| Field | Meaning |
|---|---|
| `health` | `blocked`, `cancelled`, or `done` |
| `status` | `in-progress`, `done`, or `cancelled` |
| `blockedReason` | Cause when health is `blocked` |

## Health Actions

See [Workflow Health](the-workflow.md#health) for the meaning of each value.
This table maps each value to an operator action:

| Health | Action |
|---|---|
| `active` | Wait |
| `paused` | Approve, reject, or resume |
| `blocked` | Use a recovery action below |
| `cancelled` | Reopen when necessary |
| `done` | Accept or archive |

## Recovery Commands

| Scenario | Command | Meaning |
|---|---|---|
| An automated Check failed | `mo run retry --issue <n>` | Retry the current failure point |
| Runner crashed and current work failed | `mo run retry --issue <n>` | Retry after Runner recovers |
| Rebuild the current stage completely | `mo run rerun --issue <n> --from-stage <stage>` | Discard output from the target stage and later stages, then rerun |
| The current stage is stuck | `mo run pause --issue <n>` | Pause current execution and resume later |
| Work must not continue | `mo run stop --issue <n> --yes` | Stop the run permanently; it cannot resume |
| Workflow stopped, but work was delivered another way | `mo issue done <n>` | Enter Done and retain Workflow history |
| Abandon the Issue | `mo issue close <n>` | Enter the cancelled terminal state |

Every recovery command preserves Issue history. State and artifacts remain
unless the Issue is closed and archived.

A retry restores the complete automatic recovery budget. When a review and
repair loop exhausts that budget, retry starts a new budget.

## Common Failure Modes

### 1. Plan Does Not Produce `proposal.md`

**Symptom:** Plan is blocked and `proposal.md` does not exist.

Possible causes:

- OpenCode is missing or its path is wrong.
- Model API configuration or rate limiting prevents execution.
- The Issue body is too ambiguous for a stable plan.

Inspect:

```bash
mo issue logs <n>              # Read the specific error
mo session list --issue <n>   # Inspect the Agent execution
```

Resolve:

- Confirm that `opencode --help` works.
- Inspect model configuration under Settings > OpenCode in the Web UI.
- Make the Issue body more specific, then retry.

### 2. Build Does Not Produce Code

**Symptom:** A Build task fails repeatedly.

Possible causes:

- The Repository is too large for the Agent context.
- The test suite itself fails or is unstable.
- Task definitions conflict.

Inspect:

```bash
mo session list --issue <n>
```

Resolve:

- Edit `tasks.json` and remove a blocked task.
- Reject the Plan so the Agent plans again.
- Split the Issue into smaller child Issues.

### 3. Check Review Fails

**Symptom:** The Check Agent returns a failing review verdict.

**Meaning:** The Agent found a problem while reviewing its output.

This is expected quality control. When the Profile configures convergence, the
Workflow creates a repair Build automatically.

Choose one action:

- Wait for convergence to repair the problem and inspect its panel.
- Reject so the Agent builds again.
- Approve the current output when the finding is not material.

### 4. Integrate Has a Merge Conflict

**Symptom:** Integrate is blocked with a conflict.

**Cause:** The base branch advanced while the Issue was running, because
another Issue merged or a user pushed manually.

First try automatic rebase:

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

Verify Runner state:

```bash
mo runner status
```

A waiting Workflow continues automatically when Runner returns. For a blocked
Workflow, retry after Runner recovers. Completed stages and history remain.

### 6. AgentSession Produces No Output

**Symptom:** The Issue shows running but produces no output for more than ten
minutes.

Inspect:

```bash
mo session list --issue <n>   # Inspect the latest AgentSession entry
mo server logs                # Inspect the Server application log tail
```

Resolve:

```bash
mo run pause --issue <n>     # Pause
mo run resume --issue <n>    # Continue from the pause point
# Or
mo run retry --issue <n>     # Retry the failure point
```

### 7. Drift Warning

**Symptom:** Issue details show a "Base Drift Detected" panel.

**Meaning:** The base branch advanced while the Issue was running. Execution has
not failed, but Integrate can fail later.

| Decision | Meaning | Action |
|---|---|---|
| `needs-attention` | Drift must be handled | Rebase now |
| `defer` | Mohist can handle it automatically later | Wait |
| `suggest` | Handling is recommended | Decide from current context |
| `enqueue` | Handling is queued | Wait |

Rebase explicitly with:

```bash
mo issue rebase <n>
```

Some drift resolves automatically and can be left alone.

## Failure Signals

Constant monitoring is not required. Mohist signals a problem through:

- The **Needs attention** banner above the Web UI board.
- A red blocked indicator on the Issue card.
- The red error panel on Issue details.
- A Hermes notification for an Approval point, failure, or completion. See
  [Hermes Notifications](hermes-notifications.md).

## Prevention

- **Write a specific Issue body.** Ambiguous bodies cause many poor Plans. See
  [Write an Effective Issue Body](issues.md#write-an-effective-issue-body).
- **Keep an Issue small.** One Issue should do one thing. Small Issues recover
  easily, plan better, and execute concurrently.
- **Avoid changing the base branch during Agent work.** A change can cause drift
  or conflict.
- **Monitor capacity.** `mo runner status` shows use. Work above capacity waits
  instead of failing.
- **Preserve important Workspace work remotely.** Runner materialization is
  rebuildable; commit and push changes that must survive Runner loss or cleanup.
- **Investigate repeated failure patterns.** When several Issues block on the
  same work, do not retry each one indefinitely. Common causes include an
  ambiguous input template, slow or unstable tests, unclear module boundaries,
  or a Workflow Profile that does not fit the work. Repair the common cause
  before increasing concurrency.

## Complete Diagnostics

When the cause remains unclear, collect:

```bash
mo issue logs <n>
mo issue events <n>
mo session list --issue <n>
mo server logs

# Then inspect error-level entries on the Web UI Logs page.
```

For a Mohist defect, create an Issue with the Issue number, `health`, `status`,
`blockedReason`, relevant log excerpts, and reproduction steps.

---

Implementation source: recovery spans the Issue and Workflow domains, including
health and blocked-state handling.
