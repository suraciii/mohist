# Review: issue-619

## Review mode

Re-review. I reread the current issue body and its planning-reset comment, then verified the previous review's cross-component finding against the current implementation. The new harness now invokes a real Server HTTP route and the real Node adapter handler, but the test integration is incomplete and the combined boundary coverage is narrower than the plan requires.

## Verdict

FAIL — the required end-to-end coverage is not runnable from a clean checkout through the repository's normal test gate, and the real boundary test covers only the DM case rather than the required DM/channel-root/unbound-thread matrix.

## Must-fix findings

### MF-1 — The real Server/adapter E2E test depends on ignored, manually prebuilt outputs

**Criteria violated:** Issue acceptance criterion 6: “端到端测试同时运行 ingress 与 adapter，验证 outbox 与直接发送路径不会重复”; ownership spec requirement “Combined Server ingress and adapter coverage SHALL exercise ownership end to end”; and T-004 acceptance criterion requiring the cross-component harness to run as part of the change's verification.

`packages/mohist-slack/src/adapter-ownership-http.test.ts:67-72` starts the two processes from `packages/server/tests/Mohist.Server.CrossComponentBridge/bin/Debug/net11.0/Mohist.Server.CrossComponentBridge.dll` and `packages/mohist-slack/dist/cross-component-ownership-bridge.js`. Both output directories are ignored build products. The Slack application build in `test-duration.config.jsonc:50-52` only runs `npm run build -w packages/mohist-slack`, and the Slack CI job in `.github/workflows/ci.yml:231-244` installs Node dependencies and runs `npm run test:app -- slack`; it neither installs/configures .NET nor builds `Mohist.Server.CrossComponentBridge`. The bridge is also absent from `Mohist.Server.Tests.slnf`, which is the solution used by the server application build.

Consequently, a clean checkout has no Server bridge DLL when `adapter-ownership-http.test.ts` starts. The test passed locally only after explicitly building the bridge and the Node `dist` output first; that is not evidence that the repository's normal clean test gate verifies the criterion. Wire the bridge build and its runtime prerequisites into the test path (or otherwise make the E2E self-contained and reproducibly invoked), then verify it from a clean checkout without pre-existing ignored outputs.

### MF-2 — The real cross-component harness exercises only a DM, not channel-root and unbound-thread ingress

**Criterion violated:** T-004 acceptance criterion 6: “That cross-component harness covers a Server-owned durable DM/channel-root/unbound-thread nudge with zero direct post and one normal outbox delivery, plus an adapter-owned no-intent backpressure result with exactly one direct post.” This is also the combined-boundary coverage scenario in `openspec/changes/issue-619/specs/slack-ingress-response-ownership/spec.md`.

`packages/mohist-slack/src/adapter-ownership-http.test.ts:31-42` defines one event with `channel_type: 'im'` and no mention or thread context. Both tests at lines 134-171 reuse that DM event; neither sends a channel-root mention nor a first mention in an unbound thread through the real Server route and real adapter handler. The Server channel and unbound-thread tests are isolated Server tests, so they do not establish the required no-duplication boundary across both components for those ingress classifications.

Extend the same real bridge harness to send representative channel-root and unbound-thread events, asserting the appropriate root/incoming-thread anchor, one Server-owned outbox delivery, no direct adapter rejection post, and zero execution-side-effect records for each. Keep the existing adapter-owned no-intent case in the combined harness as well.

## Review dimensions

- **Issue basis / acceptance criteria — checked, no issue.** I reread all seven current issue acceptance criteria and the planning-reset comment before reviewing the implementation.
- **Coverage — FAIL.** The Server admission, diagnostics, ownership, reconciliation, and isolated DM/channel tests cover most product cases, but the combined boundary is not clean-checkout runnable (MF-1) and does not cover all required ingress classifications at that boundary (MF-2).
- **Correctness — checked, no additional must-fix issue found.** The new DM harness does invoke the actual Server ingress HTTP endpoint, feeds the response through the actual `HttpAdapterTransport` and `SlackAdapter` event handler, drains the durable outbox, and distinguishes the adapter-owned fallback. The focused Server admission ordering, durable identity, safe summaries, and adapter ownership branches are consistent with the issue.
- **Consistency with surrounding codebase — checked, no issue.** The implementation reuses the existing Server test factory, Slack ingress routes, outbox state machine, Node transport, adapter handler, and provider-message reconciliation seams.
- **Tests / verification — FAIL.** `dotnet build packages/server/tests/Mohist.Server.CrossComponentBridge/Mohist.Server.CrossComponentBridge.csproj --no-restore -p:SkipWebBuild=true` passed, the Node package typecheck and all 85 Node tests passed after the required outputs had been built, and the focused Server unit/spec assemblies passed (3717 and 3010 tests). However, those results do not show that the new E2E runs from the repository's normal clean Slack gate; the current test setup requires the manually prebuilt ignored bridge output described in MF-1.

## Observations

- `npm run format:check` fails for changed Node files: `packages/mohist-slack/src/_adapterTestSupport.ts`, `adapter-ownership-http.test.ts`, `status-projection.test.ts`, `transport.test.ts`, and `transport.ts`. This is repository quality debt, but it is not counted as a must-fix issue for the functional acceptance criteria.
- Go contract tests could not be run because the Go toolchain is unavailable in this environment. The Node and Server focused suites did pass.

<promise>FAIL</promise>
