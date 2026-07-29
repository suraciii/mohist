# Review: Issue 514

## Findings

1. **P1 - Terminal delivery forwards raw tool output to Slack.** The terminal event explicitly includes `output` ([AgentJobLineage.cs:115](../../../packages/server/src/Mohist.Server/Agent/Grains/AgentJobLineage.cs#L115)), and the Slack renderer inserts that value verbatim, apart from generic secret-pattern replacement and truncation ([SlackTerminalDeliveryHandler.cs:78](../../../packages/server/src/Mohist.Server/Infrastructure/Slack/SlackTerminalDeliveryHandler.cs#L78)). The DM-result contract prohibits forwarding raw tool output, not just credentials. A successful Job whose output contains command logs, patches, or internal reasoning will therefore expose that material in Slack. Remove raw `output` from the Slack delivery payload/rendering path; render only an explicitly user-facing result message plus safe structured facts such as status, exit code, and artifact count. Add a regression test proving arbitrary output is absent while the conclusion, evidence, and next step remain usable.

<promise>FAIL</promise>
