### Requirement: Runtime readiness reflects OpenCode execution health

The OpenCode runtime SHALL be not ready before startup. It SHALL become ready after the shared OpenCode server starts successfully, its health check succeeds, and the runtime's server-lifecycle event handling is established. Model discovery and model-catalog availability MUST NOT be prerequisites for readiness.

#### Scenario: Server startup and health succeed
- **WHEN** the shared OpenCode server starts and passes its health check
- **THEN** the runtime SHALL become ready
- **AND** it SHALL accept OpenCode execution requests

#### Scenario: Model discovery has not completed
- **WHEN** the server has passed its health check but independent model discovery is pending
- **THEN** the runtime SHALL be ready

#### Scenario: Model discovery fails
- **WHEN** the server has passed its health check but CLI model discovery fails or returns an empty result
- **THEN** the runtime SHALL remain ready
- **AND** model discovery failure MUST NOT become a runtime-readiness diagnostic

### Requirement: OpenCodeRuntime does not own model discovery

The OpenCode runtime lifecycle SHALL NOT load, store, or refresh the model catalog and MUST NOT invoke either the OpenCode SDK v2 model-list APIs or the OpenCode CLI for model discovery. Starting, rebuilding, and shutting down the runtime SHALL operate independently of the runner host's model-discovery state.

#### Scenario: Runtime starts
- **WHEN** `OpenCodeRuntime` starts
- **THEN** it SHALL start and health-check the shared OpenCode server without loading a model catalog

#### Scenario: Runtime rebuilds after server loss
- **WHEN** the shared OpenCode server is rebuilt after an exit
- **THEN** runtime recovery SHALL require the replacement server to start and pass health
- **AND** it MUST NOT wait for model discovery before becoming ready

### Requirement: Execution-health failures still gate work claiming

If the OpenCode server cannot start or its health check fails, the runtime SHALL remain not ready and SHALL expose an actionable diagnostic for that execution-health failure. While the runtime is not ready, the runner SHALL stop claiming new work but SHALL continue reconciling already completed reports and checking for runtime recovery.

#### Scenario: Server cannot start
- **WHEN** the shared OpenCode server fails to start
- **THEN** the runtime SHALL remain not ready
- **AND** it SHALL expose a `server-spawn-failed` diagnostic
- **AND** the runner SHALL not claim new work

#### Scenario: Health check fails
- **WHEN** the shared OpenCode server starts but its health check fails
- **THEN** the runtime SHALL remain not ready
- **AND** it SHALL expose a `health-failed` diagnostic
- **AND** the runner SHALL not claim new work

#### Scenario: Healthy runtime with empty catalog
- **WHEN** the runtime is ready and the runner's discovered model catalog is empty
- **THEN** the runner SHALL continue claiming work
- **AND** OpenCode SHALL remain the final authority on whether an explicitly configured model can execute

### Requirement: Server loss invalidates readiness until health recovers

When the shared OpenCode server disconnects or its heartbeat fails, the runtime SHALL immediately become not ready, reject new runtime operations as unavailable, and rebuild its shared server lifecycle. It SHALL become ready again after the replacement server starts and passes health, without waiting for model discovery. An interrupted in-flight turn MUST NOT be automatically replayed.

#### Scenario: Shared server exits during operation
- **WHEN** the runtime observes that its shared OpenCode server has disconnected
- **THEN** it SHALL become not ready and expose a `server-exit` diagnostic
- **AND** new runtime operations and work claims SHALL be rejected until recovery

#### Scenario: Replacement server becomes healthy
- **WHEN** a replacement OpenCode server starts and passes health after server loss
- **THEN** the runtime SHALL become ready again
- **AND** the runner SHALL resume claiming work without waiting for catalog discovery

#### Scenario: In-flight turn is interrupted by server loss
- **WHEN** server loss interrupts an in-flight turn
- **THEN** the turn SHALL fail
- **AND** the runtime MUST NOT automatically replay it

### Requirement: Shutdown clears runtime readiness

Runtime shutdown SHALL mark the runtime not ready and close the shared event subscription and server. Model-discovery timer cleanup SHALL remain owned by the runner host and MUST NOT be coupled to `OpenCodeRuntime` shutdown.

#### Scenario: Runtime shuts down
- **WHEN** runtime shutdown begins
- **THEN** the runtime SHALL become not ready
- **AND** it SHALL close its shared server and event subscription
- **AND** it SHALL perform no model-catalog cleanup
