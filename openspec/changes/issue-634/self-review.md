# Self-Review — Issue 634 plan (re-review)

Reviewer: pi. I re-read the canonical issue body and acceptance criteria, then
verified the prior review findings against the current `proposal.md`,
`design.md`, `tasks.json`, and all three capability specs. I also checked the
current Connection lookup/deletion, ambiguity claim, interaction route, thread
binding, launch/follow-up, outbox identity, lease, and worker conventions.

## Verdict: FAIL

MF-1 through MF-7 are correctly disposed in the plan text, and the latest MF-7
classification change introduced no must-fix regression. One codebase-consistency
problem prevents the plan from actually satisfying issue Acceptance Criterion
#4: its prescribed selected-Connection lookup cannot detect a soft-deleted
Connection, so the required `unavailable` branch is unreachable as written.

## Must-fix findings

### MF-8 — The prescribed lookup cannot recognize a deleted selected Connection

Issue Acceptance Criterion #4 requires **“selected Connection 缺失或 lease
失效返回 unavailable”**. The corrected plan now intends to satisfy that rule by
calling the “normal project-scoped”
`AgentConnectionStore.GetAsync(ChosenProjectId, ChosenConnectionId)` and treating
a null result as absent/deleted:

- `design.md:257-264`
- `specs/slack-agent-selection-action/spec.md:46-64`
- T-003 in `tasks.json:54-65`

That approach is inconsistent with the current store semantics. Connection
deletion is soft deletion: `AgentConnectionStore.DeleteAsync` sets
`DeletedAt` (`packages/server/src/Mohist.Server/Agent/Services/AgentConnectionStore.cs:378-389`).
But `AgentConnectionStore.GetAsync` queries only by Project and id and does not
filter `DeletedAt` (`AgentConnectionStore.cs:58-67`). Therefore a Connection
deleted after chooser render still resolves as a non-null `AgentConnection`.
The plan's stated “null means absent or deleted” invariant is false.

Concrete failure case: chooser candidate `(ProjectB, ConnectionB)` is durably
snapshotted, then `ConnectionB` is deleted before click. The cleanup removes its
lease and thread mappings and stamps `DeletedAt`, but `GetAsync(ProjectB,
ConnectionB)` still returns the row. The service does not enter the planned
“deleted selected Connection → unavailable” branch; it proceeds to another
classification, most likely lease-unavailable. Although the resulting broad
state may still be `unavailable`, the deterministic deleted-Connection
criterion and its prescribed resolution boundary are not actually implemented,
and a deleted row with any surviving/fake lease context could proceed farther
than the issue permits.

The plan must specify a deletion-aware project-scoped resolution, for example
an active-only lookup or an explicit `DeletedAt` check, and the deleted-selected-
Connection deterministic test must exercise a soft-deleted row rather than a
physically absent row. This is must-fix because leaving the plan unchanged makes
its explicit implementation approach incapable of reliably enforcing issue AC
#4's selected-Connection-missing outcome.

## Previous finding dispositions

- **MF-1 — more than five candidates:** fixed. Two-to-five renders controls plus
  readable text; more than five renders one non-interactive re-mention fallback
  with no truncation, auto-selection, or pagination.
- **MF-2 — selected Connection used prompt-owner lease:** fixed. The selected
  target resolves and validates its own current lease.
- **MF-3 — validity and retention bounds:** fixed. The plan uses five minutes
  and the existing Slack event reconciliation window.
- **MF-4 — prompt-owner current-policy reauthorization:** fixed. Both roles are
  authorized before winner commit under their respective current lease context.
- **MF-5 — cross-Project ownership:** fixed. Complete `(ProjectId,
  ConnectionId)` references survive snapshot, signing, lookup, commit,
  dispatch, and recovery.
- **MF-6 — explicit existing-thread routing:** fixed. The plan distinguishes
  bound follow-up from unbound launch-and-bind under the retained thread anchor.
- **MF-7 — deleted/missing Connection outcome taxonomy:** fixed in the artifacts.
  The issue-required `unavailable` classification now appears consistently in
  proposal, design, action spec, and T-003. MF-8 is not a taxonomy regression;
  it is the codebase mismatch that makes the corrected branch unreachable for
  the repository's actual soft-delete behavior.

## Re-review checks

- **Previous dispositions:** checked every prior must-fix finding. MF-1 through
  MF-7 are represented consistently and no prior must-fix remains textually
  undisposed.
- **Regression check:** checked the MF-7 edits across proposal, design, action
  spec, and T-003. No new coverage, authorization, dispatch, recovery, or
  retention regression was introduced.
- **Pre-existing problem missed earlier:** MF-8 meets the must-fix bar because
  it invalidates the exact implementation mechanism added to satisfy issue AC
  #4. The previous review verified outcome taxonomy but incorrectly assumed
  the named `GetAsync` returned null for deleted rows; tracing the current
  `DeleteAsync` and `GetAsync` implementations exposes the soft-delete mismatch.
- **Task breakdown:** ordering is otherwise sound: T-003 depends on the launch
  extraction and durable chooser state, and T-004 depends on committed live-path
  dispatch. Acceptance criteria are deterministic and broadly verifiable.

## Observations

1. The action spec says the signed payload binds the chooser message identity,
   while Design Decision 3 signs the original message identity and checks the
   chooser presentation identity separately via the acknowledged outbox
   provider identity. The security property is planned, but the terminology
   should be kept precise during implementation.
2. The additive migration needs concrete defaults or a legacy sentinel for
   existing pre-fact rows. T-002 requires additive migration tests and blocks
   execution from incomplete claims, so this is an implementation detail rather
   than a separate must-fix omission.
3. The concurrency spec says “two users” click different candidates, but actor
   binding permits only the original sender. The meaningful winner race is
   same-actor concurrent clicks plus redelivery/failover; those cases are also
   required by the plan.
4. T-003 asks for restart-recovery coverage although the hosted obligation
   worker arrives in dependent T-004. The dispatch core can be tested directly
   in T-003, but task completion should not imply the worker already exists.
5. The plan retains finer visible outcomes for disabled Connections and
   Agent-not-ready setup nudges instead of naming every non-executable case
   literally `unavailable`. These remain resource-free and fit existing product
   behavior; the issue only pins the exact `unavailable` result for missing
   selected Connections and invalid selected leases.

<promise>FAIL</promise>