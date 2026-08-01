# Review — issue-527 (Slack files as Agent input)

Reviewed against the issue body, `proposal.md`, `design.md`, the `slack-attachment-entry`
spec, and the task acceptance criteria in `tasks.json`. Only the product code/tests were
judged; the `openspec/changes/issue-527/` artifacts are this workflow's own outputs.

The change generally lands well: the adapter forwards metadata only (`adapter.ts:157`–`192`),
the Server-side fetch reuses the issue-516 bearer pattern (`ISlackApiClient.cs:48`–`77`),
the `Source` column + migration + descriptor read are correct and additive, the premint
identity formulas mirror the Web launch/follow-up routes exactly, and the per-file ack is
surfaced through the existing outbox. But two defects must be fixed before merge.

## F1 — BLOCKER: `SlackAttachmentInputBinder.PrepareAsync` reorder loop throws on any mixed accept/reject message

`packages/server/src/Mohist.Server/Api/SlackAttachmentInputBinder.cs:121`–`145`.

The per-file loop builds two lists that grow at different rates:
- `results` gets **one entry per file unconditionally** (every branch — oversized, unsupported,
  bot-token-missing, fetch-failed, and the candidate/placeholder branches — calls `results.Add`).
- `candidates` gets an entry **only** when a file reaches the candidate stage (ExistsAsync hit,
  or a successful fetch+ingest).

So `results.Count == files.Count` always, and `results.Count > candidates.Count` whenever the
message has at least one pre-verdict or fetch rejection. The trailing reorder loop then runs:

```csharp
for (var index = 0; index < results.Count; index++)
{
    var existing = results[index];
    if (existing.Id != candidates[index])   // <-- index can reach candidates.Count
```

When `index >= candidates.Count` (guaranteed to happen once `results.Count > candidates.Count`),
`candidates[index]` throws `ArgumentOutOfRangeException`.

**Trigger (exactly a named spec scenario):** a Slack message carrying two files where one is
rejected (oversized / unsupported-type / not-readable) and one is accepted. Spec
`spec.md:77`–`81` ("An oversized file is rejected while a valid one is accepted") and the T-003
acceptance criterion ("each rejected file is reported, not silently dropped") require this to
work and to report both results. It instead crashes the ingress request. The crash also hits the
attachment-only-with-text path whenever files are mixed.

**Why the suite is green:** every attachment spec/unit test uses a *single* file
(`SlackAttachmentEntryIngressSpecs.cs` — five tests, all one-file; the only multi-file test is
`SlackAttachmentInputBinderTests.cs:26` which constructs the binding by hand and never calls
`PrepareAsync`). No test exercises `PrepareAsync` with ≥1 rejection *and* ≥1 candidate, so the
bug is unobserved.

**Fix direction:** reorder by id, not by positional index. `ValidateAndBindAgentInputAsync`
returns `bound.Results` ordered by the submitted `candidates` order; map each candidate's
verdict back to its placeholder position in `results` by matching `Id` (a dictionary
`candidateId -> bound verdict`), and leave non-candidate (pre-verdict rejected) entries as-is.
Remove the `candidates[index]` / `bound.Results[index]` positional accesses entirely.

## F2 — must fix: `IngestProviderFileAsync` conflict path deletes the winning row's bytes

`packages/server/src/Mohist.Server/Issue/Services/Attachments/AttachmentService.cs:201`–`212`.

`GenerateStoragePath` is deterministic on the id (`FileSystemAttachmentStorage.cs:42`–`47`:
`{projectId}/{attachmentId}/content`), so the same `deterministicId` always resolves to the same
storage path. On a `DbUpdateException` (unique-PK collision from a concurrent same-message
delivery — the scenario `self-review.md:37` explicitly commits to handling), the handler does:

```csharp
catch (DbUpdateException)
{
    await _storage.DeleteAsync(storagePath, CancellationToken.None);
    var winning = await LoadRowAsync(...);
    return ToUploadResult(winning);
}
```

`storagePath` is the **same path the winning row's bytes occupy**. Deleting it removes the
winner's content. The returned `winning` row now references a missing file: every later
`ReadMetadataAsync` returns null → the file verdicts `NotReadable` forever. Worse, redelivery
cannot self-heal, because `PrepareAsync`'s `ExistsAsync` short-circuit sees the (present) row
and never re-fetches/re-writes. Net effect: a permanently broken attachment from a race the
design claimed to close.

The `ExistsAsync` check-then-insert (`AttachmentService.cs:154`–`162`) does not make this
unreachable — two concurrent calls can both observe "absent" before either inserts.

**Fix direction:** on the conflict branch, do **not** delete shared storage. Either (a) leave
the loser's bytes in place (they are byte-identical content at a content-addressed-by-id path,
so they are the winner's bytes too), or (b) write to a unique per-attempt path and let the row's
`StoragePath` be authoritative. Option (a) is the smaller change and matches the
"insert-if-absent is a storage-level no-op" intent in D3.

## Minor (non-blocking)

- **Indentation drift in `SlackConnectionRoutes.cs`.** The diff re-indents large existing blocks
  with stray leading spaces (e.g. the launch block `:1164`–`1277`, follow-up call `:2057`–`2076`,
  `SlackIngressBody` `:2249`–`2257`). Not a warning under `TreatWarningsAsErrors`, but it pollutes
  the diff and future blame; restore the original indentation where only a parameter/line changed.
- **Inconsistent rollback on the launch early-reject.** The DM/channel launch sites' "no usable
  file, no text" branch (`SlackConnectionRoutes.cs:1185`–`1198` and `:1900`–`1912`) returns
  without calling `AttachmentBinder.RollbackAsync`, while the equivalent follow-up branch
  (`RouteFollowupAsync:694`–`699`) does. No leak in practice (when `AcceptedCount == 0` there are
  no newly-bound ids to unbind; ingested-but-rejected rows expire via pending TTL), but mirroring
  the follow-up path's rollback keeps the two paths obviously symmetric and safe if a future
  change binds before verdict.

## Verdict

F1 breaks a named spec scenario and the T-003 acceptance criteria with an untested code path;
F2 violates the concurrency idempotency guarantee the design commits to. Both must be fixed.

<promise>FAIL</promise>
