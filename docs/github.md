# GitHub

GitHub is the public mirror and intake surface for a Project's Issues. A
connected Repository gives each non-Draft Mohist Issue exactly one GitHub Issue
mirror. A GitHub Issue is not tracked by Mohist until an operator links an
existing Issue or an authorized commenter uses `/mohist start` to create one.

## Product Commitments

- Mohist creates and updates GitHub mirrors automatically; `mo issue github sync`
  is the repair command.
- Only an explicit `/mohist start` creates a Mohist Issue from GitHub; an
  operator may link an existing Mohist Issue explicitly.
- Title and body synchronize in both directions for a linked pair.
- Mohist remains authoritative for Workflow, Approval, and execution state.
- A mirror failure never blocks Mohist work.
- Disable and reconnect preserve links, history, and pending work.

## Mental Model

- **GitHub is the superset.** Everything Mohist tracks is visible on GitHub; a
  GitHub Issue is not tracked until it is explicitly handed to Mohist.
- **A mirror is one linked pair.** Mohist exposes its GitHub number, URL, and
  sync health. Agents do not parse labels or construct mirror URLs.
- **A linked pair shares content, not execution.** Title and body synchronize;
  Workflow, Approval, and Run state project from Mohist to GitHub.
- **A Workflow is optional.** An Issue without a Workflow Profile has no
  production line. Its linked lifecycle follows the GitHub Issue rules below.

## Connecting Repositories

A GitHub connection binds one GitHub repository to one Project Repository. A
Project can hold several connections. An Issue carries no GitHub coordinates;
its target Repository determines its mirror.

The connection uses the one GitHub App owned by the Mohist deployment:

- The deployment owns the App credential. The CLI, Web client, Repository
  configuration, and connection response never receive the private key.
- Mohist discovers the App installation for the selected Repository and verifies
  access. It records the verified installation and stable repository identity.
- If no valid installation exists, Mohist returns the App installation URL. The
  operator installs the App, selects the Repository, and retries.
- The connection becomes `Active` only after verification. Outbound calls use
  short-lived installation tokens that the operator never sees.
- A per-connection signed Repository webhook receives inbound events. The App
  has no global webhook path.
- The App needs Issues read/write, Pull Requests read, and Metadata read access.
- Runner git and Pull Request operations use the Runner host's separate `gh`
  credential. Server installation tokens are not Runner credentials.

```text diagram
             +--------------------+
             | Request connection |<---------------+
             +----------+---------+                |
                        |                          |
                        v                          |
              +------------------+                 |
              | Access verified? |                 |
              +---------+--------+                 |
           +------------+------------+             |
           v                         v             |
 +-------------------+  +------------------------+ |
 | Connection Active |  | Install App and select | |
 +-------------------+  |       Repository       | |
                        +------------+-----------+ |
                                     |             |
                                     v             |
                           +------------------+    |
                           | Retry connection +----+
                           +------------------+
```

The connection listing remains the operator's view of every Repository and its
connection state:

```bash
mo github list
```

