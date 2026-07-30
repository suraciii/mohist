# Review: Issue 133

## Findings

1. **[P1] The hot availability endpoint recomputes Readiness for every Agent.** `GET /agents/availability` calls `AgentQuerier.ListAsync` at `packages/server/src/Mohist.Server/Api/AgentDefinitionRoutes.cs:65`. That method hydrates each returned Agent with `AgentReadinessService.GetAsync` at `packages/server/src/Mohist.Server/Agent/Services/AgentQuerier.cs:90-94`, which queries the latest execution for every Agent. The endpoint is polled every five seconds and does not return Readiness, so this adds an N-per-Agent database fan-out to the very query the design separates from the definition list to decouple refresh cadence and control cost. Add a lightweight active-Agent definition read for the summary that does not hydrate Readiness, and cover that the summary path does not issue per-Agent Readiness/history reads.

2. **[P1] The detail definition summary omits the Agent description and does not state the active lifecycle.** The issue spec requires identity as name *and description* and an active/archived state. `packages/web/src/pages/agent-detail/ui/AgentDetailPage.tsx:418-432` renders the name and model but never renders `agent.description`; an Active Agent has no lifecycle indicator anywhere in the page (the Archived badge is only conditional at lines 421-426). Add both fields to the visible definition summary and tests for active and archived definitions.

3. **[P1] List waiting states do not provide a next action.** `packages/web/src/pages/agent-list/ui/AgentListPage.tsx:122-135` renders only raw Availability reason tokens such as `no-online-runner` and counts. The feedback requirement applies to list, detail, and launch surfaces and requires every obstruction class to identify an actionable next step without logs. Reuse or adapt the existing Availability feedback mapping so list rows translate runner offline, capacity/concurrency back-pressure, and dispatch-pending into user-facing guidance with the appropriate next action; add coverage for those list states.

<promise>FAIL</promise>
