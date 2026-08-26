## Why

Application and focused scopes can select only a subset of the canonical duration-measurement Tracks. The planner currently abandons the entire measurement phase when an earlier configured Track is absent, so a later selected Track loses its exclusive measurement Resource and ordering barrier; fixing the ordered-intersection behavior now restores reliable duration evidence without changing the test policy itself.

## What Changes

- Normalize the configured duration-measurement Track sequence to the Tracks present in the current selected scope, preserving the configured order.
- Apply the existing duration-measurement Resource claim and predecessor/terminal dependency barrier to every remaining selected measurement Track.
- Preserve the existing isolation-track boundary for selected scopes, without adding unrelated Tracks or dependencies to focused execution.
- Leave a scope unchanged when none of the configured measurement Tracks are selected.
- Keep malformed multi-lane measurement shapes fail-closed instead of silently scheduling an unisolated plan.
- Add deterministic planner coverage for partial matches, missing earlier Tracks, zero matches, full portfolios, focused selections, and multi-lane validation.
- Keep worker capacity, test populations, duration budgets, suite deadlines, CI topology, and product test behavior unchanged. No breaking public API or configuration contract is introduced.

## Capabilities

- `duration-measurement-scope-isolation`: Scope-aware selection and ordered isolation of canonical duration-measurement Tracks, including Resource claims, dependency barriers, isolation-track handling, zero-match no-op behavior, and fail-closed invalid lane handling.

## Impact

- **Canonical test planner:** `scripts/test-duration/guard.ts` changes the deterministic lane-plan normalization used by application, repository, and focused scopes.
- **Planner tests and evidence:** `scripts/test-duration/guard.test.ts` and related canonical test-duration checks will verify the selection matrix and generated Resources/dependencies; emitted `plan.json` evidence will reflect the selected, normalized phase.
- **CI and local verification:** Partial application scopes retain duration-measurement isolation while the existing budgets, deadlines, resource limits, and Gate evidence rules remain in force.
- **Production systems and dependencies:** No Server, Runner, CLI, Web, persistence, public API, dependency, or checked-in canonical Track configuration changes are expected.
