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
     delivery continue to use the existing Runner login; see
     [Runner Guide](runner.md). Records appear under the administrator or
     Runner account.

Optional configuration:

| Setting | Default | Meaning |
|---|---|---|
| Feed label | `mohist` | Applying it supplies the requirement |
| Feed mode | Start immediately | Use `--feed-mode backlog` to create only a backlog Issue for a later manual start |
| Approver list | Empty, disabled | Only Pull Request reviews from listed GitHub users decide an Approval |

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

| Label | Meaning |
|---|---|
| `mohist:in-progress` | The production system is running |
| `mohist:awaiting-approval` | Work is waiting at an Approval point |
| `mohist:blocked` | Work is blocked and needs intervention |
| `mohist:done` | Work is complete; the GitHub Issue closes at the same time |

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
Pull Request reviews:

| Review result | Production action |
|---|---|
| Approve | Approve |
| Request changes | Reject, using the review body as the reason |
| Comment | No Approval action |

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

## Status

Repository Connection and inbound events are implemented. `mo github connect`
creates a Connection and prints the GitHub configuration checklist. Signed
events for label changes, Issue closure, Pull Request reviews, and completed
check suites reach event routing in real time and can be subscribed to.

Intake and withdrawal are implemented. A feed label creates and starts a
Mohist Issue with title and body snapshot, `p0` through `p4` priority mapping,
traceable origin, and optional backlog-only mode through
`--feed-mode backlog`. Repeated intake creates only one Mohist Issue. Closing
the GitHub Issue cancels its Mohist Issue unless that Issue is already terminal.
When start is rejected because prerequisites are unmet or the Repository is
unavailable, the Issue remains in the backlog and a minimal PAT-authored comment
explains the result on GitHub.

Pull Request review Approval is implemented. `mo github connect --approver`
and `mo github update` configure the approver list. Approve and Request changes
reviews from listed users approve or reject a Check Approval point, attributed
to `github:<login>`, with the review body as a rejection reason. Comment reviews
and unlisted users have no effect. Mohist decides from state at event arrival
and does not revisit dismissed or stale reviews.

Issue-level progress write-back is implemented. Work start, arrival at an
Approval point, run failure, completion, and cancellation project to mutually
exclusive state labels and comments. Completion and cancellation also close
the GitHub Issue. Comments and labels are idempotent. Failure of one operation
does not block another. Failures are persisted, and a 401 or 403 also marks the
Connection as needing operations attention.

The Web and CLI do not yet expose persisted write-back failures. GitHub App
identity is also not implemented; write-back currently uses a PAT, without App
installation or Repository-scoped short-lived token exchange. GitHub remains a
delivery target through the `mohist/github-pr` Profile. Future Issues will
deliver App identity and failure inspection.

PR review Approval and completion-comment PR lookup still recognize the old
`mo/issue-N` branch form. The current Issue Workspace branch is
`mohist/ws-issue-N`, so do not rely on those two correlations until the GitHub
reader uses the Workspace branch convention.

---

See [`design/github-integration.md`](../design/github-integration.md) for design
boundaries and protocol details.
