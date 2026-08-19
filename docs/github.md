# GitHub

The GitHub integration makes GitHub a requirement intake, progress board, and
Approval source for Mohist. A GitHub Issue enters the production system as a
requirement. Progress and results are written back to that Issue. A Pull
Request review can decide an Approval. Mohist remains the sole authority for
state; GitHub shows a projection of the production system, not a second copy of
its state.

Delivery through a GitHub Pull Request during the Integrate stage is separate.
See [Workflow Profiles](workflow-profiles.md) and
[GitHub PR Actions](actions/github-pr.md).

## Mental Model

- **GitHub owns what is wanted; Mohist owns producing it.** A GitHub Issue is a
  requirement. Mohist executes and tracks it after intake.
- **Three GitHub actions drive the production system:** Applying a feed label
  supplies a requirement, a Pull Request review decides an Approval, and
  closing an Issue withdraws the requirement.
- **Mohist leaves a GitHub record:** State labels, comments at important
  moments, and closing the Issue on completion.
- **Mohist records work under a separate bot identity:** Comments, labels, and
  Pull Requests are distinct from the administrator's manual work. Branch
  protection therefore applies equally to production output because a bot
  cannot approve its own Pull Request.
- **A snapshot becomes independent:** Mohist copies the GitHub Issue title and
  body when creating its Issue. After that, both evolve independently and do
  not read changes back. Change the requirement on the Mohist Issue.

## Connecting a Repository

First register the Repository with the Project; see
[Repositories](repositories.md). Then create a Connection:

```bash
mo github connect owner/repo
```

Mohist matches the URL to a registered Repository resource. If no resource
matches, the command explains that it must be registered first. The guide
prints steps for two GitHub-side configurations:

1. **Event delivery:** Add a Repository webhook that targets the Mohist Server
   URL so GitHub actions reach Mohist in real time.
2. **GitHub identity:** Configure the identity used by Mohist on GitHub in one
   of two forms:

   - **GitHub App, recommended:** Install a deployment-specific bot such as
     `your-mohist[bot]` in the Repository. Mohist obtains short-lived,
     Repository-scoped tokens as needed instead of retaining a long-lived
     GitHub access token. Write-back and delivery, including push and Pull
     Request creation, use the bot identity. Runner does not retain GitHub
     credentials.
   - **Fine-grained PAT, fallback:** Give it only Issues read and write access
     for write-back. It cannot modify code. Clone, push, and Pull Request
     delivery continue to use the Runner's existing Git credential; see
     [Runner Guide](runner.md). Records appear under the administrator or
     Runner account.

Optional configuration:

- **Feed label:** Defaults to `mohist`. Applying it supplies the requirement.
- **Feed mode:** Starts work immediately by default. Use `--feed-mode backlog`
  to create only a backlog Issue for a later manual start.
- **Approver list:** Empty and disabled by default. Only Pull Request reviews
  from listed GitHub users decide an Approval.

One GitHub Repository can connect to only one Project. A Project can connect to
several Repositories.

## Requirement Intake by Label

Applying the feed label to a GitHub Issue creates a corresponding Mohist Issue.
Mohist copies its title and body, selects the Repository bound to the
Connection, maps labels `p0` through `p4` to Mohist priority, and ignores other
labels. The default feed mode starts work immediately, without leaving GitHub.

Rules:

- **A GitHub Issue is supplied once.** Removing and reapplying the label does
  not create a duplicate.
- **Origin is traceable.** The Mohist Issue links to its GitHub origin.
- **Withdrawal cancels work.** Closing the GitHub Issue cancels the Mohist
  Issue if it is not complete. The production system does not continue work
  after the requester withdraws it.
- **Repository permission controls intake.** Anyone who can apply labels can
  supply a requirement. Mohist adds no additional intake list. The separate
  list controls only Approvals.

## Progress Write-back

Mohist projects progress from an Issue with a GitHub origin back to that GitHub
Issue.

**State labels** use the mutually exclusive `mohist:` prefix, with at most one
present at a time:

- `mohist:in-progress` — the production system is running.
- `mohist:awaiting-approval` — work is waiting at an Approval point.
- `mohist:blocked` — work is blocked and needs intervention.
- `mohist:done` — work is complete; the GitHub Issue closes at the same time.

Mohist writes four types of low-volume comments: intake confirmation with the
Mohist Issue link, arrival at an Approval point, completion with a delivery
summary and Pull Request link, and cancellation with a reason. Failure details
go through a notification channel; see
[Hermes Notifications](hermes-notifications.md). They are not repeated on
GitHub.

A write-back failure does not block production. GitHub is a progress board, not
the production state. Mohist records the failure for inspection.

## Pull Request Review as Approval

When a Connection has an approver list, a Check Approval point accepts GitHub
Pull Request reviews. An **Approve** review approves. A **Request changes**
review rejects, using the review body as the reason. A **Comment** review has
no Approval action.

- Only a review by a listed GitHub user counts. An empty list disables the
  capability.
- The Approval record attributes the GitHub user. `mo run approve` attributes
  its authenticated caller instead.
- This applies to Check Approval points for code review. A Plan Approval occurs
  before a Pull Request exists and does not use this path.
- Mohist decides from state at event arrival. It does not reverse a decision if
  a review is later dismissed or becomes stale after a push.

## GitHub Events and Event Routing

After a Connection is established, GitHub actions such as label changes,
closure, review, and Pull Request check results reach Mohist as events in real
time. [Event routing](event-routing.md) expressions can subscribe directly. A
rule can, for example, ask a supervising Agent to inspect a newly failing Pull
Request check. A GitHub event with an origin link belongs to the corresponding
Issue lineage, so subscribing to every event under Issue #42 includes it.

## Non-goals

- **Bidirectional synchronization:** GitHub title and body edits are not read
  back. State projects only from Mohist to GitHub. Two state authorities would
  diverge.
- **GitHub Projects:** Mohist does not read or write board columns or custom
  fields. A Projects Status field belongs to a board, and one Issue can have
  different status in two boards. That conflicts with Mohist as sole state
  authority. GitHub labels and Issue state already appear naturally in a
  Projects board.
- **Agent triggers from GitHub comment mentions:** Evaluate this in a later
  phase when a real requirement exists.
- **Hierarchy mapping:** GitHub sub-issues and milestones do not map to Mohist
  child Issues or Epics. Manage requirement hierarchy in Mohist.
- **Runtime control on GitHub:** Exceptional operations such as pause, stop,
  and retry remain in Mohist interfaces such as CLI, Web, Slack, and suggested
  notification actions.

---

See [`design/github-integration.md`](../design/github-integration.md) for design
boundaries and protocol details.
