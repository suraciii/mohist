### Requirement: The adapter is a stateless protocol translator

The `mohist-slack` adapter SHALL translate Slack Socket Mode wire payloads into normalized Connection envelopes carrying a stable provider identity, and SHALL translate outbound Connection delivery intents into Slack wire payloads. The adapter SHALL NOT persist provider inbox entries, conversation mappings, pending deliveries, or any shadow copy of Agent instructions, configuration, or skills. Anything that must survive a restart SHALL live in the Server, not in the adapter.

#### Scenario: Inbound events become normalized envelopes
- **WHEN** the adapter receives a Slack event over Socket Mode
- **THEN** it submits a normalized envelope with a stable provider identity to the Server Connection boundary and holds no durable record of the raw payload after the Server acknowledges

#### Scenario: The adapter holds no durable provider state
- **WHEN** the adapter is asked what provider inbox entries, conversation mappings, or pending deliveries it owns
- **THEN** it owns none of them; they all live in the Server

### Requirement: The adapter enters through the Connection boundary and cannot bypass the Agent API

The adapter SHALL reach Agent execution exclusively through the Server Connection boundary, which in turn invokes the Agent API. The adapter MUST NOT bypass that boundary by shelling out to `mo`, reading or writing the Mohist database, calling grains or Runner or Runtime protocols directly, parsing Runner logs, or storing provider credentials or Agent configuration locally.

#### Scenario: Dispatch flows through the Connection boundary
- **WHEN** the adapter handles an inbound DM task
- **THEN** it submits the normalized envelope to the Server Connection boundary and the Server invokes the Agent API; the adapter does not call the Agent API, grains, Runner, or the database directly

#### Scenario: No local credential or config shadow
- **WHEN** the adapter process is inspected
- **THEN** it contains no persisted Slack credentials and no copy of Agent instructions, runtime, model, or skills

### Requirement: The adapter is a CLI-managed service with an independent lifecycle

The `mohist-slack` adapter SHALL run as a CLI-managed service whose install, status, and update are controlled by `mo install slack`, `mo service status slack`, and `mo update slack`. The adapter SHALL be restartable independently of the Server, and a Server restart SHALL NOT require the adapter to lose its in-memory protocol state beyond what Socket Mode reconnection re-establishes.

#### Scenario: Independent adapter restart
- **WHEN** the adapter is restarted while the Server stays up
- **THEN** the adapter reconnects over Socket Mode and resumes pulling unconverged inbound and outbound items from the Server without local state recovery

#### Scenario: Service lifecycle commands operate on the adapter
- **WHEN** a user runs `mo install slack`, `mo service status slack`, or `mo update slack`
- **THEN** the command operates on the `mohist-slack` adapter and not on the Server or Runner

### Requirement: One adapter per Server carries many Connections but never shares Bot identity

One Mohist Server SHALL operate one `mohist-slack` adapter that carries all of that Server's Slack Connections. Each Connection SHALL continue to use its own independent App and Bot credentials; sharing one adapter process SHALL NOT imply sharing one Bot identity across Connections.

#### Scenario: Multiple connections on one adapter
- **WHEN** a Server manages several Slack Connections
- **THEN** a single `mohist-slack` adapter serves all of them, each using its own App and Bot credentials

### Requirement: The adapter bounds instantaneous concurrency without owning a persistent queue

The adapter SHALL bound its instantaneous in-flight concurrency to protect the Server and Slack rate limits, but SHALL NOT own a persistent queue or apply product-level backpressure. Persistent ingress capacity, outbound outbox capacity, and backpressure decisions belong to the Server; the adapter only rate-limits its own in-flight work.

#### Scenario: Concurrency is bounded in the adapter
- **WHEN** a burst of inbound Slack events arrives
- **THEN** the adapter limits its in-flight concurrency and does not push an unbounded burst to the Server

#### Scenario: Backpressure is decided by the Server
- **WHEN** persistent capacity is exceeded
- **THEN** the Server, not the adapter, decides that the Connection is Backpressured and stops accepting new input
