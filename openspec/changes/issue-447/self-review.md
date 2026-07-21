# Self Review

## Findings

1. **Blocking: the narrow host has no migration for built-ins that require private identity/context.** The capability spec forbids Actions from receiving identity and dispatch metadata, and the design requires every built-in to use only `(inputs, host)`. However, `issue-fields.ts` resolves `issue.title` and `issue.body` from `context.issueNumber` and `context.projectId`; `opencode.ts` composes `context.parentIssueContext` into the prompt and uses workflow/work identity for session binding and runtime events; `archiveChangeAction` derives and validates its retry checkpoint with `context.workflowRunId`. The design and task do not say whether each value moves into an existing `with` input, an additional declared capability, or an executor-owned adapter. Implementing the plan as written either breaks these built-ins or leaves forbidden metadata exposed, violating the required behavior boundary and the preserved built-in behavior contract.

2. **Blocking: the plan does not replace `mohist/openspec-tasks`' name-gated `rawTask` behavior.** `WorkExecutor` currently branches on the Action name to inject the unrendered `work.with.task` as `rawTask`; the loader preferentially uses it when constructing follow-up tasks. The proposed host forbids dispatch metadata and the design promises to remove name-based Action behavior, but neither the specs nor the design define how the loader retains the raw task default, including deferred template values, after `with` validation/rendering. The implementation task only says to preserve parsing and task construction, which is insufficient to choose a correct replacement. This must be specified so the migration neither retains a hidden name-based context exception nor changes generated OpenSpec task content.

## Conclusion

The artifacts correctly identify capability gating, deferred effects, and capability-driven promise projection, but the two unresolved context migrations above make the atomic implementation task underspecified.

<promise>FAIL</promise>
