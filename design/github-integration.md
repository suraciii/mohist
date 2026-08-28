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
coordinates, carries the bookkeeping that makes every outbound operation
idempotent (pending creation intent, invisible body marker, emitted milestone
comments, current state label), and reports sync health: healthy, or the last
synchronization error.

The link is the idempotency key for every inbound and outbound path. It is
created exactly once per pair — at mirror creation, at `/mohist start`, or at
manual `link` — and survives disable/enable cycles of its connection.

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

GitHub can accept a request and lose its response. A retry can then create a
second mirror or duplicate a comment. The integration therefore separates
remote outcome certainty from local request execution and keeps each outbound
intent recoverable.

An outbound operation is one intended GitHub change. It may create or update a
mirror, post a Mohist comment, replace the `mohist:` state label, close a
mirror, or deliver a command reply. The operation is durable before any GitHub
request is sent. It has one stable operation identity, one logical operation
key, and one target identity. A mirror operation also carries the mirror
incarnation that it targets.

A mirror incarnation is one concrete GitHub Issue identity linked to one Mohist
Issue. A mirror-create reservation defines the next incarnation and binds it
when a confirmed create or exact marker probe identifies the GitHub Issue. A
replacement mirror starts a new incarnation. A retry keeps the same operation
identity and incarnation. A new logical change gets a new operation identity.
Operations that target a source GitHub Issue use that Issue identity; operations
that target a mirror use both the Issue identity and its incarnation.

### Outcome certainty

Every outbound result has exactly one certainty:

- **Confirmed success.** The provider response or an exact remote probe proves
  that the intended change is present. The operation is settled.
- **Definite failure.** The provider proves that it rejected the request before
  the remote state could change. The reservation may be released for a retry.
- **Unknown.** A timeout, transport error, server error, or unusable response
  cannot prove whether GitHub applied the change. The reservation stays in
  place and must be reconciled before another request.
- **Ambiguous.** Reconciliation returns conflicting, multiple, mismatched, or
  incomplete evidence. The operation fails closed. It receives no automatic
  retry or cleanup that could change the remote state.

### Reserve before send

Before any GitHub request, the system durably reserves the exact logical
operation, target identity, mirror incarnation when applicable, and the probe
needed to establish its result. The reservation is not success. Only the
reservation owner may send the request, and a duplicate event that sees the
same reserved or settled operation sends nothing.

An unknown result never permits a blind retry. The system first runs the exact
probe for that operation:

- A mirror create or comment uses an exact marker probe on the intended GitHub
  scope. One matching result confirms success. Zero matches proves no effect
  was observed and permits a controlled retry of the same operation. Multiple
  matches are ambiguous and fail closed.
- A state-label operation uses a state probe against the exact target. The
  expected `mohist:` label with no competing `mohist:` state label confirms
  success. A missing expected label permits a controlled retry. Conflicting or
  incomplete label state is ambiguous.
- A close operation uses the exact target identity and its state. `closed` with
  the exact intended `state_reason` confirms success. `open` permits a
  controlled retry. A different close reason, a mismatched target, or an
  incomplete state is ambiguous.

A definite failure may release the reservation because the provider proved that
no remote change occurred. The next attempt uses the same logical operation
and current target. An unknown result retains the reservation until its probe
proves success, proves no effect, or fails closed as ambiguous.

### Incarnation fencing and reset

Every completion, failure update, retry release, lease release, and cleanup
must match the durable operation identity and the current target incarnation.
A stale action is a no-op. It must not settle a newer operation, release or
delete a newer reservation, change current mirror health, or modify a
replacement mirror.

Reset is an identity transition, not an ordinary retry. Only a 404 from the
exact mirror content endpoint, for the currently linked target, may prove that
the mirror identity is gone and start a reset. Reset fences the old
incarnation, retires only its target-specific outbound bookkeeping, establishes
a new mirror intent, and reprojects the current Mohist state. A definite
mirror-create failure can release a pending reservation without a reset. An
unknown mirror-create result keeps its reservation and requires the exact
marker probe first.

A 404 from a comment, state-label, close, or Pull Request endpoint does not
prove that the mirror is gone. It must not reset or replace the mirror. The
operation follows its own certainty and reconciliation rule instead.

### Disable and enable

`Disabled` is a pause, not a cancel or delete. It stops new sends and recovery,
while preserving pending and unknown operations, their target, and current
projection state. A request already sent may finish, but its completion remains
subject to incarnation fencing. Disabling a connection must not lose pending
work.

When a connection becomes `Active`, pending operations resume under the same
certainty and fencing rules. The system then reprojects every linked Issue
from current Mohist state. Reprojection covers current content, state labels,
required milestone comments, and terminal close state. It is idempotent and
survives process restart.

A link may report healthy only after its complete current-state projection is
confirmed. An enabled connection clears its reprojection obligation only after
every linked Issue meets that condition. A failed or ambiguous operation keeps
the relevant error visible and leaves recovery pending.

### State transitions

1. `unreserved -> reserved`: persist the exact operation before sending.
2. `reserved -> confirmed`: the response or exact probe proves the change.
3. `reserved -> definite-failure`: the provider proves no remote change.
4. `reserved -> unknown`: the response cannot prove the remote outcome.
5. `unknown -> confirmed`: the exact probe finds the intended effect.
6. `unknown -> reserved`: the exact probe proves no effect, so the same
   operation may be sent again.
7. `unknown -> ambiguous`: evidence conflicts, is multiple, or is incomplete;
   fail closed.
8. `reserved` or `unknown -> paused`: the connection becomes `Disabled`; keep
   the operation.
9. `paused -> reserved` or `unknown`: the connection becomes `Active`; resume
   reconciliation and projection.
10. `current incarnation -> reset`: the exact mirror content endpoint proves
    the target is missing; fence the old incarnation and start a replacement.
11. `stale completion or cleanup -> ignored`: the target or operation identity
    no longer matches the current incarnation.

### Test and review matrix

- **Outcome certainty:** tests cover confirmed, definite, unknown, and
  ambiguous results. Review checks that every unknown path probes before a
  second request.
- **Marker and state probes:** tests cover zero, one, and multiple marker
  matches plus exact label and close-state results. Review checks that probes
  use the intended target and all required state fields.
- **Reservation and duplicate delivery:** tests prove reserve-before-send and
  one remote write for duplicate events. Review checks that retries reuse the
  same logical operation and never create a second unproven write.
- **Incarnation and reset:** tests prove that reset fences old completion,
  failure, lease release, and cleanup. Review checks that stale work cannot
  settle, delete, or alter current-incarnation state.
- **404 handling:** tests prove that only the mirror content endpoint can
  trigger reset. Review checks that comment, label, close, and Pull Request
  404s do not replace the mirror.
- **Disable, enable, and health:** tests prove that Disabled retains work and
  Active resumes it, reprojects every link, and clears health only after
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
