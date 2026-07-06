# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs` (`LinkIssuesAsync` batch wake path)
  Evidence: After a `DbUpdateException` on an active-membership insert, `db.ChangeTracker.Clear()` detached the in-memory `EpicRow`, so subsequent items in the same batch used a stale `row.Status` snapshot. If the first open item failed to wake the epic due to a race, the next successful item would insert its link/active row but could not persist the epic status change, leaving the epic `done` with open work. Re-fetching `row` from the DB after clearing the change tracker restores the committed state for the rest of the batch.
  Verification: `dotnet test Mohist.sln --no-build` — 4060 passed, 12 skipped; `dotnet test ... --filter "FullyQualifiedName~Epic"` — 407 passed.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Epic/Domain/EpicLifecycleExceptions.cs:107-108`
  Evidence: `EpicClosedCannotLinkException` message said "reopen to running", but `Reopen` transitions to `idle`, not `running`. Changed to "reopen before linking issues".
  Verification: `dotnet build Mohist.sln` clean; server tests pass.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Grain/EpicBatchMembershipSpecs.cs:23-28` and `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Api/EpicBatchMembershipApiSpecs.cs:13-25`
  Evidence: Class comments still referred to stale task numbering ("T-003") and did not mention the issue-392 wake-up / closed-rejection behavior now covered by these files.
  Verification: `dotnet build Mohist.sln` clean.
  Status: resolved

- [ID: item-4]
  Severity: blocking
  Scope: `design/epic-status-revival.md` (new) and `design/README.md`
  Evidence: Issue acceptance criteria explicitly require decision records for (a) dropping the "archive-only link to terminal epic" capability and (b) the `closed`-rejection decision, to be written to `design/` or an issue comment. No `design/` or `docs/` changes were present in the candidate. Added `design/epic-status-revival.md` with the two decisions and the historical epic #40 handling strategy, and indexed it in `design/README.md`.
  Verification: File created and indexed; `dotnet build Mohist.sln` and full test suites unaffected.
  Status: resolved

## Blocking Items

_None — all blocking findings were repaired and verified._

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Epic/Domain/Epic.Transitions.cs:146-149`
  Evidence: `WakeFromDone` throws `EpicAlreadyTerminalException` when the epic is not `done`. The exception type and message ("Epic is already {status}; cannot transition to running.") are misleading for non-terminal statuses such as `running`, and the exception name implies the epic is terminal. Consider introducing a dedicated `EpicNotDoneException` or similar guard exception. Existing test `EpicWakeUpSpecs.WakeFromDone_OnNonDoneEpic_ThrowsEpicAlreadyTerminal` currently encodes the current behavior.
  SuggestedAction: Add a domain-specific guard exception for misuse of `WakeFromDone` and update the test.
  Status: follow-up

- [ID: item-6]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Api/EpicBatchMembershipApiSpecs.cs`
  Evidence: The batch closed-rejection API mapping is tested (`BatchLink_OnClosedEpic_Returns409EpicClosedCannotLink_NoPerItemOutcomes`), but there is no corresponding API-level spec for the single-link route (`POST /{id}/issues`). The single-link grain path is covered by `EpicWakeUpSpecs.LinkIssueAsync_ClosedEpic_ThrowsEpicClosedCannotLinkException_NoRowsCreated`.
  SuggestedAction: Add an API spec mirroring the batch test for the single-link endpoint.
  Status: follow-up

- [ID: item-7]
  Severity: minor
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Grain/EpicWakeUpSpecs.cs:358-383`
  Evidence: Test `LinkIssue_OnClosedEpic_IsRejected_EvenForAlreadyLinkedIssue` is named as if it asserts rejection for an already-linked issue, but the body actually asserts that re-linking an already-linked issue to a closed epic is idempotent (no throw), and only a *new* issue is rejected. The behavior is correct but the name is confusing.
  SuggestedAction: Rename the test to match the actual assertion, e.g. `LinkIssue_OnClosedEpic_AlreadyLinkedIsIdempotent_NewIssueIsRejected`.
  Status: follow-up

## Pre-existing or Out-of-scope Items

_None._

<promise>PASS</promise>
