# Review: issue-619

## Verdict

FAIL — the required real Server-ingress-plus-Node-adapter end-to-end boundary is not implemented by the current tests.

## Must-fix findings

### MF-1 — The claimed cross-component E2E test does not exercise the Server ingress

**Criterion violated:** Issue acceptance criterion 6: “端到端测试同时运行 ingress 与 adapter，验证 outbox 与直接发送路径不会重复”. The ownership spec further requires the harness to send events through the **real Server ingress path** and the real Node adapter handler, and explicitly says shared fixtures or isolated tests are not a substitute.

`packages/mohist-slack/src/cross-component-ownership.test.ts:14-48` defines `routeHarness`, whose `/ingress` handler returns the test's preconstructed `directResult` at line 31. No Mohist Server is started or invoked, no Server ingress route is executed, and no Server database/outbox row is created. The tests at lines 76-110 therefore exercise the real Node adapter and HTTP transport against a fake response, but they do not prove that a blocked DM/channel event causes the Server to persist a nudge and return `responseOwner: server`, nor that a real Server backpressure path returns `responseOwner: adapter` without a durable intent.

The Server spec tests and the Node adapter test are consequently still isolated tests. Replace this harness with a cross-component test that invokes the actual Server ingress HTTP endpoint using the blocked-event fixture/state, feeds that HTTP response through the actual `HttpAdapterTransport` and `SlackAdapter` event handler, and observes the real durable outbox plus instrumented Slack post/ack behavior for both Server-owned and adapter-owned paths. The Server-owned case must verify one Server outbox row and one normal outbox delivery with no direct rejection post; the adapter-owned case must verify no durable nudge and exactly one direct post.

## Review dimensions

- **Issue basis / acceptance criteria — checked, no issue.** I reread the current issue body before reviewing the change, including all seven acceptance criteria and the planning-reset comment.
- **Coverage — FAIL.** Admission, ownership, deduplication, uncertainty/reconciliation, diagnostics, and preservation cases have focused coverage, but the required real ingress-plus-adapter end-to-end criterion is missing as described in MF-1.
- **Correctness — checked, no additional must-fix issue found.** The implemented DM/channel admission ordering, `new task` classification, durable dispatch identity, response-owner branching, and uncertain-delivery reconciliation are consistent with the issue and the focused tests pass.
- **Consistency with surrounding codebase — checked, no issue.** The change reuses the existing Slack outbox, diagnostic service, readiness service, transport, and adapter state machine.
- **Tests / verification — FAIL for the acceptance boundary.** The focused Node suite passed (85 tests), the focused Server unit suite passed (3717 tests), and the focused Server spec suite passed (3010 tests), but those results do not compensate for the missing real cross-component E2E test in MF-1.

## Observations

- `npm run verify` reaches the test suites successfully but fails the repository format check because changed Node files are not Biome-clean (`packages/mohist-slack/src/_adapterTestSupport.ts`, `cross-component-ownership.test.ts`, `status-projection.test.ts`, `transport.test.ts`, and `transport.ts`). This is a repository-quality issue, but it is not counted as a must-fix finding for the issue's functional acceptance criteria.
- Go contract tests could not be run because the Go toolchain is unavailable in this environment. The Node and Server focused suites did pass.

<promise>FAIL</promise>
