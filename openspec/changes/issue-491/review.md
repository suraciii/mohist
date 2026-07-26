# Review Findings

## P1: Raw AgentJob failures no longer emit the required public event

`packages/server/src/Mohist.Server/Agent/Grains/AgentJobLineage.cs:84` switches jobs without an agent identity to the new `com.mohist.agent-job.raw.failed` type. This type is outside the approved contract: the issue requires every terminal AgentJob failure to emit `com.mohist.agent.job.failed`, with the same routing standing and the default inbox/Hermes behavior where issue context exists. The raw type is not subscribed by `InboxProjectionHandler` or `HermesIssueNotificationHandler`, so a context-bearing raw job failure cannot produce the required notification; it also bypasses any routing rules for the specified failure event. Do not introduce a separate raw event type without a spec change. Preserve the required event contract and supply the required agent identity on all AgentJob launch paths, or explicitly reject unsupported raw jobs before they become AgentJobs.

<promise>FAIL</promise>
