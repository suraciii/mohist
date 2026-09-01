# Planning with Epics

An Epic organizes Issues around one product goal and feeds ready work into the
production line. It records the goal and current membership; each Issue keeps
its own Workflow and state.

## Product Commitments

- An Epic uses its Project-scoped number as its permanent identity.
- A new Epic is `idle` and does not advance work until a user starts it.
- A running Epic starts one ready linked Issue at a time.
- Pause stops future advancement without interrupting the current Issue.
- Epic progress and the next candidate derive from current Issue membership and state.
- A child Issue cannot belong to an Epic. A parent Issue can be an Epic member.
- A cancelled Issue satisfies Epic completion but does not count as delivered.
- Repeating a lifecycle operation in its target state is safe and has no side effects.

## When to Use an Epic

Use an Epic when:

- A product goal needs at least three Issues, such as a complete authentication
  system.
- You need a roadmap view or progress for the complete goal.
- Ready Issues under one goal should advance automatically in sequence.

Do not use an Epic for a small independent change or a goal that is not
understood yet. Keep an unclear idea in the backlog first.

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

An Epic has a short title, a long description, a priority from `p0` through
`p4`, and Project-scoped membership. The description can contain Goal,
Background, Non-goals, and scope.

Both Epics and Issues use their Project-scoped number as their permanent
identity. Commands, pages, and events use that number. Users do not need a
second ID system.

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

Adding a link changes the Issue's current Epic to the selected Epic. If it
belonged to another Epic, the operation moves it directly. Removing a link has
an effect only when the Issue currently belongs to the selected Epic. Repeating
either operation is safe.

### Web UI

On the Issue details page, select **Edit** and choose an Epic. You can also add
or remove an Issue in the Linked Issues section of the Epic details page.

An Issue belongs to at most one Epic. The Issue owns this membership. The Epic
derives its members, progress, and next candidate from current Issue state.

A `closed` Epic rejects new links and must be reopened first. Linking an open
Issue to a `done` Epic changes the Epic to `running`. Linking a terminal Issue
does not wake the Epic.

## View an Epic

### Web UI

- The **Epics list page** groups Epics by status and shows current status and
  next candidate.
- The **Epic details page** shows linked Issues, delivered and total progress,
  current status, and next action.

### CLI

```bash
# List all Epics
mo epic list --project <project>

# Show details by the Project-scoped Epic number
mo epic view <epic-number> --project <project>
```

The details view shows delivered, total, blocked, and in-progress Issue counts.
It identifies the next Issue, explains why no Issue is advancing, and reports
whether the Epic can be marked Done.

## Epic Lifecycle

An Epic has five lifecycle states: `idle`, `running`, `paused`, `done`, and
`closed`.

```text diagram
         +---+
         | * |
         +-+-+
           |
           v
       +------+
       | idle +<------++
       +--+---+       ||
          |           ||
          v           ||
     +---------+      ||
     | running +<-----++++
     +----+----+      ||||
     +----+------+    ||||
     v           v    ||||
+--------+   +------+ ||||
| paused +---| done +<+++|
+----+---+   +------+ || |
     +-+              || |
       v              || |
  +--------+          || |
  | closed +<---------++-+
  +--------+
```

A new Epic is `idle`. Start changes it to `running` and tries to start the
first startable linked Issue. Pause changes `running` to `paused`; future
advancement stops, but the current in-progress Issue continues. Resume changes
`paused` to `running` and evaluates readiness again.

The Epic is `done` when it is not `paused` or `closed` and all linked Issues are
terminal. A user can mark it Done, or automatic progress recalculation can find
the same condition. Close from `idle`, `running`, or `paused` changes the Epic
to `closed` and stops future work. Reopen changes `done` or `closed` to `idle`.
Linking a new open Issue to a `done` Epic changes it to `running`; a `closed`
Epic accepts no new links.

### Start, Pause, and Resume

Start (`mo epic start <number>`, or **Start Epic** in the Web UI) changes
`idle` to `running` and tries to start the first startable linked Issue. Pause
(`mo epic pause <number>`, or **Pause**) changes `running` to `paused` and
stops future advancement without interrupting the current Issue. Resume
(`mo epic resume <number>`, or **Resume**) changes `paused` to `running`,
evaluates readiness again, and advances.

Repeating an operation when the Epic is already in its target state is safe and
has no side effects. Mohist rejects an operation from another incompatible
state and reports the current state.

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
`running` Epic starts the next startable Issue. An Epic in `idle` or `paused`
does not advance automatically.

The Epic starts one ready Issue at a time. This differs from a Composite Issue,
which advances its children in parallel. The normal concurrency limit still
applies.

A `running` Epic with open linked Issues but no startable next Issue remains in
the observable **running-but-idle** condition. This is not a sixth state. The
Epic details explain why nothing is advancing, such as an in-progress Issue,
a blocked Issue, or an unmet prerequisite.

When there are no linked Issues, the details identify an empty Epic. When all
linked Issues are terminal, the details indicate that the Epic can be marked
Done and automatic recalculation may change it to `done`.

Advancement does not occur when:

- The Epic is not `running`.
- The Epic has no linked Issues.
- All linked Issues are terminal.
- The next Issue is blocked or has an unmet prerequisite.

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

Delivered progress counts only Issues in `done`. A `cancelled` Issue is
terminal and satisfies completion, but it does not count as delivered.

## Recommended Workflow

1. Create an Epic with a Goal, Background, and Non-goals. It starts in `idle`.
2. Create or link Issues over time. Give each Issue one clear deliverable.
3. Run `mo epic start` to begin automatic advancement. Use `pause` and `resume`
   as needed.
4. Run `mo epic done` when no linked Issue remains open. Use `reopen` and Start
   when the goal needs more work.

## Relationship to Workflows

An Epic controls when linked Issues advance. It does not change the execution
rules of an individual Issue's Workflow.

Each linked Issue uses its own Workflow, such as `mohist/local` or a per-Issue
selection. The Epic sends the next Issue to that Workflow but does not define
its stages or tasks.

## Relationship to Child Issues

Epics and Composite Issues are independent organization axes. An Epic organizes
multiple deliverables under a product goal. A Composite Issue divides the work
for one requirement.

- A child Issue must not link to an Epic. Epic advancement never operates on a
  child Issue.
- A parent Issue is a normal Epic member. When selected, the Epic starts the
  parent, which advances its children. When the parent reaches Done, it counts
  toward Epic progress.
- The Epic does not inspect the Composite Issue structure.

## Boundary

Epics are not nested. They have no dependency graph, roadmap timeline view, or
single command that starts every backlog Issue as a batch. Use Issue
prerequisites and the normal one-at-a-time advancement for ordering.

## Implementation Gaps

The Web UI provides a list rather than a roadmap timeline. Mohist cannot start
every backlog Issue in an Epic as one batch.

---

Implementation source: `packages/server/src/Mohist.Server/Epic/` and
`Api/EpicRoutes.cs`.
