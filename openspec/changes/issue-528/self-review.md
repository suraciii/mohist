# Self-Review (pass 2) — Issue 528

Reviewer mode: re-reviewed `proposal.md`, `specs/`, `design.md`, `tasks.json` against the issue body
after the fix commit (`11c112cd2`). Acting as a reviewer only; no artifact other than this file was
modified.

## Resolution of pass-1 findings

- **F1 (was must-fix) — RESOLVED.** T-002 `acceptanceCriteria[1]` now reads "on receiving a
  backpressured result (only) … existing server-enqueued 'rejected' kinds are NOT rendered by the
  adapter, so no Slack reply is duplicated", and the `output` line is scoped to "the backpressured kind
  only". This matches the `description` and `notes`. The contradiction that would have caused duplicate
  Slack replies is gone.
- **N1 — RESOLVED.** T-001 gained a 6th criterion asserting replaceable-progress still merges and
  terminal/failure/user-action rows are neither merged nor dropped across a backpressure episode,
  explicitly covering the issue's AC3.
- **N2 — RESOLVED.** T-003 now `dependsOn: ["T-002"]`; both edit `packages/mohist-slack/src/adapter.ts`
  (handleEvent vs drain) and are serialized. DAG re-checked: T-003(prio 3) → T-002(prio 2) is strictly
  lower; graph is acyclic.
- **N3 — RESOLVED.** design D1 and T-001's description now name the new
  `AgentConnectionStore.ListBackpressuredAsync` query and the `SlackProviderInboxStore` / query
  injection into `SlackOutboxDispatcherService` (the grain's DI unchanged).

## Fresh consistency checks

- **Issue AC → plan:** AC1 (T-001 regression), AC2 (T-001 diagnostic + T-002 rejection), AC3
  (T-001 merge regression), AC4 (T-003 retry split), AC5 (T-003 uncertain + authority), AC6 (T-001
  recovery sweep), AC7 (T-004 gap notice). All seven covered.
- **Spec requirement → task:** all 4 `slack-capacity-backpressure`, 3 `slack-delivery-outcomes`, and 3
  `slack-offline-gap-notice` requirements map to a task; every task maps back to a spec. No orphaned
  requirement, no orphaned task.
- **Non-goals:** no adapter-side persistence (D4 posts via the existing bot-token client, no cache), no
  exactly-once claim, no auto-replay (D6 explicit), no credential/owner/disable work.
- **Adapter-rendered rejection correctness:** backpressure is uniformly adapter-rendered (server returns
  `IngressResult{backpressured}` and does not enqueue), which is correct for both inbox-overflow and
  outbox-overflow — no conditional enqueue path, no duplicate.
- **Spec format:** normative SHALL/MUST, `#### Scenario` with exactly four hashtags, ≥1 scenario per
  requirement. Capability names consistent across proposal/specs/tasks. `tasks.json` is valid JSON.

## Non-blocking observation (not a fix requirement)

- T-004 (clears `OfflineGapAt` in the ingress acceptance path) and T-002 (changes the backpressure
  rejection branch) both touch the ingress route handler in `SlackConnectionRoutes.cs`. They edit
  distinct branches and T-004 already `dependsOn` T-001; under the runner's priority-ordered sequential
  execution there is no conflict, and git would auto-merge the non-overlapping hunks. Flagged only for
  builder awareness; no `dependsOn` change required.

## Verdict

All pass-1 findings are resolved and no new must-fix problem was found. The plan is internally
consistent, covers every acceptance criterion and spec requirement, respects the non-goals, and the
task graph is a valid DAG. Ready to build.

<promise>PASS</promise>
