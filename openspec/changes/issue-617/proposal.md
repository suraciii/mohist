## Why

The current Slack collaboration Skill allows an Agent to end a turn silently when it has no new information, but it does not explicitly distinguish that case from a human's direct question or define how the Agent should behave after restart, Session recovery, or context compaction. This can leave a person waiting for an answer or produce unnecessary interruption and recovery chatter; the executable Skill should match the documented Slack collaboration contract now.

## What Changes

- Extend the versioned `mohist-slack-collaboration` Skill so a direct human question always receives an active answer, including a concise statement that there is nothing additional to add when appropriate.
- Preserve the prohibition on empty acknowledgements: a direct answer must contain useful response content, while an acknowledgement-only turn may remain silent.
- Define silent recovery behavior after process restart, Session recovery, or context compaction: reconstruct state from durable records and the Slack thread, then continue without announcing the interruption or asking the user how to proceed.
- Keep reply authorship with the Agent's explicit Slack reply action, using the Server-provided reply anchor and producing self-contained conclusions, evidence, and next steps.
- Update the Skill asset identity/hash and contract coverage so these rules are versioned, embedded, and verifiable at dispatch time.
- Leave normal Web, CLI, and Workflow execution unchanged. No breaking changes are required.

## Capabilities

- `slack-collaboration-skill`: Defines the injected, versioned collaboration contract for Slack Agent turns, including mandatory responses to direct human questions, valid silence for non-informational turns, self-contained replies, and silent continuation after restart, Session recovery, or context compaction.

## Impact

- **Server Agent assets and contracts:** Update the embedded `mohist-slack-collaboration` Skill and its version/content-hash contract tests. The existing Server-owned `SlackExecutionContext` remains the source of the reply anchor and dispatch-scoped Slack facts.
- **Runner dispatch:** Continue resolving the Skill inline only for Slack execution and carrying the existing system facts; verify that the updated Skill is delivered without changing non-Slack execution envelopes.
- **Slack response behavior:** Direct questions and recovery turns become more reliable and less noisy. Reply destination selection, reply-action authorization, outbox delivery, liveness projection, and duplicate/reconciliation behavior are unchanged.
- **Configuration and dependencies:** No database migration, public API change, Agent definition change, new dependency, or credential-flow change is expected. Tests remain focused on the Server asset contract and Slack-specific Runner dispatch behavior.
