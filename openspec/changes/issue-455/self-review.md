# Self Review

## Findings

### 1. High: The mobile action contract no longer guarantees the issue's one-gesture approval flow

The current issue says the owner should read the approval evidence and then approve or open send-back in one gesture; its Product Shape places Approve and Send back alongside the content and says nothing requires opening a dialog. That constraint is weakened to mere reachability in the proposal (`proposal.md:7-10`) and capability spec (`specs/issue-decision-surface/spec.md:51-65`). The design then explicitly retains the existing fixed mobile action control (`design.md:34`), and T-001 only requires both actions to be reachable through it (`tasks.json:16`).

The existing `MobileActionBar` is a launcher that opens a modal action drawer before exposing Approve and Send back. The plan can therefore satisfy every written spec and task while preserving the extra gesture and disclosure that this issue is meant to remove.

The proposal, spec, design, and T-001 acceptance criteria must state the direct mobile interaction: Approve is visible alongside the review package and executes in one tap; Send back is visible there and opens its structured form in one tap; neither requires opening the generic action drawer or another dialog. Non-approval states may retain the existing mobile action sheet.

### 2. High: Browser verification omits the phone Plan package and mobile action completion

The spec independently requires Plan and Check packages to work at phone width (`specs/issue-decision-surface/spec.md:55-65`). T-001's Playwright criterion instead covers Plan on desktop and Check on a phone (`tasks.json:22`). This does not verify phone rendering of `tasks.json`, the artifact most likely to expose long-token horizontal overflow, nor the complete phone Plan package with both actions.

The same criterion checks only fixed-action reachability and location; it does not execute mobile Approve or open and submit mobile Send back. A broken, multi-step, or still drawer-mediated primary workflow could pass.

T-001 must require real-browser phone scenarios for both Plan and Check. Those scenarios must verify inline evidence, no horizontal document overflow, safe-area clearance, direct Approve execution, one-tap opening and successful submission of structured Send back, and absence of an intervening action drawer/dialog.

## Confirmed

- The two capability specs correspond to the proposal capabilities and use testable normative scenarios.
- The design preserves the existing approval and text-feedback API contracts and keeps the server authoritative.
- The two-task split is coherent: T-002 consumes T-001's package and controlled form, and `T-001 -> T-002` is an acyclic dependency with strictly increasing priority.
- Each task includes its relevant test and typecheck verification rather than creating standalone test tasks.

<promise>FAIL</promise>
