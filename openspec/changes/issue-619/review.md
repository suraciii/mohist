# Review: issue-619

## Review mode

Re-review. I reread the current issue acceptance criteria and the plan/spec artifacts, then checked both findings from the previous review and the regressions introduced by their fixes.

## Verdict

PASS — no must-fix problems remain; the change is ready to merge.

## Previous findings verification

- **MF-1 — clean-checkout end-to-end execution:** Fixed. The Slack application build now builds `packages/server/tests/Mohist.Server.CrossComponentBridge`, the bridge is included in `Mohist.Server.Tests.slnf`/`Mohist.sln`, and the Slack CI job installs .NET, restores the bridge, and runs the normal Slack application gate. The current `npm run test:app -- slack` path rebuilt the Node and bridge outputs and passed all 87 Slack tests.
- **MF-2 — combined DM/channel coverage:** Fixed. `packages/mohist-slack/src/adapter-ownership-http.test.ts` now sends DM, channel-root, and unbound-thread events through the real Server bridge, the real HTTP transport, and the real Node adapter handler. It asserts the respective durable reply anchors, one outbox delivery, no direct rejection post, and zero inbox/session/job side effects. The adapter-owned backpressure case still asserts one direct post and no durable nudge.

## Review dimensions

- **Issue basis / acceptance criteria — checked, no issue.** New DMs, explicit leading `new task` DMs, channel roots, and first mentions in unbound threads are gated only when new work is being admitted. Durable nudges are safe, anchored, deduplicated, and explicitly Server-owned. The legacy no-intent backpressure path remains adapter-owned.
- **Coverage — checked, no issue.** Server specs cover readiness and Connection blocks, marker classification, ordinary DM and bound-thread follow-ups, diagnostic projection, redelivery/concurrency, anchors, and no-execution side effects. Adapter tests cover ownership decoding, acknowledgment timing, malformed outcomes, direct-send failure, and uncertain delivery. The real cross-component harness covers the required DM/channel-root/unbound-thread and direct-fallback matrix.
- **Correctness — checked, no issue.** DM classification recognizes `new task` before consulting the current mapping. Channel admission runs after routing/access/binding decisions but before history import, inbox acceptance, attachment preparation, reservation, or launch. Admission uses `AgentReadinessService`; `unknown` remains admitted, while `not-configured` and `not-executable` block. The authorized diagnostic route exposes the canonical executability result and concrete gaps without putting those details in caller-facing text.
- **Consistency with surrounding codebase — checked, no issue.** The implementation reuses the existing Slack outbox, `UserAction` kind, unique dispatch identity, `client_msg_id` reconciliation, lease/transport boundaries, and existing Disabled discard and follow-up paths. No schema migration or alternate delivery store was introduced.
- **Tests / verification — checked, no issue.** The normal Slack application gate passed with 87 Node tests. The focused Server runs passed the complete available assemblies: 3010 spec tests and 3717 unit tests. The Go contract tests were not runnable because the Go toolchain is unavailable in this environment; this does not reveal a product failure in the reviewed change.

## Observations

- `npm run format:check` currently reports formatting differences in changed TypeScript files: `packages/mohist-slack/src/_adapterTestSupport.ts`, `packages/mohist-slack/src/status-projection.test.ts`, `packages/mohist-slack/src/transport.test.ts`, and `packages/mohist-slack/src/transport.ts`. This is repository quality debt, not a must-fix issue for the acceptance criteria.
- Some pre-existing generic outbox-backed replies in `SlackConnectionRoutes.cs` (for example owner-claim, empty-prompt, and ambiguous-routing replies) still omit `responseOwner`; the adapter therefore treats them as the compatibility `none` outcome. They do not cause duplicate posts because direct sending is now authorized only for `adapter`, and the issue-critical admission-nudge paths explicitly return `server`. This is less explicit than the broad wording in the design decision, but does not make the issue-619 behavior wrong or incomplete.

<promise>PASS</promise>
