# GitHub

The GitHub integration makes a connected GitHub repository the public mirror of
a Mohist Project's work. Once a Repository is connected, every Mohist Issue
targeting it has exactly one GitHub Issue as its mirror. A GitHub Issue, in
contrast, belongs to GitHub alone until someone explicitly hands it to Mohist.

The two directions follow different rules:

- **Mohist to GitHub is passive.** Creating and updating a Mohist Issue
  projects to its GitHub mirror automatically. Nobody asks for synchronization.
- **GitHub to Mohist is imperative.** A GitHub Issue enters Mohist only through
  an explicit command in a GitHub comment. Nothing is imported by labels or
  filters.

Mohist remains the authority for execution state. For a linked pair, the two
sides keep title and body in sync, while Workflow state projects only from
Mohist to GitHub.

Delivery through a GitHub Pull Request during the Integrate stage is separate.
See [Workflow Profiles](workflow-profiles.md) and
[GitHub PR Actions](actions/github-pr.md).

## Mental Model

- **The GitHub Issue is the superset.** Everything Mohist tracks is visible on
  GitHub; not everything on GitHub is tracked by Mohist.
- **A mirror, not a second issue tracker.** The mirror's number, URL, and sync
  health are first-class facts of the Mohist Issue. An Agent reading
  `mo issue view` never parses labels or constructs URLs to find it.
- **One command brings work in.** `/mohist start` in a GitHub comment hands an
  Issue to the Mohist Workflow.
- **A linked pair shares content, not execution.** Title and body synchronize
  in both directions. Stage, Approval, and Run state project only outward.
- **A Workflow is optional.** An Issue without a Workflow Profile runs no
  production line; when linked, its lifecycle follows the GitHub Issue. See
  [Issue Management](issues.md).

## Connecting Repositories

A GitHub connection binds one GitHub repository to one registered Repository of
the Project; see [Repositories](repositories.md). A Project that declares
several repositories can hold several connections. An Issue never carries
GitHub coordinates: its mirror location is determined by its target repository.

```bash
mo github connect owner/repo                 # matched to a registered Repository by git URL
mo github connect owner/repo --repo docs     # explicit when the match is ambiguous
mo github list                               # every Repository and its connection state
```

