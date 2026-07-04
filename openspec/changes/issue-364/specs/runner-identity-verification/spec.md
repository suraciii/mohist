### Requirement: Runner identity verification polls for registration readiness

After resolving the source HEAD, `RuntimeConsistencyValidator.CheckRunnerIdentityAsync` (the `VerifyRuntime` stage of `mo update`) MUST poll `GET /api/runner/identity` until a non-null identity carrying a non-empty `buildGitHash` is returned, or until a bounded timeout elapses. This tolerates the registration lag between systemd reporting the runner process `active` and the server actually serving a populated `/api/runner/identity` payload. The per-attempt probe MUST reuse the existing identity-fetching helper rather than duplicating the HTTP/deserialization logic.

#### Scenario: Identity already registered on the first probe

- **WHEN** the runner has completed its WebSocket handshake and registered its identity before the first probe, and its `buildGitHash` equals the source HEAD
- **THEN** `CheckRunnerIdentityAsync` returns `Pass` on the first probe without issuing any poll delay

#### Scenario: Identity becomes available after several polls

- **WHEN** `/api/runner/identity` returns a null/empty payload for the first N probes (the runner is still reconnecting) and then returns an identity whose `buildGitHash` matches the source HEAD before the timeout elapses
- **THEN** `CheckRunnerIdentityAsync` returns `Pass`, having issued N-1 bounded poll delays between probes

#### Scenario: Identity never registers within the bounded window

- **WHEN** `/api/runner/identity` keeps returning a null/empty payload throughout the entire bounded window
- **THEN** `CheckRunnerIdentityAsync` returns `Warn` with the "did not respond" message, so a genuinely broken runner is still surfaced rather than masked by an infinite wait

### Requirement: Bounded window with injectable timeout, poll interval, and time source

The verification window MUST be bounded by a timeout and a fixed poll interval that are injectable via constructor parameters (defaults: 30s timeout, 500ms interval, matching `RunnerRefreshVerifier`). All waiting — both the deadline computation and the inter-probe delay — MUST be driven through an injectable `TimeProvider` (default `TimeProvider.System`), following the established CLI convention in `ServiceReadinessProbe`. The implementation MUST NOT read wall-clock time via `DateTime.UtcNow` or `Environment.TickCount`, and MUST NOT use a `while (now < deadline)` loop keyed off a non-injected clock.

#### Scenario: Non-default timeout and poll interval are honored

- **WHEN** a caller constructs `RuntimeConsistencyValidator` with a custom timeout and poll interval
- **THEN** the verification window uses exactly those values to bound the polling and to space the inter-probe delays

#### Scenario: Timing behavior is fully testable without wall-clock

- **WHEN** the validator is constructed with a fake `TimeProvider` and a fake HTTP handler that withholds the identity until a configured number of probes has elapsed
- **THEN** advancing the fake clock by exactly the configured poll interval between probes causes the next probe to fire, and no real time is consumed

### Requirement: Present-but-unusable identity outcomes are unchanged

Only the "identity not yet registered" path changes behavior (from immediate Warn to polled wait). For an identity payload that is present but unusable, the comparison semantics MUST remain as they were before this change: a non-null identity whose `buildGitHash` is empty/whitespace yields `Warn`; a non-null identity whose `buildGitHash` differs from the resolved source HEAD yields `Warn`; a non-null identity whose `buildGitHash` equals the source HEAD yields `Pass`.

#### Scenario: Identity reports an empty buildGitHash

- **WHEN** a polled probe returns a non-null identity whose `buildGitHash` is empty or whitespace
- **THEN** `CheckRunnerIdentityAsync` returns `Warn` reporting that the runner did not report a buildGitHash, without continuing to poll further

#### Scenario: Identity buildGitHash differs from source HEAD

- **WHEN** a polled probe returns a non-null identity whose non-empty `buildGitHash` differs from the resolved source HEAD
- **THEN** `CheckRunnerIdentityAsync` returns `Warn` reporting the mismatch, without continuing to poll further

### Requirement: Unresolvable source HEAD short-circuits to a skip Warn

If the source HEAD cannot be resolved, `CheckRunnerIdentityAsync` MUST return `Warn` with the "Source HEAD could not be determined; skipping identity check" message and MUST NOT issue any probe against `/api/runner/identity`. This path is unchanged from before this change.

#### Scenario: Source HEAD resolution fails

- **WHEN** the source HEAD cannot be resolved (git rev-parse fails or returns empty) before verification begins
- **THEN** `CheckRunnerIdentityAsync` returns `Warn` mentioning "Source HEAD" and issues zero HTTP requests to `/api/runner/identity`
