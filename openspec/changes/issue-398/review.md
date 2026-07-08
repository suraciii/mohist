# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/widgets/runner-status/ui/RunnerSummary.tsx`, `packages/web/src/shared/status-presentation/cross-surface.equivalence.spec.tsx`
  Resolution: `RunnerSummary` now preserves stale and offline as separate states, so offline-only summaries render with the muted runner family instead of the stale warning family. The cross-surface spec now requires `RunnerList` and `RunnerSummary` to match `familyFor('runner', state)` for every runner state.
  Status: fixed

- [ID: item-2]
  Severity: blocking
  Scope: `packages/web/src/widgets/kanban-board/model/stage-colors.ts`, `packages/web/src/shared/status-presentation/cross-surface.equivalence.spec.tsx`
  Resolution: The cancelled kanban stage now resolves to the muted family, matching cancelled issue-health surfaces. Unit and cross-surface tests now lock this mapping.
  Status: fixed

- [ID: item-3]
  Severity: warning
  Scope: `packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx`
  Resolution: `NeedsAttentionSummary` and `RunnerUnavailableBanner` now render through semantic warning treatments instead of raw `amber-*` and `bg-white` classes, with tests asserting the warning family and token classes.
  Status: fixed

- [ID: item-4]
  Severity: blocking
  Scope: `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx`, `packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx`, `packages/web/src/entities/issue/model/attention.ts`
  Resolution: attention items now carry a simple `kind`, and both attention surfaces derive each item and aggregate family from that kind. Approval remains warning, interrupted remains warning, and blocked or integration-failed items make the aggregate surface danger.
  Status: fixed

- [ID: item-5]
  Severity: blocking
  Scope: `packages/web/src/widgets/session-health/ui/ContextHealthBar.tsx`
  Resolution: the critical context banner now follows the server-provided red health state instead of re-deriving danger from percent alone. The banner uses alert semantics only for red health, and yellow high-percent usage remains yellow.
  Status: fixed

- [ID: item-6]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs`, `packages/server/tests/Mohist.Server.SpecTests/Specs/Runner/Api/RunnerHeartbeatConnectionApiSpecs.cs`
  Resolution: heartbeat requests that only report `connectionId` now preserve the runner's registered capabilities, hostname, project, models, build hash, variants, kind, and registration time instead of overwriting identity fields with partial defaults.
  Status: fixed

- [ID: item-7]
  Severity: warning
  Scope: `packages/server/tests/Mohist.Server.ArchTests/ArchitectureRules.cs`, `Mohist.sln`
  Resolution: architecture rules now target the real spec-test root and current namespaces, and the unit-test project is included in the solution so solution-level test runs cover it.
  Status: fixed

- [ID: item-8]
  Severity: warning
  Scope: `packages/web/src/widgets/coder-session/model/useSessionTimeline.test.ts`
  Resolution: the rAF branch test now advances to the next animation frame directly, removing the intermittent full-suite failure caused by guessing a fixed 30ms delay.
  Status: fixed

## Blocking Items

- None.

## Follow-up Items

- None.

## Verification

- `npm run typecheck -w packages/web` passed.
- `npm test -w packages/web -- --run src/entities/issue/model/attention.test.ts src/widgets/attention-hero/ui/AttentionHero.test.tsx src/widgets/kanban-board/ui/kanban-board-query.counts.test.tsx src/widgets/session-health/ui/ContextHealthBar.test.tsx` passed: 4 files passed, 99 tests passed.
- `npm test -w packages/web -- --run src/widgets/coder-session/model/useSessionTimeline.test.ts` passed: 1 file passed, 39 tests passed.
- `npm run test:run -w packages/web` passed: 306 files passed, 4650 tests passed, 1 skipped.
- `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --filter "FullyQualifiedName~RunnerHeartbeatConnectionApiSpecs" -p:SkipWebBuild=true` passed: 9 tests passed.
- `dotnet test packages/server/tests/Mohist.Server.ArchTests/Mohist.Server.ArchTests.csproj -p:SkipWebBuild=true` passed: 24 tests passed, 3 skipped.
- `dotnet test Mohist.sln -p:SkipWebBuild=true` passed: CLI 865 tests passed, ArchTests 24 passed and 3 skipped, SpecTests 4093 passed and 10 skipped.

<promise>PASS</promise>
