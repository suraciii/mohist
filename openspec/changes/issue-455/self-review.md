# Self Review

## Findings

### 1. High: The approval-specific phone bar can drop applicable secondary actions

The issue says this review package mounts onto issue 453's unified decision surface. That prerequisite guarantees that a narrow viewport exposes the complete applicable action list, not only the primary action. Current `deriveIssueDecisionActions` appends Ask Agent for an active issue and View transcript when a workflow session exists, including while approval is pending.

The revised design replaces the approval-mode `MobileActionBar` with a fixed bar containing only direct Approve and Send back controls and explicitly removes the generic action sheet (`design.md:34`). The capability spec only requires “every authorized approval action” (`specs/issue-decision-surface/spec.md:3`), and T-001 likewise specifies a two-column bar with those two controls and no sheet (`tasks.json:16`). Neither artifact says where other applicable descriptors remain reachable. The phone Playwright criteria verify only Approve and Send back (`tasks.json:23-24`).

The plan can therefore satisfy every new criterion while making Ask Agent or View transcript disappear during approval on a phone. The proposal and spec must preserve the complete applicable action list while keeping Approve and Send back direct. The design must define a non-blocking location for secondary descriptors that does not add a disclosure before either approval action, and T-001 must cover secondary action reachability and navigation in approval mode at phone width.

## Confirmed

- The earlier one-tap mobile action finding is fixed: approval no longer uses the generic action drawer, and Send back opens an inline form.
- The earlier browser coverage finding is fixed: T-001 now requires phone-width Plan and Check evidence plus real Approve and Send back completion.
- The artifact/API boundaries, structured feedback contract, keyboard requirements, two-task split, and `T-001 -> T-002` dependency remain coherent.

<promise>FAIL</promise>
