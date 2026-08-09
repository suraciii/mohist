# Epic State Reflects Reality (issue-392)

> The product decision in this record remains valid. The write authority and
> transaction design for membership are superseded by
> [`issue-owns-epic-membership.md`](issue-owns-epic-membership.md). Issue commits
> `EpicNumber?` in its own transaction. A durable event makes Epic recompute
> and converge state. Membership and Epic no longer share one transaction.

## Background

Epic has two terminal states: `done` for normal completion and `closed` for an
abandoned goal. Previously, `LinkIssueAsync` treated a terminal Epic link as an
archival link that recorded membership without changing state. Adding an open
Issue therefore left Epic state inconsistent with reality.

## Decision

### 1. Revive Done Automatically

Linking to a `done` Epic is not purely archival. An open, nonterminal linked
Issue moves Epic from `done` to `running`. A terminal linked Issue leaves Epic
`done`.

Reasons:

- `done` must mean that no incomplete work currently remains. State must change
  as soon as open work joins.
- Revival goes directly to `running`, not `idle`, because the new open Issue is
  active work.
- A terminal Issue introduces no work and does not revive the Epic. Historical
  records can still be added to a Done Epic.

### 2. Reject Links to Closed

`closed` is a true terminal state for an abandoned goal. No Issue can link to a
`closed` Epic. The caller must Reopen first.

This distinguishes `done` from `closed`. If an Issue could revive both, the
states would behave identically. Reopen is the only explicit exit from
`closed`.

### 3. Do Not Rewrite Historical Data Automatically

Historical Epics that are `done` but already link an open Issue remain
unchanged. An operator can repair them with unlink plus relink, or Reopen plus
Start. Do not silently rewrite existing state at scale.

## Implementation

- Add the `WakeFromDone` domain transition from `done` to `running` and call it
  after confirming that the linked Issue is open.
- Reject a `closed` link with `EpicClosedCannotLinkException` in the domain and
  map it to `409 EPIC_CLOSED_CANNOT_LINK` at the API.
- Issue commits membership first in its transaction. `IssueEpicChanged`
  triggers Epic recomputation. If Epic save fails, event redelivery recovers it
  without rolling back committed Issue membership.
- Single and bulk link follow the same rules. A Closed Epic rejects the complete
  bulk operation. A Done Epic with open items revives once at the first open
  item.
