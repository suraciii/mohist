## Context

The issue detail page already derives a `RuntimeDecision` from the Issue, Workflow timeline, recovery projection, runner capacity, and active-agent facts. That model is authoritative for the workflow summary and server-offered workflow actions, but its presentation is fragmented: desktop renders `RuntimeDecisionSurface`, narrow viewports render only `decision.primary` through `MobileActionBar`, lifecycle and agent actions live in `IssueActionsCard`, and `InlineApproval` contains a second approval command path.

Status is similarly repeated by `StatusHeadline`, the header `RuntimeSummaryPill`, the current-task pill, and stage rows in `IssueDetailsCard`. Transcript navigation is available only as a small active-session link, while the modeled inspect action is permanently disabled. Disabled runtime and Rebase controls rely mainly on `title` and generic opacity, which does not provide a visible reason on touch devices.

Composite parents are a required issue-detail case but intentionally have no workflow run or `RuntimeDecision`; their current actions still live in the rail. This is a Web-only presentation and orchestration change. The Server remains the state authority and continues to decide workflow action availability, while current Issue facts remain authoritative for existing lifecycle applicability; no API, persistence, Runner, CLI, workflow profile, or lifecycle contract changes are required. The primary stakeholders are issue owners using desktop and phone viewports, including keyboard and touch users.

## Goals / Non-Goals

**Goals:**

- Make the sticky headline the only issue status statement and remove repeated task/stage status metadata.
- Build one canonical issue-detail context and action model from the optional runtime decision plus existing Issue lifecycle, delegation, child-summary, and session facts.
- Drive desktop and mobile controls from the same action list and command controller.
- Preserve server authorization while giving every displayed unavailable action a visible product-language reason and unmistakable disabled styling.
- Keep workflow evidence available while removing its independent approval mutation path.
- Promote a real session transcript route to an action and remove the synthetic disabled inspect action.

**Non-Goals:**

- Changing runtime summary precedence, workflow action authorization, lifecycle rules, or Server projections.
- Adding lifecycle actions, changing approval points, or determining who is permitted to approve.
- Moving Rebase out of its branch context or redesigning the issue reading flow.
- Adding the approval-time artifact review package owned by the sibling issue.
- Broad visual-system or shared Button redesign.

## Decisions

### Normalize workflow and issue-only decision context at the page boundary

`RuntimeDecision` and `deriveRuntimeDecision` remain limited to workflow status, rationale, next action, and server-offered workflow commands. A page-owned `IssueDecisionContext` will use that decision unchanged for workflow-capable issues. When a composite parent has no runtime decision, it will instead derive an issue-only headline, rationale, and next action from the parent's current Issue status, health, draft state, and child summary; it will not invent a workflow stage, task, or workflow action.

The same pure page model under `pages/issue-detail/model` will derive `IssueDecisionAction` descriptors by composing:

- The existing runtime actions and their primary ordering.
- Existing issue lifecycle predicates for mark ready, close, and mark as done.
- Ask Agent navigation.
- A transcript action when a workflow session exists.

Each descriptor will carry a stable kind, label, enabled state, optional disabled reason, emphasis/destructive intent, and interaction mode such as immediate, confirmation, feedback, or navigation. Mutation objects and callbacks stay outside the pure derivation function. Workflow descriptors are copied from `RuntimeDecision`; lifecycle descriptors continue to use the existing Issue predicates. A composite parent therefore receives only applicable issue lifecycle and delegation descriptors. This gives desktop and mobile one ordered contract without making workflow adjudication depend on page navigation, child-issue facts, or lifecycle mutations.

Alternative considered: extend `RuntimeActionKind` and `deriveRuntimeDecision` with composite-parent status, lifecycle, agent, transcript, and Rebase actions. This would reuse the existing `primary/actions` shape, but would fabricate workflow decisions for parents and mix server-projected workflow decisions with page-owned navigation and issue lifecycle policy, so it is rejected.

