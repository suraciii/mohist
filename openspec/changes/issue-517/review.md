# Review: Issue 517

## Findings

1. **P1: Enable replays replies generated while the Connection was disabled.** Disabling prevents `ClaimAsync` from claiming a pending row only while `DesiredState == Disabled` ([SlackOutboxStore.cs](/home/szf/.mohist/projects/workspaces/wr_70319ded1dec458599350de7f864eeaf/packages/server/src/Mohist.Server/Infrastructure/Slack/SlackOutboxStore.cs:212)), but both enqueue paths still insert rows without checking Desired state ([SlackOutboxStore.cs](/home/szf/.mohist/projects/workspaces/wr_70319ded1dec458599350de7f864eeaf/packages/server/src/Mohist.Server/Infrastructure/Slack/SlackOutboxStore.cs:121)). In particular, an AgentJob accepted before Disable emits its terminal event afterward, and `SlackTerminalDeliveryHandler` unconditionally calls `EnqueueRequiredAsync` ([SlackTerminalDeliveryHandler.cs](/home/szf/.mohist/projects/workspaces/wr_70319ded1dec458599350de7f864eeaf/packages/server/src/Mohist.Server/Infrastructure/Slack/SlackTerminalDeliveryHandler.cs:38)). Enable restores `DesiredState` to Enabled, so that pending terminal reply is immediately claimable and is sent to Slack. This violates both the immediate stop on new replies and the explicit requirement that Enable not replay disabled-period messages. Prevent disabled-period deliveries from becoming claimable after Enable while retaining the Job/Session records, and add an end-to-end spec that disables a Connection, emits a terminal delivery, enables it, and verifies that no Slack delivery is claimed or sent.

2. **P2: The replacement route specs dropped required accepted-work preservation coverage.** The change deletes `SlackConnectionLifecycleSpecs`, which was the only issue-517 test that seeded and verified surviving AgentJob and AgentSession rows for Disable/Delete. Its replacement only verifies secrets on Delete ([SlackConnectionApiSpecs.cs](/home/szf/.mohist/projects/workspaces/wr_70319ded1dec458599350de7f864eeaf/packages/server/tests/Mohist.Server.SpecTests/Specs/Slack/SlackConnectionApiSpecs.cs:141)) and never creates an AgentJob, AgentSession, SessionInput, AgentTurn, or attachment. Add HTTP-entry-point specs that create accepted work before Disable and Delete, then assert all of those records remain addressable afterward. This is an explicit acceptance criterion and protects the intended separation between deleting the Connection boundary and deleting Agent work.

## Verification

`npm test` passed in the current change. `git diff --check master...HEAD` passed.

<promise>FAIL</promise>
