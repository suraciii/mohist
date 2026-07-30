### Requirement: Each Agents-list row shows identity and purpose

Every Agent row in the Agents list SHALL display the Agent's name and its description (purpose). A first-time user SHALL be able to read what the Agent is for without opening the detail page. When a description is absent, the row SHALL show an explicit empty/pending affordance rather than silently leaving the purpose blank, so the missing field is discoverable.

#### Scenario: A row shows the description as purpose

- **WHEN** the Agents list renders an Agent with a non-empty description
- **THEN** the row SHALL display the Agent's name and its description text

#### Scenario: A missing description is visible as a gap

- **WHEN** the Agents list renders an Agent whose description is empty or absent
- **THEN** the row SHALL surface an explicit indication that no purpose is set

### Requirement: Each row shows the server's Readiness conclusion

Every active Agent row SHALL display the server-returned Readiness conclusion — Ready, Needs setup, or Unknown. The conclusion SHALL come directly from the server's list response; the list SHALL NOT synthesize, infer, or cache a Readiness verdict from local data. Unknown SHALL be visually distinct from both Ready and Needs setup.

#### Scenario: Ready, Needs setup and Unknown each render distinctly

- **WHEN** the list renders active Agents whose server Readiness conclusions are Ready, Needs setup and Unknown respectively
- **THEN** each row SHALL display its own conclusion, and the three conclusions SHALL be distinguishable from one another

#### Scenario: The list does not invent a verdict when the server omits Readiness

- **WHEN** the server returns an Agent without a Readiness conclusion
- **THEN** the row SHALL display Unknown, not Ready or Needs setup

### Requirement: Each row shows Availability and active/queued workload

Every active Agent row SHALL display the server's Availability conclusion (can start now, or waiting with its reason) together with the Agent's current workload: the count of active executions and the count of queued (waiting) executions. Runner offline, runner capacity full, agent concurrency limit, and dispatch-pending SHALL each be presented as Availability, never as a Readiness or configuration state.

#### Scenario: A row shows can-start-now with zero workload

- **WHEN** the server reports an Agent as available with no active and no queued work
- **THEN** the row SHALL indicate it can start now and SHALL show an active count of zero and an empty queue

#### Scenario: A row shows waiting workload with a reason

- **WHEN** the server reports an Agent as waiting (for example, runner offline, capacity full, or concurrency limit reached) with active and queued work
- **THEN** the row SHALL indicate it is waiting, SHALL show the waiting reason, and SHALL show the active and queued counts

#### Scenario: Runner offline is not a configuration error

- **WHEN** the server reports an Agent as Ready but waiting because no runner is online or runner capacity is full
- **THEN** the row SHALL present that state as Availability (waiting), and SHALL NOT present it as Needs setup or any configuration gap

### Requirement: Availability and workload are served at list scope

The Agents list SHALL obtain Availability and workload for every listed Agent without issuing a separate Availability request per Agent. Assembling the list SHALL NOT require N round-trips or per-row polling that scales with the number of Agents. Archived Agents SHALL remain listed (in their section) and SHALL NOT be required to report live Availability or workload.

#### Scenario: A single list response carries Availability for all Agents

- **WHEN** the list renders N active Agents
- **THEN** the Availability and workload for all N Agents SHALL be obtainable without issuing one Availability request per Agent

#### Scenario: Archived rows do not demand live Availability

- **WHEN** the list renders an archived Agent
- **THEN** the row SHALL be visually distinct as archived, and SHALL NOT require a live Availability fetch to render
