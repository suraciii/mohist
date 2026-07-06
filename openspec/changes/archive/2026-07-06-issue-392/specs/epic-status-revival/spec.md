### Requirement: Done epic SHALL wake to running when an open issue is linked

Linking an open linked issue (any non-terminal issue, per the existing `IsOpen` definition) to a `done` epic SHALL revert the epic from `done` to `running` in the same commit as the link. The wake-up is implicit: it SHALL NOT require a separate `start` or `resume` call, and it SHALL target `running` (not `idle`) because the newly linked open issue represents active work. This replaces the prior "archive-only link to a terminal epic" semantics — linking to a `done` epic is no longer a pure record; it is a state-changing operation.

Linking an already-terminal issue (done or cancelled) to a `done` epic SHALL leave the epic `done`, because no new open work was introduced.

#### Scenario: Done epic linked with an open issue transitions to running

- **WHEN** an epic is in status `done`
- **AND** an issue that is open (non-terminal) is linked to it via `LinkIssueAsync`
- **THEN** the epic's status SHALL become `running`
- **AND** the status change SHALL be persisted in the same `SaveChangesAsync` commit that persists the new link row

#### Scenario: Done epic linked with an already-terminal issue stays done

- **WHEN** an epic is in status `done`
- **AND** an issue that is already terminal (done or cancelled) is linked to it via `LinkIssueAsync`
- **THEN** the epic's status SHALL remain `done`
- **AND** no wake-up SHALL occur

#### Scenario: Wake to running requires no manual start or resume

- **WHEN** a `done` epic is woken by linking an open issue
- **THEN** the epic SHALL reach `running` without any preceding or subsequent `start` / `resume` call from the caller
- **AND** the wake-up SHALL NOT transition the epic through `idle`

#### Scenario: Idempotent re-link of an already-linked issue does not wake the epic

- **WHEN** an issue is already linked to a `done` epic
- **AND** the same issue is linked again to that epic via `LinkIssueAsync`
- **THEN** the call SHALL be idempotent (no duplicate link row)
- **AND** the epic's status SHALL be unchanged by the duplicate call

### Requirement: Batch link SHALL apply the same wake-up rules as single link

`POST /{id}/issues:batch` (`LinkIssuesAsync`) SHALL evaluate wake-up using the same conditions as single link: if the batch contains at least one open issue being linked to a `done` epic, the epic SHALL wake to `running`; if the batch contains only already-terminal issues being linked to a `done` epic, the epic SHALL stay `done`. The wake decision is based on the open-vs-terminal state of the issues actually being linked in the batch, not on the batch size.

#### Scenario: Batch containing at least one open issue wakes a done epic to running

- **WHEN** an epic is in status `done`
- **AND** a batch link request (`LinkIssuesAsync`) includes at least one open issue that is successfully linked
- **THEN** the epic's status SHALL become `running`
- **AND** the wake-up SHALL be persisted atomically with the successful link(s)

#### Scenario: Batch containing only terminal issues leaves a done epic done

- **WHEN** an epic is in status `done`
- **AND** a batch link request (`LinkIssuesAsync`) includes only already-terminal issues (no open issue is linked)
- **THEN** the epic's status SHALL remain `done`
- **AND** no wake-up SHALL occur

### Requirement: Closed epic SHALL reject all issue links

`closed` SHALL be a true terminal state representing an abandoned milestone: linking any issue to a `closed` epic SHALL be rejected. This applies to both single link (`LinkIssueAsync`) and batch link (`LinkIssuesAsync`). The single-link path SHALL raise a domain exception that the API maps to a `409 Conflict` with a closed-link-rejection error code (distinct from the membership-duplicate case). A batch link whose target epic is `closed` SHALL be rejected as a whole (a `409 Conflict`) rather than producing per-item outcomes, because no item in the batch can be accepted. The only way to exit `closed` SHALL remain the explicit `Reopen` operation.

#### Scenario: Single link to a closed epic is rejected

- **WHEN** an epic is in status `closed`
- **AND** an issue is linked to it via `LinkIssueAsync`
- **THEN** the call SHALL be rejected with a domain exception
- **AND** the API SHALL return `409 Conflict` with a closed-link-rejection error code
- **AND** no link row and no active-membership row SHALL be created

#### Scenario: Batch link to a closed epic is rejected as a whole

