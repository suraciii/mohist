# Self-Review — Issue 634 plan (re-review)

Reviewer: pi. I re-read the canonical issue body and acceptance criteria, then
verified every prior must-fix disposition against the current `proposal.md`,
`design.md`, `tasks.json`, all three capability specs, and the relevant current
Connection-store, lease, thread-binding, interaction-route, outbox, launch, and
recovery conventions.

## Verdict: PASS

No must-fix problem remains. The prior MF-8 failure is correctly disposed, the
fix matches the current codebase's soft-delete semantics, and it introduces no
must-fix regression. The plan is ready to build.

## Must-fix findings

None.

## Previous finding dispositions

- **MF-1 — more than five candidates:** fixed. Two-to-five candidates render
  signed controls plus readable text; more than five renders exactly one
  non-interactive re-mention fallback with no truncation, automatic choice, or
  pagination.
- **MF-2 — selected Connection used the prompt-owner lease:** fixed. The chosen
  target resolves and validates its own current lease under
  `connection:{ChosenProjectId}:{ChosenConnectionId}`, and the prompt-owner
  lease is not accepted as a substitute.
- **MF-3 — validity and retention bounds:** fixed. The action lifetime is the
  issue-pinned five minutes, and finished records use the existing
  `SlackProviderOptions.SlackEventRetentionWindow` rather than a new long-term
  retention regime.
- **MF-4 — prompt-owner current-policy reauthorization:** fixed. Before winner
  commit, both prompt owner and selected Connection are re-authorized under
  their respective current lease contexts and mutable policy state; either
  denial is resource-free and visible.
- **MF-5 — cross-Project ownership:** fixed. Candidate identity remains the
  complete `(ProjectId, ConnectionId)` pair through discovery, durable
  snapshot, signing, lookup, lease targeting, CAS, dispatch, identity
  allocation, persistence, and recovery.
- **MF-6 — explicit existing-thread routing:** fixed. The durable ambiguity and
  dispatch kinds distinguish root launch, selected bound follow-up, selected
  unbound launch-and-bind under the retained thread anchor, and multi-bound
  reply follow-up.
- **MF-7 — missing/deleted selected Connection taxonomy:** fixed. An absent or
  soft-deleted selected Connection is `unavailable`; stale/no-longer-valid is
  reserved for an active Connection whose snapshotted workspace or required
  thread facts changed.
- **MF-8 — soft-deleted selected Connection lookup:** fixed. The plan now calls
  `AgentConnectionStore.GetAsync(ChosenProjectId, ChosenConnectionId)` and
  explicitly rejects `null` or non-null `DeletedAt` before binding, lease,
  policy, executability, or commit checks (`design.md:254-274`,
  `specs/slack-agent-selection-action/spec.md:44-71`, T-003 in
  `tasks.json:54-65`). This matches the repository: `GetAsync` does not filter
  `DeletedAt` (`packages/server/src/Mohist.Server/Agent/Services/AgentConnectionStore.cs:58-67`),
  while `DeleteAsync` stamps `DeletedAt` (`AgentConnectionStore.cs:378-391`).
  T-003 also requires a real soft-delete test and surviving fake downstream
  artifacts, proving the deletion check's precedence rather than merely
  testing a physically absent row.

## Re-review checks

- **Every prior finding:** checked. MF-1 through MF-8 are disposed consistently
  across proposal, design, capability specs, and task acceptance criteria.
- **Fix correctness:** checked. MF-8's explicit `DeletedAt` predicate reaches
  the issue-required `unavailable` outcome under the codebase's actual normal
  deletion path and closes the concrete failure case from the prior review.
- **Regression check:** checked. The MF-8 edits only sharpen selected-Connection
  resolution and deterministic coverage. They do not weaken candidate snapshot
  validation, cross-Project attribution, per-Connection lease/policy checks,
  dispatch classification, single-winner CAS, recovery, or retention.
- **Task breakdown:** checked. T-001 extracts the reusable launch boundary;
  T-002 creates the durable chooser authority; T-003 depends on both and adds
  acceptance, CAS, and live dispatch; T-004 depends on T-003 and adds hosted
  recovery/cleanup. Each task has deterministic acceptance criteria and named
  regression suites.
- **Artifact integrity:** checked. `tasks.json` parses, the task spec anchors
  name existing requirement headings, and `git diff --check` reports no
  whitespace errors in the post-review plan changes.
- **Newly found must-fix problems:** none.

## Observations

1. The action spec describes the signed payload as binding the chooser message
   identity, while Design Decision 3 signs the original message identity and
   enforces chooser presentation identity separately through the acknowledged
   outbox `ProviderMessageIdentity`. The security property is covered, but
   implementation and documentation should preserve this terminology
   distinction.
2. The additive migration will need concrete defaults or a legacy sentinel for
   existing pre-fact rows. T-002 already requires additive migration coverage
   and forbids incomplete claims from executing, so this is an implementation
   detail rather than a missing issue requirement.
3. The concurrency attribution spec says “two users” click different choices,
   although actor binding permits only the original sender. The meaningful
   winner race is same-actor concurrent clicks plus interaction redelivery and
   failover; those cases are also explicitly required.
4. T-003 includes direct restart-recovery coverage even though the hosted
   obligation worker is delivered by dependent T-004. The dispatch/recovery
   core can be tested directly in T-003; task completion should not imply that
   the hosted worker already exists.
5. Disabled Connections and Agent-not-ready cases retain the existing
   `connection_disabled` and setup-nudge presentations rather than naming every
   non-executable case literally `unavailable`. They remain visible,
   resource-free outcomes, while the issue's exact `unavailable` requirements
   for absent/deleted Connections and invalid selected leases are explicit.

<promise>PASS</promise>