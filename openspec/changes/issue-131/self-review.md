# Self Review: Issue 131

## Findings

1. **Blocking: the plan omits `mohist/agent` support for stage checks.**
   The issue scope explicitly says a task/check can use an Agent profile. The proposal, design, and spec limit the capability to task work: the spec begins with “for task work”, and the design only transforms task dispatch in `WorkflowItemTranslator`. Checks are emitted as a separate `WorkDispatch` whose individual check `uses` values remain runner-side (`WorkflowItemTranslator.BuildChecksDispatchAsync`), so the proposed virtual Action cannot resolve an Agent or become an OpenCode/Pi Action for a check. The plan must either cover transformation and failure/reporting semantics for checks or obtain an explicit scope change from the issue owner.

2. **Blocking: the claimed per-attempt Agent snapshot has no durable home in the proposed integration.**
   `design.md` calls the transformed `WorkDispatch` immutable for the claimed attempt and says redelivery must use the existing dispatch, while retry resolves again. Current Workflow work is intentionally re-rendered by the stateless `DispatchService`; `RunnerWork.DispatchSnapshot` is only stored for AgentJob work. `RenderActiveWorkAsync` calls `WorkflowItemTranslator.TranslateToDispatchAsync` again on every reoffer, so an Agent edit between the first offer and a redelivery changes the effective definition for the same attempt. The plan must specify and implement durable attempt-scoped storage or another canonical snapshot boundary that survives reoffers, server restart, and retry distinction.

3. **Blocking: no execution contract exists for carrying Agent instructions and complete Agent configuration through either selected Workflow Action.**
   The issue and accepted product/design contracts require instructions, runtime, model, and execution configuration to reach OpenCode or Pi while keeping the workflow prompt as the current goal. The plan says the translator will put the snapshot into existing Runtime Action `options` and that runtime adapters will combine instructions with the prompt, but the current `mohist/opencode` and `mohist/pi` Action contracts accept only `options.model` and `options.variant`; neither accepts or composes `instructions`, and arbitrary Agent configuration is rejected or ignored. This is left as an open question in `design.md`, so the implementation path cannot satisfy the required input composition. Define the exact server-to-runner payload and the Action/runtime changes for both backends, including timeout behavior, configuration validation, and instruction precedence; test that contract at the Workflow Action boundary.

4. **Blocking: `agent_not_found` cannot be preserved by the stated dispatch-rejection path.**
   `design.md` proposes `WorkflowDispatchRejectedException` with an error code, but the existing exception has only a message. `DispatchService.RejectDispatchAsync` passes only that message to `RejectActiveWorkDispatchAsync`, which constructs `TaskResult("failed", message)` without an `ExecutionError`. Consequently recovery cannot match `failure.error.code == agent_not_found`, contrary to the issue and the spec. The plan must include a structured rejection/report path that stores `ExecutionError("agent_not_found", actionableMessage)` on the owning TaskRun and retains existing Workflow-owned recovery decisions.

5. **Blocking: the requested `tasks.json` artifact is absent.**
   `openspec/changes/issue-131/` contains `proposal.md`, `design.md`, and `specs/workflow-agent-action/spec.md`, but no `tasks.json`. Without the implementation task breakdown, owners, or acceptance-oriented execution steps, the plan is incomplete and cannot be reviewed as build-ready.

## Required Corrections

- Add a `tasks.json` that addresses every finding with testable acceptance criteria.
- Extend the proposal, design, and spec consistently for check support, or record an approved narrowing of the issue scope.
- Resolve the snapshot, runtime-input, and structured-error designs before implementation; do not leave them as open questions.

<promise>FAIL</promise>
