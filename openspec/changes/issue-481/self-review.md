# Self-Review - Issue 481

Reviewed `proposal.md`, `design.md`, `tasks.json`, and all capability specs against the issue and the current Activity/Event implementation.

## Findings

### P1 - The Activity plan does not deliver the requested persistent cross-domain Activity read

The issue defines `mo activity list` as a Project-scoped, persistent, cross-domain record for recent user-understandable changes across Issue, WorkflowRun, AgentSession, and Runner. The plan instead makes `GET /api/projects/{projectRef}/activity` return only `AgentActivityFeedAssembler.GetActivityAsync(...).Sessions` (`design.md:31-35`; `tasks.json:T-001` acceptance criterion 1), explicitly dropping the existing feed's `summary` and `waiting` companions.

That source is not a persisted Activity history: `AgentActivityFeedAssembler` queries Project-labeled AgentSessions ordered by `CreatedDescending`, reconciles their current state, and derives their latest transcript preview at read time (`packages/server/src/Mohist.Server/AgentOps/Services/AgentActivityFeedAssembler.cs:87-109`). Its returned `ActivityCardDto` is a session card; it has Issue/workflow/session context but no Runner activity record (`packages/server/src/Mohist.Server/Sessions/AgentSessionReadModels.cs:296-315`). The only current Runner fact is capacity inside `ActivityDto.Summary`, and `Waiting` is a separate current Issue projection (`AgentRoutes.cs:34-39`); both are removed by the proposed collection endpoint.

As a result, T-001/T-002 can satisfy their own snapshot-array tests while failing the user-visible Activity contract. The specs also conceal the gap by only asserting a finite re-readable collection, not coverage of the promised Activity facts.

**Required fix:** resolve the Activity source and public item contract before implementation. The plan must either identify an existing persisted cross-domain Activity record source and define how Issue, WorkflowRun, AgentSession, and Runner entries are represented and bounded, or explicitly obtain a scope/product correction that limits `activity list` to AgentSession cards. Then align the proposal, `activity-list` spec, design, and T-001/T-002 acceptance criteria; do not implement the endpoint as `ActivityDto.Sessions` while claiming it is the required Activity history.

## Verified Correct

- The Event tail plan preserves the current server-side match compiler, post-subscription NDJSON behavior, and cancellation boundary.
- The dead-letter migration retains the existing local credential and loopback protections rather than moving them into an anonymous Event read.
- The task graph is valid JSON and has a strictly ordered acyclic dependency chain.

## Verdict

The Event/dead-letter migration is planned coherently, but the unresolved Activity data-model mismatch blocks implementation.

<promise>FAIL</promise>
