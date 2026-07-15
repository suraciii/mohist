# Review Report

## Result: FAIL

Reviewed the live issue, proposal, design, tasks, all candidate changes through `b1d39842a`, and adjacent membership, recovery, query, retry, and artifact paths. The latest candidate fixes the repeated stopped-run activation and adds happy-path terminal/reopen snapshot tests. It still commits active-membership changes before producer snapshots, so event-time lineage can be permanently stale; its bounded discovery can also permanently starve valid gated starts.

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: terminal/reopen epic lineage transaction boundary
  Evidence: Terminal paths save the changed epic status and removed active memberships before restaging Issue/Workflow snapshots (`EpicGrain.cs:714-724`, `:868-873`, `:983-987`); reopen likewise saves reclaimed active memberships before restaging (`EpicGrain.cs:770-774`). Each `SaveChangesAsync` commits its own transaction. Between those writes, an Issue or workflow append reads the prior scalar and persists a historical envelope with the wrong `epicid`; that event cannot be repaired later. If the restage write fails, the membership/status is already committed, the terminal command cannot be replayed from its new state, and the terminal event append has not yet occurred to drive recovery. The added tests assert only final scalar values after successful sequential writes (`EpicMembershipSpecs.cs:490-507`; `EpicReopenSpecs.cs:246-272`). [disallowed:atomic producer-snapshot data safety]
  SuggestedAction: Use one explicit transaction for membership/status, resolver-visible snapshot restaging, and the corresponding durable epic events. Do not expose the new membership truth before its producer snapshots commit.
  Verification: Barrier-test Issue and workflow appends between membership mutation and snapshot staging, and inject a snapshot-stage failure in done, closed, auto-done, and reopen paths. Assert no stale envelope can persist and each transition is retryable or rolls back entirely.
  Status: unresolved

- [ID: item-2]
  Severity: warning
  Scope: bounded gated-start discovery
  Evidence: `FindGatedStartsAsync` always reads only the first `limit * 4` IDs sorted lexically (`WorkflowRunQuerier.cs:94-129`). Corrupt, ungated, or structurally invalid `Created` rows in that fixed prefix are skipped, but the next poll begins from the same prefix. Sixteen skipped rows therefore permanently hide a valid gated start after them; it will never reach `DispatchService` activation (`DispatchService.cs:96-109`).
  SuggestedAction: Use a durable/rotating scan cursor, or persist/query the dispatch gate so the database can select actual candidates without a fixed rejected prefix.
  Verification: Seed more than sixteen corrupt or non-gated `Created` rows followed by a valid gated run, poll repeatedly, and assert the valid run is eventually recovered.
  Status: unresolved

- [ID: item-3]
  Severity: test-gap
  Scope: guarded-start and terminal lineage recovery protocols
  Evidence: The new tests cover only final scalar snapshots for close/reopen. No test covers `FindGatedStartsAsync`, poll batch ordering, malformed-prefix starvation, bound-start concurrency retry, unbound compensation across restart, or fault/interleaving boundaries around the new two-phase terminal writes. The new paths therefore have no regression coverage for their failure semantics.
  SuggestedAction: Add focused querier, dispatcher, grain, and integration specs for these durable boundaries and failure modes.
  Verification: Assert bounded recovery progression, no pre-bind dispatch, correct post-link envelope lineage, and atomic terminal/reopen outcomes under injected save failures.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: bounded batch affiliation persistence retries
  Evidence: The candidate still lacks a spec that injects the changed `DbUpdateConcurrencyException` path for batch workflow snapshot persistence. Existing batch failure coverage uses generic active-membership failures, so it does not prove the three-total-attempt contract.
  SuggestedAction: Add link and unlink specs for one through four workflow snapshot concurrency conflicts while preserving committed membership outcomes.
  Verification: Assert success on every permitted retry and deterministic failure after the third total persistence attempt.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: recovery activation ownership
  Evidence: `WorkflowGrain.OnActivateAsync` independently activates any `Created` gated run (`WorkflowGrain.cs:75-87`), so API/control reads can recover starts outside `DispatchService`'s four-per-poll budget and its redelivery-first ordering.
  SuggestedAction: Decide whether recovery belongs exclusively to scheduled dispatch reconciliation; if so, make grain activation load-only and centralize recovery admission.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: warning
  Scope: rehomed retained links in epic progression
  Evidence: Reopen deliberately retains a link that another active epic owns (`EpicReopenSpecs.cs:194-245`), but later `TryStartNextAsync` evaluates every retained `EpicIssues` row without filtering active ownership (`EpicGrain.cs:898-927`). Starting the reopened epic can therefore call `StartWorkAsync` for an issue owned by another epic. This predates the lineage snapshot change, but route lineage makes the ambiguity visible.
  SuggestedAction: Track a separate epic-progression change to restrict execution candidates to active ownership or reclassify retained historical links.
  Status: pre-existing

- [ID: item-7]
  Severity: warning
  Scope: `EpicGrain` post-commit event persistence
  Evidence: Existing close, reopen, and auto-done paths save authoritative state before the exception-swallowing post-commit append (`EpicGrain.cs:718-724`, `:770-775`, `:870-874`, `:1194-1228`). A failure can leave a transition without its durable epic audit event. This candidate retains rather than introduces that behavior.
  SuggestedAction: Track transactional event persistence for the remaining epic mutation paths separately.
  Status: pre-existing

## Verification

- `npm test` passed: 865 CLI, 1,408 server unit, 2,790 server spec, 22 architecture, 4,653 web, and 1,014 runner tests.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 333 files and 4,653 tests.
- `git diff --check master...HEAD` passed.

<promise>FAIL</promise>
