---
name: mohist-create-epic
description: Mechanical execution of creating a Mohist epic: write the milestone description (Goal/Background/Non-goals/Scope), set the priority, run mo epic create after confirmation, then link child issues, set prerequisites, and manage the autopilot lifecycle (start/pause/resume plus the done/close terminal states). Use when the user wants to land a set of explored issues that share one milestone goal as an epic. Trigger phrases include "create an epic", "link issue to epic", "mo epic create", "epic lifecycle", "autopilot". The issue-vs-epic decision is made by mohist-explore.
---

# mohist-create-epic

This skill owns the **mechanics** of creating a Mohist epic and driving it through
its full lifecycle. An epic is an organizational milestone that groups 3+ issues
toward a single product goal. Epic content is a lightweight milestone description,
not the five-section PRD used for issues.

Whether to create an epic (versus standalone issues) is decided upstream in
`mohist-explore` — its Scope stage determines whether the work shares one
milestone goal. This skill executes epic creation once that decision is made;
it does not re-litigate issue-vs-epic.

### Epic shape (no frontmatter)

Unlike issues, an epic has **no frontmatter, no workflow, no risk**. Do not invent
frontmatter fields for an epic — they are ignored. An epic has only:
`title`, `description` (long markdown), `priority`, and a derived `status`.

The `description` follows the milestone template in `references/epic-templates.md`:
Goal, Background, Non-goals, Scope (the issues it will contain).

The description is agent context too — member issues are planned and built from it.
The same writing rules as issue bodies apply (see `mohist-create-issue`'s universal
writing rules): state the milestone goal in one sentence, record the decisions and
the issue split with its dependency order, and cut anything the agent can cheaply
look up in the code.

### Priority guidance for epics

Epic priority rates the **milestone's** importance, not any single issue's. Use
`p0`–`p3` (lowercase), same scale semantics as issues but applied to the milestone.

### Creating an epic

```bash
mo epic create "<title>" --description "<markdown>" --priority p2
# --description: the milestone markdown (see epic-templates.md)
# --priority: p0|p1|p2|p3
# --project <id>: target project (else active project)
```

Note: `mo epic create` currently takes the description inline via `--description` only; there
is no `--description-file` yet. For long descriptions, write the markdown to a
file first, then pass its contents to `--description` via your shell, or use the API. (A
`--description-file` flag to match `mo issue create --body-file` is tracked as a
follow-up.)

### Linking issues to an epic

```bash
mo epic add <epic-id-or-number> <issue-id-or-number>
mo epic remove <epic-id-or-number> <issue-id>
```

Constraint: **an issue belongs to at most one primary epic.** Linking an issue
already in another epic fails with `DUPLICATE_EPIC_MEMBERSHIP`. Both args accept
id or number.

### Setting issue prerequisites (execution order)

When an epic's issues have a start order (issue B requires issue A first), record
it as prerequisites so the epic can advance one issue at a time without false
starts:

```bash
mo issue prereq add <B-number> <A-number>
mo issue prereq remove <B-number> <A-number>
```

A starts first; B becomes start-blocked ("waiting for #A") until A is delivered,
then B is free to start. Prefer fewer prerequisites — only real data/scaffold/invariant dependencies. Use the API only as a fallback when the CLI is unavailable.

### Lifecycle: prefer autopilot (start / pause / resume)

After an epic is created and its linked issues are in place, **the default way
to drive it is the autopilot lifecycle** — not manually starting each member
issue one by one. A `running` epic auto-advances to the next startable linked
issue whenever the current one reaches a terminal state, so you only step in
when the plan itself changes (pause) or the milestone is done.

The five lifecycle states (`idle` / `running` / `paused` / `done` / `closed`)
are managed by five operations; the autopilot three drive day-to-day progression,
`done` / `close` are the terminal tail.

