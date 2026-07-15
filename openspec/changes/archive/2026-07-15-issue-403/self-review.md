# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: completeness
  Evidence: The spec listed dedicated scenarios for 4 of the 5 `ChangesUnavailableReason` values (runner_unavailable, workspace_removed, branch_missing, not_started) but had no dedicated scenario for `git_error`. `git_error` was named in the umbrella requirement text, but it is precisely the reason the design (Decision 5) and T-001 flag as currently mishandled — `getDiffAvailability` collapses it into a generic fallback (`diffData.message ?? 'Failed to load changes'`). The absence of an explicit scenario left the most regression-prone reason only implicitly covered, while every other reason had its own.
  Verification: Added `#### Scenario: Server-reported git error renders the recoverable surface` to `specs/changed-files-recovery/spec.md` between the branch_missing and not_started scenarios, mirroring the existing reason scenarios and asserting the surface must not fall back to a generic message that drops the git-error cause. Verified with `grep` that all 5 reasons now have dedicated scenarios and the file structure is intact. The addition is additive, changes no product direction, and aligns the spec with the design message map and T-001 acceptance criterion ("git_error -> '...due to a git error.'").
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: alignment
  Evidence: The proposal Impact section hedges that `FullFilePane.tsx` ("has its own fetch error with no retry") is "in scope if the full-file fetch failure is part of the recoverable surface," and that the inline `IssueDiffFilesSection` / `IssueCommitsSection` "may surface a lightweight recovery hint." The design resolves both firmly to Non-Goals (deferred), which is consistent with the issue's page-level focus and Non-Goals. The tension is benign — the proposal uses conditional language ("in scope if", "may") that the design authoritatively resolves — but a reader of the proposal alone might expect those sites to change.
  SuggestedAction: No change required for this change set. If a future follow-up issue is opened for per-file / inline-section recovery (already tracked in design Open Questions), reference it from the proposal at that time.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: consistency
  Evidence: The spec's "Issue context preserved" requirement states the visible context "SHALL include the issue number, the issue title, and the issue health badge" in absolute terms, while the scenarios and design honestly omit title/health when the issue query itself fails (only the number is available). This is the only feasible behavior and the scenarios disambiguate it, but the blanket SHALL wording could be misread as always-required.
  SuggestedAction: Optionally soften the requirement prose to "when the issue has been fetched" to match the scenarios and design Risk 1. Not blocking; the scenarios are the operative test targets.
  Status: follow-up

## Verification Summary

- **Alignment**: Every "What Changes" entry traces to an issue acceptance criterion (context preserved, product-language explanation, retry, return-to-issue, related-session link, both failure paths). No issue requirements missing or misinterpreted.
- **Completeness**: All 5 issue acceptance criteria are covered by 6 spec requirements, all of which have tasks (T-001 covers requirements 1–5; T-002 covers requirement 6). Edge cases considered: issue-load failure (number-only), loading-flash prevention, retry persistence, session absence, and `git_error` fallback (now with a dedicated scenario).
- **Consistency**: Tasks reference correct, existing spec anchors (`#unified-recoverable-error-surface-for-both-failure-paths`, `#related-session-link-when-a-session-is-known`). Design Decisions 1–6 align with spec requirements and proposal Capabilities. Message map reasons match the `ChangesUnavailableReason` type exactly. Design tokens (danger-subtle/warning-subtle/danger-border/warning-foreground) confirmed present in the codebase.
- **Feasibility**: Codebase references verified accurate against current source — `IssueChangedFilesPage.tsx` state machine (lines 726–748), `useChangedFilesData` dropping refetch functions (lines 580–593), `getDiffAvailability` reason handling (lines 595–620), `useWorkflowRunSessions` gating + staleTime (lines 25–26), `IssueDetailPage` session selection (lines 109–115) and link path (lines 170–176), session route (`App.tsx:68`), and Activity retry precedent (`ActivityPage.tsx:249–266`). Dependencies are available or created by earlier tasks; no circular dependencies. Task granularity appropriate — each task is a complete feature slice (T-001: unified surface + context + language + retry + return + tokens + hook refactor + tests; T-002: net-new session awareness + link + tests), neither is a pure rename/move/standalone-test task.
- **Dependency completeness**: T-001 has no `dependsOn` (priority 1); T-002 depends on T-001 (priority 2), pointing to an existing lower-priority ID. No cycles. `tasks.json` is valid JSON.

<promise>PASS</promise>
