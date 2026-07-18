### Requirement: Periodic rediscovery timer

The runner SHALL run a single periodic timer that re-invokes opencode coder-model discovery at a fixed interval. The default interval SHALL be 30 minutes. The timer SHALL first fire one interval after runner start (not at start), because startup discovery already runs as part of `connectRunner`. The timer SHALL be registered alongside the existing heartbeat, self-check, convergence, and cleanup timers in `RunnerHost.run()`, and SHALL be cleared when the runner's run loop terminates. This periodic timer SHALL be the sole trigger for rediscovery after startup — the runner SHALL NOT rely on heartbeat piggybacking or any other path to invoke discovery.

#### Scenario: Timer first fires one interval after runner start

- **WHEN** the runner has completed startup (registered with the server via `connectRunner`, which performs the initial discovery)
- **AND** the rediscovery interval is set to 30 minutes
- **THEN** the runner SHALL NOT run rediscovery again before 30 minutes elapse
- **AND** at 30 minutes the timer SHALL fire and invoke discovery exactly once

#### Scenario: Timer fires repeatedly at the configured interval

- **WHEN** the runner keeps running past the first fire
- **THEN** the timer SHALL continue to fire at every subsequent interval boundary
- **AND** each fire SHALL invoke discovery exactly once

#### Scenario: Timer is cleared when the run loop ends

- **WHEN** the runner's run loop aborts (signal aborted) or returns
- **THEN** the rediscovery timer SHALL be cleared so no further fire can occur after shutdown

#### Scenario: Interval is configurable

- **WHEN** the runner is constructed with a non-default rediscovery interval
- **THEN** the timer SHALL fire at that configured interval rather than the 30-minute default

### Requirement: Discovery module always executes the underlying command

The discovery module SHALL NOT short-circuit calls via an internal time-based cache guard. Every invocation of `discoverOpencodeModels` SHALL execute the underlying `opencode models --verbose` command and parse its output. The previous 30-minute TTL cache guard inside `opencode-models.ts` SHALL be removed. The runner SHALL NOT cache empty results, so that a successful subsequent call replaces the previously failed/empty state.

#### Scenario: Repeated calls each invoke the underlying command

- **WHEN** `discoverOpencodeModels` is called twice in succession
- **THEN** the underlying `opencode models --verbose` command SHALL be invoked twice
- **AND** both calls SHALL return the result parsed from the most recent command output

#### Scenario: Empty result is not cached

- **WHEN** a discovery call returns an empty model set (command failed or parsed zero models)
- **AND** the next discovery call succeeds with a non-empty set
- **THEN** the second call SHALL return the non-empty result
- **AND** the empty result SHALL NOT be served from any cache

### Requirement: Order-insensitive set comparison

After each rediscovery, the runner SHALL compare the newly discovered `coderModels` and `coderModelVariants` against the last-reported values by content, ignoring array ordering. Two model-id lists SHALL be considered equal if and only if they contain the same elements regardless of order. Two variant maps SHALL be considered equal if and only if they have the same keys and, per key, the same variant-element set regardless of order. The comparison SHALL NOT use order-sensitive deep equality, because opencode's output ordering can vary between invocations.

#### Scenario: Same models in a different order is treated as no change

- **WHEN** rediscovery returns `["openai/gpt-5.5", "anthropic/claude-sonnet-4"]`
- **AND** the last-reported set was `["anthropic/claude-sonnet-4", "openai/gpt-5.5"]`
- **THEN** the comparison SHALL evaluate the sets as equal

#### Scenario: Same variants in a different order is treated as no change

- **WHEN** rediscovery returns variants `{"openai/gpt-5.5": ["low", "high"]}` for a model
- **AND** the last-reported variants were `{"openai/gpt-5.5": ["high", "low"]}` for that model
- **THEN** the comparison SHALL evaluate the variants as equal

#### Scenario: A new model id is treated as a change

- **WHEN** rediscovery returns a set that contains a model id not present in the last-reported set
- **OR** omits a model id that was present in the last-reported set
- **THEN** the comparison SHALL evaluate the sets as changed

#### Scenario: A changed variant set is treated as a change

- **WHEN** rediscovery returns, for any model id, a variant-element set that differs from the last-reported variant-element set for that model
- **THEN** the comparison SHALL evaluate the variants as changed

### Requirement: Change-gated heartbeat uplink

When the order-insensitive comparison determines the rediscovered set is unchanged from the last-reported set, the runner SHALL NOT send any heartbeat beyond what the existing heartbeat timer would send anyway. When the comparison determines the set has changed, the runner SHALL update its local `coderModels` and `coderModelVariants` state to the rediscovered values and SHALL send exactly one immediate heartbeat through the existing heartbeat channel. The runner SHALL NOT introduce a new IPC, RPC, or push channel to deliver model-set updates.

#### Scenario: Unchanged rediscovery triggers no extra heartbeat

- **WHEN** rediscovery completes and the order-insensitive comparison finds no change
- **THEN** the runner SHALL NOT invoke the heartbeat channel as a result of this rediscovery
- **AND** the local `coderModels` and `coderModelVariants` state SHALL remain unchanged

#### Scenario: Changed rediscovery triggers one immediate heartbeat

- **WHEN** rediscovery completes and the order-insensitive comparison finds a change
- **THEN** the runner SHALL update its local `coderModels` and `coderModelVariants` to the rediscovered values
- **AND** SHALL send exactly one immediate heartbeat carrying the updated registration state

