# Self-Review — Issue 528

Reviewer mode: reviewed `proposal.md`, `specs/`, `design.md`, `tasks.json` against the issue body
(User Voice, Product Shape, Domain Model, 7 Acceptance Criteria, Non-Goals). Acting as a reviewer
only; no artifact other than this file was modified.

## Coverage check (issue Acceptance Criteria → plan)

- AC1 (accepted inputs & final results not dropped under pressure) — preserved from #514; T-001
  acceptance asserts recovery does not delete/alter accepted entries or terminal rows. OK (regression).
- AC2 (Degraded(Backpressured) + reject new input + reason visible) — T-001 (diagnostic state) + T-002
  (visible rejection). OK.
- AC3 (replaceable merge; terminal/failure/user-action not merged or dropped) — merge/no-drop is
  existing #514 behavior this issue does not change; T-001 covers the no-drop half through recovery.
  See note N1.
- AC4 (explicit failure safe retry, no duplicate) — T-003 adapter retry-vs-uncertain split. OK.
- AC5 (unknown → Delivery uncertain, no blind resend, execution result unchanged) — T-003 (uncertain
  surfacing) + `slack-delivery-outcomes` "transitions never reclassify" requirement, locked in T-003
  acceptance. OK.
- AC6 (backlog recedes → auto-recover, no rebuild) — T-001 recovery sweep. OK.
- AC7 (long offline → prompt possible gap, user resends) — T-004. OK.

All three capabilities have specs; every spec requirement maps to a task; every task maps back to a
spec. Spec format is correct (normative SHALL/MUST, `#### Scenario`, ≥1 scenario per requirement).
Non-goals are respected (no adapter persistence, no exactly-once claim, no auto-replay, no
cred/owner/disable work). Capability names are consistent across proposal/specs/tasks.

## Findings

### F1 (must fix) — T-002 acceptance criterion contradicts its own description/notes and would cause duplicate Slack replies

`tasks.json` T-002:
- `description` scopes adapter rendering to "user-facing rejection kinds (backpressured, and any
  future can't-enqueue kind)".
- `notes` states "only the can't-enqueue (backpressure) case is rendered by the adapter. … to avoid
  double rendering".
- but `acceptanceCriteria[1]` asserts: "The adapter, on receiving a backpressured **(or rejected)**
  result, posts the reason … via runtime.web".

The server **already enqueues** a Slack reply for existing `rejected` results (empty-prompt and
agent-needs-setup at `SlackConnectionRoutes.cs` ~1062–1063 and ~1088) and the adapter today discards
the `IngressResult` (`adapter.ts:110` `void result`), so only the enqueued reply reaches the user. If
a builder satisfies acceptance[1] literally and renders `rejected` too, the user receives **two**
messages for every existing rejection path — the exact "blind duplication" this issue exists to
prevent. The criterion as written is not satisfiable correctly. Fix: restrict acceptance[1] (and the
`output` line "rendering rejection kinds") to the `backpressured` kind only, matching the description
and notes; or explicitly state that server-enqueued `rejected` kinds are NOT adapter-rendered.

### N1 (note, not blocking) — AC3 replaceable-merge is not re-tested by this plan

The replaceable-progress merge and terminal-no-drop are #514 behavior this issue preserves rather
than rebuilds. T-001 verifies no-drop through the recovery transition but does not re-exercise the
merge itself. Acceptable since the code path is unchanged, but the fixer may want one regression spec
asserting merge still holds after a backpressure episode so AC3 is explicitly covered.

### N2 (note, not blocking) — T-003 has no dependency on T-002 despite both editing `packages/mohist-slack/src/adapter.ts`

T-002 edits `handleEvent`, T-003 edits `drain` (different functions, same file). With strict
priority-ordered sequential execution this is fine and the hunks do not overlap (git auto-merges). If
the runner ever parallelizes independent tasks, add `T-003 dependsOn: ["T-002"]` to serialize adapter
edits. Not a DAG error today (T-003 genuinely does not consume T-002's output).

### N3 (clarification, not blocking) — recovery sweep's connection-enumeration source is under-specified

Design D1 / T-001 say the sweep iterates "Degraded(Backpressured) Enabled Connections" but neither
names where that list comes from. `SlackOutboxDispatcherService` currently injects only
`SlackOutboxStore`/`IDeadLetterStore`/time/options — it has no way to enumerate Connections and no
`SlackProviderInboxStore`. The builder will need to add a backpressured-connection query (likely on
`AgentConnectionStore`) and inject the inbox store + that query into the dispatcher service. Worth
spelling out, but inferable.

## Verdict

One must-fix defect (F1): an acceptance criterion that contradicts its task and would introduce
duplicate Slack replies. The rest are notes/clarifications. The plan is not ready to build until F1
is corrected.

<promise>FAIL</promise>
