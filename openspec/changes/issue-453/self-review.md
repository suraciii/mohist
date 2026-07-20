## Findings

### High: Composite-parent issues have no defined status or action path

The spec requires the status summary exactly once for any issue state and requires lifecycle/delegation actions in the decision surface (`specs/issue-decision-surface/spec.md:3`, `:34`). The design builds that surface from the existing `RuntimeDecision` (`design.md:32`), but current issue-detail composition intentionally does not derive a runtime decision or render runtime surfaces for composite parents. T-001 removes the Details status rows, while T-002 removes the rail Actions card and merely lists composite parents in a predicate test matrix (`tasks.json:13`, `:34`, `:36`); no artifact defines what supplies a parent headline, rationale, or action container when `RuntimeDecision` is absent. Resolve this explicitly by either narrowing the normative requirements to workflow-capable leaf issues or designing and testing an issue-only fallback decision/status model for composite parents. As written, a valid current issue kind can lose its remaining status and action presentation.

### Medium: The source-of-truth contract contradicts lifecycle action composition

The issue and proposal say the runtime decision projection is the single source for offered actions and that action availability continues to derive from it (`proposal.md:12`). The design instead moves frontend lifecycle predicates out of `IssueActionsCard` and re-derives mark-ready, close, and mark-done applicability in a page model (`design.md:32-39`, `:51-55`). The no-server-change constraint makes that composition understandable, but the artifacts currently describe two different authority models. Clarify that the runtime projection is authoritative only for workflow actions and identify the existing Issue facts as authoritative for lifecycle applicability, or revise the design to consume one authoritative projection. The proposal, spec, design, and task wording must agree before implementation.

### Medium: Mutation-pending disabled states are not covered by the universal reason rule

The spec applies visible, accessible reasons to every shown unavailable action (`specs/issue-decision-surface/spec.md:70`). The design places mutation pending state in the controller while descriptor reasons come from pure applicability derivation (`design.md:39`, `:45`), and T-002 tests product reasons for temporarily unavailable runtime actions but does not require a visible reason for workflow or lifecycle controls disabled because a mutation is pending (`tasks.json:35`, `:38`). Define whether an explicit busy label is the visible reason or whether helper text is required, then add acceptance coverage for pending approve/send-back/stop and lifecycle mutations on desktop and mobile. Otherwise an implementation can satisfy the task while violating the spec's universal rule.

## Validation

- `tasks.json` parses as valid JSON.
- Task IDs and dependencies form a valid DAG; T-002 depends on lower-priority T-001.
- All proposal capabilities have a spec file, and every spec requirement has at least one correctly headed scenario.

<promise>FAIL</promise>