#### Scenario: Variant-only change triggers one immediate heartbeat

- **WHEN** rediscovery completes with the same model ids as the last-reported set
- **AND** at least one model's variant-element set differs from the last-reported set
- **THEN** the runner SHALL update its local `coderModelVariants` to the rediscovered values
- **AND** SHALL send exactly one immediate heartbeat carrying the updated registration state

### Requirement: Discovery failure preserves previously registered state

When a rediscovery call throws an exception, returns an empty model set, or otherwise fails, the runner SHALL preserve the previously reported `coderModels` and `coderModelVariants` in both local state and on the server. The runner SHALL NOT send a heartbeat that would clear or empty the server-registered model set as a consequence of this failure. The periodic timer SHALL continue to fire on its next interval and retry discovery.

#### Scenario: Thrown exception keeps prior state

- **WHEN** a rediscovery fire's underlying `opencode models --verbose` command throws
- **THEN** the runner SHALL leave its local `coderModels` and `coderModelVariants` unchanged
- **AND** SHALL NOT send a heartbeat that clears or empties the model set on the server
- **AND** the next periodic fire SHALL invoke discovery again

#### Scenario: Empty result keeps prior state

- **WHEN** a rediscovery fire returns an empty model set
- **THEN** the runner SHALL leave its local `coderModels` and `coderModelVariants` unchanged
- **AND** SHALL NOT send a heartbeat that clears or empties the model set on the server
- **AND** the next periodic fire SHALL invoke discovery again

### Requirement: Timer callback is exception-safe

The rediscovery timer callback SHALL catch any exception thrown by the discovery flow or the conditional heartbeat, log it, and return without rethrowing. Exceptions from one fire SHALL NOT bubble to an unhandled rejection, SHALL NOT abort the run loop, and SHALL NOT suppress subsequent fires.

#### Scenario: Thrown discovery error is logged and contained

- **WHEN** a rediscovery fire throws
- **THEN** the runner SHALL log the error
- **AND** SHALL NOT propagate the error as an unhandled rejection
- **AND** the run loop SHALL continue running

#### Scenario: Thrown heartbeat error is logged and contained

- **WHEN** the immediate heartbeat sent after a changed rediscovery throws
- **THEN** the runner SHALL log the error
- **AND** SHALL NOT propagate the error as an unhandled rejection
- **AND** the next periodic fire SHALL still occur

### Requirement: Time is injectable

Every time-driven decision in the rediscovery path — the timer interval, the periodic-fire accounting, and any time judgment remaining inside the discovery module — SHALL read from an injected clock, not from `Date.now()` or any other wall-clock source. The runner SHALL accept this clock via its construction/option surface so that tests can drive it. Spec tests for the rediscovery behavior SHALL verify the timer's interval and periodic-fire semantics by advancing fake timers, not by sleeping on the real wall clock.

#### Scenario: Tests drive rediscovery via fake timers

- **WHEN** a spec test sets up the runner with a fake clock and advances time to just before the configured interval
- **THEN** the timer SHALL NOT have fired yet
- **AND** when time is advanced past the interval, the timer SHALL fire and discovery SHALL run

#### Scenario: No Date.now in the rediscovery path

- **WHEN** the discovery module or the rediscovery timer reads the current time
- **THEN** it SHALL read from the injected clock
- **AND** SHALL NOT call `Date.now()` directly

### Requirement: Server contract is unchanged

The runner SHALL deliver rediscovered model-set updates exclusively through the existing heartbeat/registration channel. The change SHALL NOT alter the server-side `RunnerInfo` shape, the runner registration interface, the heartbeat endpoint, or the `/api/projects/{id}/opencode/models` response shape. No server-side code change SHALL be required to support runner model-set updates.

#### Scenario: Heartbeat carries the updated model set

- **WHEN** the runner sends a change-triggered immediate heartbeat
- **THEN** the heartbeat body SHALL use the same `coderModels` and `coderModelVariants` fields the existing heartbeat timer already sends
- **AND** SHALL NOT introduce new fields or endpoint parameters

#### Scenario: Server-side model list endpoint is unchanged

- **WHEN** the server serves `/api/projects/{id}/opencode/models` after a runner heartbeat with an updated model set
- **THEN** the response shape SHALL be identical to the pre-change shape
- **AND** no server-side code change SHALL be required for the new model set to appear

### Requirement: Registered set converges with opencode-exposed set within one interval

After any change to the opencode process's exposed coder model set (configuration edit, auth refresh, opencode binary upgrade), the runner's server-registered model set SHALL converge with the opencode-exposed set within at most one rediscovery interval. The runner SHALL NOT require a process restart for the convergence to occur.

#### Scenario: New provider appears within one interval

- **WHEN** the user adds a provider or model to opencode configuration while the runner is running
- **THEN** within at most one rediscovery interval the server's `/api/projects/{id}/opencode/models` response SHALL include the new entries

#### Scenario: Removed provider disappears within one interval

- **WHEN** the user removes a provider or model from opencode configuration while the runner is running
- **THEN** within at most one rediscovery interval the server's `/api/projects/{id}/opencode/models` response SHALL omit the removed entries

#### Scenario: No restart is required for convergence

- **WHEN** the opencode-exposed model set changes after the runner has started
- **THEN** the runner SHALL converge the server-registered set without a process restart
