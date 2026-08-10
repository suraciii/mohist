# Self-Review: issue-561

## Artifacts Reviewed

- `proposal.md` - issue motivation, capabilities, impact, migration, and breaking changes
- `design.md` - source snapshot, build isolation, managed release store, launchers, identity, activation, recovery, migration, and projection ownership
- `tasks.json` - five implementation slices, acceptance criteria, and dependency graph
- `specs/update-source-identity/spec.md`
- `specs/managed-runtime-artifacts/spec.md`
- `specs/update-runtime-consistency/spec.md`
- `specs/update-runtime-recovery/spec.md`

The requested `mo issue show 561 --project proj_f6c141d63b6243bfbb481737b2243b87` command is not supported by this CLI. The supported `mo issue view 561 --project proj_f6c141d63b6243bfbb481737b2243b87` command reports issue 561 as a P0, plan-stage, `in_progress` issue titled `mo update: Server and Runner use the same build from the specified repo-root`; its body is empty. This review therefore uses the issue title, the plan's stated objective, and the current CLI/Server/Runner implementation boundaries.

The preceding plan revision addressed the nine findings in the earlier review on paper. The findings below are remaining contract gaps in the current revision.

## Findings

### 1. P1 - Activation handoff conflicts with launcher reconciliation

`design.md:168` says that after `CandidateStaged` the coordinator publishes `active.json`, lets stable launchers select the candidate, and then restarts the requested services. The recovery contract says that a launcher must invoke reconciliation before starting a target associated with a nonterminal transaction (`specs/update-runtime-recovery/spec.md:19,44-47`), and also says that stable launchers must not start a target while its transaction is unresolved (`specs/update-runtime-recovery/spec.md:149`). The candidate is still nonterminal in `CandidateActivated` and `Verifying` while the coordinator expects those launcher-started processes to run for verification.

The plan does not define an activation lease, an authorized launcher phase, or whether the coordinator or the launcher owns reconciliation during this handoff. A launcher can therefore deadlock behind the transaction lock while waiting for the coordinator, reconcile and roll back a candidate that the coordinator is still activating, or start an unverified candidate despite the no-unresolved-transaction rule. Crash behavior at this exact handoff is also ambiguous.

**Required revision:** Define one executable activation handoff. Specify the state and ownership token that authorizes a candidate launcher to start, how the reconciler behaves while that owner is active, and how a crash transfers ownership to recovery. Add tests for launcher start during `CandidateActivated`/`Verifying`, concurrent reconciliation, and a crash before and after service restart.

### 2. P1 - CLI continuation still has no durable transaction handoff

The design requires the refreshed CLI to continue by transaction ID (`design.md:53`; `specs/update-source-identity/spec.md:54-57`; `tasks.json:T-001`), but the handoff remains illustrative rather than executable. The update command currently defines only `--continue-after-cli-update` and no transaction identifier (`packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:17-33`). `ContinueWithUpdatedCliAsync` forwards only the CLI path and optional repository root (`packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:327-368`), while each process creates a new `JobId` in `UpdateContext` (`packages/cli/Mohist.Cli/UpdateContext.cs:63-70`).

After the CLI slot changes, the continuation can therefore construct a new context, re-resolve the source, create a different transaction/projection lease, or fail to find the previous recovery slot. That violates the source-identity and crash-recovery contract even though the plan says the original context is durable.

**Required revision:** Define a mandatory continuation option and its parsing contract, load the transaction record by that ID before constructing update state, and reject missing, mismatched, terminal, or wrong-slot continuations. Specify how the existing lock, projection nonce, source context, and recovery CLI path are carried across the process boundary. Add tests for normal continuation, missing ID, wrong ID, retry, and crash after CLI-slot replacement.

### 3. P1 - Ownerless persisted jobs cannot be safely migrated as web jobs

`design.md:220` and `specs/update-runtime-recovery/spec.md:163,177-182` require every record without `owner` to be classified as `web` and quarantined. The current persisted model has no owner field (`packages/server/src/Mohist.Server/SystemInfo/SystemUpdateJobState.cs:3-22`), but the same Server store is written by both the web update flow and the CLI outcome endpoint: `RecordCliOutcomeAsync` creates and saves CLI-reported states (`packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs:211-289`), and the route accepts those reports at `/api/system/update/outcome` (`packages/server/src/Mohist.Server/Api/SystemRoutes.cs:36-57`). The CLI actively posts those states through `packages/cli/Mohist.Cli/MohistCliCommands.Update.Outcome.cs:31-49`.

An ownerless `running` or `waiting-for-reconnect` record can therefore be an old CLI projection, not a web-owned mutation. The startup reconciler would mark that projection `rejected` and release its lock, while the plan separately promises that CLI-owned projections are not quarantined. The statement that all current Server-written records are web-owned is not true for the existing storage path, and there is no persisted fact from which the migration can infer the origin.

**Required revision:** Define the legacy-origin migration explicitly. Either add a durable pre-migration discriminator and preserve identifiable CLI projections, or classify ownerless records as an `unknown-legacy` terminal state with an explicit operator recovery path rather than asserting `web` ownership. Define lock handling and behavior for an interrupted legacy CLI update, and test ownerless web, ownerless CLI, terminal, and active records during startup.

### 4. P1 - Crash between lock creation and `Prepared` has no reconciliation path

