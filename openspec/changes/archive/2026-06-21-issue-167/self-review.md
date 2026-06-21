# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: Verified the `dashboard-shell` delta's MODIFIED requirement header (`### Requirement: Dashboard provides four zone mount-point slots`) matches the original at `openspec/specs/dashboard-shell/spec.md:25` character-for-character, and that only that one requirement is modified (the landing-page and empty-state requirements are correctly left untouched). No change needed.
  Verification: `npx openspec validate issue-167 --strict` returns `Change 'issue-167' is valid`.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: The Hero's Runner-down entry keys off `agentStatus.runnerAvailable === false` (per `dashboard-attention-hero` spec), while the existing Kanban `RunnerUnavailableBanner` keys off `useRunnerSummary().hasConnectedCapacity`. The two signals can transiently disagree. This divergence is documented and accepted in `design.md` (Risks) and `tasks.json` (notes), and the spec is internally consistent, so it is not a plan defect — but the project-wide runner-down signal could eventually be unified.
  SuggestedAction: Out of scope for #167. Consider a follow-up issue to reconcile the runner-down signal across the Hero and the Kanban banner (one hook, one truth).
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: The shared `AttentionItem` shape (`{ issueNumber, issueId, label, detail? }`) carries no explicit resume-eligibility flag, so the Hero infers the Resume-vs-Approve action from the item `label` (non-`Approval needed` → Resume). This is safe today because the `issue-attention-derivation` spec locks the category set to exactly four labels and forbids adding new categories, but it is a label-to-action coupling the implementer should be aware of.
  SuggestedAction: None required for #167. If a future attention category is added, update the Hero's label→action mapping alongside it (the derivation spec's "no new categories" guard makes this unlikely).
  Status: follow-up

## Summary

- **Alignment**: Every proposal "What Changes" entry and all four issue acceptance criteria (list+direct action, Runner down, All clear + Productivity placeholder, Kanban parity) trace to `dashboard-attention-hero` spec scenarios. All Non-Goals (no judgment rewrite, no Productivity content, no notifications) are reflected.
- **Completeness**: The 6 `dashboard-attention-hero` requirements and the 1 `dashboard-shell` MODIFIED requirement are all covered by task T-001's acceptance criteria. Edge cases (empty-items + runner-down, loading/undefined `runnerAvailable`, derivation order, dedup/first-match) are addressed.
- **Consistency**: Capability names match across proposal / specs / tasks / design (`dashboard-attention-hero`, `dashboard-shell`). T-001 references `specs/dashboard-attention-hero/spec.md` and notes the `dashboard-shell` satisfaction. Design D1–D6 map 1:1 to spec requirements.
- **Feasibility**: All reused seams verified present (`useIssues`, `useAgentStatus`, `deriveAttentionItems`/`AttentionItem`, `approveIssue`, `resumeIssue`, `useProjectPath`). Single cohesive task — not over-split (no interface/register/move/test-only fragments); test coverage lives inside the implementation task.
- **Dependencies**: Single task with empty `dependsOn`; DAG is trivially acyclic and well-formed (verified programmatically).

<promise>PASS</promise>