### Use one action controller for both responsive presentations

The issue detail page will create the runtime mutation adapter once and bind the composed descriptors to one action controller. The controller will own command dispatch, pending/error lookup, recoverable versus terminal Stop selection, Stop confirmation, and send-back feedback validation. It will overlay pending mutation state on the pure descriptor as a rendered disabled state with an operation-specific progress label and reason. Desktop will render the full action list inline; the narrow bottom bar will retain a compact primary affordance and open an action sheet containing the complete action list and decision context.

The desktop region and mobile sheet may use different layout components, but they will consume the same descriptors and controller. The mobile launcher remains reachable even when the primary command is disabled, preventing a disabled primary action from hiding enabled secondary actions or its explanation.

Alternative considered: enhance `MobileActionBar` independently while leaving `RuntimeDecisionSurface` unchanged. This is smaller locally but preserves duplicated pending, error, confirmation, and invocation logic, which is the source of divergent behavior, so it is rejected.

### Centralize applicability but preserve existing authorization

Lifecycle applicability predicates currently embedded in `IssueActionsCard` will move into the pure page action derivation and retain their current behavior for drafts, composite parents, active execution, stopped/completed workflows, archived issues, and terminal issue states. These predicates consume current Issue facts; they are not additions to the workflow projection. Workflow actions remain enabled only when the existing runtime projection and local readiness gates allow them, and no workflow descriptor is emitted without a runtime decision.

Actions that are irrelevant to the current state will be omitted. Actions that remain relevant but are temporarily unavailable, such as Start blocked by a draft or capacity and Stop unavailable during an active task boundary, will remain visible with a reason. This reduces dead control sets without inventing permissions. If a running issue has no executable action, the decision context will state the blocking condition and next transition rather than presenting unexplained disabled buttons.

Alternative considered: display every possible action in every state. This makes capability discovery easy, but creates noisy all-disabled surfaces and weakens the meaning of “applicable now,” so it is rejected.

### Make status and decision copy have one authority

`StatusHeadline` remains sticky and renders the normalized issue decision context as one textual status statement. Workflow-capable issues render the runtime summary, headline, stage progress, and current task; composite parents render an issue-only summary and child-progress context with no workflow stage or task. The header runtime pill, the separate current-task pill, and Issue/Workflow Stage rows in `IssueDetailsCard` will be removed; relationship, project, and repository metadata remain.

`runtime-presentations` remains the authority for rationale and next-action copy. Its approval wording will describe a pending approval decision without assuming the viewer is the approver. Existing approval and interrupted/recovery facts will distinguish approval pauses from manual stops; summary precedence itself will not change.

Alternative considered: keep the repeated values but restyle them as less prominent metadata. This still permits contradictions and fails the exactly-once contract, so it is rejected.

### Treat disabled explanations as visible content

Runtime presentation code will replace implementation-jargon fallbacks with action- and state-specific product reasons. The canonical action renderer will show a disabled reason as helper text associated through `aria-describedby`; pointer tooltips may supplement but will not replace visible text. When the controller marks an action pending, the renderer will use a specific progress label such as `Approving...` or `Closing...` and a visible helper reason that another request is unavailable until the operation finishes; the associated status will use `aria-live="polite"`. Disabled destructive actions will use a neutral disabled treatment rather than retaining live destructive emphasis.

Rebase remains in `BranchBar` because it is tied to branch/workspace evidence rather than the issue decision action list. `BranchBar` will apply the same contract: derive a reason for workspace checking, unavailable status, or conflict resolution; render it visibly; and neutralize disabled styling. This narrow duplication is preferable to introducing a shared cross-domain action abstraction for two components.

Alternative considered: rely on native `title` tooltips and the shared Button opacity. Native tooltips are unavailable to touch users and do not make the reason persistently readable, so this is rejected.

### Make the workflow view evidence-only on issue detail

