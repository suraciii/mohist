---
status: wip
---

# GitHub Integration

GitHub integration lets GitHub act as a demand intake, progress board, and
Approval source. This document defines component boundaries: inbound receipt
and signature verification, event normalization, feed, close, and Approval
translators, the write-back adapter, and credentials. See
[`docs/github.md`](../docs/github.md) for product behavior. The PR delivery
Action family is defined in [`workflow/actions.md`](workflow/actions.md) and
[`docs/actions/github-pr.md`](../docs/actions/github-pr.md) and is not repeated
here.

This is not bidirectional synchronization; edits made on GitHub are not read
back. It is not GitHub Projects integration and does not replace the Runner's
`gh` delivery Action family.

## Model

### GitHubConnection

This Project-scoped resource belongs to an independent GitHub integration
supporting context, with the same placement rule as Slack integration; see
[`domain-analysis.md`](domain-analysis.md). It declares which GitHub repository
connects to which Project under which policy and owns no execution state.

A GitHubConnection declares:

- `Id` and `ProjectId`: identity and owning Project.
- `Owner` and `Repo`: GitHub repository coordinates. `(Owner, Repo)` is unique
  across the Server, so one GitHub repository connects to one Project.
- `RepositoryName`: the bound Repository resource name. Connect matches a
  registered repository by Git URL, and writes validate its existence.
- `IntakeLabel`: the feed label, default `mohist`. It cannot start with
  `mohist:`, which is reserved for write-back labels.
- `FeedMode`: `start`, the default, starts fed work; `backlog` only adds it to
  the backlog.
- `Approvers`: a GitHub login list. An empty list disables review Approval.
- `Status`: `Active` or `Disabled`.
- `IdentityKind`: `app`, the default GitHub App identity, or `pat`, a fallback
  fine-grained PAT used only for write-back.
- `InstallationId`: required for `IdentityKind=app` and parsed from the GitHub
  installation URL during connect.

Credentials do not enter the connection table. They are encrypted in
[`ISecretStore`](../packages/server/src/Mohist.Server/Infrastructure/Security/Secrets/ISecretStore.cs)
following the namespace precedent in
[`outbound-webhook.md`](outbound-webhook.md): an inbound signature secret and a
fallback write-back PAT with Issues read and write only and no code permission
per connection, plus one deployment-level GitHub App private key shared by
every `app` connection.

Credential boundaries always hold:

- The only long-lived GitHub secrets on Server are the signature secret, App
  private key, and fallback write-back PAT without code access.
- Server holds no long-lived GitHub access token. When needed, it signs a
  ten-minute JWT with the private key, exchanges it for a one-hour installation
  access token, restricts `repositories` to the target repository, caches it in
  memory until near expiry, and never writes it to disk.
- Direct Server calls to the GitHub API are limited to Issue comment, label, and
  close write-back and perform no Git content operation.
- Server issues an installation token for push and PR delivery to the Runner on
  demand; see Delivery-token Issuance. Runner does not retain it long term.
  [`RepositoryPolicy`](../packages/server/src/Mohist.Server/Project/Domain/RepositoryPolicy.cs)
  still prohibits credentials in gitUrl.

### GitHubIssueLink

This Server infrastructure integration record is analogous to a Slack
conversation mapping, not an aggregate fact. It maps
`(ProjectId, RepositoryName, GithubIssueNumber)` to `IssueNumber` and stores
write-back state required for idempotency, including the current state label
and the set of emitted milestone comments. It is immutable after creation and
is the feed idempotency key.

A PR-to-Issue association has no independent record. It is parsed from the
`pull_request` branch under the named Workspace convention
`mohist/ws-issue-N`. An unparseable branch causes the event to be ignored.

## Semantics

### Inbound Receipt and Normalization

`POST /api/github-connections/{connectionId}/ingress` does not require an
operator token. It verifies `X-Hub-Signature-256` over the raw request body with
HMAC-SHA256, using the same algorithm as
[`hermes-webhook.md`](hermes-webhook.md) and the `:webhook` secret. Failure
returns 401 and writes no event. Success normalizes the event into `IEventStore`,
returns 200, and leaves all later processing asynchronous.

Normalization rules:

- `type` is `com.mohist.github.<entity>.<action>`. The v1 set is
  `issues.labeled`, `issues.closed`, `issues.reopened`,
  `pull-request.reviewed`, and `check-suite.completed`, registered in
  [`EventCatalog`](../packages/server/src/Mohist.Server/Infrastructure/Events/EventCatalog.cs).
- `source` is
  `/mohist/projects/{projectId}/github-connections/{connectionId}`.
- Lineage always stamps `projectid`. `githubrepo` and `githubissue` carry GitHub
  coordinates. When a GitHubIssueLink exists, it also stamps `issue` and the
  Issue's Epic at that moment, snapshotted from the link record. Reading a
  mapping in this integration context does not violate the no-cross-aggregate
  stamping rule in [`event-protocol.md`](event-protocol.md).
- Payload preserves the GitHub event body unchanged. Routing never reads `data`;
  consumers use it only as evidence.

GitHub delivery is at least once and may be out of order, so every consumer is
idempotent.

### Feed Translator

A durable handler subscribes to `com.mohist.github.issues.labeled`. It skips
the event when the label is not the connection's `IntakeLabel` and when a
GitHubIssueLink already exists, which makes feeding idempotent. Otherwise it
creates an Issue with the GitHub title and body as a snapshot, the connection's
`RepositoryName` as target repository, a `p0`-`p4` priority from the event
labels, and the GitHub coordinates as origin, then writes the GitHubIssueLink.
With `FeedMode = start` it starts the Issue. When a prerequisite or repository
availability rejects the start, the Issue stays in the backlog and the handler
writes one explanatory comment.

