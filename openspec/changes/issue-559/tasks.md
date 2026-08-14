## Current-master Delivery Plan

- [x] Add the Server-only handoff command, immutable invocation, request
  fingerprint, preflight port, definitive rejection, and acceptance receipt.
  Focused grain tests cover replay, conflict, frozen runtime snapshot, and no
  AgentJob or AgentSession record before or after acceptance.
- [ ] Materialize provisional AgentJob and AgentSession participants from an
  accepted receipt, with idempotent activation.
- [ ] Add typed Runner-to-Server transport and acknowledgement without running
  an Action or runtime directly.
- [ ] Freeze the Workflow completion contract and add the AgentJob terminal to
  Workflow finalizer with idempotent completion-effect receipts.
- [ ] Switch new `mohist/agent` dispatches only after the finalizer exists.
