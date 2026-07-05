### Requirement: Runner config endpoint reads the latest reloaded options per request

The `GET /api/runner/{runnerId}/config` handler SHALL obtain `CleanupPolicyOptions` via `IOptionsSnapshot<CleanupPolicyOptions>` (or an equivalent live-reading source such as `IOptionsMonitor<CleanupPolicyOptions>`) injected per request, NOT via the singleton startup snapshot `IOptions<CleanupPolicyOptions>`. Each invocation of the endpoint MUST re-read the currently-bound options so that a configuration reload (triggered by editing `config.jsonc`) is reflected in the response without restarting the server.

#### Scenario: edited cleanup policy is returned without a server restart

- **WHEN** `config.jsonc` is edited at runtime to change `Mohist:WorkspaceCleanup:StorageBudgetBytes` (e.g. 10G → 64G) and the configuration reloads
- **THEN** a subsequent `GET /api/runner/{runnerId}/config` returns a `cleanupPolicy` whose `storageBudgetBytes` reflects the newly-edited value, with no server restart in between.

#### Scenario: each request reads the current options rather than a startup snapshot

- **WHEN** the `/config` handler is invoked twice across a configuration change (once before the edit, once after the reload)
- **THEN** the two responses carry the pre-edit and post-edit `cleanupPolicy` values respectively — the handler does not return a value frozen at server startup.

### Requirement: Response contract is unchanged

Switching the options source from `IOptions<>` to `IOptionsSnapshot<>` SHALL NOT alter the wire contract of `GET /api/runner/{runnerId}/config`. The response MUST remain a `200 OK` `RunnerConfigResponse { cleanupPolicy }` body produced by the existing `ToCleanupPolicyDto` projection, with the same null-sentinel semantics ("null means unlimited / disabled") and the same always-present field serialization. Only the freshness of the values improves; the shape, status code, and field semantics are identical.

#### Scenario: response shape and null semantics are preserved

- **WHEN** the `/config` endpoint serializes `CleanupPolicyOptions` after the source switch
- **THEN** the JSON body shape is identical to before (`RunnerConfigResponse` wrapping a `CleanupPolicyDto` with `retentionDays`, `storageBudgetBytes`, `storageTargetWatermarkBytes`, each always emitted and null when unconfigured/non-positive).

#### Scenario: unconfigured policy still returns 200 with null fields

- **WHEN** `CleanupPolicyOptions` has no fields configured and the endpoint is hit
- **THEN** the endpoint returns `200 OK` with a `RunnerConfigResponse` whose `cleanupPolicy` fields are all `null` — the live-read source does not change the "no config ⇒ all-null policy" behavior.
