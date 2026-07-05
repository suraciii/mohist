# Review Report

## Verdict

FAIL. The candidate implements most of issue 94's surface area and the automated suites pass, but there are unresolved behavior and contract defects in batch membership, event persistence, and web sorting. These are not safe review-time repairs because they affect product behavior, public response contracts, and persisted audit history.

## Blocking Items

1. Batch link conflict can persist an event for a link that did not commit.

   `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:206` records `EpicIssueLinked` before inserting the membership rows. If `db.SaveChangesAsync()` fails on the active-membership insert, the `DbUpdateException` conflict branch at `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:231` clears the change tracker, reports a conflict, but still calls `PersistEpicEventsAsync(domain, pending, now)` at `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:242`. That creates an audit timeline entry for a link that was not actually created, violating the activity timeline requirement that issue link/unlink entries describe real mutations (`openspec/changes/issue-94/specs/epic-activity-timeline/spec.md:48`). Drop pending events on failed commit paths, or only drain/persist domain events after the state write succeeds.

2. Batch HTTP results can lose the original requested identifier when number and id resolve to the same issue.

   `MergeBatchOutcomes` groups grain outcomes by `IssueId` and reuses the first outcome for every requested identifier that resolved to that issue (`packages/server/src/Mohist.Server/Api/EpicRoutes.cs:403` and `packages/server/src/Mohist.Server/Api/EpicRoutes.cs:432`). A request like `["5", "issue_x"]` can therefore return two entries whose `identifier` is both `"5"`, even though the second entry corresponds to a distinct requested identifier. This violates the batch HTTP contract that the response include one outcome entry per requested identifier (`openspec/changes/issue-94/specs/epic-batch-membership/spec.md:82`). The merge layer should preserve the caller's identifier when projecting a grain outcome back to each requested token.

3. Changing only the web sort field can show a selected sort while the server still returns default ordering.

   The list page keeps `sortField` and `sortDir` independently and passes `sort: sortField` with `dir: undefined` when the user changes only the field (`packages/web/src/pages/epics/ui/EpicListPage.tsx:379` and `packages/web/src/pages/epics/ui/EpicListPage.tsx:384`). The server falls back to default ordering whenever either token is missing (`packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:127`). That means selecting `Priority` or `Updated` alone changes the visible control state but does not reorder according to the selected sort, violating the web sort-control scenario (`openspec/changes/issue-94/specs/epic-list-query/spec.md:54`). The UI should either send a valid default direction with any selected field, or model the sort as a single valid field/direction selection.

## Follow-up Items

1. The candidate exposes a `created` sort mode in both web and server (`packages/web/src/pages/epics/ui/EpicListPage.tsx:351`, `packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:123`), while the spec only defines `priority` and `updated` (`openspec/changes/issue-94/specs/epic-list-query/spec.md:16`). Either remove it from the issue 94 deliverable or update the spec and tests to make the expanded public contract intentional.

2. `GET /epics/{id}/events?limit=` is passed directly into `Take(limit)` with no validation (`packages/server/src/Mohist.Server/Api/EpicRoutes.cs:135`, `packages/server/src/Mohist.Server/Infrastructure/Data/Events/EventStore.cs:153`). The OpenSpec does not define invalid limit behavior, so I did not count this as blocking, but a bounded positive range would make the endpoint more predictable.

3. Regression coverage should add cases for mixed number/id request identifiers resolving to the same issue, failed active-membership insert not appending an `issue-linked` event, and changing the web sort field without manually choosing a direction.

## Verification

- Read issue details with `mo issue show 94 --project-id proj_f6c141d63b6243bfbb481737b2243b87`.
- Reviewed the issue 94 proposal, design, tasks, self-review, all delta specs, and the changed server/web/migration/test files in the candidate snapshot.
- Ran `npm test`: passed, 65 files and 908 tests.
- Ran `npm run typecheck -w packages/web`: passed.
- Ran `npm run test:run -w packages/web`: passed, 271 files and 4338 tests passed with 1 skipped.

<promise>FAIL</promise>
