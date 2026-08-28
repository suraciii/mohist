# GitHub Integration

GitHub integration makes a connected GitHub repository the public mirror of a
Project's Issues and the imperative entry point for handing work to Mohist.
This document defines component boundaries and the decisions that keep the
integration reliable. See [`docs/github.md`](../docs/github.md) for product
behavior. The PR delivery Action family is defined in
[`workflow/actions.md`](workflow/actions.md) and
[`docs/actions/github-pr.md`](../docs/actions/github-pr.md) and is not repeated
here.

## Model

### GitHubConnection

A Project-scoped resource in the GitHub integration supporting context, placed
like Slack integration; see [`domain-analysis.md`](domain-analysis.md). It
declares which GitHub repository mirrors which Project Repository under which
policy and owns no execution state.

A connection declares the GitHub coordinates (`Owner`, `Repo`, plus the
repository's stable node id so a rename or transfer can be detected), the bound
Repository resource name, an optional Approvers list for review Approval, and
`Active` or `Disabled` status. `(Owner, Repo)` is unique across the Server, and
a Repository has at most one connection, so an Issue's target repository
unambiguously determines its mirror location.

Credentials stay out of the connection record and follow the existing
[`ISecretStore`](../packages/server/src/Mohist.Server/Infrastructure/Security/Secrets/ISecretStore.cs)
boundaries: an inbound signature secret and, for the current connection API, a
required fine-grained PAT with Issues read and write only. The Server stores the
PAT as a secret and never exposes it in connection reads. GitHub App
installation-token exchange is not implemented yet. Direct Server calls to the
GitHub API are limited to Issue and comment operations; git content operations
remain Runner-side through delivery-token issuance.

### GitHubIssueLink

A Server infrastructure integration record — analogous to a Slack conversation
mapping, not an aggregate fact — mapping a Mohist Issue to its GitHub mirror
one-to-one. It stores the GitHub Issue's stable node id alongside its current
coordinates, carries separate bookkeeping for pending mirror creation,
delivered comments and closes, and the current state label, and reports sync
health: healthy, or the last synchronization error.

The link identifies the pair and scopes its mirror bookkeeping. Durable
non-idempotent deliveries use their own intent or operation records; title/body
and state-label projection use current-state replacement. The link is created
exactly once per pair — at mirror creation, at `/mohist start`, or at manual
`link` — and survives disable/enable cycles of its connection.

## The Direction Contract

The two directions are asymmetric by design:

- **Mohist to GitHub is passive projection.** Issue domain events drive
  mirroring without any user request.
- **GitHub to Mohist is imperative.** The only entry that creates Mohist state
  from GitHub is a command comment. Everything else inbound either maintains an
  existing link (edit sync, lifecycle events) or feeds event routing.

## Inbound

`POST /api/github-connections/{connectionId}/ingress` verifies
`X-Hub-Signature-256` over the raw body, normalizes the event into
`IEventStore`, and returns; all processing is asynchronous and every consumer
is idempotent, because GitHub delivery is at least once and may be out of
order. The normalized event set grows to cover the command entry and content
sync: issue comments, issue edits, closure and reopening, Pull Request reviews,
and check results. Payloads are preserved unchanged as evidence; lineage stamps
follow the existing rules.

### Command Translator

A durable handler subscribes to issue-comment events. A comment whose body
starts with `/mohist` from an author GitHub reports as owner, member, or
collaborator is a command; everything else is ignored. The translator shares
its verb vocabulary with `mo` — a GitHub-side verb names the same domain
action as the CLI verb, with comment replies in place of flags and JSON.

`start` creates the Mohist Issue from the GitHub title and body (the creating
side owns initial content), maps `p0`–`p4` labels to priority, writes the link,
and starts the Workflow with the Project's default Profile. The unique
constraint on the link makes a repeated command a no-op that replies with the
existing Issue. Refusals — an unavailable Repository, an unknown verb — are
answered with one reply comment. The translator is deterministic
configuration-driven code; no Agent or prompt participates in parsing or
permission.

### Edit Translator

Issue-edited events on a linked pair synchronize title and body into Mohist.
The guard is content equality: an inbound edit matching the current Mohist
value is the echo of Mohist's own outbound write and is dropped. A real edit
updates the Issue record and is attributed to `github:<login>` in the timeline.
It does not retroactively change the input of a Workflow already running — the
new content takes effect at the next planning or review point.

### Lifecycle Translator

Close and reopen events apply the product rules in
[`docs/github.md`](../docs/github.md#linked-pairs), with two reliability
guards:

- **Terminal check.** Mohist makes an Issue terminal before write-back closes
  the mirror, so the returned closed event is a no-op without identifying the
  actor.
- **Integrate guard.** A close event arriving while the Issue's Run is at or
  past the Integrate stage is a delivery echo — most importantly the automatic
  close triggered by merging a linked Pull Request — and is ignored. Withdrawal
  by closing is only possible before delivery begins.

## Outbound: the Mirror Adapter

Handlers subscribe to Issue and Workflow events and maintain the mirror through
the connection identity:

- **Mirror creation.** A non-Draft Issue whose target repository is connected
  gets a GitHub mirror. The mirror has no visible tracking footer, and the
  confirmation comment links it back to Mohist.
- **Content sync.** Title and body edits project outward. Content equality
  suppresses the returning echo.
- **Progress projection.** The mutually exclusive `mohist:*` state labels and
  the four milestone comment classes project Workflow progress.
- **Finalization.** Completion closes the mirror as completed. Cancellation
  closes it as not planned.

Mohist never places GitHub closing keywords in Pull Request bodies, so delivery
cannot close the mirror early. Durable operation, retry, reconciliation, and
fencing rules are defined in [Durable Outbound Operations](#durable-outbound-operations).

## Durable Outbound Operations

Not every outbound change uses a durable operation record. Reserve-before-send
applies only where repeating a request could create a duplicate and the shipped
path has durable intent or operation state.

- **Mirror creation.** A pending link is saved before the first GitHub create
  request. It carries one invisible HTML marker and a durable
  `MirrorCreateAttempted` flag. This is mirror-create intent, not a general
  outbound operation ledger. A marker search covers GitHub Issues in all states.
  Before the first create attempt, zero exact matches permits one create after
  the intent is atomically reserved. After a create request has been attempted,
  zero exact matches does not authorize another create: the intent remains
  unresolved, the link records an unknown synchronization error, and a later
  sync or mirror event probes again. One exact match binds the link. Multiple
  matches fail closed. A definite rejection can clear the attempted flag only
  when the absence of a remote side effect is known.
- **Comment and close delivery.** Supported Mohist milestone comments and
  mirror closes use `GitHubIssueCommentOperation` rows. The row is reserved
  before the request, stores the exact GitHub Issue number, and stores either a
  comment marker and body or a close reason. A duplicate event that finds the
  existing reservation or posted bookkeeping sends nothing. Comment recovery
  searches the exact marker: one match settles the row, zero matches permits a
  controlled post of the same comment, and multiple matches fail closed. Close
  recovery reads the exact Issue state: the expected closed reason settles the
  row, an open Issue permits the close request, and a different reason is
  ambiguous. Incomplete or unsupported state remains unknown. Command replies
  use a separate durable reply ledger and marker-based worker; they are not
  `GitHubIssueCommentOperation` rows.
- **Current-state projection.** Title and body writes send the complete current
  Mohist values as an idempotent replacement. The mirror marker is included in
  the body, but these writes have no comment-operation row and no marker probe
  for their result. State-label writes replace the `mohist:` label family while
  preserving other labels, then persist the projected label locally. These
  writes also have no comment-operation row or marker probe. A failure records
  durable write-back error and link health; a later event, explicit sync, or
  connection enable can project the current desired value again.

### Outcome certainty

The following certainty model applies to mirror creation, durable comment and
close delivery, and command replies:

- **Confirmed success.** The provider response or an exact remote probe proves
  that the intended change is present. The durable intent or operation is
  settled.
- **Definite failure.** The provider or integration knows that the request was
  rejected before a remote side effect occurred. The intent or reservation may
  be made retryable for a later attempt.
- **Unknown.** A timeout, transport error, server error, or unusable response
  cannot prove whether GitHub applied the change. The durable intent or
  reservation stays in place and must be reconciled before another
  non-idempotent request.
- **Ambiguous.** Reconciliation returns multiple, conflicting, mismatched, or
  incomplete evidence that cannot safely determine the result. The operation
  fails closed and receives no automatic request that could create another
  side effect.

Title/body and state-label projection uses a different rule. Its request
replaces the current desired state, so a later projection may repeat the same
request after an unknown or recorded failure. It does not use the durable
comment-operation ledger or a marker probe.

### Reserve before send

Mirror creation, supported comment and close delivery, and command replies
persist intent or a reservation before sending a non-idempotent request. A
reservation is not success. Only the reservation owner sends the request; a
duplicate event uses the existing durable state instead.

For comment and close delivery, a definite failure releases the reservation
only when the absence of a remote side effect is known. An unknown result keeps
the reservation for reconciliation. Comment recovery uses the exact marker;
close recovery uses the exact target Issue state and `state_reason`. A zero
comment-marker match permits a controlled retry of that comment. It does not
permit a second mirror create after a mirror create has already been attempted.

### Target fencing and reset

The target-fencing contract is limited to shipped durable comment and close
operations. Their rows capture the GitHub Issue number at reservation time. The
recovery worker compares that number with the link's current mirror before
performing recovery. Completion updates link bookkeeping only for the current
target, and recovery failure deletion uses the durable operation id. A stale
recovery action therefore cannot settle or delete a later comment or close
reservation for another mirror target.

This contract does not claim an expected-target guard for state-label success,
title/body success, or generic link error clearing. Those paths use current link
bookkeeping and durable failure/health records rather than the comment/close
operation fence.

Reset is allowed only after a 404 from the exact mirror content endpoint for
the currently linked target. It clears the old mirror's comment and label
bookkeeping, deletes its comment/close operation rows, and starts a pending
mirror projection. A 404 from a comment, state-label, close, or Pull Request
endpoint does not prove that the mirror is gone and must not reset or replace
it.

### Disable and enable

`Disabled` is a pause, not a cancel or delete. It blocks new mirror work,
inbound translation, and recovery claims while preserving pending mirror
intent, uncertain comment/close reservations, command reply rows, and current
projection state. A request already sent may finish while the connection is
disabled. A confirmed result can still settle when its target matches the
current link; an unknown result remains retained for later reconciliation.

When a connection becomes `Active`, durable delivery recovery resumes and the
connection records a reprojection obligation. Reprojection runs sync for every
linked Issue from current Mohist state. It covers current title and body, state
labels, required milestone comments, and terminal close state. The obligation
is cleared only after every linked Issue succeeds, and it survives a process
restart. A failed projection keeps the relevant error visible until a later
successful projection clears it.

### State transitions

Mirror creation follows these transitions:

1. A pending intent with no attempted create performs an exact marker probe. If
   no marker exists, it reserves the intent and sends the first create.
2. An attempted create with an unknown result, an unusable success response, or
   a later zero-match probe remains pending and records an unknown error. It
   must probe again later and must not send another create.
3. One exact marker match links the mirror. Multiple matches fail closed. A
   known no-effect rejection releases the attempted flag for a later create.

Durable comment and close delivery follows these transitions:

1. No operation row becomes a reserved row before the request.
2. A confirmed result becomes posted bookkeeping.
3. A known no-effect failure releases or deletes the reservation.
4. An unknown result retains or defers the reservation until recovery probes
   the remote state.
5. Ambiguous evidence becomes terminal ambiguous state with no automatic
   request.

Current-state projection sends the current title/body or state-label value as a
replacement. A successful state-label request persists local label bookkeeping.
A failure or unknown response records link error/health, and a later event,
explicit sync, or enable reprojection sends the current desired value again.

### Test and review matrix

- **Mirror create:** tests cover the first zero-match probe, an attempted create
  followed by zero, one, or multiple marker matches, definite no-effect
  failure, and an unusable create response. Review checks that zero after an
  attempted create never sends another create.
- **Durable delivery:** tests cover reserve-before-send, duplicate delivery,
  comment marker zero/one/multiple matches, close state and reason
  reconciliation, and command reply marker recovery. Review checks that
  unknown results are reconciled before another non-idempotent request.
- **Current-state projection:** tests cover full title/body replacement,
  content echo suppression, state-label replacement, preservation of other
  labels, durable failure/health, and later reprojection. Review does not
  assume an operation row or marker probe for these writes.
- **Target fencing and reset:** tests prove that stale durable comment/close
  recovery cannot settle or delete a reservation for another GitHub Issue
  target. Review checks the stored target number and durable operation id.
- **404 handling:** tests prove that only the mirror content endpoint can
  trigger reset. Review checks that comment, label, close, and Pull Request
  404s do not replace the mirror.
- **Disable, enable, and health:** tests prove that Disabled retains durable
  work, already sent confirmed results can settle, Active resumes recovery,
  reprojects every link, and clears the reprojection obligation only after
  complete current-state projection. Review checks restart recovery and
  partial-failure behavior.

## Failure Model

An outbound failure records the error on the link and surfaces it in CLI and
Web. It never blocks the Workflow or rolls back Mohist Issue state. The
`mo issue github sync` command is the operator repair path for a missing or
failed mirror. All retry, reconciliation, fencing, pause, reset, and health
rules are defined in [Durable Outbound Operations](#durable-outbound-operations).

## Status

Implemented today: #770 no-Workflow Issue lifecycle, #771 GitHub mirror
visibility in the Issue read models, CLI, and Web, #772 automatic ready-only
mirroring with durable Pending intent, invisible marker reconciliation, and
two-way title/body sync with equality echo suppression, #773 `/mohist start`
command intake with GitHub permission gating, p0-p4 priority mapping,
idempotent link creation, durable command replies, refusal replies, and
reliable command reply recovery, #774 linked lifecycle translation with the
Integrate delivery-echo guard, and #775 reconcile-based recovery. Signed
ingress and normalization, close withdrawal, Pull Request review Approval,
and best-effort write-back with durable failure records are included. No-Workflow
closes honor GitHub's `completed` versus `not_planned` reason, cancelled Issues
reopen to backlog, and completed Issues remain terminal with one follow-up
suggestion. The built-in GitHub PR path omits closing keywords from PR bodies;
terminal write-back still closes mirrors. GitHub links persist healthy/error sync
health and the last error; issue-scoped `sync`, `link`, and `unlink` operations
reconcile or pair mirrors, and connection disable/enable pauses translation and
reprojects existing links. New feed-created Issues no longer emit the
`github-issue` origin label; historical feed-created links may retain it as
data. Connection creation uses a fine-grained PAT with Issues read/write.
Connection configuration contains only Repository binding, identity, and
Approvers.

The remaining gap is GitHub App identity and installation-token exchange.