- **WHEN** an epic is in status `closed`
- **AND** a batch link request (`LinkIssuesAsync`) is made containing one or more issue identifiers
- **THEN** the request SHALL be rejected as a whole with a `409 Conflict`
- **AND** no per-item linked/conflict outcomes SHALL be produced for a `closed` target
- **AND** no link rows SHALL be created

#### Scenario: Reopen remains the only exit from closed

- **WHEN** an epic is in status `closed`
- **THEN** a subsequent successful link SHALL be impossible until the epic is reopened
- **AND** only the explicit `Reopen` operation SHALL transition the epic out of `closed`

### Requirement: Wake-up SHALL re-establish active-membership rows atomically with the link

When a `done` epic wakes, the `EpicActiveIssues` row for the newly linked open issue SHALL be (re-)established in the same database commit as the status change and the link row, mirroring the active-membership re-claim performed by `ReopenAsync`. This ensures the cross-aggregate active-membership uniqueness invariant (an issue belongs to at most one non-terminal epic) and autopilot issue selection observe the newly linked open issue. The wake-up SHALL honor that uniqueness invariant: if the issue is already actively owned by another non-terminal epic, the link SHALL be rejected as a membership conflict rather than producing a duplicate active-membership row. If the commit fails, the epic SHALL remain `done` and the wake-up SHALL be retryable as a whole (no partial idle/running-without-active-row state).

#### Scenario: Active-membership row added in the same commit as the wake-up

- **WHEN** an open issue is linked to a `done` epic, waking it to `running`
- **THEN** an `EpicActiveIssues` row for that issue and epic SHALL exist after the commit
- **AND** the row SHALL be created in the same transaction as the status and link-row change

#### Scenario: Wake-up respects cross-aggregate active-membership uniqueness

- **WHEN** an issue is already actively owned by another non-terminal epic
- **AND** an attempt is made to link that issue to a `done` epic
- **THEN** the link SHALL be rejected as a membership conflict
- **AND** no second `EpicActiveIssues` row for that issue SHALL be created
- **AND** the `done` epic's status SHALL remain `done`

#### Scenario: Wake-up that fails to persist rolls back the status change

- **WHEN** a wake-up's `SaveChangesAsync` fails (e.g. the active-membership insert fails)
- **THEN** the epic SHALL remain `done`
- **AND** no active-membership row and no new link row SHALL be left behind
- **AND** a retry of the same link SHALL be able to perform the full wake-up again

### Requirement: Wake-up SHALL hand the newly linked open issue to autopilot

Because the wake-up transitions the epic to `running` (not `idle`), the existing autopilot reconcile path SHALL pick up the newly linked open issue and advance it without any caller-initiated `start`. The wake-up SHALL NOT alter autopilot's start/pause/resume internals or its serial "at most one in-progress" selection rule; it only restores the epic to the `running` state from which autopilot drives.

#### Scenario: Autopilot advances the newly linked open issue after wake-up

- **WHEN** a `done` epic is woken to `running` by linking an open issue
- **AND** the epic has no other in-progress linked issue
- **THEN** the autopilot reconcile path SHALL select and start the newly linked open issue
- **AND** this SHALL happen without the caller issuing a separate `start` for the issue or the epic

### Requirement: MarkDone and auto-done SHALL be rejected while open linked issues exist

As the symmetric pin to wake-up, an epic SHALL NOT be allowed to reach `done` while it has any open linked issue. Both the explicit `MarkDone` transition and the automatic `AutoMarkDoneIfReadyAsync` / reconcile-on-terminal-event path SHALL be rejected (or, for the auto path, SHALL be a no-op that leaves the epic non-terminal) when at least one open linked issue exists, raising `EpicNotReadyToMarkDoneException` for the explicit path. Together with wake-up this closes the loop: open issues exhausted → auto-done; new open issue linked → wake to running.

#### Scenario: Manual mark done with an open linked issue is rejected

- **WHEN** an epic has at least one open linked issue
- **AND** `MarkDone` (via `SetStatusAsync("done")`) is invoked
- **THEN** the call SHALL raise `EpicNotReadyToMarkDoneException`
- **AND** the epic's status SHALL remain unchanged

#### Scenario: Auto-done with an open linked issue is a no-op

- **WHEN** an epic has at least one open linked issue
- **AND** `AutoMarkDoneIfReadyAsync` (or the reconcile-on-terminal-event path) is invoked
- **THEN** the epic SHALL NOT transition to `done`
- **AND** the epic's status SHALL remain unchanged
