### Requirement: Runner fetches config on each cleanup-loop tick

The runner's `ServerConnection` SHALL expose a `fetchConfig(signal): Promise<CleanupPolicy | null>` method that performs a `GET /api/runner/{runnerId}/config` request and returns the `cleanupPolicy` from the response. The runner's cleanup-loop tick (`runCleanupOnce`) SHALL call `fetchConfig` on every cycle and pass the returned policy to `cleanupLoop.runOnce`, instead of reading a previously cached policy.

#### Scenario: cleanup-loop tick fetches config and drives cleanup from the result

- **WHEN** the cleanup-loop timer fires (default every 2 minutes)
- **THEN** the runner calls `GET /api/runner/{runnerId}/config`, takes the `cleanupPolicy` from the response, and invokes `cleanupLoop.runOnce` with that policy.

#### Scenario: fetch frequency is one GET per cleanup-loop cycle with no caching or version negotiation

- **WHEN** consecutive cleanup-loop ticks fire
- **THEN** each tick issues its own `GET /api/runner/{runnerId}/config`; the runner performs no ETag / If-None-Match / version-based conditional fetch, and does not cache the policy between ticks.

### Requirement: Workspace cleanup runs while the system is idle

The runner SHALL execute workspace cleanup (retention and budget eviction) whenever the cleanup-loop tick fires and the fetched config yields a non-null policy, even when `POST /api/runner/{runnerId}/poll` is continuously returning `204 No Content` (no work being dispatched). Cleanup availability MUST NOT depend on a work dispatch having occurred.

#### Scenario: idle system with a configured policy still performs cleanup

- **WHEN** `poll` has been returning `204` for an extended period (no work dispatched) and `GET /api/runner/{runnerId}/config` returns a `cleanupPolicy` with at least one enabled strategy (retention or budget)
- **THEN** on the next cleanup-loop tick the runner fetches the config, obtains the non-null policy, and runs eviction against eligible workspaces per the retention/budget rules — cleanup is NOT skipped due to the absence of work dispatches.

#### Scenario: idle system with a fully-unconfigured policy performs no eviction

- **WHEN** `poll` is returning `204` and `GET /api/runner/{runnerId}/config` returns a `cleanupPolicy` whose fields are all `null` (no strategy enabled)
- **THEN** on the cleanup-loop tick the runner fetches the config, observes that no eviction strategy is enabled, and removes nothing — matching the existing "null means do not evict" contract.

### Requirement: Cleanup-loop cadence and algorithm are unchanged

This change SHALL NOT alter the cleanup-loop period (default 2 minutes), the convergence backstop, or the retention/budget eviction algorithm in `cleanup-loop.ts`. Only the source of the policy (fetched config vs. dispatch-cached field) changes.

#### Scenario: cleanup-loop period and eviction logic remain as before

- **WHEN** the runner is configured with the default cleanup-loop interval and a fetched policy that enables retention and/or budget eviction
- **THEN** the cleanup loop fires at the same cadence as before this change and applies the same retention-cutoff and budget-target-watermark algorithm; only the policy's point of origin (fetched from `/config` rather than read from the last dispatch) is different.
