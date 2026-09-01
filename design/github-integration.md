# GitHub Integration

GitHub is the public mirror and imperative intake surface for a Project's
Issues. This document defines the boundaries and contracts that keep the
integration reliable. Product behavior is in [`docs/github.md`](../docs/github.md).
The GitHub PR Action family is defined in
[`workflow/actions.md`](workflow/actions.md) and
[`docs/actions/github-pr.md`](../docs/actions/github-pr.md).

## Core Decisions

- One deployment owns one GitHub App. A GitHubConnection stores its verified
  installation and Repository binding, not deployment credentials.
- Server owns mirror state and GitHub Issue operations. Runner keeps its own
  host-local `gh` credential for git and Pull Request work.
- Mohist-to-GitHub projection is passive. GitHub-to-Mohist intake is explicit:
  only a `/mohist` comment creates Mohist state.
- The existing per-connection signed Repository webhook is the only inbound
  boundary. A global GitHub App webhook is not used.
- Installation tokens are short-lived and never become connection state or
  caller-visible data.
- Disable and reconnect preserve links, history, and durable work. Unknown
  external outcomes converge through reconciliation, never blind replay.
- Each WorkflowRun owns one write-once Pull Request identity for review
  correlation.

## System Boundary

```text diagram
+----------+   +--------------------+             +--------+
| Operator |   | Repository webhook |             | Runner |
+-----+----+   +----------+---------+             +---+----+
      +---------+---------+                           |
                v                                     v
        +---------------+                     +---------------+
        | Mohist Server |                     | gh credential |
        +-------+-------+                     +---------------+
        +-------+--------+
        v                v
 +------------+   +------------+
 | GitHub App |   | GitHub API |
 +------------+   +------------+
```

- **Mohist Server** owns connections, mirror links, command translation, event
  normalization, review translation, and durable outbound work.
- **GitHub** owns App installations, GitHub Issues, Pull Requests, and provider
  delivery.
- **Repository webhook** supplies signed inbound events for one connection.
  It does not create a second global ingress path.
- **Runner** uses its host-local `gh` credential for git and Pull Request work.
  The Server installation token cannot authorize Runner work.

## Model

### Deployment GitHub App

The deployment owns exactly one GitHub App credential. App ID, slug, and the
protected private-key file are deployment configuration. Only the Server
GitHub integration reads them. The CLI, Web client, Repository configuration,
connection responses, and Runner never receive the private key.

The App credential discovers installations and mints short-lived installation
tokens. It does not replace the signed Repository webhook secret or Runner's
`gh` credential.

### GitHubInstallation

A GitHub installation authorizes the deployment App for one account and a set
of repositories. Mohist discovers an installation for the requested Repository
and verifies access before binding it. GitHub owns the installation; Mohist
stores its verified identity and access facts.

### GitHubConnection

A Project-scoped resource binds one Project Repository to one GitHub repository
under one policy. It contains the bound Repository name, current `Owner` and
`Repo`, stable GitHub repository identity, verified installation identity, an
optional Pull Request approver list, and `Active` or `Disabled` status.

`(Owner, Repo)` is unique across the Server. A Repository has at most one
connection. Stable repository identity remains authoritative when owner or
repository name changes.

A connection stores no App private key, installation token, or operator PAT.
Its per-connection webhook secret remains a separate Server secret. Direct
Server GitHub API calls are limited to Issue and comment operations. Git and
Pull Request content operations remain Runner-side.

### GitHubIssueLink

A GitHubIssueLink maps one Mohist Issue to one GitHub mirror. It stores the
GitHub Issue's stable node ID and current coordinates, mirror operation state,
and synchronization health. The link is created once per pair and survives
connection disable and reconnect.

Durable non-idempotent deliveries use their own operation records. Title/body
and state-label projection uses current-state replacement.

### WorkflowRun Pull Request Identity

WorkflowRun owns one write-once Pull Request identity: its bound Repository and
Pull Request number. The first `github.pr.number` carrier that reaches the
WorkflowRun through the Workflow grain records the number. The carrier may
come from Runner `setVars` or Agent Job `setVars`. Repeating it is idempotent;
a conflicting number is rejected before dispatch.

