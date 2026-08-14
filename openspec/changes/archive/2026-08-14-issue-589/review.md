# Issue 589 Review

## Verdict

PASS. No must-fix problems were found. The change is ready to merge.

## Must-Fix Findings

None.

## Dimension Checks

### Acceptance Coverage — checked, no issue

All eight issue acceptance criteria are covered:

- Inconclusive stop, idle/completed physical activity, and Runner disconnect remain observations under the original execution identity; they do not become `TaskFailed` or completion.
- Repeated delivery and recovery preserve the existing settlement and stop operation, and same-target stop redelivery is gated on positive activity reconciliation.
- The fixed deadline transitions unresolved work to a visible nonterminal `blocked` state with the stable `agent-result-unconfirmed` reason and recovery guidance.
- A matching late authoritative result remains eligible after blocking and clears the attention through the normal completion or failure path exactly once; stale observations and reports remain side-effect free.
- The changed status, event, Issue, Inbox, CLI, and Web projections represent blocked work as attention rather than failure, while retry/rerun are withheld and explicit stop remains available.
- The implementation applies the same settlement behavior to the issue's observed plan/build failure shapes without changing unrelated conclusive failure semantics.

### Correctness — checked, no issue

The Workflow status mapper derives run, stage, and task blocked status from a running task's durable settlement and leaves the persisted run lifecycle and failure view untouched ([WorkflowStatusMapper.cs](/home/szf/.mohist/projects/workspaces/wr_eff80c4f2b30407eb98c090a1ba97893/packages/server/src/Mohist.Server/Workflow/Services/WorkflowStatusMapper.cs:104)). The indexed `AttentionStatus` projection is rebuilt on every WorkflowRun save and is cleared when the settlement is no longer blocked ([WorkflowRunStore.cs](/home/szf/.mohist/projects/workspaces/wr_eff80c4f2b30407eb98c090a1ba97893/packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:176)). The control guard permits only stop for the blocked wire state ([WorkflowControlGuard.cs](/home/szf/.mohist/projects/workspaces/wr_eff80c4f2b30407eb98c090a1ba97893/packages/server/src/Mohist.Server/Api/WorkflowControlGuard.cs:13)).

The Runner now retains an awaiting report unless the server response explicitly says `tracked: true` ([host.ts](/home/szf/.mohist/projects/workspaces/wr_eff80c4f2b30407eb98c090a1ba97893/packages/runner/src/runtime/host.ts:786)). The Inbox projection consumes only `WorkflowRunBlocked` for the new notification kind ([InboxProjectionHandler.cs](/home/szf/.mohist/projects/workspaces/wr_eff80c4f2b30407eb98c090a1ba97893/packages/server/src/Mohist.Server/Inbox/Subscriptions/InboxProjectionHandler.cs:58)), so failure-only subscribers are not broadened accidentally.

### Consistency — checked, no issue

The implementation follows the existing WorkflowRun JSON/store projection, event catalog and CloudEvent lineage, Inbox subscription, CLI read, and Web timeline patterns. The new migration updates both the indexed blocked-attention projection and the Inbox notification constraint ([20260906000000_AddWorkflowBlockedAttentionProjection.cs](/home/szf/.mohist/projects/workspaces/wr_eff80c4f2b30407eb98c090a1ba97893/packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260906000000_AddWorkflowBlockedAttentionProjection.cs:9)). No unrelated product files were modified by the review.

### Tests — checked, no must-fix issue

`npm run test:fast` passed on the current commit with zero failures or skips: 2,643 Server unit tests, 69 architecture tests, 175 Workflow definition tests, 1,822 CLI tests, 4,708 Web tests, 1,626 Runner tests, and all typechecks.

The changed behavior has focused Server, CLI, Runner, Web, Inbox, projection, and browser-spec coverage. The focused changed-area tests passed as part of the fast gate and the exact late-result status projection paths are covered by the current test suite.

## Observations

- `npm run verify` completed docs checks, builds, and every other test lane, but its 3,900-test Server Spec run had one failure in the unrelated `AgentJobDispatchRouteSpecs.RunnerPollEndpoint_ForAgentJob_ExposesOwnerKindAndAgentJobId` fixture. The exact test was rerun directly and passed 1/1; no changed file is involved.
- The added Playwright browser spec was not run by `npm run test:fast` or the repository `verify` script in this environment. Web unit tests, typechecks, and the server-side projection tests passed; the browser scenario remains an additional, non-blocking verification gap.

<promise>PASS</promise>