An optional approver list enables [Pull Request Review at an Approval Point](#pull-request-review-at-an-approval-point).

## Connection Lifecycle

An `Active` connection can mirror Issues and receive signed webhook events. An
operator may disable it. Mohist disables it when the installation is suspended,
removed, or no longer includes the Repository. `Disabled` pauses projection and
marks reconnect-required when the installation must be repaired. Connection
delete is unsupported.

Disable and reconnect preserve the connection, links, pending work, and
history. Existing PAT-backed connections are `Disabled` with
reconnect-required status. Mohist does not convert or use the PAT as a fallback.
An expired installation token is replaced without changing a valid connection's
state.

## The Mirror: Mohist to GitHub

When a non-Draft Issue targets a connected Repository, Mohist creates one GitHub
mirror and records the permanent link. Mohist owns the initial title and body.

The mirror receives:

- Title and body, synchronized in both directions. Mohist keeps one invisible
  HTML marker in the raw GitHub body for unknown-create reconciliation, but the
  rendered body has no tracking footer.
- Lifecycle projection. Completing or cancelling the Mohist Issue closes the
  mirror with the matching reason.
- Workflow progress through mutually exclusive `mohist:*` labels
  (`in-progress`, `awaiting-approval`, `blocked`, `done`) and four milestone
  comment classes: mirror confirmation, Approval Point arrival, completion, and
  cancellation.

Mohist does not mirror comments, labels other than `mohist:*`, priority, model
or runtime configuration, prerequisites, Epic membership, Workspace facts, or
Session facts.

## Handing a GitHub Issue to Mohist

A comment starting with `/mohist` on an Issue in a connected Repository is a
command. The first supported verb is:

```text literal
/mohist start
```

It creates the Mohist Issue from the GitHub title and body, records the link,
and starts the Workflow with the Project default Profile. GitHub labels `p0`
through `p4` map to Mohist priority at hand-in.

- **Permission:** only a commenter GitHub reports as a repository owner, member,
  or collaborator can hand work in. Other commands are ignored.
- **Idempotency:** a repeated command on a linked Issue returns the existing
  Mohist Issue and starts nothing.
- **Reply:** confirmation and refusal are posted as one reply comment on
  GitHub. Command comments and ordinary discussion do not enter Mohist's
  comment thread.

Future GitHub commands reuse `mo` domain verbs, with comment arguments and
replies instead of CLI flags and JSON.

## Linked Pairs

Title and body edits synchronize in both directions. Content equality identifies
Mohist's own echo and drops it. A real edit updates the other side and records
`github:<login>` or the Mohist caller in the Issue timeline. An edit does not
change the input of a running Workflow; it takes effect at the next planning or
review point.

Lifecycle depends on whether the Issue has a Workflow:

- **Without a Workflow:** GitHub close as completed marks the Issue Done; close
  as not planned cancels it; reopening a cancelled Issue returns it to Backlog.
  Reopening a completed Issue does not erase the delivery. Mohist suggests a
  follow-up Issue instead.
- **With a Workflow:** closing before Integrate withdraws the requirement and
  cancels the work. At or after Integrate, a close is a delivery echo,
  including the close caused by merging a linked Pull Request. Mohist never
  places closing keywords in Pull Request bodies.

Two existing Issues can be paired by hand, with Mohist as the content source:

```bash
mo issue github link 42 owner/repo#817
mo issue github unlink 42    # stops synchronization; both sides keep existing
```

## Sync Health and Recovery

Every link is healthy or carries its last synchronization error. CLI and Web
show this state. One reconcile command creates a missing mirror, pushes current
Mohist state, and clears the error:

```bash
mo issue github sync 42
```

State-label writes also retry at the next Workflow milestone.

## Pull Request Review at an Approval Point

A connection's approver list can provide a decision only at the **Check Approval
Point** of the WorkflowRun associated with that Pull Request. WorkflowRun
records its Pull Request number once. Repeating the same number is idempotent;
a conflicting number is rejected. Review translation uses this number, not a
branch, mutable Run Variables, or a Pull Request URL.

An **Approve** review maps to Approve. A **Request changes** review maps to
Request Changes and uses the review body as Approval Feedback. A **Comment**
review has no decision. Only listed GitHub users count, and the timeline records
the GitHub user. WorkflowRun must still wait at Check Approval, and its bound
Definition must allow Request Changes. Plan and custom Approval Points use
ordinary Mohist decision surfaces. A later review dismissal does not reverse a
decision.

## GitHub Events and Event Routing

Comments, edits, closure, reviews, and check results on a connected Repository
reach Mohist as real-time events and can feed [Event routing](event-routing.md).
An event on a linked Issue belongs to that Issue's lineage. A subscription to
Issue #42 therefore includes its GitHub events.

See [`design/github-integration.md`](../design/github-integration.md) for
component boundaries and protocol contracts.

## Boundary

- GitHub OAuth user tokens and device flow are not used.
- Web connection management is not part of this contract.
- One deployment owns one GitHub App configuration.
- A global GitHub App webhook is not used.
- Runner `gh` authentication remains separate.
- A PAT is not converted automatically into an App installation.
- Connecting a Repository does not bulk-create mirrors for existing Issues.
- GitHub discussion is not synchronized. Mohist publishes only milestone and
  command replies.
- Assignees, milestones, GitHub Projects, and sub-issue hierarchy are not
  synchronized. `p0` to `p4` is the only label read at hand-in.
- GitHub-side runtime control is limited to supported `/mohist` verbs.

## Implementation Gaps

User-level OAuth, multiple App configurations, and GitHub Enterprise Server are
not supported. The App identity is configured per deployment.
