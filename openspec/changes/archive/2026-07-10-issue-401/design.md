## Context

The issue detail page already implements a three-tier visual-weight hierarchy — **status-header** (weight 3, sticky, the control region), **reading-flow** (weight 2, `lg:col-span-2`), and **reference-rail** (weight 1, `lg:col-span-1`) — landed by issues 340–342, with a shared status/theme baseline from issue 398. The status-header tier renders `StatusHeadline` + the issue identity header + `RuntimeDecisionSurface` (desktop only). `deriveRuntimeDecision` + `runtime-presentations` are the single adjudication spine that produces the summary, rationale, action set, and per-action enable/disable gating consumed unchanged by the surface.

Today the first screen answers "what is this issue?" more than "what must I do with this run right now?": the latest plan/check artifacts sit mid-reading-flow below `WorkflowView`; the sessions panel sits in `runtime-evidence-frame`; base-drift detail is collapsed in the reference rail and its rebase action lives in `BranchBar` at the top of reading-flow; description, comments, model, and prerequisites share attention with operational content. An owner who opens an issue to approve a gate, recover from a block, or act on drift must scroll to assemble the full decision picture.

This change reorganizes the **status-header tier into an execution control workspace** by promoting decision-adjacent evidence, a compact execution signal, and drift recovery into the control region, while demoting descriptive/conversational content. It touches **Web only** — no server, runner, CLI, API, DTO, or projection changes. The adjudication and action-gating logic is consumed unchanged; no workflow action is added, removed, or respecified.

Stakeholders: issue owners (the operator opening a page to act), and the existing test suite that encodes the tier invariants and `data-testid` anchors as a contract.

## Goals / Non-Goals

**Goals:**
- Make the status-header tier (control region) carry everything an owner needs to act on the current run without scrolling: identity + adjudicated status (already present), plus decision-adjacent plan/check evidence, a compact runner/session signal, and first-screen drift recovery.
- Present each approval gate as **one decision context**: the awaiting-approval stage, Approve, Reject/send-back, and the plan/check evidence needed to decide, all reachable from the same surface.
- Make the latest plan/check artifacts reachable from the control region during an approval or recovery decision.
- Promote blocked / interrupted / drift recovery onto the first screen, with drift becoming a first-screen recovery entry point (not collapsed only in the rail) when it blocks progress.
- Demote description, comments, model selection, and prerequisites below the operational control region.
- Preserve every existing lifecycle action, its gating, every `data-testid` anchor, and the tier-weight invariants.

**Non-Goals:**
- No redesign of Activity, Coder Session, Files, or Diff pages.
- No change to issue lifecycle semantics; no new/removed/respecified workflow actions; `deriveRuntimeDecision` and `runtime-presentations` consumed unchanged.
- No server / runner / CLI / API / DTO changes; consumes existing projections only.
- No new external dependencies.
- No new narrow-viewport interaction model beyond keeping `MobileActionBar` for the primary action; the compact evidence/session/drift signals target the desktop first screen (narrow viewport behavior preserved as-is).

## Decisions

### D1. Enrich `RuntimeDecisionSurface` as the single decision context (not a new wrapper)

The control region's decision context is `RuntimeDecisionSurface`. The spec requires the approval actions and the evidence that backs the decision to live in **one** context, not split across sibling regions. Rather than introducing a new "control region" wrapper component (which would split the decision from its evidence and force the test suite to learn a new composition), the surface itself is enriched with **optional, summary-gated** slots.

- New optional props on `RuntimeDecisionSurfaceProps`:
  - `evidence?: DecisionEvidence` — compact, openable plan/check artifact references, rendered inside the surface **only** when `decision.summary` is `approval-required | blocked | failed` (the states where an owner decides with evidence).
  - `executionSignal?: ExecutionSignal` — a compact active-session cue and/or runner-state reason, rendered when a session is active or the runner gates the current decision.
  - `driftRecovery?: DriftRecoveryAction` — the rebase recovery entry, rendered when drift needs attention and blocks progress.
- When a slot's data is absent or the summary does not call for it, the surface renders **exactly as today** — preserving every existing `data-testid`, `data-summary`, `data-tone`, and the action/send-back/stop flows byte-for-byte on non-applicable paths.