This identity is WorkflowRun state, not a new resource or DSL construct. It is
the authority for review correlation. Profiles may pass
`vars.github.pr.number` to Action inputs and retain `vars.github.pr.url` for
presentation. Review translation reads the immutable number, not a branch,
mutable Run Variables, or URL.

## Direction Contract

The two directions are asymmetric:

- **Mohist to GitHub:** Issue and Workflow events passively maintain the
  mirror.
- **GitHub to Mohist:** only a `/mohist` comment creates Mohist state. Other
  inbound events maintain an existing link or feed event routing.

## Setup and Readiness

The operator connects a registered Project Repository to a GitHub repository.
Server discovers the installation with the deployment App and verifies access
before setting the connection `Active`. The operator provides no PAT, private
key, or installation token.

If no valid installation exists, Server returns the App installation URL and an
actionable reason. The operator installs the App, selects the Repository, and
retries. Server records the verified installation and stable repository identity
before activation.

The per-connection signed Repository webhook remains the inbound boundary. The
App requires Issues read/write, Pull Requests read, and Metadata read access.

```text diagram
          +--------------------+
          | Connection request |
          +----------+---------+
                     |
                     v
         +-----------------------+
         | Discover installation |<--+
         +-----------+-----------+   |
                     |               |
                     v               |
          +--------------------+     |
          | Repository access? |     |
          +----------+---------+     |
           +---------+--------+      |
           v                  v      |
+--------------------+   +--------+  |
| Reconnect required +---| Active |--+
+--------------------+   +----+---+  |
                              |      |
                              v      |
                        +----------+ |
                        | Disabled | |
                        +-----+----+ |
                              |      |
                              v      |
                        +----------+ |
                        | Recovery +-+
                        +----------+
```

## Connection Lifecycle

`Active` means installation access is verified for the bound Repository.
`Disabled` pauses outbound projection. Mohist sets reconnect-required when the
installation is suspended, removed, or no longer includes the Repository. An
operator can disable a connection for the same pause boundary. Connection
delete is unsupported.

Disable and reconnect preserve the connection, links, pending durable work, and
history. Existing PAT-backed connections are Disabled with reconnect-required
status. Mohist does not convert or use the PAT as a fallback.

## Installation Token Semantics

Each outbound GitHub API operation obtains a short-lived token from the Active
connection's verified installation. GitHub's expiration bounds any local cache.
An expired token is discarded and replaced. The token is never a connection
field and never appears in operator, CLI, Web, or Runner data.

If expiry is known before a request, Server obtains one fresh token. If a
request may have applied remotely, Server reconciles before repeating it. Token
refresh never creates a second mirror, comment, close, or reply delivery.

Runner git and Pull Request operations continue to use the Runner host's
separate `gh` credential.

## Inbound

`POST /api/github-connections/{connectionId}/ingress` remains the signed
Repository webhook boundary. Server verifies `X-Hub-Signature-256` over the raw
body, normalizes the event, and processes it asynchronously. Consumers are
idempotent because GitHub delivery is at least once and may be out of order.
A GitHub App global webhook is not accepted.

The normalized event set covers issue comments, issue edits, closure and
reopening, Pull Request reviews, and check results. Payloads remain unchanged
as evidence; lineage stamps follow the existing event rules.

### Command Translator

A comment whose body starts with `/mohist` is a command only when GitHub reports
the author as an owner, member, or collaborator. Other comments are ignored.
The translator shares its verb vocabulary with `mo`; comments carry arguments
where the CLI carries flags and JSON.

`start` creates the Mohist Issue from the GitHub title and body, maps `p0` to
`p4` labels to priority, writes the link, and starts the Project default
Workflow Profile. The link constraint makes a repeated command a no-op that
returns the existing Issue. Refusals return one reply comment. No Agent or
Prompt parses permissions or commands.

### Pull Request Review Translator

No GitHub review entity is introduced. The translator reads WorkflowRun's
write-once Pull Request number. It may decide only the Check Approval Point:
Approve maps to Approve; Request changes maps to Request Changes and uses the
review body as Approval Feedback; Comment has no decision.

Only listed GitHub approvers count. WorkflowRun must still wait at Check
Approval and Request Changes must be available in its bound Definition. Plan
and custom Approval Points use ordinary Mohist decision surfaces. The
translator owns provider mapping; WorkflowRun owns Approval Point state and
Feedback Task execution. A later review dismissal does not reverse a decision.