The write order starts with lock acquisition and then durable `Prepared` (`specs/update-runtime-recovery/spec.md:17,146-155`; `design.md:174`), but no rule covers a process crash after the exclusive lock file is created and before the transaction record exists. The current Server boundary has exactly this interval: `TryAcquireLockAsync` creates the lock file (`packages/server/src/Mohist.Server/SystemInfo/FileSystemSystemUpdateStore.cs:60-77,173-180`), and `StartAsync` persists the initial state only afterward (`packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs:70-91`).

The lock contains a job ID, but without a durable transaction record the reconciler cannot compare candidate and previous target sets or produce the terminal state it is required to resolve before removing a stale lock. A dead process can consequently leave an orphan lock that either blocks all future updates or is removed without a documented transaction outcome. The current process-local semaphore does not cover another process.

**Required revision:** Specify the orphan-lock protocol for the lock-to-`Prepared` crash window. The reconciler must be able to prove owner death, create or persist a diagnostic terminal record for the orphan, and release the lock only after that record is durable, or use a single recoverable acquisition record that makes the interval explicit. Add Linux and Windows kill-point tests for lock creation, missing transaction record, stale owner, live owner, and terminal release retry.

### 5. P1 - The documented bootstrap action is unreachable without a trusted CLI slot

The recovery contract says the stable CLI wrapper must refuse to execute the candidate and return `bootstrap_required` when no trusted recovery slot is runnable (`design.md:176`; `specs/update-runtime-recovery/spec.md:127-144`). The same contract names `mo install cli` as the manual repair operation, but it does not define an external bootstrap executable or a wrapper exception that can execute that command while all trusted slots are unavailable. The current install command registers only `server`, `runner`, and `slack` subcommands (`packages/cli/Mohist.Cli/MohistCliCommands.Install.cs:9-21`), so the new command also needs a bootstrap entry point and source of the replacement binary.

As written, the state that requires manual repair refuses the executable needed to perform manual repair. An operator can be left with no defined path to recreate the recovery slot, especially on Windows where the stable `.cmd` launcher is the service entry point.

**Required revision:** Define the trusted bootstrap path independently of the candidate slot, including the binary/source used by `mo install cli`, wrapper dispatch rules, and Linux/Windows installation behavior. The wrapper must either permit only this narrowly defined bootstrap command or direct the operator to a separate runnable command. Test both slots missing, recovery slot corrupt, and candidate slot present but untrusted.

### 6. P1 - Projection sequence and state-transition checks are not atomic or enumerated

The plan requires the Server to read `projection-lease.json`, reject non-increasing sequences, enforce an invalid-transition rule, and persist the projection (`design.md:222`; `specs/update-runtime-recovery/spec.md:165,184-197`). It does not define the allowed status/stage transition graph or an atomic compare-and-set across the lease's `last accepted sequence`, the local CLI transaction record, and the Server's latest projection. Those are separate persistence surfaces, and the existing Server store only serializes access within one process (`packages/server/src/Mohist.Server/SystemInfo/FileSystemSystemUpdateStore.cs:21-26,43-57`).

Two validly authenticated retries can race so that a lower sequence overwrites a higher sequence, or a terminal failure can be followed by an accepted success if both checks observe the same prior lease. “Invalid transition” is not enough to determine whether cancellation, failure, recovery, and committed states are terminal or which nonterminal stages may follow them.

**Required revision:** Add the complete projection transition table, terminal immutability rules, and one cross-process compare-and-set/lock protocol that advances the lease and projection together. Define the committed-record identity check in that same operation and test concurrent sequences, duplicate payloads, terminal replay, cancellation versus success, and out-of-order delivery.

## Issue Coverage Check

| Issue objective | Planned coverage | Review status |
|---|---|---|
| Build Server and Runner from the selected root | Immutable snapshot, explicit build roots, and isolated dependency projection | Blocked by the undefined continuation handoff (finding 2) and activation/launcher ownership (finding 1) |
| Prove the running artifacts match the target | Artifact-owned identities and exact Runner connection generation | Blocked at the activation handoff where the candidate is started and verified (finding 1) |
| Success only after verified consistency | Candidate manifest and explicit state machine | Blocked by unresolved launcher ownership and non-atomic outcome projection (findings 1 and 6) |
| Failure does not leave a half-update | Rollback, active-target record, lock, and reconciliation | Blocked by orphan-lock recovery and the unresolved activation handoff (findings 1 and 4) |
| Existing installations can adopt the managed runtime | Bootstrap, stable launchers, and recovery slots | Blocked by the unreachable no-slot bootstrap path (finding 5) |
| Server status reflects the authoritative CLI transaction | Owner-tagged projection and startup quarantine | Blocked by ambiguous migration of existing ownerless records (finding 3) |

## Verification Limits

- No product tests or full gates were run; this was a read-only plan review and implementation cross-check.
- `jq empty openspec/changes/issue-561/tasks.json` passed before this review.
- `git diff --check` passed before this review; it must be rerun after the review artifact update.
- `npm run docs:check` is unavailable in this workspace because `tsx` is not installed (`sh: 1: tsx: not found`).
- The issue body returned no acceptance-criteria list; the review used the issue title, P0 plan-stage status, and the plan's stated objective.
- Only `openspec/changes/issue-561/self-review.md` was changed in this review pass.

## Verdict

The plan is not ready to build. The earlier artifact, identity, cancellation, web-ownership, lock, and no-Runner revisions are present, but the current contract still leaves the activation handoff, CLI process continuity, legacy ownership migration, orphan-lock recovery, bootstrap reachability, and concurrent projection ordering undefined at the boundaries that determine whether a same-source update is trustworthy.

<promise>FAIL</promise>
