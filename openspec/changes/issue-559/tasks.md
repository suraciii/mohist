## Current-master Delivery Plan

- [x] Add the Server-only handoff command, immutable invocation, request
  fingerprint, preflight port, definitive rejection, and acceptance receipt.
  Focused grain tests cover replay, conflict, frozen runtime snapshot, and no
  AgentJob or AgentSession record before or after acceptance.
- [x] Extend the handoff receipt with a frozen completion snapshot: exact
  Workflow/task/work/stage/workspace, canonical Agent identity, and rendered
  `expect`/artifacts/`setVars`/recovery declarations. The fingerprint has a
  deterministic JSON-object encoding and tests preserve the no-participants
  boundary.
- [ ] Materialize provisional AgentJob and AgentSession participants from an
  accepted receipt, using a persisted activation cursor and frozen
  Agent/workspace/task facts. Keep this support dark: it has no production
  Workflow caller until terminal settlement exists.
- [ ] Add one typed AgentJob terminal delivery with a stable identity and
  acknowledgement. It carries the full Workflow and participant lineage plus
  terminal facts and completion evaluation; it is not a Workflow task report.
- [ ] Add the Workflow-owned finalizer that validates the frozen invocation and
  records per-effect receipts before task outcome, `expect`, artifacts,
  variables, recovery, and advancement.
- [ ] Switch new `mohist/agent` dispatches only after the activation, terminal
  delivery, and finalizer contract is deployed and replay-tested together.
