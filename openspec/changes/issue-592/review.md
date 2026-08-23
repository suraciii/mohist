# Review — issue-592

## Verdict

**FAIL**

## Must-fix findings

### MF-1 — The change is not limited to the web package

The issue plan explicitly scopes the change to `packages/web` and states that there are no server, runner, or CLI changes. The current diff nevertheless includes unrelated server test and architecture-ledger modifications:

- `packages/server/tests/Mohist.Server.SpecTests/Specs/Agent/Api/AgentSessionLaunchRuntimeResolutionSpecs.cs`
  changes runtime-resolution test orchestration from polling to claiming jobs and changes the dispatch helper shape.
- `packages/server/tests/Mohist.Server.ArchTests/SpecUnitMigrationLedger.json`
- `packages/server/tests/Mohist.Server.ArchTests/SpecUnitMigrationLedgerModel.cs`
- `packages/server/tests/Mohist.Server.ArchTests/SpecUnitMigrationProvenance.json`
  update the checked-in source-tree digest to account for that server test change.

These files are outside the issue's stated `packages/web` impact and are unrelated to deleting the dead web session/UI surface. Leaving them in this change violates the explicit scope/no-server-change constraint, even though the server tests pass. The separate fix-up must remove these server changes (or deliver them independently) so issue-592 contains only its web changes and workflow artifacts.

## First-review sweep

- **Acceptance criteria — FAIL:** The web acceptance criteria are implemented, but the current change violates the plan's explicit package scope/no server-change requirement noted above.
- **Coverage — checked:** The dead `widgets/coder-session` chain, legacy `entities/session` view projection, duplicate session data clients, inferred session data contract, custom toast host, retired markdown renderer/toggles, duplicate viewport hooks, and query-client convention are all addressed in the current tree.
- **Correctness — checked, no web must-fix issue found:** The unified session hook is the sole session-detail data source; removed fields and shell branches are absent; generic followup/turn-control and recovery paths remain wired; the surviving timeline projection and widget exports remain present.
- **Consistency — checked:** The web edits follow the existing FSD public-API and `@x` boundary conventions. `SessionTranscriptLayout.tsx` has a behavior-neutral local type rename (`SessionTimelineView` to `SessionTranscriptViewMode`) to satisfy the removed-token sweep; this is noted as an observation because the surrounding timeline chain otherwise remains intact.
- **Tests — checked:** `npm run typecheck -w packages/web`, `npm run check:fsd -w packages/web`, `npm run check:test-boundaries -w packages/web`, and `npm run test:ci -w packages/web` pass. The full repository `npm run verify` also passes, including the web build and all server/runner/Slack tests.

## Observations

- The production build still emits existing non-blocking Rollup warnings about SignalR `/*#__PURE__*/` annotations and large chunks; no issue-592 acceptance criterion is affected.
- The surviving session-transcript layout received only the disclosed local type-alias rename needed for the exact removed-family sweep; no timeline behavior changed.

<promise>FAIL</promise>
