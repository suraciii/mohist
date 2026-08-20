# Review - Issue 617

This is a re-review. I reread the current issue acceptance criteria before checking the implementation and the OpenSpec proposal, design, task, and specification artifacts.

## Must-fix findings

None.

## Prior finding dispositions

- **Source-less compatibility rejected the pre-change Slack Skill snapshot:** fixed properly. `packages/runner/src/runtime/slack-execution-context.ts` distinguishes a genuinely omitted `executionSource` from an explicit null/value, routes only the omitted case through the bounded legacy parser, and keeps explicit `slack` on the published identity/hash validator. The regression test uses the actual pre-change Skill fixture and digest.
- **Explicit `non-slack` plus Slack context was relabeled as Slack:** fixed properly. `AgentJobInput.ExecutionSource` is nullable for legacy persisted records, `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Dispatch.cs` reconciles only an absent source with trusted Slack context, and explicitly rejects `non-slack` with Slack context. The Server dispatch specs cover both mismatch rejection and legacy Slack reconciliation.
- **Mixed-source follow-up batching could permanently block a Session:** fixed properly. `ChooseFollowupTurnForAssignment` now joins queued inputs only when their effective sources match, and `BeginNextFollowupDispatchAsync` retains a defensive source-consistency check. The follow-up grain regression test proves Slack and non-Slack inputs receive separate turns and dispatches.

## Dimension checks

- **Issue acceptance criteria:** checked, no issue. Slack initial launches and follow-ups use the same Server-resolved versioned Skill and complete Server-owned anchor. The Skill explicitly gives direct human questions priority over silence, rejects acknowledgement-only replies, requires silent recovery from durable state and the Slack thread, and preserves the six documented collaboration rules. Non-Slack envelopes omit Slack Skill, anchor, and Slack system facts. Invalid or tampered current-version contexts, missing contexts, incomplete anchors, source mismatches, unsupported versions, and hash mismatches fail before Runtime invocation.
- **Coverage:** checked, no issue. Tests cover the canonical asset and exact digest, all six rule requirements, same-version drift, initial/follow-up Skill and anchor parity, DM and channel roots, deterministic batched representatives, source-separated follow-up turns, legacy compatibility, malformed source/context pairs, invalid AgentJob and follow-up handling, and non-Slack envelope preservation.
- **Correctness:** checked, no issue. The Server constructs the context from trusted Slack origin and durable provenance, persists the effective bound root, uses the first durable input as a batched representative, and uses the follow-up operation as `dispatchRef`. The Runner validates the exact UTF-8 content hash and published Skill identity before composing the Slack envelope. Normal execution keeps user text, Agent Instructions, configured Skills, and Slack control facts in their intended boundaries.
- **Consistency with surrounding codebase and conventions:** checked, no issue. The change reuses the existing embedded asset catalog, AgentJob dispatch, AgentSession follow-up state machine, Runner control validation, managed Skill resolution, execution-envelope composition, and Agent-owned Slack reply action. It does not modify Slack outbox ownership, reply authorization, thread mapping, or delivery protocol.
- **Tests and verification:** checked, no issue. `npm run test:ci --prefix packages/runner` passed 155 files and 1,680 tests. Runner production and test typechecks passed. `dotnet test packages/server/tests/Mohist.Server.UnitTests/Mohist.Server.UnitTests.csproj --no-restore` passed 3,082 tests. `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore` passed 3,701 tests. Formatting, file-size, and whitespace checks passed, and the embedded asset hashes to `dedf18a796543ade06a9e0ece00c086577153e1e633f868c099b01cf910d641b`.

## Observations

- `BeginNextFollowupDispatchAsync` validates durable Slack provenance before the delivery `try` block in `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.FollowupDispatch.cs`. A malformed pre-existing durable turn could therefore remain queued and retry if its bound root or representative provenance is missing. Current producers construct complete provenance and the acceptance path fails before Runtime invocation, so this is outside the must-fix bar for Issue 617.
- The compatibility parser intentionally accepts a source-less, self-consistent legacy Skill snapshot rather than requiring the current published identity. This preserves the pre-change wire behavior during the bounded rollout; explicit v1 Slack payloads remain pinned to the current Skill identity and digest.
- Server and Runner independently pin the published Skill digest. The current catalog, parity fixture, and full test suites protect the present value; a future cross-project generated fixture could reduce maintenance drift.

## Verdict

**PASS** - no must-fix problems remain; the change is ready to merge.

<promise>PASS</promise>