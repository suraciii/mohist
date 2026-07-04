# Review Report

## Result: FAIL

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: candidate deliverable / `packages/web`
  Evidence: The post-build candidate contains no product deliverable outside workflow artifacts. `git diff --name-status origin/master...HEAD` lists only `openspec/changes/issue-340/{proposal.md,design.md,tasks.json,self-review.md,specs/...}` and no `packages/web` files. This leaves every product acceptance criterion from issue #340 unimplemented in the reviewed snapshot. [disallowed:reason] Repair would require the full product implementation, not a small local review fix.
  SuggestedAction: Implement the issue-detail frontend changes outside `openspec/changes/issue-340/`, then rerun the review on the product snapshot.
  Verification: `git diff --name-status origin/master...HEAD` showed only eight OpenSpec artifact additions; `git diff --stat origin/master...HEAD` showed 601 inserted lines exclusively under `openspec/changes/issue-340/`.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`, `packages/web/src/pages/issue-detail/model/actionsState.ts`, `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts`
  Evidence: The single runtime decision model acceptance criterion is not met. `IssueDetailPage.tsx:21-22` still imports both `useIssueDetailMutations` and the independent `computeActionsState`; `IssueDetailPage.tsx:107-122` still computes right-rail `actionsState` from raw issue, agent, timeline, and mutation errors; `IssueDetailPage.tsx:251-256` still mounts `RuntimeDecisionSurface`, which separately derives its own decision. `actionsState.ts:97-212` still exists as a full parallel judge. `derive-runtime-decision.ts:35-45` still exposes only `summary`, `headline`, `rationale`, `currentTask`, `nextAction`, `actions`, `waitReason`, `driftNote`, and `blockedReason`; it has no `primary` or `stopRecoverable` output required by the spec. [disallowed:reason] Repair requires product behavior and model contract changes.
  SuggestedAction: Extend `deriveRuntimeDecision` into the unique source for situation, primary action, and stop recoverability; move right-rail rendering to consume that decision; delete `computeActionsState` after coverage is migrated.
  Verification: Code inspection via `read` and `grep` confirmed `computeActionsState` imports/usages and no `primary`/`stopRecoverable` fields in `RuntimeDecision`.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: `packages/web/src/widgets/issue-workflow/ui/RuntimeDecisionSurface.tsx`, `packages/web/src/pages/issue-detail/model/useIssueDetailMutations.ts`
  Evidence: Write operations do not share one mutation state. `RuntimeDecisionSurface.tsx:262-295` still declares private `useMutation` instances for approve, send-back, retry, resume, rerun, stop, and start. The right rail still receives a separate mutation set from `useIssueDetailMutations` in `IssueDetailPage.tsx:49-77` and `IssueDetailPage.tsx:411-429`. This means pending/error state for the same operation can still diverge between the top surface and the right rail. [disallowed:reason] Repair changes component contracts and mutation ownership.
  SuggestedAction: Make `useIssueDetailMutations` own every issue-detail write action, including approve and send-back, and pass a narrow mutation prop set into `RuntimeDecisionSurface`.
  Verification: `grep` for `useMutation\(` and code inspection showed the private widget mutations still present.
  Status: open

- [ID: item-4]
  Severity: blocking
  Scope: `packages/web/src/pages/issue-detail/ui/cards/IssueActionsCard.tsx`, `packages/web/src/widgets/issue-workflow/ui/RuntimeDecisionSurface.tsx`
  Evidence: The user-facing Stop entry is not unified and is not recoverability-selected. The right rail still renders a recoverable `Force Stop` button wired to `forceStopMutation` at `IssueActionsCard.tsx:185-239` and a separate terminal `Stop Workflow` button wired to `stopMutation` at `IssueActionsCard.tsx:298-320`. The top `RuntimeDecisionSurface` also renders action kind `stop` from its own actions list and always calls its private terminal `stopMutation` at `RuntimeDecisionSurface.tsx:287-288` and `RuntimeDecisionSurface.tsx:394-404`. The recoverable stop confirmation has no explanatory consequence text; it only changes the button label to `Confirm Force Stop` at `IssueActionsCard.tsx:234-238`. [disallowed:reason] Repair changes user-facing action semantics.
  SuggestedAction: Render exactly one Stop control across the page, drive it from `decision.stopRecoverable`, invoke `forceStopIssue` only for recoverable stops and `stopIssue` only for terminal stops, and use distinct confirmation copy that states resumable vs. irreversible consequences.
  Verification: Code inspection confirmed two right-rail stop buttons plus the top stop action remain.
  Status: open

- [ID: item-5]
  Severity: blocking
  Scope: `packages/web/src/app/styles/index.css`, `packages/web/src/shared/ui/components/card-section.tsx`, `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`, `packages/web/src/widgets/issue-workflow/ui/RuntimeDecisionSurface.tsx`, `packages/web/src/pages/issue-detail/ui/pills.tsx`
  Evidence: The tokenized visual substrate is not implemented. `index.css:9-50` exposes no semantic `--color-success`, `--color-warning`, `--color-info`, or `--color-danger`, and `:root`/`.dark` define no corresponding variables. `CardSection` still hardcodes raw tone palettes such as `bg-amber-50 border-amber-200` and `text-red-800` at `card-section.tsx:21-37`. Detail-page surfaces still use raw colors and near-white blocks, including `IssueDetailPage.tsx:281-314` (`bg-white`, `text-gray-*`, `text-green-600`, `text-red-500`) and `IssueDetailPage.tsx:335-342` (`rounded-lg bg-white p-4`). `RuntimeDecisionSurface.tsx:90-98` and `RuntimeDecisionSurface.tsx:318-320` still use raw Tailwind palettes and `bg-white`. `pills.tsx:40-57` embeds raw hex colors for `HealthPill`, and `pills.tsx:27-33` uses inline accent colors for `WorkflowStagePill`. [disallowed:reason] Repair requires a broad visual migration.
  SuggestedAction: Add semantic status tokens with light/dark values, route `CardSection` tones through tokens, migrate detail-page surfaces to `bg-card`/token utilities, and replace inline hex/status styles with a token-backed pill renderer.
  Verification: `grep` for `bg-white|bg-[a-z]+-50|text-[a-z]+-700|#[0-9A-Fa-f]{6}` and file reads confirmed the raw styles remain.
  Status: open

- [ID: item-6]
  Severity: blocking
  Scope: `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`, `packages/web/src/pages/issue-detail/ui/cards/IssueActionsCard.tsx`, `packages/web/src/pages/issue-detail/ui/pills.tsx`
  Evidence: Status badges are not grouped or deduplicated. The header still renders identity and runtime signals in one flat row: priority, draft, archived, workflow stage, `WorkflowRunStatusPill`, `HealthPill`, inline `running-pill`, and inline `Approval needed` at `IssueDetailPage.tsx:147-172`. These runtime badges read different raw fields instead of the single runtime decision. `DraftPill` is still duplicated in the action card for draft readiness at `IssueActionsCard.tsx:92-120`, while it also renders in the header at `IssueDetailPage.tsx:153`. [disallowed:reason] Repair requires UI restructuring and model wiring.
  SuggestedAction: Split badges into identity-metadata and runtime-status groups, render exactly one runtime badge from the same `decision.summary` used by the actions, and remove duplicate `DraftPill` rendering.
  Verification: Code inspection confirmed overlapping runtime badges and duplicate draft badge remain.
  Status: open

- [ID: item-7]
  Severity: test-gap
  Scope: `packages/web/src/pages/issue-detail/model/actionsState.test.ts`, `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.test.ts`, issue-detail page tests
  Evidence: Regression coverage was not migrated or added. Because no product test files changed in the candidate, the old `actionsState.test.ts` still exists and imports `computeActionsState`; `derive-runtime-decision.test.ts` still asserts existing `summary`/`actions` behavior but has no assertions for the required `primary` or `stopRecoverable` fields; no page tests were updated to assert one primary action, shared mutation pending/error state, one Stop entry, grouped badges, tokenized colors, or dark-mode no-white-block behavior. [disallowed:reason] Repair requires implementing the new behavior and updating tests around it.
  SuggestedAction: Migrate `actionsState` coverage into `derive-runtime-decision.test.ts`, add direct tests for `primary` and recoverability, update page/component tests for action convergence and badge grouping, and add the separate dark-mode visual/a11y check requested by the plan.
  Verification: `git diff --name-status origin/master...HEAD` showed no changed product test files; `grep` confirmed `actionsState.test.ts` remains and no `primary`/`stopRecoverable` coverage exists.
  Status: open

- [ID: item-8]
  Severity: warning
  Scope: branch / integration readiness
  Evidence: The candidate snapshot is based on an older merge base (`212df2e1`) and diverges from current `origin/master`, which contains five newer commits. A local `git merge --ff-only origin/mohist/run-wr_ad7cebde79cb48ffa0f125ea18dcb326` from the current workspace branch failed with `Not possible to fast-forward, aborting.` This is an integration risk for a post-build candidate, especially because the reviewed branch lacks current master changes. [disallowed:reason] Repair involves workflow/rebase policy rather than a local review fix.
  SuggestedAction: Rebase or otherwise refresh the issue branch against current `origin/master` before integrate, then rerun review on the refreshed product snapshot.
  Verification: `git log --oneline --left-right --cherry-pick origin/master...origin/mohist/run-wr_ad7cebde79cb48ffa0f125ea18dcb326` showed five commits only on `origin/master` and five issue artifact commits only on the candidate branch.
  Status: open

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: info
  Scope: local verification environment
  Evidence: Verification commands could not execute because dependencies are not installed in this workspace: `npm run typecheck -w packages/web` failed with `sh: 1: tsc: not found`, and `npm run test:run -w packages/web` failed with `sh: 1: vitest: not found`.
  SuggestedAction: Install workspace dependencies and rerun `npm run typecheck -w packages/web` plus `npm run test:run -w packages/web` after implementing the product changes.
  Status: out-of-scope

<promise>FAIL</promise>
