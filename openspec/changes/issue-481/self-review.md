# Self-Review - Issue 481

Reviewed `proposal.md`, `design.md`, `tasks.json`, and all capability specs against the issue and the current Activity/Event implementation.

## Findings

### P1 - The planned Runner snapshots contradict the Activity Project-scope contract

The corrected plan makes `ActivityEvidenceAssembler` include `RunnerStatusService` snapshots and requires T-001 to prove project isolation (`design.md:34-40`, `tasks.json:T-001` acceptance criteria 1 and 4). The issue and `activity-list` spec both require `activity list` to be Project-scoped (`issue` acceptance criteria; `specs/activity-list/spec.md:24-32`).

Current Runner status cannot satisfy that isolation: `RunnerRegistryGrain.ListEligibleRunnersAsync(projectId)` ignores its `projectId` argument and returns every registered runner (`packages/server/src/Mohist.Server/Runner/Grains/RunnerRegistryGrain.cs:137-140`). `RunnerStatusService` deliberately documents the same fact: runners are global execution resources, `RunnerInfo.ProjectId` does not bind them to a project, and the projected scope is always `global` (`RunnerStatusService.cs:130-134`). Therefore two Project Activity reads will receive the same Runner snapshot entries. A test that asserts full collection project isolation will fail; one that permits them will leave the API's scope semantics undocumented.

**Required fix:** decide and specify the Runner visibility rule. Either exclude global Runner snapshots from the Project-scoped Activity collection until there is a durable project association, or retain them as explicitly `global` context with a scope field and state that Project scope filters only project-bound recorded/snapshot entries. Update the proposal, `activity-list` and separation specs, design, T-001/T-002 acceptance criteria, and source/test plan so the per-entry scope rule and the project-isolation assertion agree. Do not claim that all Runner snapshots are Project-isolated under the current registry implementation.

## Verified Correct

- The Activity plan now uses `ProjectEventFeedAssembler` for persisted Issue, WorkflowRun, and AgentSession history, instead of narrowing the command to live session cards.
- The plan makes recorded history and current snapshots distinguishable through a mandatory `provenance` field.
- The Event tail and dead-letter migration preserves the current server-side match compiler, NDJSON/cancellation behavior, and operator credential protections.
- The task graph is valid JSON and has a strictly ordered acyclic dependency chain.

## Verdict

The Activity source contract is substantially improved, but global Runner visibility remains incompatible with the claimed Project-isolation behavior.

<promise>FAIL</promise>