| Operation | CLI | Effect |
|---|---|---|
| Start | `mo epic start <id>` | `idle` → `running`; auto-advances to the first startable linked issue |
| Pause | `mo epic pause <id>` | `running` → `paused`; stops future advancement, does NOT interrupt the in-progress issue |
| Resume | `mo epic resume <id>` | `paused` → `running`; re-evaluates readiness and advances |
| Done | `mo epic done <id>` | terminal `done` (requires no open linked issues / all linked issues terminal; fails with `EPIC_NOT_READY_TO_MARK_DONE` otherwise) |
| Close | `mo epic close <id>` | terminal `closed` (abandon the milestone) |

#### Idempotency

Each autopilot operation is **idempotent against its current state**: starting an
already-`running` epic is a no-op (does not error), pausing an already-`paused`
epic is a no-op, and resuming an already-`running` epic is a no-op. Only an
unexpected source state produces a conflict error (e.g. Start on a `paused`
epic). This makes autopilot safe to retry from automation without bookkeeping.

#### Running-but-idle

A `running` epic can be observable-but-not-advancing when there are still open
linked issues but **no startable next issue right now** (e.g. waiting on a
dependency, next issue is blocked). This **running-but-idle** is NOT a separate
state — the epic's `status` stays `running`, and `progress.nextIssueReason` in
`mo epic view` explains why. Use it to decide whether to wait, set
prerequisites, or `Pause` to stop the autopilot until you can unblock it.

```bash
# Drive the autopilot lifecycle
mo epic start  <epic-id-or-number>   # idle → running; auto-advances first issue
mo epic pause  <epic-id-or-number>   # running → paused (current issue keeps running)
mo epic resume <epic-id-or-number>   # paused → running

# Check why a running epic is idle
mo epic view <epic-id-or-number>     # inspect progress.nextIssue + nextIssueReason
```

**Recommend autopilot over manual per-issue starts.** Manually `mo issue start`
ing each member defeats the milestone model — you lose the running-but-idle
signal, the idempotent retry, and the auto-advancement on terminal. Use Start
once, then watch `mo epic view`; only fall back to manual issue commands when
the autopilot is `paused` and you specifically want to start one out of order.

### Lifecycle: terminal (done / close)

- `mo epic done <id>` — marks the milestone shipped. Requires **no open linked
  issues**: all linked issues must be terminal (`done` or `cancelled`); else
  fails with `EPIC_NOT_READY_TO_MARK_DONE`. `deliveredCount` counts only
  delivered issues, so cancelled linked issues satisfy readiness but do not
  count as delivered. `done` and `closed` are terminal — once entered, the epic
  cannot transition out.
- `mo epic close <id>` — abandons the milestone (not done, just dropped).

Use `done` for completed milestones, `close` for cancelled ones. The system may
also auto-transition a non-`paused`, non-terminal epic to `done` when it
recomputes and finds no open linked issues, so observing `done` is not by itself
evidence of a user action.

### User confirmation flow

Before creating, present to the user and wait for confirmation:

1. `title`, a one-line `description` gist, and `priority`.
2. The planned linked-issue list (numbers + titles) — or state "link later".
3. On confirm, run `mo epic create`; then `mo epic add` for each planned issue.

After creation, also confirm the autopilot posture:

4. Whether to `mo epic start` immediately (default for active milestones) or
   leave `idle` for now.

Never create an epic without confirmation.

### End-to-end creation checklist

- [ ] Every planned child issue passes the Scope gate defined in `mohist-explore` — **regardless of how the requirement content was produced**. The epic advances one issue at a time, so a child with no standalone value stalls the milestone; fix the split before creating.
- [ ] `description` follows Goal/Background/Non-goals/Scope.
- [ ] `priority` is `p0`–`p3`.
- [ ] No frontmatter/workflow/risk fields invented.
- [ ] User confirmed title, description summary, priority, and link plan.
- [ ] Autopilot posture confirmed: `mo epic start` now vs leave `idle`.
- [ ] Lifecycle choice understood: autopilot (`start` / `pause` / `resume`) for
      day-to-day driving; `done` / `close` only as terminal tail.
