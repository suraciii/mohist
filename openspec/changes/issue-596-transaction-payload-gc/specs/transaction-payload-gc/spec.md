### Requirement: Managed updates serialize runtime ownership

The managed runtime transaction owner SHALL hold an exclusive runtime-root
update lock from after runtime-root resolution through Commit or Rollback.

#### Scenario: concurrent update is rejected before activation

- **WHEN** the runtime update lock cannot be acquired
- **THEN** managed update preparation returns an actionable failure
- **AND** no active pointer, service target, or candidate release is activated

#### Scenario: preparation failure releases ownership

- **WHEN** preparation fails after acquiring the lock
- **THEN** the lock is released before the failed preparation returns

### Requirement: History collection is payload-only

The collector SHALL remove only `snapshot`, `build`, and `candidate` directories
under an eligible historical transaction. It SHALL retain the transaction
directory, its `state.json`, `active.json`, `verified.json`, launcher backup,
and every path under `releases`.

The removal primitive SHALL make only the exact payload tree owner-writable
before deletion because source snapshots are persisted read-only. It SHALL not
follow symbolic links while changing permissions or deleting.

#### Scenario: old verified payload is reclaimed

- **GIVEN** a transaction has a valid `state.json` with `status=verified`
- **AND** its id is not the current, active, or verified transaction id
- **WHEN** collection runs
- **THEN** its disposable payload directories are removed
- **AND** its state and release paths remain

#### Scenario: old rolled-back payload is reclaimed

- **GIVEN** a transaction has a valid `state.json` with `status=rolled-back`
- **WHEN** collection runs
- **THEN** only its disposable payload directories are removed

### Requirement: Live and uncertain transactions are preserved

The collector SHALL preserve any transaction that is current, referenced by a
pointer, candidate-staged, candidate-activated, recovery-failed, unknown,
missing-state, malformed-state, or symlinked.

#### Scenario: candidate or recovery transaction is retained

- **GIVEN** a transaction has `status=candidate-staged`, `candidate-activated`,
  or `recovery-failed`
- **WHEN** collection runs
- **THEN** all of its payload and state paths remain unchanged

#### Scenario: malformed state fails open

- **WHEN** a transaction state cannot be read or parsed
- **THEN** collection skips that transaction without deleting any child

### Requirement: Collection failure is non-fatal

Collection SHALL report diagnostics and continue the managed update when a
payload deletion fails. It SHALL never delete a different protected path as a
fallback.
