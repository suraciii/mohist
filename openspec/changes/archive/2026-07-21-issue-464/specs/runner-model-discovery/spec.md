### Requirement: The runner discovers models at startup

The runner SHALL perform one best-effort CLI model discovery during startup before its first registration. A successful discovery SHALL populate the first registration with the discovered models and variants. A failed or empty initial discovery SHALL populate the first registration with an empty model list and empty variant map and MUST NOT prevent the runner from connecting or starting its worker loop.

#### Scenario: Initial discovery succeeds
- **WHEN** startup discovery returns models and variants
- **THEN** the runner's first registration SHALL carry those values in `coderModels` and `coderModelVariants`

#### Scenario: Initial discovery fails while the runtime is healthy
- **WHEN** startup discovery fails or returns an empty result
- **AND** the OpenCode runtime is healthy
- **THEN** the runner SHALL register with empty `coderModels` and `coderModelVariants`
- **AND** it SHALL continue polling and claiming work

### Requirement: The runner periodically rediscovers models

After startup discovery, first registration, and startup convergence, the runner SHALL register a single periodic model-rediscovery task immediately before entering the worker loop. Its default interval SHALL be 30 minutes, a configured interval below 60 seconds SHALL be clamped to 60 seconds, and its first periodic invocation SHALL occur one full interval after that timer registration. The task SHALL continue independently of OpenCode runtime readiness and SHALL be stopped when the runner run loop terminates.

#### Scenario: Default interval elapses
- **WHEN** no rediscovery interval is configured
- **AND** the runner has registered the periodic task after startup discovery, first registration, and startup convergence
- **THEN** the first periodic rediscovery SHALL run 30 minutes after timer registration
- **AND** subsequent rediscoveries SHALL run every 30 minutes

#### Scenario: Connection delays timer registration
- **WHEN** startup discovery completes but runner connection or startup convergence has not completed
- **THEN** the periodic rediscovery timer SHALL NOT yet be registered
- **AND** its first interval SHALL be measured only after connection and startup convergence complete

#### Scenario: Configured interval is below the minimum
- **WHEN** the configured rediscovery interval is less than 60 seconds
- **THEN** the runner SHALL use a 60-second interval

#### Scenario: Runtime is temporarily not ready
- **WHEN** a rediscovery interval elapses while the OpenCode runtime is not ready
- **THEN** the runner SHALL still invoke the independent CLI model discovery task

#### Scenario: Runner shuts down
- **WHEN** the runner run loop terminates
- **THEN** the periodic rediscovery task SHALL be cleared
- **AND** no later rediscovery invocation SHALL occur

### Requirement: Failed rediscovery preserves the last non-empty catalog

An empty or failed periodic rediscovery MUST NOT overwrite the runner's last non-empty `coderModels` and `coderModelVariants` snapshot and MUST NOT send an immediate heartbeat that clears the server's registered catalog. The next scheduled interval SHALL execute discovery again.

#### Scenario: Empty rediscovery follows a successful catalog
- **WHEN** the runner has a non-empty catalog
- **AND** periodic rediscovery returns an empty result
- **THEN** the runner SHALL retain the previous models and variants
- **AND** it SHALL NOT send an immediate heartbeat for the empty result

#### Scenario: Rediscovery command fails
- **WHEN** a periodic rediscovery fails after a successful discovery
- **THEN** the runner SHALL retain the previous models and variants
- **AND** the next interval SHALL invoke discovery again

#### Scenario: No successful catalog exists yet
- **WHEN** startup discovery was empty and a later rediscovery also fails or is empty
- **THEN** the runner SHALL continue reporting an empty model list and empty variant map
- **AND** it SHALL retry at the next interval

### Requirement: Catalog changes trigger an immediate heartbeat

After each non-empty rediscovery, the runner SHALL compare the new models and per-model variants with its current snapshot by set content, ignoring model order and variant order. If either set changes, the runner SHALL replace its local snapshot and attempt one immediate heartbeat carrying the new snapshot through the existing registration contract. If neither set changes, rediscovery SHALL NOT trigger an additional heartbeat.

#### Scenario: Model order changes only
- **WHEN** rediscovery returns the same model identifiers in a different order with the same variants
- **THEN** the runner SHALL treat the catalog as unchanged
- **AND** it SHALL NOT send an immediate heartbeat

#### Scenario: Variant order changes only
- **WHEN** rediscovery returns the same variant keys for each model in a different order
- **THEN** the runner SHALL treat the catalog as unchanged
- **AND** it SHALL NOT send an immediate heartbeat

#### Scenario: A model is added or removed
- **WHEN** a non-empty rediscovery adds or removes a model identifier
- **THEN** the runner SHALL update its local snapshot
- **AND** it SHALL attempt one immediate heartbeat with the updated catalog

#### Scenario: Only a model's variants change
- **WHEN** the model identifiers are unchanged but any model gains, loses, or renames a variant
- **THEN** the runner SHALL update its local snapshot
- **AND** it SHALL attempt one immediate heartbeat with the updated variant map

### Requirement: Rediscovery failures are contained

The periodic task SHALL contain and log failures from discovery and from the change-triggered heartbeat. A failure in one invocation MUST NOT terminate the runner run loop, become an unhandled rejection, or suppress the next scheduled invocation.

#### Scenario: Discovery invocation throws unexpectedly
- **WHEN** a periodic discovery invocation throws
- **THEN** the runner SHALL log and contain the error
- **AND** the worker loop and future rediscovery intervals SHALL continue

#### Scenario: Immediate heartbeat fails
- **WHEN** a changed catalog causes an immediate heartbeat attempt and that attempt fails
- **THEN** the runner SHALL log and contain the error
- **AND** the normal heartbeat and rediscovery tasks SHALL continue

### Requirement: The model registration contract remains unchanged

The runner SHALL report model identifiers in the existing `coderModels` list and per-model variant names in the existing `coderModelVariants` map on registration and heartbeat. The change MUST NOT add or alter runner-to-server fields, server aggregation semantics, or the project model-list response shape.

#### Scenario: A discovered model has variants
- **WHEN** discovery returns model `openai/gpt-5` with variants `low` and `high`
- **THEN** registration and heartbeat state SHALL include `openai/gpt-5` in `coderModels`
- **AND** `coderModelVariants["openai/gpt-5"]` SHALL contain `low` and `high`

#### Scenario: Existing model selectors consume the corrected catalog
- **WHEN** the project model-list response contains variants for a model
- **THEN** the Issue model selector, Agent editor model selector, and AI settings default and stage model selectors SHALL render those exact variants as selectable chips on that model's row
- **AND** selecting a variant SHALL continue to persist the model-and-variant combination and show that variant as active when reopened
- **AND** each selector SHALL continue to support model-only selection and clearing the selected model