### Close Translator

A handler subscribes to `com.mohist.github.issues.closed`. When a link exists
and the Issue is nonterminal, it cancels the Issue.

The feedback loop is inherently safe. Mohist makes the Issue terminal before
write-back closes the GitHub Issue. The returned closed event is a no-op at the
terminal check, without identifying who closed it.

### Approval Translator

A handler subscribes to `com.mohist.github.pull-request.reviewed`. It parses
the Issue number from the branch name `mohist/ws-issue-N` and ignores an
unparseable branch. It ignores a reviewer whose login is not in the
connection's `Approvers` list and an Issue that is not at the Check approval
point. An `APPROVED` review approves with `decidedBy = "github:" + login`; a
`CHANGES_REQUESTED` review rejects with the same `decidedBy` and the review
body as message; a `COMMENTED` review is ignored.

The Approvers list is deterministic configuration read directly by the
translator. No Agent or prompt decides Approval.

### Write-back Adapter

Handlers subscribe to Issue and Workflow events for start, approval point,
blocked, complete, and cancel. For an Issue with a GitHubIssueLink, the adapter
calls GitHub REST using the connection identity: an App installation token or
fallback `:api` PAT.

- **Mutually exclusive state labels**: Remove other `mohist:*` labels and add
  the current-state label.
- **Four milestone comment classes**: Feed confirmation, approval point,
  completion with delivery summary and PR link, and cancellation with reason.
  The link record's emitted set prevents a duplicate for the same milestone and
  Issue.
- **Finalization**: completed closes as completed; cancelled closes as not
  planned.

Reliability is best effort. A non-2xx response, network failure, or timeout
writes a log and durable failure record shaped like outbound-webhook failures.
It does not retry, block the production line, or roll back state. An
authentication failure, 401 or 403, also marks the connection as needing
attention for Web and CLI.

### Delivery-token Issuance

Runner requests a token when delivery needs a GitHub identity through
`POST /api/github-connections/{connectionId}/delivery-token`. It requires the
`runner` scope under the authentication model in [`auth.md`](auth.md):

```text literal
input: { permissions: ["contents:write", "pull-requests:write"] }
server:
  if connection is not Active or IdentityKind != app:
    return 409 (fallback delivery uses the Runner's own login)
  sign JWT with App private key
  exchange for installation token restricted to this repository
    and the requested permissions
  audit runnerId, connectionId, permissions, and time
output: { token, expires_at, bot_login }
```

V1 does not validate that the Runner's current work is bound to the repository.
The token is repository-scoped and expires after one hour, limiting exposure to
that repository.

Runner injects the token into the execution environment as `GH_TOKEN`, Git
credentials, and a Git author identity matching `bot_login`. Otherwise the Bot
would open the PR while commits were attributed to a user. The token lives only
in the process environment and is never written to Runner disk.

## Examples

Feed through completion with `FeedMode = start` and `alice` in Approvers:

1. A user adds `mohist` to `owner/repo#7`. The feed translator creates Issue
   42, writes the link, and starts it.
2. Write-back comments that it was accepted as Mohist Issue #42 and adds
   `mohist:in-progress`.
3. At the Check approval point, the label becomes
   `mohist:awaiting-approval` and a comment requests a decision.
4. `alice` requests changes on the PR. The Approval translator rejects, sending
   work back to Build.
5. After repair, the Run reaches Check again. `alice` approves, Integrate
   completes, and Issue 42 becomes Done. Write-back changes the label to
   `mohist:done`, comments with delivery summary and PR link, and closes
   `owner/repo#7`.
6. The returned GitHub closed event arrives after Issue 42 is terminal and is a
   no-op.

Routing subscriptions use the same semantics as Mohist domain events, without
a special case:

```text literal
event.type == "com.mohist.github.check-suite.completed" && event.issue == "42"
```

## Status

Repository Connection, signed ingress, feed and withdrawal, Pull Request review
Approval, and idempotent progress write-back are implemented. Server remains
the Mohist state authority, while GitHub provides external identity and a
projection of progress.

The Web and CLI do not yet expose persisted write-back failures. Write-back
also uses a PAT rather than GitHub App installation identity and short-lived,
Repository-scoped tokens.

PR review correlation and delivery-PR lookup still recognize the retired
`mo/issue-N` branch form, while named Issue Workspaces create
`mohist/ws-issue-N`. Until those readers converge on the Workspace convention,
review Approval and completion comments cannot reliably associate a PR created
by the built-in GitHub Profile.

Open questions:

- With out-of-order events, `closed` can arrive before `labeled` while no link
  exists, and the later `labeled` event can feed a closed GitHub Issue. V1
  accepts this edge. Observe it before deciding whether feed must first confirm
  that the GitHub Issue is still open.
- `check-suite.completed` enters the event set for routing consumption. Workflow
  PR-check waiting still polls.
- Reuse of the mention channel in
  [`agent-mentions.md`](agent-mentions.md) for an Agent invoked by a GitHub
  comment mention waits for concrete demand.
- App installation tokens creating PRs in repositories under personal accounts
  may have compatibility limits. Some reports say PR creation requires a
  collaborator role that a GitHub App cannot hold, despite long-running
  practices such as Dependabot.
