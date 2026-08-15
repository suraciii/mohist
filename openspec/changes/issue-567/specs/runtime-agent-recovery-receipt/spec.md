## Requirements

### Requirement: Only a runtime-owned receipt can resolve future interrupted Agent work

For an Agent Workflow execution, a Runner SHALL report a terminal result or
update interruption only through a durable receipt carrying the complete
frozen execution binding and a stable receipt id. The Server SHALL validate
the receipt against the current Workflow settlement before applying it.

#### Scenario: Terminal result delivery is interrupted

- **WHEN** the runtime has produced a normalized terminal result and the
  Runner loses the report transport before acknowledgement
- **THEN** the Runner SHALL retain the same receipt and redeliver it with the
  original execution identity
- **AND** the Server SHALL apply that result at most once

#### Scenario: Host abort follows a returned result

- **WHEN** the run-lifetime cancellation signal fires after an Action has
  returned a normalized result
- **THEN** the Runner SHALL durably record and replay that result through the
  existing result-report acknowledgement path
- **AND** it SHALL NOT execute the Action again after restart

#### Scenario: Process loss leaves only a started fence

- **WHEN** a Runner process is lost after a dispatch is journaled as started
  but before it has durably written a receipt
- **THEN** no runtime activity, session history, or reconnect SHALL create a
  task outcome or replacement dispatch
- **AND** the Workflow SHALL retain its existing unresolved recovery state

#### Scenario: Host abort occurs before a terminal result exists

- **WHEN** the run-lifetime signal aborts while the Action has not returned a
  normalized `WorkItemResult`
- **THEN** the Runner SHALL leave the dispatch in the durable `started` state
  without sending a result report
- **AND** a restarted Runner SHALL refuse the same dispatch at the journal
  fence and SHALL NOT execute it again

This addendum distinguishes an abort after a returned result, which is safely
completed and redelivered, from an abort that has no terminal receipt. The
latter remains unresolved even when the Action threw because cancellation,
process loss, and OOM do not prove that no physical effect occurred.

### Requirement: Confirmed update interruption creates a distinct attempt

A confirmed update interruption SHALL be a physical-stop fact rather than a
task result. Only a receipt matching a durable update-operation fence may
allow the Server to create a replacement attempt.

#### Scenario: Old turn event arrives after a replacement starts

- **WHEN** the Server has accepted a confirmed interruption receipt and
  created a new AgentTurn for the replacement execution
- **THEN** an event or report carrying the original AgentTurn identity SHALL
  be stale
- **AND** it SHALL NOT settle or change the replacement attempt

#### Scenario: Interruption receipt is replayed

- **WHEN** the same confirmed interruption receipt is delivered more than
  once
- **THEN** the Server SHALL return the same durable acknowledgement
- **AND** it SHALL NOT create more than one replacement attempt

### Requirement: Update status remains explicit when a receipt is unavailable

The managed update workflow SHALL require acknowledgement of an exact receipt
for every affected active Agent work before it reports that work as recovered.

#### Scenario: Old Runner is lost during update interruption

- **WHEN** the old Runner exits or becomes unreachable before it has written
  an exact receipt for an affected work item
- **THEN** the update result SHALL identify that work as unresolved
- **AND** it SHALL NOT claim that the work was recovered or re-dispatch it
