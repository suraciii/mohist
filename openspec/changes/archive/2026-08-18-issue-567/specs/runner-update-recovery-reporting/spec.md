### Requirement: The update waits boundedly for per-work recovery acknowledgement

After a confirmed interrupt, the managed update flow SHALL wait no longer than a bounded interval for a Server-side recovery acknowledgement of every affected work id named by the interrupt confirmation. When the bound expires, the update SHALL stop waiting and report the outstanding work as unresolved; it MUST NOT wait indefinitely for long Agent turns or a missing Runner.

#### Scenario: All affected work is acknowledged within the bound

- **WHEN** every affected work id receives a recovery acknowledgement within the bounded interval after activation
- **THEN** the update SHALL proceed to report each affected work as recovered

#### Scenario: The recovery wait bound expires

- **WHEN** the bounded interval expires before an affected work id is acknowledged
- **THEN** the update SHALL stop waiting and report that work as unresolved with its identity and state
- **AND** the update SHALL NOT block further completion on that work

#### Scenario: Old Runner is lost before acknowledgement

- **WHEN** the old Runner exits before providing the acknowledgement boundary for affected work
- **THEN** the update SHALL treat that work as unresolved rather than recovered
- **AND** it SHALL NOT convert the service restart itself into a recovery claim

### Requirement: The update outcome reports each affected work with identity and state

The CLI SHALL report, for every work id named by the confirmed interrupt, whether that work recovered or remains unresolved, together with the work identity and its current recovery state. The persisted update outcome SHALL carry the same per-work results.

#### Scenario: Per-work results are listed

- **WHEN** the managed update finishes its bounded recovery wait with a mix of recovered and unresolved work
- **THEN** the CLI output SHALL list each affected work with its identity and its recovered or unresolved state
- **AND** the persisted update outcome SHALL record the same per-work results

#### Scenario: Interrupt with no active work needs no recovery reporting

- **WHEN** the confirmed interrupt reports zero affected work ids
- **THEN** the update SHALL report no affected work and SHALL NOT claim recovery for any work

### Requirement: Success is never claimed while affected work is unresolved

If any affected work is unresolved when the update completes, the update SHALL NOT claim complete success: the emitted outcome and exit semantics SHALL reflect the unresolved work even when candidate activation, service restart, and identity verification all succeeded. An update whose affected work is all recovered MAY claim success.

#### Scenario: Unresolved work forces a non-success outcome

- **WHEN** the Runner update activated and verified the candidate but at least one affected work id is unresolved at the end of the bounded wait
- **THEN** the update outcome SHALL report the update as not fully successful
- **AND** the CLI exit semantics SHALL reflect the unresolved affected work

#### Scenario: Fully acknowledged update claims success

- **WHEN** every affected work id is acknowledged as recovered and runtime verification succeeded
- **THEN** the update MAY report complete success
- **AND** the reported success SHALL include the per-work recovered results it is based on