### Edit Translator

An issue-edited event on a linked pair synchronizes title and body into Mohist.
Content equality identifies Mohist's own echo and drops it. A real edit updates
the Issue and attributes the change as `github:<login>` in the timeline. It
does not change the input of a running Workflow; it takes effect at the next
planning or review point.

### Lifecycle Translator

For an Issue without a Workflow, GitHub closure drives the linked Issue:
completed closes it as Done; not planned cancels it; reopening a cancelled
Issue returns it to Backlog; reopening a completed Issue does not erase the
delivery.

For an Issue with a Workflow, closing before Integrate withdraws the
requirement and cancels the work. At or after Integrate, a close is a delivery
echo, including the close caused by merging a linked Pull Request. Mohist never
places closing keywords in Pull Request bodies. Mohist makes an Issue terminal
before write-back closes its mirror, so the returned close event is a no-op.

## Outbound: the Mirror Adapter

Handlers maintain mirrors through the Active connection's verified installation
identity. Each request obtains an installation token.

- A non-Draft Issue with a connected target Repository gets one mirror.
- Title and body edits project outward. Content equality suppresses the echo.
- Mutually exclusive `mohist:*` labels and four milestone comment classes
  project Workflow progress.
- Completion closes the mirror as completed. Cancellation closes it as not
  planned.

The mirror has no visible tracking footer. The confirmation comment links it to
the Mohist Issue. Mohist does not mirror comments, labels, priority, runtime or
model configuration, prerequisites, Epic membership, Workspace facts, or
Session facts.

## Durable Outbound Operations

A durable record is required when replaying a request could create a duplicate.
Mirror creation uses a pending link intent and an invisible marker. Milestone
comments and closes use `GitHubIssueCommentOperation`. Command replies use a
separate reply ledger. Title/body and `mohist:*` label writes replace the
complete current state and may be re-projected after an error.

```text diagram
               +-----------------+
               | Write or create |
               +--------+--------+
                        |
                        v
                  +----------+
                  | Evidence +<-----------------------++
                  +-----+----+                        ||
                        |                             ||
                        v                             ||
                   +---------+                        ||
                   | Unknown |                        ||
                   +----+----+                        ||
     +------------------+--------------------+        ||
     v                  v                    v        ||
+--------+    +-------------------+   +-------------+ ||
| Settle |<---| Retry same intent +<--| Fail closed |<++
+--------+    +-------------------+   +-------------+
```

- A confirmed remote change settles the same intent.
- A rejection before any remote effect may retry the same intent.
- An unknown result retains the intent for reconciliation. It is never resent
  blindly.
- Conflicting or incomplete evidence fails closed.
- Mirror creation reconciles the exact marker across GitHub Issues in all
  states. One match links the mirror; multiple matches fail closed; zero matches
  after an attempted create remains unresolved and never sends a second create.
- A stale operation carries its operation ID and GitHub Issue number. It cannot
  settle or delete another target's reservation.
- Only a 404 from the exact mirror content endpoint can reset and replace a
  mirror. A 404 from comment, label, close, or Pull Request endpoints cannot
  reset it.

A link is healthy only after current projection succeeds. Disabled work remains
durable. Enable resumes recovery and reprojection. Errors remain visible until
a later successful projection clears them.

## Failure Model

An outbound failure records the error on the link and surfaces it in CLI and
Web. It never blocks the Workflow or rolls back Mohist Issue state. The
`mo issue github sync` command repairs a missing or failed mirror. All retry,
reconciliation, fencing, pause, reset, and health behavior uses the same durable
operation contract above.

## Non-Goals

- GitHub OAuth user tokens or device flow.
- Web connection management UI.
- Multiple GitHub App configurations in one deployment.
- GitHub App global webhook ingress.
- Replacement of Runner `gh` authentication.
- Automatic PAT conversion into an App installation.
- Bulk mirror backfill when a Repository is connected.
- Comment synchronization beyond Mohist milestone and command replies.
- Assignees, milestones, GitHub Projects, and sub-issue hierarchy.
- GitHub-side runtime control beyond `/mohist` verbs.