The mutation-bearing `InlineApprovalControls` path will be removed from the workflow view while task, check, artifact, report, and feedback evidence remains. Approval and send-back commands will execute only through the canonical issue decision controller. Existing stage/evidence navigation behavior will otherwise remain unchanged.

Alternative considered: retain inline approval controls but hide them through `readOnly`. The current page does this, but the second implementation can drift and remains easy to re-enable accidentally, so removing the command path provides a stronger single-owner invariant.

### Resolve transcript actions from concrete sessions

The synthetic `inspect` action and its permanently disabled View transcript button will be removed from runtime presentations. The page action model will choose a concrete workflow session deterministically: prefer active/running/probing sessions, then the most recently started or created session, with session name as a stable tie-break. It will create an enabled transcript navigation descriptor using that session's route. Ask Agent remains a separate delegation action. When no session exists, no transcript action is emitted.

Alternative considered: keep a disabled transcript placeholder until a session becomes active. This creates a dead command and does not satisfy the requirement that transcript actions lead to real sessions, so it is rejected.

## Risks / Trade-offs

- `[Risk] Lifecycle predicates change while moving out of IssueActionsCard` -> Preserve them in a table-driven unit test covering draft, archived, composite-parent, active-agent, stopped, completed, done, and cancelled states before deleting the old card.
- `[Risk] Composite parents lose their only status or action entry when runtime-only UI is removed` -> Derive and test an issue-only decision context and lifecycle/delegation action set before removing the Details status rows and rail Actions card.
- `[Risk] Desktop and mobile still diverge through separate markup` -> Keep ordering, enabled state, reasons, dispatch, pending state, and errors in the shared descriptor/controller; limit renderer differences to layout and disclosure.
- `[Risk] A disabled primary action blocks access to enabled secondary mobile actions` -> Keep the action-sheet launcher independently enabled and test mixed enabled/disabled action sets at phone width.
- `[Risk] Session ordering selects the wrong transcript` -> Use explicit status priority and timestamp/name ordering rather than API array order; retain the sessions panel for access to all sessions.
- `[Risk] Removing InlineApproval regresses evidence or review reports` -> Remove only mutation controls and keep StepList evidence rendering covered by existing workflow-view specifications.
- `[Risk] Visible helper text increases action-surface height` -> Use compact action rows and disclosure on narrow screens; do not hide reasons behind hover-only affordances.
- `[Risk] Mutation-pending controls become disabled without satisfying the reason contract` -> Treat pending as a rendered unavailable state with a progress label, visible associated reason, polite announcement, and desktop/mobile controller tests.
- `[Trade-off] Rebase remains outside the canonical action list` -> Keep it next to branch evidence, but apply the same disabled-reason and visual-state rules and cover it in the capability tests.

## Migration Plan

1. Add the normalized issue decision context, pure action derivation, and unit matrix for both workflow-capable issues and composite parents while keeping existing renderers in place.
2. Add the shared action controller and migrate desktop runtime actions, lifecycle actions, Ask Agent, and transcript navigation to the composed descriptors.
3. Replace the primary-only mobile path with the compact bar and complete action sheet backed by the same descriptors/controller.
4. Remove the rail Actions card, synthetic inspect action, duplicate header/status metadata, repeated Details rows, and workflow-local approval controls.
5. Update focused Web specs for status uniqueness, action applicability, approval routing, disabled reasons, transcript routing, and narrow viewport parity. Add browser coverage for desktop/phone layout, mobile sheet reachability, disabled appearance, and transcript navigation.
6. Run Web typecheck and unit/spec suites, then the focused browser specification.

No data migration or staged backend deployment is needed. The change ships as one Web bundle against unchanged APIs. Rollback is a Web-code revert; because no persisted shape or server contract changes, the previous presentation can be restored without data repair.

## Open Questions

None. The issue and capability spec establish the action set, responsive behavior, transcript rule, and ownership boundary needed for implementation.
