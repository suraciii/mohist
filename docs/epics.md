# Planning with Epics

An Epic organizes separate Issues around one product goal and continuously
feeds ready work into the production line. It defines which Issues belong to
the goal and which Issue can enter a Workflow next.

## When to Use an Epic

Use an Epic when:

- A product goal needs at least three Issues, such as a complete authentication
  system.
- You want to plan a roadmap and identify the goals for a future period.
- You need progress for the complete goal instead of only individual Issues.
- Ready Issues under one goal should advance automatically in sequence.

Do not use an Epic for:

- A small, independent change.
- A goal that is not understood yet. Keep those ideas in the backlog first.

## Create an Epic

### CLI

```bash
mo epic create "Add user authentication" \
  --description "Complete authentication: registration, login, password reset, and session management" \
  --priority p1 \
  --project <project-name-or-id>
```

`--description` or `-d` accepts long Markdown. Write it in a file first when
practical. `--priority` accepts `p0` through `p4`.

### Web UI

On the Epics page in the top navigation, select **New Epic**.

### Epic Properties

An Epic has a short title and a long description that holds the Goal,
Background, Non-goals, and member Issues. Its priority runs from `p0` through
`p4`. Its status is `idle`, `running`, `paused`, `done`, or `closed`,
controlled by the lifecycle.

Both Epics and Issues use their Project-scoped number as their permanent
identity. Commands, pages, and events use that same number. Users do not need
to understand another ID system.

The following is an effective Epic description:

```markdown
## Goal
Users can register, sign in, and recover a password.

## Background
The product has no identity system, and every API is public. Identity is needed
before personalized features can be added.

## Non-goals
- Do not add OAuth. Use email and password first.
- Do not add RBAC. Use one admin role first.
- Do not add 2FA.

## Scope
- Registration with email and password.
- Sign in and sign out.
- Password reset through an email link.
- Session management with JWT.
- Middleware that protects APIs.
```

## Link an Issue to an Epic

### CLI

```bash
mo epic add <epic-number> <issue-number>
mo epic remove <epic-number> <issue-number>
```

Adding a link changes the Issue's current Epic to the selected Epic. If the
Issue belonged to another Epic, this operation moves it directly. Removing the
link has an effect only when the Issue currently belongs to the selected Epic.
Repeating the same operation is safe.

### Web UI

On the Issue details page, select **Edit** and choose an Epic. You can also add
or remove an Issue in the Linked Issues section of the Epic details page.

An Issue must not belong to more than one Epic at a time. This membership is part of
the Issue. The Epic derives its members, progress, and next candidate from the
current membership of its Issues.

A `closed` Epic rejects new links and must be reopened first. Linking an open
Issue to a `done` Epic changes the Epic to `running`. Linking a terminal Issue
does not wake the Epic.

## View an Epic

### Web UI

- The **Epics list page** groups all Epics by status and shows the current
  status and next candidate for each Epic.
- The **Epic details page** shows the Epic, linked Issues, delivered and total
  progress, current status, and next action.

### CLI

```bash
# List all Epics
mo epic list --project <project>

# Show details by the Project-scoped Epic number
mo epic view <epic-number> --project <project>
```

The details view in the Web UI or `mo epic view` shows how many Issues are
delivered, total, blocked, and in progress. It also identifies the next Issue,
explains why no Issue is advancing, and reports whether the Epic can be marked
Done.

## Epic Lifecycle

An Epic has five lifecycle states. User operations and automatic advancement
both drive this state.

A new Epic is `idle`: created, but not advancing automatically. Start from
`idle` enters `running`, where the Epic advances linked Issues automatically.
Pause from `running` enters `paused`: future advancement is paused, but the
current in-progress Issue continues. The Epic is `done` when it is currently
complete, with no open linked Issues: mark it Done while it is not `paused` or
`closed` and all linked Issues are terminal, or let automatic progress
recalculation find the same condition. Close from `idle`, `running`, or
`paused` enters `closed`: the Epic is closed and will not continue.

- A new Epic starts in `idle` and does not advance automatically. You must
  explicitly Start it to enter `running`.
- `done` and `closed` are completion states. Reopen explicitly restores either
  state to `idle`. In addition, linking a new open Issue to a `done` Epic
  automatically restores it to `running`. A `closed` Epic does not accept new
  links.

### Start, Pause, and Resume

