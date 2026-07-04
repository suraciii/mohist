# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The design (D2 table row for `IssueActionsCard`, D7 paragraph) and task T-003 acceptance criteria place the non-runtime `IssueActionsCard` (Mark ready / Close / Ask Agent / archived note / draft readiness) in the reference rail. However, both the proposal Capability description and the `issue-detail-reference-rail` spec requirement "Metadata and Low-Frequency Configuration Only" stated the rail holds "only metadata and low-frequency configuration", omitting `IssueActionsCard` entirely. The restrictive word "only" created a spec-vs-design inconsistency that could mislead an implementer reading the spec in isolation.
  Verification: Updated `specs/issue-detail-reference-rail/spec.md` requirement body to enumerate the non-runtime `IssueActionsCard` alongside metadata/config, and added a "Non-runtime issue actions live in the rail" scenario asserting the card appears in the rail without duplicating the seven header-tier runtime actions. The requirement heading text was preserved so T-003's `#Metadata and Low-Frequency Configuration Only` spec anchor remains valid. Updated `proposal.md` reference-rail Capability to mirror the same wording. Re-read both files to confirm the proposal, design D2/D7, spec, and T-003 acceptance criteria now agree on `IssueActionsCard` placement.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: completeness
  Evidence: The status-header spec requirement "Adjudicated Runtime Situation" enumerates all six situations (running, queued, approval-required, blocked, failed, done) but provides explicit scenarios for only three (running, approval-required, done-archived). Queued, blocked, and failed lack dedicated scenarios. The requirement text is normative for all six, so this is illustrative coverage rather than a gap, but adding scenarios would harden the spec against a headline that mishandles an under-exercised situation.
  SuggestedAction: During implementation (T-001), consider adding scenarios for queued/blocked/failed if the headline rendering branches per situation; otherwise leave as-is since the requirement already constrains all six values.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: consistency
  Evidence: Design D2 Open Question proposes removing the standalone "Workflow Interrupted" card (subsumed by `deriveRuntimeDecision`'s blocked rationale). Task T-001 enacts this removal, but no spec requirement explicitly addresses interrupted-health surfacing via the headline. The status-header spec's "Adjudicated Runtime Situation" implicitly covers it (blocked is one of the six situations), so the behavior is constrained, but an explicit scenario tying interrupted health to the blocked rationale would make the removal's safety more visible.
  SuggestedAction: If during T-001 implementation the reducer's blocked wording proves insufficient for interrupted health, restore the card as a collapsed rail item per the design's stated fallback; optionally add a blocked-from-interruption scenario to the status-header spec.
  Status: follow-up

<promise>PASS</promise>
