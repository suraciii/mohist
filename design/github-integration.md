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
  gets a pending link persisted before the GitHub create. The mirror body
  carries one invisible, deterministic HTML marker owned by Mohist; it has no
  visible footer. If the create result is unknown (timeout, 5xx), bounded
  reconciliation searches all GitHub states for that marker. Exactly one match
  is linked, multiple matches fail closed, and no match is not posted again
  until reconciliation has completed. The backlink to Mohist lives in the
  confirmation comment.
- **Content sync.** Title and body edits project outward, subject to the same
  content-equality rule that suppresses the returning echo.
- **Progress projection.** The mutually exclusive `mohist:*` state labels and
  the four milestone comment classes (confirmation, Approval point, completion
  with delivery summary and PR link, cancellation with reason), gated by the
  link's bookkeeping so redelivery never repeats a milestone.
- **Finalization.** Completion closes the mirror as completed; cancellation
  closes it as not planned.

Two content rules protect the loop. Mohist never places GitHub closing keywords
in Pull Request bodies, so delivery cannot close the mirror early. And the
mirror body carries no visible tracking footer: its one invisible marker is
stripped before content enters Mohist and retained on outbound writes, while

the backlink to Mohist lives in the confirmation comment.

## Failure Model

Mirroring is reliable by reconciliation, not by queueing:

- A failed outbound operation records the error on the link and surfaces it in
  CLI and Web. It never blocks the Workflow and never rolls back Issue state.
- `mo issue github sync` is the single repair verb: it creates a missing
  mirror, pushes current Mohist state, and clears the error. It is safe to run
  at any time because every outbound operation is idempotent.
- State-label projection is self-healing: each Workflow milestone replaces the
  whole label set from current state.
- Disabling a connection pauses all inbound translation and outbound mirroring;
  enabling re-projects every linked Issue once. There is no connection
  deletion, so a link never outlives its credentials.

## Status

Implemented today: #770 no-Workflow Issue lifecycle, #771 GitHub mirror
visibility in the Issue read models, CLI, and Web, #772 automatic ready-only
mirroring with durable Pending intent, invisible marker reconciliation, and
two-way title/body sync with equality echo suppression, #773 `/mohist start`
command intake with GitHub permission gating, p0-p4 priority mapping,
idempotent link creation, durable command replies, refusal replies, and
reliable command reply recovery, and #774 linked lifecycle translation with the
Integrate delivery-echo guard. Signed ingress and normalization, close
withdrawal, Pull Request review Approval, and best-effort write-back with
durable failure records are included. No-Workflow closes honor GitHub's
`completed` versus `not_planned` reason, cancelled Issues reopen to backlog, and
completed Issues remain terminal with one follow-up suggestion. The built-in
GitHub PR path omits closing keywords from PR bodies; terminal write-back still
closes mirrors. New feed-created Issues no longer emit the `github-issue` origin
label; historical feed-created links may retain it as data. Connection creation
configures a fine-grained PAT with Issues read/write when supplied; GitHub App
identity remains unimplemented. Connection configuration contains only
Repository binding, identity, and Approvers. The later sync-health and
operator-recovery slice remains open.

Open questions:

- With out-of-order delivery, a close can arrive before the command that
  created the link; the command path re-checks GitHub state before starting, but
  the acceptable stale window is unobserved in practice.
- App installation tokens creating Pull Requests in repositories under personal
  accounts may have compatibility limits despite long-running practices such as
  Dependabot.