Start (`mo epic start <number>`, or **Start Epic** in the Web UI) changes
`idle` to `running` and tries to start the first startable linked Issue. Pause
(`mo epic pause <number>`, or **Pause**) changes `running` to `paused` and
stops future advancement without interrupting the current in-progress Issue.
Resume (`mo epic resume <number>`, or **Resume**) changes `paused` to
`running`, evaluates readiness again, and advances.

Repeating an operation when the Epic is already in its target state is safe and
has no side effects. For example, Start succeeds for an Epic that is already
`running`. Mohist rejects an operation from any other incompatible state and
reports the current state.

```bash
# Start: idle -> running, then try to start the first linked Issue.
mo epic start 12

# Pause: running -> paused without interrupting the current Issue.
mo epic pause 12

# Resume: paused -> running and resume advancement.
mo epic resume 12
```

### Automatic Advancement and Running-but-Idle

When the current in-progress linked Issue reaches `done` or `cancelled`, a
`running` Epic automatically advances to the next startable Issue. An Epic in
`idle` or `paused` does not advance automatically.

This behavior does not start all Issues at once. The Epic sends one ready Issue
at a time to its Workflow so that the owner does not have to advance each one
manually.

A `running` Epic with open linked Issues but no next startable Issue is in the
observable **running-but-idle** condition. Its state remains `running`; this is
not a sixth state. The Epic details in the Web UI or `mo epic view` explain why
nothing is advancing. For example, an in-progress Issue might still be running,
or the next Issue might be blocked or have an unmet prerequisite.

When there are no linked Issues, the details identify an empty Epic. When all
linked Issues are terminal, the details indicate that the Epic can be marked
Done, and the system can change it to `done` automatically.

#### Conditions That Prevent Advancement

- The Epic is not `running`.
- The Epic has no linked Issues.
- All linked Issues are terminal.
- The next Issue is not startable because it is blocked or has an unmet
  prerequisite.

### Mark Done, Close, and Reopen

```bash
# Mark Done. The Epic must not be paused or closed, and it must have no open linked Issues.
mo epic done <epic-number>

# Close and stop future work.
mo epic close <epic-number>

# Reopen: done or closed -> idle.
mo epic reopen <epic-number>
```

The Epic details page provides **Mark Done**, **Close Epic**, and **Reopen**.

In addition to manual Mark Done, the system changes an eligible Epic that is
not `paused` or `closed` to `done` when recalculation finds that all linked
Issues are terminal. The observed completion does not require another user
operation.

Delivered progress counts only Issues in `done`. A `cancelled` Issue is terminal
and satisfies the completion condition, but it does not count as delivered.

## Recommended Workflow

1. Create an Epic with a Goal, Background, and Non-goals. It starts in `idle`.
2. Create or link Issues over time. Give each Issue one clear deliverable.
3. Run `mo epic start` to begin automatic advancement. Use `pause` and `resume`
   as needed.
4. Run `mo epic done` when no linked Issue remains open. Use `reopen` and Start
   when the goal needs more work.

## Relationship to Workflows

An Epic affects when linked Issues advance. It does not change the execution
rules of an individual Issue's Workflow.

Each linked Issue still uses its own Workflow, such as the default
`mohist/local` or a per-Issue selection. The Epic decides when to send the next
Issue to a Workflow. It does not define the Steps inside that Workflow.

## Relationship to Sub-Issues

Epics and composite Issues are independent organization axes. See
[Composite Issues and Sub-issues](sub-issues.md). An Epic organizes multiple
deliverables under a product goal. A composite Issue divides the internal work
for one deliverable. The boundary rules are:

- A child Issue must not link to an Epic. Epic advancement never operates on a
  child Issue.
- A parent Issue is a normal Epic member. When selected, the Epic starts the
  parent, which advances its children. When the parent reaches Done, it counts
  toward Epic progress. The Epic does not inspect the composite structure, and
  these rules do not otherwise change Epic behavior.

## Status

Epic commands use the Project-scoped Epic number, including `reopen`. Each
Issue owns its current Epic membership, while Epic reads derive progress and
the next startable Issue from current Issue state. This keeps membership under
one authority and avoids a second list that can drift.

## Current Limitations

- There is no roadmap timeline view, only a list.
- Epics cannot be nested.
- There is no dependency graph between Epics.
- Mohist cannot start every backlog Issue in an Epic as one batch.

---

Implementation source: `packages/server/src/Mohist.Server/Epic/` and `Api/EpicRoutes.cs`.
