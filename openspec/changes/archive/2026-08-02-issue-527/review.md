# Review — issue-527 (Slack files as Agent input)

Re-review after the fix-up commits (`3310ec0be`, `21b5f1a71`). The change was judged
against the issue body, `proposal.md`, `design.md`, the `slack-attachment-entry` spec,
and the `tasks.json` acceptance criteria. Only product code/tests were judged; the
`openspec/changes/issue-527/` artifacts are this workflow's own outputs.

The implementation lands the capability cleanly: the adapter forwards metadata only
(`adapter.ts:157`–`192`), the Server fetches content via the issue-516 bearer pattern
(`ISlackApiClient.cs:48`–`77`), the `Source` column + migration + descriptor read are
additive and correct, premint identities mirror the Web launch/follow-up routes, and the
per-file verdict rides the existing outbox. The two prior blockers and the minors are
resolved. No new blocking issues found.

## Prior findings — status

### F1 (was BLOCKER: binder reorder crash) — RESOLVED

`SlackAttachmentInputBinder.PrepareAsync` (`SlackAttachmentInputBinder.cs:121`–`138`)
previously reordered bound verdicts by positional index (`candidates[index]` over a
`results.Count` loop), which threw `ArgumentOutOfRangeException` on any message mixing a
rejected and an accepted file. The fix replaces the positional access with an id-keyed
`verdictById` dictionary: placeholders whose id appears in the batch are overwritten with
the bound verdict; pre-verdict rejections (ids not in the batch) are left untouched. This
is correct and removes all `candidates[index]`/`bound.Results[index]` positional accesses.

Verified by the new spec `Ingress_launch_with_mixed_oversized_and_valid_file_accepts_valid_and_reports_rejection`
(`SlackAttachmentEntryIngressSpecs.cs`), which sends [oversized, valid] on a DM launch and
asserts the valid file binds (`Source = slack`), the oversized file is not fetched, and the
request returns 200 (it returned 500 before the fix). The full SpecTests suite (3710) is green.

### F2 (was must-fix: ingest conflict deletes shared bytes) — RESOLVED

`AttachmentService.IngestProviderFileAsync` (`AttachmentService.cs:201`–`218`) no longer
calls `DeleteAsync(storagePath)` on the `DbUpdateException` conflict branch. Because
`GenerateStoragePath` is deterministic on the id, the loser's bytes occupy the winner's
content-addressed path; the loser now just adopts the winning row. This is correct — the
bytes are byte-identical content at the same id-keyed path, so leaving them is a storage-level
no-op, and a destructive delete would have permanently `NotReadable`-ed the winning row under
concurrent same-message delivery. The existing idempotency unit test
(`IngestProviderFileAsync_IsIdempotentOnDeterministicId_...`) still passes (sequential path
unchanged); the conflict branch is only reachable under true concurrency.

### Minors — RESOLVED

- Stray indentation drift across `SlackConnectionRoutes.cs` (launch/follow-up blocks,
  prompt guards, parameter lists, factory `new(...)` calls, `SlackIngressBody`) is restored
  to the surrounding indent. Build is warning-clean under `TreatWarningsAsErrors`.
- Both launch early-reject branches (DM `:1185`, channel root `:1897`) now call
  `AttachmentBinder.RollbackAsync`, mirroring the follow-up path. No leak existed before
  (nothing is bound when `AcceptedCount == 0`), but the two paths are now symmetric.

## New check — no blocking issues

- **`AttachmentIds` includes rejected ids.** `SlackAttachmentBinding.AttachmentIds`
  (`:172`) returns every result id (accepted + rejected) and is forwarded to
  `LaunchConnectionAsync` as `attachmentIds` → `AgentLaunchCoordinatorRequest.AttachmentIds`.
  Verified this is consumed **only** by the idempotency fingerprint
  (`AgentLaunchCoordinatorTypes.cs:238`–`242`); the actual binding uses `candidates` and
  `AcceptedDescriptors`, so the Agent only ever sees accepted files. The ids are
  deterministic per message+file, so including rejected ids in the fingerprint is stable
  across redelivery and does not cause false conflicts. Not a defect.
- **`BuildAttachmentAck` indexes `files[index]` by result position** (`:2003`–`2010`).
  Safe because every call site passes the `body.Files` the binding was prepared from, and
  `PrepareAsync` appends exactly one result per file (`results.Count == files.Count`). Latent
  fragility if a future caller passes a mismatched binding, but no current call site does.
- **Spec coverage.** All four dispatch paths are wired; the binder is the single shared
  pipeline. Coverage spans channel-root launch, DM attachment-only launch, DM redelivery,
  DM follow-up, follow-up oversized rejection, and the new mixed accept/reject case. The
  thread-reply follow-up shares `RouteFollowupAsync` with the DM follow-up, so it is covered
  transitively.
- **Credential isolation.** Bot token is decoded only inside the fetch call, `url_private`
  is consumed and discarded inside `OpenFileContentAsync`, and the stored attachment id is an
  opaque `StableToken` output (no embedded Slack file id). The "no Slack secrets in record /
  observation / transcript" invariant holds.
- **Build/test.** `Mohist.sln` warning-clean; Server Unit 1747/1747, Server Spec 3710/3710,
  Server Arch 51/51.

## Verdict

Both blockers and the minors are fixed and verified; no new blocking issues found.

<promise>PASS</promise>
