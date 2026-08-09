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

| Field | Description |
|---|---|
| `Id` / `ProjectId` | Identity and owning Project |
| `Owner` / `Repo` | GitHub repository coordinates; `(Owner, Repo)` is unique across the Server, so one GitHub repository connects to one Project |
| `RepositoryName` | Bound Repository resource name; connect matches a registered repository by Git URL, and writes validate its existence |
| `IntakeLabel` | Feed label, default `mohist`; cannot start with `mohist:`, which is reserved for write-back labels |
| `FeedMode` | `start`, the default, starts fed work; `backlog` only adds it to the backlog |
| `Approvers` | GitHub login list; an empty list disables review Approval |
| `Status` | `Active` or `Disabled` |
| `IdentityKind` | `app`, the default GitHub App identity; `pat`, a fallback fine-grained PAT used only for write-back |
| `InstallationId` | Required for `IdentityKind=app` and parsed from the GitHub installation URL during connect |

Credentials do not enter the connection table. They are encrypted in
[`ISecretStore`](../packages/server/src/Mohist.Server/Infrastructure/Security/Secrets/ISecretStore.cs)
using the namespace precedent in
[`outbound-webhook.md`](outbound-webhook.md):

- `SecretStoreAddress(projectId, "<connectionId>:webhook")`: Inbound signature
  secret.
- `SecretStoreAddress(projectId, "<connectionId>:api")`: Fallback write-back
  PAT with Issues read and write only and no code permission.
- `SecretStoreAddress("_server", "github-app:key")`: GitHub App private key,
  using `SecretKind.AppToken`. One deployment key is shared by every `app`
  connection.

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
`pull_request` branch under the Workflow naming convention `mo/issue-N`. An
unparseable branch causes the event to be ignored.

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

A durable handler subscribes to `com.mohist.github.issues.labeled`:

```text
if event label != connection.IntakeLabel: skip
if GitHubIssueLink exists: skip                          # idempotent
issue = create Issue(title and body snapshot,
                     target repository = connection.RepositoryName,
                     priority = p0-p4 from event labels,
                     origin = GitHub coordinates)
write GitHubIssueLink
if FeedMode == start:
    start Issue
    if rejected by prerequisite or repository availability:
        leave in backlog and write one explanatory comment
```

### Close Translator

A handler subscribes to `com.mohist.github.issues.closed`. When a link exists
and the Issue is nonterminal, it cancels the Issue.

The feedback loop is inherently safe. Mohist makes the Issue terminal before
write-back closes the GitHub Issue. The returned closed event is a no-op at the
terminal check, without identifying who closed it.

### Approval Translator

A handler subscribes to `com.mohist.github.pull-request.reviewed`:

```text
issue number = parse branch name mo/issue-N; if invalid, ignore
if reviewer.login not in connection.Approvers: ignore
if Issue is not at the Check approval point: ignore
APPROVED          -> approve(decidedBy = "github:" + login)
CHANGES_REQUESTED -> reject(decidedBy = "github:" + login, message = review body)
COMMENTED         -> ignore
```

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

```text
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

```text
event.type == "com.mohist.github.check-suite.completed" && event.issue == "42"
```

## Status

None of this design is implemented. Recommended delivery order for
`mohist-explore` slices: inbound receipt and normalization, feed translator and
GitHubIssueLink, write-back adapter, then Approval translator.

Open questions:

- With out-of-order events, `closed` can arrive before `labeled` while no link
  exists, and the later `labeled` event can feed a closed GitHub Issue. V1
  accepts this edge. Observe it before deciding whether feed must first confirm
  that the GitHub Issue is still open.
- `check-suite.completed` enters the event set for routing consumption. Workflow
  PR-check waiting still polls; an event-driven replacement is follow-up work.
- Reuse of the mention channel in
  [`agent-mentions.md`](agent-mentions.md) for an Agent invoked by a GitHub
  comment mention waits for concrete demand.
- App installation tokens creating PRs in repositories under personal accounts
  may have compatibility limits. Some reports say PR creation requires a
  collaborator role that a GitHub App cannot hold, despite long-running
  practices such as Dependabot. Implementation first verifies with a test App.
  If the limitation is real, a separate design adds a machine-user plus
  fine-grained-PAT identity for personal repositories.
