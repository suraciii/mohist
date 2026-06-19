## ADDED Requirements

### Requirement: Shared attention-item derivation lives in the Issue context

The shared Issue context SHALL expose a single derivation function that turns a list of `Issue` records (together with the current `AgentStatus`) into the set of `AttentionItem` values that represent user-actionable work. This derivation SHALL be the only authority on which issues are surfaced as needing user attention, and SHALL be consumed by every UI surface that renders such a list (currently the Kanban widget, and in the future the Dashboard Attention Hero).

The derivation SHALL cover exactly these rules, in this evaluation order against each `Issue`:

1. **Approval pending**: if `issue.approvalState?.status === 'awaiting'`, produce an `AttentionItem` with `label = "Approval needed"` and `detail = issue.title`.
2. **Integrate failure**: if the issue is in `WorkflowStage.Integrate` AND `issue.health` is `Blocked` or `Interrupted`, produce an `AttentionItem` with `label = "Integration failed"` and `detail = issue.title`.
3. **Interrupted**: if `issue.health === Interrupted`, produce an `AttentionItem` with `label = "Interrupted"` and `detail = issue.title`.
4. **Blocked**: if `issue.health === Blocked`, produce an `AttentionItem` with `label = "Needs action"` and `detail = issue.blockedReason ?? issue.title`.

The first matching rule SHALL win for any given issue. Each `Issue` SHALL appear at most once in the output. The function SHALL be pure: it SHALL NOT mutate its inputs and SHALL return a fresh `AttentionItem[]` on every call. The `AgentStatus` parameter is currently unused by the rules above and SHALL be retained in the signature so that future rule additions (e.g. agent-quiet gating) can read it without changing the call site.

#### Scenario: Approval-pending issue is surfaced

- **WHEN** the input list contains an `Issue` whose `approvalState.status` is `'awaiting'`
- **THEN** the output contains an `AttentionItem` for that issue with `label = "Approval needed"` and `detail = issue.title`

#### Scenario: Integrate-stage blocked issue is surfaced as integration failure

- **WHEN** the input list contains an `Issue` with `workflowStage === WorkflowStage.Integrate` and `health === IssueHealth.Blocked`
- **THEN** the output contains an `AttentionItem` for that issue with `label = "Integration failed"` and `detail = issue.title`

#### Scenario: Integrate-stage interrupted issue is surfaced as integration failure

- **WHEN** the input list contains an `Issue` with `workflowStage === WorkflowStage.Integrate` and `health === IssueHealth.Interrupted`
- **THEN** the output contains an `AttentionItem` for that issue with `label = "Integration failed"` and `detail = issue.title`

#### Scenario: Interrupted (non-integrate) issue is surfaced

- **WHEN** the input list contains an `Issue` with `health === IssueHealth.Interrupted` and `workflowStage !== WorkflowStage.Integrate`
- **THEN** the output contains an `AttentionItem` for that issue with `label = "Interrupted"` and `detail = issue.title`

#### Scenario: Blocked issue uses blockedReason as detail

- **WHEN** the input list contains a non-integrate `Issue` with `health === IssueHealth.Blocked` and a non-empty `blockedReason`
- **THEN** the output contains an `AttentionItem` for that issue with `label = "Needs action"` and `detail = issue.blockedReason`

#### Scenario: Blocked issue without blockedReason falls back to title

- **WHEN** the input list contains a non-integrate `Issue` with `health === IssueHealth.Blocked` and `blockedReason` is null/empty
- **THEN** the output contains an `AttentionItem` for that issue with `label = "Needs action"` and `detail = issue.title`

#### Scenario: First matching rule wins per issue

- **WHEN** the input list contains an `Issue` that matches more than one rule (for example an `awaiting`-approval issue that is also `Blocked`)
- **THEN** the output contains exactly one `AttentionItem` for that issue, using the label of the first matching rule in evaluation order

#### Scenario: Duplicate issue ids are deduplicated

- **WHEN** the input list contains the same `Issue.id` more than once
- **THEN** the output contains at most one `AttentionItem` for that `id`

#### Scenario: Healthy issues produce no attention items

- **WHEN** the input list contains only issues that do not match any of the four rules
- **THEN** the output is an empty array

### Requirement: Attention derivation is consumed from the Issue public API, not from widget-local code

The derivation function and the `AttentionItem` type SHALL be reachable through the shared Issue-context public API (the entity layer consumed by any UI surface). The Kanban widget SHALL import the derivation from this shared location and SHALL NOT re-implement the rules locally. No widget-local derivation copy of the four rules SHALL exist after the change.

#### Scenario: Kanban widget imports derivation from the shared location

- **WHEN** the Kanban widget source is inspected
- **THEN** its import of `deriveAttentionItems` (and any related attention types) comes from the shared Issue-context module
- **AND** the original widget-local `homepage-attention.ts` derivation file is removed

#### Scenario: Dashboard and other surfaces can consume the same derivation

- **WHEN** any future UI surface (such as the Dashboard Attention Hero from Epic #9) imports `deriveAttentionItems` and `AttentionItem` from the shared Issue-context module
- **THEN** it SHALL receive the same output for the same input as the Kanban widget does

### Requirement: Behaviour is preserved across the move

The refactor that relocates the derivation SHALL NOT change observable behaviour. For any input, the derivation SHALL produce an output that is equal to the output produced by the previous widget-local implementation, item-for-item (same `issueId`, `issueNumber`, `label`, `detail` values, in the same evaluation order). Existing tests that exercised the widget-local derivation SHALL continue to pass once pointed at the shared module, without modification of their assertions.

#### Scenario: Output matches prior widget-local implementation

- **WHEN** the shared derivation is invoked with the same `(issues, agentStatus)` input that the prior widget-local implementation was invoked with
- **THEN** the resulting `AttentionItem[]` is equal to the prior output (same items in the same order)

#### Scenario: Existing tests pass without assertion changes

- **WHEN** the existing derivation tests are migrated to import from the shared Issue-context module
- **THEN** all assertions about labels, details, and ordering pass unchanged

### Requirement: Server-side runtime status remains the authority

The shared derivation SHALL rely on the same `Issue.health`, `Issue.workflowStage`, and `Issue.approvalState` fields that the server-side `MohistDefaultWorkflowProjection.RuntimeStatus` produces. The relocation MUST NOT introduce a new client-side interpretation of those fields, MUST NOT add new attention categories, and MUST NOT change the `AttentionItem` shape.

#### Scenario: No new attention categories are introduced

- **WHEN** the shared derivation source is inspected
- **THEN** the set of rules is exactly the four listed in the first requirement (approval-pending, integrate failure, interrupted, blocked)
- **AND** no additional categories have been added

#### Scenario: AttentionItem shape is unchanged

- **WHEN** the shared `AttentionItem` type is compared with the prior widget-local `AttentionItem` type
- **THEN** the fields (`issueNumber`, `issueId`, `label`, optional `detail`) and their types are identical
