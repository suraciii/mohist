## Why

Mohist lets users create later issues early, but it does not remember or enforce that an issue may only start after a prerequisite issue has been delivered. This matters now because sequential work chains such as #200 -> #201 -> #202 can be started out of order, causing agents to plan or build against changes that are not yet available on the mainline.

## What Changes

- Add first-class issue-level start prerequisites so users can declare that one issue requires another issue to be delivered before start.
- Compute start eligibility for each issue from its current workflow state and prerequisite issue delivery state.
- Define delivered for prerequisite evaluation as `stage=done`, `status=completed`, and `mergeState=merged`.
- Reject start requests when a prerequisite issue is still waiting for delivery, with an actionable message such as `Issue #201 is waiting for prerequisite #200 to be delivered.`
- Ensure rejected starts do not create an agent session, create or start a `WorkflowRun`, or enqueue `start-pipeline`.
- Expose structured prerequisite and start eligibility data through the API so CLI and Web UI clients do not parse issue body text.
- Show start prerequisites, delivered prerequisites, and concise waiting-for-delivery reasons in issue detail and list/card views.
- Reject circular start prerequisite declarations.
- Keep issue-level start prerequisites separate from task-level `tasks.json` `dependsOn` execution ordering.
- Treat waiting for delivery as start eligibility state, not as `blocked` status, agent failure, session failure, or workflow stage failure.

## Capabilities

### New Capabilities

- issue-prerequisites

### Modified Capabilities

- local-issue-store
- http-api
- cli-interface
- web-ui
- workflow-run

## Impact

- SQLite issue storage needs persistent issue-level start prerequisite records, lookup helpers, and circular declaration validation.
- Shared issue/API types need structured `prerequisites`, `startEligibility`, and `waitingForDelivery` fields for issue list/detail responses.
- Issue start handling in `POST /api/issues/:number/start` needs the shared start eligibility guard before queueing work.
- `AgentRunnerService` `start-pipeline` execution needs the same guard as a backstop for stale or manually queued tasks, before worktree/session/run creation.
- CLI issue create/list/show/start output needs to display waiting-for-delivery state and surface server rejection messages clearly while remaining a thin client.
- Web issue cards, issue detail prerequisite display, Start controls, and frontend API types need to consume server-provided start eligibility data.
- Tests should cover prerequisite declaration, delivered evaluation, start rejection without `start-pipeline`, circular declaration rejection, API response shape, CLI rendering, Web UI rendering, and separation from task-level `tasks.json` `dependsOn`.
