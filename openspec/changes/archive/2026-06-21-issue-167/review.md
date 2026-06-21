# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: info
  Scope: runner-down signal consistency
  Evidence: The issue-167 design intentionally keys the Hero Runner-down entry on `agentStatus.runnerAvailable === false` (`packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx:49`), while nearby Kanban code derives its banner from a runner-capacity path documented as an accepted trade-off in `openspec/changes/issue-167/design.md:67`. This is out of scope for the candidate and does not violate the issue acceptance criteria.
  SuggestedAction: Consider a future issue to unify dashboard/Kanban runner availability semantics if users observe divergent runner-down surfaces.
  Status: out-of-scope

## Review Evidence

- Issue acceptance criteria are satisfied by `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx:44`, `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx:49`, `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx:56`, `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx:72`, and `packages/web/src/pages/dashboard/ui/DashboardPage.tsx:67`.
- The previous premature all-clear defect is resolved by the loading guard at `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx:42` and `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx:52`, with regression coverage at `packages/web/src/widgets/attention-hero/ui/AttentionHero.test.tsx:557` and `packages/web/src/widgets/attention-hero/ui/AttentionHero.test.tsx:569`.
- Direct actions, navigation, invalidation, runner-down, all-clear, passive-surface, and dashboard slot behavior are covered by `packages/web/src/widgets/attention-hero/ui/AttentionHero.test.tsx:128` through `packages/web/src/widgets/attention-hero/ui/AttentionHero.test.tsx:670` plus `packages/web/src/pages/dashboard/ui/DashboardPage.test.tsx:105` through `packages/web/src/pages/dashboard/ui/DashboardPage.test.tsx:145`.
- Verification passed: `npm run typecheck -w packages/web`; `npm run test:run -w packages/web -- AttentionHero DashboardPage` (2 files, 33 tests); `npx openspec validate issue-167 --strict`.

<promise>PASS</promise>
