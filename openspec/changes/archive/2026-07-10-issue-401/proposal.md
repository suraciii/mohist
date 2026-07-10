## Why

The issue detail page already grades its content into three tiers (sticky status headline, reading flow, reference rail), but the first screen still answers "what is this issue?" more than "what must I do with this run right now?" An owner who opens an issue to control production — approve a gate, recover from a block, inspect the plan artifact behind an approval, or act on drift — must scroll past the workflow view to find the latest plan/check evidence, dig into the reference rail to recover from drift, and hunt for the runner/session signal, while description, comments, and settings share the same attention level as the operational reality. This change makes the first screen an execution control workspace: the current workflow state, the owner's next action, the evidence behind an approval or recovery decision, and the recovery entry points all reach the owner without scrolling, while description, comments, and settings remain available but yield. It is needed now because issue 398 landed the shared status/theme baseline and issues 340–342 converged the single status adjudication and tier hierarchy this workspace depends on, so the page can be reorganized around control without re-litigating status, color, or action placement.

## What Changes

- Make the first screen an execution control workspace: issue identity, current workflow stage, health, approval state, runner/session signal (when relevant), and the primary owner action are all visible without scrolling.
- Surface each approval gate as one decision context: what is awaiting approval is shown together with the Approve and Reject actions, alongside the plan/check evidence the owner needs to decide — not split across the page.
- Make the latest plan/check artifacts discoverable from the operation context where the owner makes an approval or recovery decision, instead of buried mid-reading-flow below the workflow view.
- Promote blocked, interrupted, and drift recovery onto the first screen so the relevant recovery action (retry / resume / rerun / rebase / stop) is reachable without scrolling into the reference rail. Drift, today collapsed in the rail, becomes a first-screen recovery entry point when it blocks progress.
- Show the runner/session signal in the control region when a session is active or the runner state is relevant to the current decision.
- Make invalid or unsafe actions visually secondary or unavailable so the owner's attention rests on the one valid next action.
- Demote description, comments, model selection, prerequisites, and lower-frequency settings: they remain fully available but no longer share the first screen's operational attention.
- Preserve every existing issue lifecycle action and its semantics; no workflow action is added, removed, or respecified.

Non-goals (per issue): do not redesign Activity, Coder Session, Files, or Diff pages; do not change issue lifecycle semantics or add new workflow actions; do not alter how approval, rejection, retry, rerun, resume, or rebase work; do not replace CLI operations with Web-only behavior.

## Capabilities

- `issue-detail-control-workspace`: The issue detail first screen as an execution control surface — the control contract defining what an owner sees without scrolling to act on the current run: issue identity, current workflow stage, health, approval state (with what is awaiting approval plus approve/reject in one decision context alongside the plan/check evidence needed to decide), runner/session signal when relevant, the single primary owner action, decision-adjacent latest plan/check artifacts, and recovery entry points for blocked/interrupted/drift states; the rule that invalid or unsafe actions are visually secondary or unavailable; and the rule that descriptive and conversational content (description, comments) plus low-frequency configuration (model, prerequisites) remain available but yield to operational content on the first screen. This builds on the existing status-header / reading-flow / reference-rail tier hierarchy (issues 340–342) without redefining the tier structure.

## Impact

- **Web** (`packages/web/src`):
  - Page composition: `pages/issue-detail/ui/IssueDetailPage.tsx` — the first-screen hierarchy is reworked so the control region surfaces approval-with-evidence, decision-adjacent artifacts, drift recovery, and the session signal above the fold, while description and comments are demoted.
  - Control surface: `widgets/issue-workflow/ui/RuntimeDecisionSurface.tsx` — enriched to carry decision-adjacent evidence and the runner/session cue beside the action buttons.
  - Artifacts: `widgets/issue-workflow/ui/LatestArtifactsPanel.tsx` — repositioned/linked from the operation context (the approval/recovery decision point) rather than sitting mid-reading-flow.
  - Drift recovery: `pages/issue-detail/ui/cards/IssueDriftCard.tsx` and `widgets/issue-workflow/ui/BranchBar` — drift recovery promoted to a first-screen entry point (the rail card remains for full detail).
  - Session signal: `widgets/issue-workflow/ui/WorkflowSessionsPanel.tsx` — a compact signal surfaced in the control region.
  - Status presentation consumes the shared baseline landed in issue 398; `deriveRuntimeDecision` and `runtime-presentations` action gating are reused unchanged.
- **Server / runner / CLI**: none. No API, DTO, query, or data-source changes; the rework consumes existing projections (`deriveRuntimeDecision`, `workflowStageProgress`, the workflow timeline, artifacts, sessions, workspace-status).
- **Dependencies**: none added.
- **Tests** (`packages/web`): the `pages/issue-detail/ui/IssueDetailPage.*.test.tsx` suite (reading-flow, reference-rail, status-header, capacity-gating, archived) is updated for the new first-screen hierarchy and demoted secondary content; new spec tests cover approval-with-evidence, decision-adjacent artifact discoverability, first-screen drift recovery, session-signal surfacing, and secondary-content demotion. Existing `data-testid` anchors and the tier-weight invariants are preserved.
- **Risk (medium)**: this changes the main issue operation surface. Mitigated by reusing the existing status adjudication and action-gating logic unchanged, preserving every lifecycle action, and asserting every conditional path (approval, blocked, interrupted, drift, archived, backlog readiness) against the new first-screen control contract.
