# Self-Review: issue-561

## Artifacts Reviewed

- `proposal.md` - issue motivation, capabilities, impact, migration, and breaking changes
- `design.md` - source snapshot, build isolation, managed release store, launchers, identity, activation, recovery, migration, and projection ownership
- `tasks.json` - five implementation slices, acceptance criteria, and dependency graph
- `specs/update-source-identity/spec.md`
- `specs/managed-runtime-artifacts/spec.md`
- `specs/update-runtime-consistency/spec.md`
- `specs/update-runtime-recovery/spec.md`
- Current CLI and Server update boundaries referenced by the plan

The canonical issue read was `mo issue view 561 --project proj_f6c141d63b6243bfbb481737b2243b87`. It reports issue 561 as a P0, plan-stage, `in_progress` issue titled `mo update: Server and Runner use the same build from the specified repo-root`; the issue body is empty. This re-review therefore uses the issue title, the plan's stated objective, and the current implementation boundaries as the issue-level acceptance basis.

This is a re-review of the six P1 findings from the preceding review. Each finding was checked against the current plan, and the plan was also checked for regressions introduced by those fixes.

## Previous Findings Disposition

### 1. Activation handoff and launcher reconciliation

**Disposition: fixed.** `design.md:113,133,163-181` defines `ActivationAuthorized`, `CandidateActivated`, and `Verifying`, a live activation lease containing the owner process-start token, the launcher exception for a matching live lease, and stale-owner transfer to reconciliation. `specs/update-runtime-recovery/spec.md:16-60` defines the same handoff and crash behavior. `tasks.json:T-004` requires live/stale handoff, concurrent reconciliation, and kill-point tests.

### 2. CLI continuation lacks a durable transaction handoff

**Disposition: fixed.** `design.md:51-57` requires the exact hidden command and `OpenContinuationAsync` before constructing update state. `specs/update-source-identity/spec.md:72-84` makes `--update-id` mandatory, rejects unknown, terminal, wrong-slot, and already-claimed continuations, and preserves the original lock, lease, source context, and recovery path. `tasks.json:T-001` covers parsing, retry generation claims, and continuation state propagation.

### 3. Ownerless persisted jobs cannot be migrated as web jobs

**Disposition: fixed.** `design.md:227` and `specs/update-runtime-recovery/spec.md:199-232` migrate ownerless records to `owner: "legacy"`; active ownerless records become terminal `legacy-unknown` with an operator action, terminal records retain their status, explicit web records alone become `rejected`, and stale-lock release is durable/retryable. `tasks.json:T-005` requires tests for explicit web, explicit CLI, ownerless active, and ownerless terminal records.

### 4. Crash between lock creation and `Prepared`

**Disposition: fixed.** `design.md:181` and `specs/update-runtime-recovery/spec.md:176-197` define the flushed `acquiring` lock phase, owner liveness proof, `OrphanedLock` diagnostic record, terminal outcome before lock removal, and live/ambiguous-owner fail-closed behavior. `tasks.json:T-004` includes acquisition-before-`Prepared`, orphan-lock, platform, and release-retry tests.

### 5. Bootstrap action unreachable without a trusted CLI slot

**Disposition: fixed.** `design.md:63,183` and `specs/update-runtime-recovery/spec.md:103-126,152-174` define an independently installed bootstrap helper and trusted payload, the restricted `mo install cli` wrapper exception, candidate-slot refusal, payload corruption behavior, and Linux/Windows launcher behavior. `tasks.json:T-004` requires bootstrap-helper tests on both platforms and recovery with both managed slots unavailable.

### 6. Projection ordering and transitions are not atomic or enumerated

**Disposition: fixed.** `design.md:229-231` and `specs/update-runtime-recovery/spec.md:202-248` define the OS-backed cross-process `projection.lock`, write-ahead intent, expected hash/sequence compare-and-set, allowed transitions, terminal immutability, duplicate handling, and durable `Committed`/`RolledBack`/cancellation checks. `tasks.json:T-005` requires concurrent sequence, terminal replay, out-of-order, and crash-intent tests.

## Dimension Checks

### Issue basis

Checked, no issue. The issue body contains no separate acceptance-criteria list; the review uses the stated same-source Server/Runner objective and the plan's explicit source-identity and verified-release contract.

### Coverage

Checked, no issue. The proposal, source-identity spec, artifact spec, consistency spec, and recovery spec cover source selection, immutable build inputs, self-contained Server/Runner artifacts, runtime identity verification, activation, rollback, and reporting. The six previously missing boundary contracts are now represented in the normative specs and task acceptance criteria.

### Correctness

Checked, no issue. The plan prevents source re-resolution after snapshot creation, requires one candidate identity for the full release, refuses success without required runtime readback, keeps launchers behind a live activation lease, quarantines ambiguous legacy state, and serializes outcome projection across processes.

### Codebase consistency

Checked, no issue. The current CLI and Server code still exhibits the pre-change behavior identified by the plan, including per-process job IDs, source-bound continuation, ownerless persisted state, and process-local update locking. The task outputs explicitly replace those boundaries while retaining the repository's fake process/filesystem/service/HTTP/time seams; this is expected implementation work rather than a plan contradiction.

### Task breakdown

Checked, no issue. The dependency order `T-001` through `T-005` follows source identity, artifact staging, identity readback, activation/recovery, and reporting/migration. The acceptance criteria include the relevant Linux/Windows kill points, continuation cases, launcher handoff, migration cases, and projection races.

## Observations

- The issue body is empty, so no narrower issue-specific acceptance criteria were available.
- `npm run verify` remains blocked in this workspace because `tsx` is not installed (`sh: 1: tsx: not found`); no product implementation tests were run for this plan-only review.
- The plan retains two non-blocking open questions about release retention and future dirty-worktree support; both are explicitly outside the first implementation contract.

## Verdict

No must-fix problems remain. The plan is ready to build against the issue's stated objective.

<promise>PASS</promise>