The guide prints the GitHub-side setup: a Repository webhook targeting the
Mohist Server, and the identity Mohist uses on GitHub — a deployment-specific
GitHub App (recommended, short-lived repository-scoped tokens) or a
fine-grained PAT with Issues read and write as fallback. An optional approver
list enables [Pull Request Review as Approval](#pull-request-review-as-approval).

Disabling a connection pauses mirroring and synchronization. Existing links
stay visible on Issues and show the paused state. Enabling re-projects every
linked Issue once. A connection cannot be deleted.

## The Mirror: Mohist to GitHub

When a new Issue's target repository is connected, Mohist creates the GitHub
mirror as part of creation and records a permanent one-to-one link. A Draft is
not mirrored; the mirror appears when the Issue is marked ready. The creating
side owns the initial content: a mirror created from Mohist takes the Mohist
title and body.

After creation, the mirror carries:

- **Title and body**, kept in sync in both directions; see
  [Linked Pairs](#linked-pairs).
- **Lifecycle projection.** Completing or cancelling the Mohist Issue closes
  the mirror with the matching reason.
- **Workflow progress** as the mutually exclusive `mohist:*` state labels
  (`in-progress`, `awaiting-approval`, `blocked`, `done`) and four low-volume
  milestone comments: mirror confirmation with the Mohist Issue link, arrival
  at an Approval point, completion with a delivery summary and Pull Request
  link, and cancellation with a reason.

Never mirrored: Mohist comments, labels, priority, model and runtime
configuration, prerequisites, Epic membership, and Workspace or Session facts.

A mirror failure never blocks the production system. See
[Sync Health and Recovery](#sync-health-and-recovery).

## Handing a GitHub Issue to Mohist

A comment starting with `/mohist` on an Issue in a connected repository is a
command. The first verb is:

```text literal
/mohist start
```

It creates the Mohist Issue from the GitHub title and body, records the link,
and starts the Workflow with the Project's default Profile — one utterance for
what `mo issue create` plus `mo issue start` do. GitHub labels `p0` through
`p4` map to Mohist priority at this moment.

Rules:

- **Permission comes from GitHub.** Only a commenter GitHub reports as a
  repository owner, member, or collaborator can hand work in. Other commands
  are ignored.
- **Idempotent.** A repeated `/mohist start` on an already-linked Issue replies
  with the existing Mohist Issue link and starts nothing.
- **The bot answers in place.** The confirmation reply carries the Mohist Issue
  link; a refusal replies with the reason, such as an unavailable Repository.
- **Comments stay on GitHub.** Command comments and ordinary discussion never
  enter the Mohist comment thread.

The verb vocabulary is shared with `mo`. Future GitHub-side commands reuse the
same domain verbs (`stop`, `retry`, …) with comment replies instead of flags
and JSON output.

## Linked Pairs

Title and body of a linked pair synchronize in both directions. An edit whose
content equals the current value is the echo of Mohist's own write and is
ignored; a real edit updates the other side and is attributed in the Issue
timeline (`github:<login>` or the Mohist caller). An edit arriving while a
Workflow runs updates the Issue record but does not retroactively change the
running Workflow's input.

Lifecycle depends on whether the Issue runs a Workflow:

- **Without a Workflow**, the GitHub Issue drives. Close as completed marks the
  Issue Done; close as not planned cancels it; reopening returns a cancelled
  Issue to the backlog. Reopening a completed Issue does not erase the
  delivery — Mohist suggests creating a follow-up Issue instead.
- **With a Workflow**, Mohist drives. Closing the GitHub Issue before the
  Integrate stage withdraws the requirement and cancels the work. Once the Run
  reaches Integrate, a GitHub close — including the automatic close caused by
  merging a linked Pull Request — is a delivery echo and is ignored. Mohist
  never places GitHub closing keywords in Pull Request bodies; the write-back
  closes the mirror after the Workflow completes.

Two existing Issues can be paired by hand, with the Mohist side as the content
source:

```bash
mo issue github link 42 owner/repo#817
mo issue github unlink 42    # stops synchronization; both sides keep existing
```

## Sync Health and Recovery

Every link is either healthy or carries the last synchronization error, shown
on the Issue in CLI and Web. One reconcile verb repairs a link — creating a
missing mirror, pushing the current Mohist state, and clearing the error:

```bash
mo issue github sync 42
```

State-label write-backs also heal themselves at the next Workflow milestone.

## Pull Request Review as Approval

When a connection has an approver list, a Check Approval point accepts GitHub
Pull Request reviews. An **Approve** review approves; a **Request changes**
review rejects with the review body as the reason; a **Comment** review has no
Approval action. Only reviews by listed GitHub users count, and the Approval
record attributes the GitHub user. Mohist decides from state at event arrival
and does not reverse a decision if a review is later dismissed.

## GitHub Events and Event Routing

GitHub actions on a connected repository — comments, edits, closure, reviews,
check results — reach Mohist as events in real time and can feed
[Event routing](event-routing.md). An event on a linked Issue belongs to that
Issue's lineage, so subscribing to every event under Issue #42 includes it.

## Non-goals

- **Bulk backfill.** Connecting a repository does not mass-create mirrors for
  existing Issues; `mo issue github sync` reconciles on demand.
- **Comment synchronization.** GitHub discussion is not imported; Mohist
  comments are not published. The bot's own milestone and command replies are
  the only Mohist-authored comments.
- **Rich GitHub fields.** Assignees, milestones, GitHub Projects, and sub-issue
  hierarchy are not read or written. The `p0`–`p4` priority mapping at hand-in
  is the only label read.
- **Runtime control on GitHub.** Beyond `/mohist` verbs, exceptional operations
  such as pause, stop, and retry remain in Mohist interfaces.

---

See [`design/github-integration.md`](../design/github-integration.md) for design
boundaries and protocol details.

## Status

The target model above replaces the earlier one-way intake design. Implemented
today: repository connection with signed ingress, feed-by-label intake, close
withdrawal, Pull Request review Approval, best-effort progress write-back,
Issues that explicitly run without a Workflow across Server, CLI, and Web, and
first-class mirror link visibility in CLI and Web. The link currently exposes a
deliberately provisional `healthy` sync state and does not report write-back
failures; real sync health belongs to the later recovery slice. Not yet
implemented: automatic mirroring of Mohist Issues, GitHub-driven lifecycle for
no-Workflow Issues, the `/mohist` command entry, two-way title and body sync,
and reconcile-based recovery. Feed-by-label intake and its connection
options are removed when the command entry lands.
