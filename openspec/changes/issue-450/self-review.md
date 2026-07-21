# Self-Review - Issue #450 Pi Workflow Path

Scope: issue #450 and `openspec/changes/issue-450/{proposal.md,design.md,tasks.json,specs/}`, checked against the issue-designated product/runtime contracts, current Runner/Server APIs, repository architecture, and testing rules. This review modifies no other file.

## Finding

### F-1 High: Startup cannot discover Server streams whose local manifest is missing

The corrected plan makes Runner-local manifests rebuildable at any Server cursor and requires lifecycle recovery before Runner registration/polling (`design.md:166-174`; `specs/pi-workflow-session/spec.md:154-156`; `tasks.json:104,109,170`). That works when a valid/corrupt local file identifies the logical Session, but total root loss or selective deletion leaves startup with no key for a Server point lookup. Current Runner APIs open a Workflow Session only when `(projectId, workflowRunId, sessionName)` or `sessionId` is already known (`packages/runner/src/server/connection.ts:259-274`; `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:253-319`).

No plan artifact assigns a Runner-scoped authoritative inventory route. After total local loss, startup can therefore report ready and poll before discovering admitted Pi/OpenCode turns, contradicting the pre-registration recovery and host test. Add a Server query authenticated/scoped by `runnerId` that enumerates current Workflow Action streams owned by that Runner with logical identity, physical binding, stream ID, cursor, projector checkpoint, and latest lifecycle. Assign the Server contract to T-003, inventory/local reconciliation to T-004, pre-registration composition to T-006, and tests for empty local storage with nonzero admitted Pi/OpenCode streams plus selective manifest loss.

## Structural Checks

- `tasks.json` parses as valid JSON; all seven task IDs and dependencies resolve and the graph is acyclic.
- All referenced spec files and requirement anchors resolve.
- All three proposal capabilities and the issue's seven acceptance criteria are represented.
- Check-stage rejection, OpenCode/Pi checkpoint recovery, stable transcript-turn projection, rebuildable local state, Action-event ownership, lifecycle persistence, and exact schema migrations are otherwise coherent.
- Catalog reporting/UI, Pi AgentJob and Session-command implementation, ACP/RPC, and a generic `AgentRuntime` remain outside scope.

## Verdict

The plan is otherwise implementation-ready, but pre-registration recovery still needs an authoritative Runner-scoped stream discovery contract.

<promise>FAIL</promise>