**Alternatives considered:**
- *(a) Sibling components inside `status-header-tier` (e.g., a separate evidence card beside the surface).* Rejected: splits the approval decision from its evidence across regions, violating the "one decision context" requirement, and adds DOM siblings the tier-invariant tests would have to absorb.
- *(b) A new `IssueControlRegion` wrapper composing the surface + new pieces.* Rejected: indirection with no payoff; the surface already is the decision context. Kept the composition in `IssueDetailPage` (which already owns the tier) and pushed the new slots into the surface where the decision lives.

### D2. Decision-adjacent evidence: compact, openable artifact references inside the surface; full panel stays in reading-flow

The owner must be able to **open** plan/check artifacts from the control region without scrolling. `LatestArtifactsPanel` (the full browsing list + `ArtifactContentViewer` modal) stays in reading-flow for complete browsing. The surface gains a compact evidence block that lists the most relevant artifacts as openable buttons, each opening the same `ArtifactContentViewer` modal.

- Extract the openable-artifact behavior (query + item row + viewer-modal local state) currently inlined in `LatestArtifactsPanel` into a small reusable `ArtifactOpener` (artifact list rows + `selectedArtifactId` state + `ArtifactContentViewer`). Both the surface's compact evidence and the full panel compose it.
- The surface's evidence slot renders `ArtifactOpener` in a compact mode (top items, minimal chrome) when the summary is decision-active. The full panel renders it in full mode (all items, card chrome) as today.
- TanStack Query dedupes the artifact query by key, so the two call sites do not double-fetch. Each instance owns its own `selectedArtifactId` so only one viewer opens at a time.
- Existing `data-testid="latest-artifacts-list"` / `latest-artifact-item` anchors are reused (the surface's compact list carries the same item testids so content-viewer tests apply to both); the full panel's frame anchor is unchanged.

**Alternatives considered:**
- *(a) Move `LatestArtifactsPanel` entirely into the surface.* Rejected: makes the surface heavy in every state and removes the complete browsing list from reading-flow, which the spec does not ask for ("discoverable from the operation context", not "moved into it").
- *(b) A scroll-to-artifacts anchor in the surface.* Rejected: the spec explicitly forbids requiring the owner to scroll into reading-flow to find artifacts ("SHALL NOT need to scroll into the reading-flow tier to find the artifacts"); a scroll anchor still makes them scroll.

### D3. Compact execution signal: derived on the page, passed into the surface

The control region needs a compact runner/session signal only when relevant: an active coder session, or a runner state that gates the current decision (unavailable / capacity-full). The full `WorkflowSessionsPanel` stays in reading-flow.

- `IssueDetailPage` computes an `ExecutionSignal` from data it already holds / can cheaply read: the active session (a lightweight read of the sessions for the run) and the runner state already feeding `decision.waitReason` (`agentStatus.runnerAvailable`, `capacity`).
- The signal is passed to the surface and rendered compactly (one line: session name + transcript link, or the runner-gating reason). It is omitted entirely when no session is active and the runner does not gate a decision, so backlog/done states keep their current first screen.
- Reuse the runner-gating reason text already produced by `buildWaitReason` rather than re-deriving, so the signal and the existing `runtime-wait-reason` stay consistent.

**Alternatives considered:**
- *(a) Run the sessions query inside the surface.* Rejected: pushes query ownership into a pure decision component and couples it to session entities; keeping the page as the data owner matches the existing pattern where the page computes `decision` and passes it down.
- *(b) Extend `RuntimeDecision` to carry the session signal.* Rejected: `deriveRuntimeDecision` is an adjudication function over issue/timeline/agent projections; session detail is execution-plane evidence, not a status adjudication. Mixing concerns and risks touching the "consumed unchanged" adjudication spine.

### D4. First-screen drift recovery: promote the rebase action into the control region; keep full detail in the rail

Drift recovery (rebase) is currently owned by `BranchBar` (top of reading-flow, its own rebase mutation + `useWorkspaceStatus`). The spec requires drift that blocks progress to become a first-screen recovery entry point, while the reference-rail `IssueDriftCard` retains full detail.

- When `issue.drift?.drifted` and the drift decision needs attention (the condition that already sets `decision.driftNote`), render a compact drift-recovery block in the surface carrying the rebase action. The rebase trigger reuses the same mutation path as `BranchBar`.
- Extract the rebase-recovery trigger (mutation + workspace-status read + pending/error states) into a small reusable hook/handler so both the surface's drift slot and `BranchBar` share one implementation; `BranchBar` stays in reading-flow for ongoing branch-status display.
- The rail `IssueDriftCard` is unchanged (full base SHA, defer reason, conflicts). The first-screen entry promotes the **action**, not the detail — satisfying "promote its recovery action, do not replace the rail card."

**Alternatives considered:**
- *(a) Add `rebase` to the `RuntimeActionKind` set so drift recovery flows through the existing action buttons.* Rejected: explicitly forbidden by the spec ("no workflow action is added"). Rebase is a branch operation, not a workflow lifecycle action, and is not in the backend's `allowedActions` set.
- *(b) Move `BranchBar` into the control region.* Rejected: `BranchBar` carries branch-status detail relevant to reading-flow; only the recovery action needs first-screen promotion.

### D5. Preserve the tier invariants and anchors; demote by document order

Demotion is structural, not deletion: description, comments, model selection, and prerequisites already reside below the control region (description/comments in reading-flow; model/prerequisites in the rail configuration card). The change ensures no new operational content leaks into reading-flow above them, and that nothing descriptive is pulled into the status-header tier.

- The three `data-tier-weight` containers, the single-sticky `StatusHeadline`, the `lg:col-span-2`/`col-span-1` ratio, the spacing scale, and the cross-tier "every block in exactly one tier" audit are preserved.
- All existing `data-testid` anchors are kept; new anchors (`runtime-evidence`, `runtime-execution-signal`, `runtime-drift-recovery`) are additive and follow the existing `runtime-*` naming.

## Risks / Trade-offs

- **[Risk: enriching the surface increases its prop surface and re-render cost]** -> Mitigation: the new props are optional and summary-gated; non-applicable states render identically to today. Evidence/session data flows through TanStack Query dedupe, not new network calls.
- **[Risk: duplicating artifact/session data between control region and reading-flow]** -> Mitigation: shared query keys (deduped), a shared `ArtifactOpener` for the viewer, and a shared rebase hook. One viewer is open at a time (local `selectedArtifactId` per instance).
- **[Risk: the main operation surface changes, so a regression could hide in a conditional path]** -> Mitigation: assert every conditional path (running, approval-required, blocked, interrupted, drift, failed, queued/backlog, done, archived) against the new first-screen control contract; reuse adjudication/action-gating unchanged; preserve every `data-testid` and tier invariant so existing assertions keep guarding.
- **[Risk: narrow viewport loses evidence/session/drift on the first screen]** -> Mitigation: documented non-goal; `MobileActionBar` continues to carry the primary action. Desktop-first control workspace is the scope; mobile evidence surfacing deferred to a follow-up.
- **[Trade-off: drift recovery in two places (surface + `BranchBar`)]** -> Accepted: the surface promotes the action for the decision moment; `BranchBar` retains ongoing branch status. Shared rebase hook prevents logic drift.

## Migration Plan

This is a Web-only, presentation-layer rework with no data/API/CLI changes — deploy and rollback are a single frontend release.

1. Extract `ArtifactOpener` (openable artifact rows + viewer) from `LatestArtifactsPanel`; verify the full panel renders unchanged.
2. Extract the rebase-recovery hook/handler from `BranchBar`; verify `BranchBar` rebase behaves unchanged.
3. Add the optional `evidence`, `executionSignal`, and `driftRecovery` slots to `RuntimeDecisionSurface`; render them only on their applicable summaries; verify non-applicable states render byte-for-byte as before.
4. Wire `IssueDetailPage` to compute `ExecutionSignal` and `DriftRecoveryAction` and pass the three new props; keep the full panels in reading-flow/rail.
5. Update the `IssueDetailPage.*.test.tsx` suite for the new first-screen hierarchy; add spec tests for approval-with-evidence, decision-adjacent artifact discoverability, first-screen drift recovery, session-signal surfacing, and secondary-content demotion.

**Rollback:** revert the frontend release; no server/runner migration or data backfill is involved. The adjudication and action layers are untouched, so rollback cannot affect issue lifecycle behavior.

## Open Questions

- Should the compact evidence list in the surface show all artifacts or only the plan/check artifact most relevant to the awaiting stage? (Lean: show the top items from the existing artifact list, consistent with the full panel, to avoid per-stage relevance logic that would leak stage semantics into the view.)
- Should `MobileActionBar` eventually carry a compact evidence link for parity on narrow viewports? (Deferred — out of scope for this issue; flagged for a follow-up.)
