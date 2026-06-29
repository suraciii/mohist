# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:165, packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx:2326, docs/web-ui.md:172
  Evidence: The issue acceptance criteria explicitly call out `Active`, `Start`, and `No linked issues` copy that can preserve the old mental model. The list page uses the clarified `Start next issue` label, but the Epic detail linked-issue row still renders a bare `Start` button for starting an individual linked issue (`{startPending ? 'Starting...' : 'Start'}`), and the test suite pins that exact label with `expect(startButton.textContent).toBe('Start')`. The updated docs also describe the detail page as having a "单个 issue 的 Start 按钮", so the candidate leaves the ambiguous label in the target Epic surface instead of making the Start Epic vs. per-issue start distinction consistent. [disallowed:product-behavior/copy-contract-change]
  SuggestedAction: Rename the Epic detail linked-issue inline action and its docs/tests to an unambiguous label such as `Start issue` or `Start next issue`, matching the list-page convention and keeping `Start Epic` reserved for the Epic lifecycle action.
  Verification: Run `npm run test:run -w packages/web -- EpicDetailPage.test.tsx EpicListPage.test.tsx` and re-run a scoped grep over `packages/web/src/pages/epic-detail`, `packages/web/src/pages/epics`, and `docs/` for bare Epic-related `Start` labels.
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: docs/epics.md:207, packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:357-363, packages/server/src/Mohist.Server/Epic/Services/EpicProgress.cs:64-65
  Evidence: The docs correctly avoid saying an empty Epic is ready to mark done in the read model, but the sentence "所有 linked issues 都已进入终态时，`readyToMarkDone` 会变为 true，并可能由系统自动转为 `done`" compresses two distinct rules: the read model requires at least one linked issue for `readyToMarkDone`, while reconciliation/manual done uses no-open-linked readiness and can mark even a no-link candidate done. This is not a blocker because the docs already separate the no-linked-issues case in the preceding sentence and the added tests pin the intended wording.
  SuggestedAction: Consider splitting read-model readiness from reconciliation auto-done in a future cleanup if users report confusion around empty Epics.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: warning
  Scope: packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:1059-1063, packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:269-280 and 601-602
  Evidence: The Close Epic confirmation still says closing will unlink associated issues, but the server close path marks the Epic closed and releases active membership rows while retaining `EpicIssueRow` links; the nearby comment explicitly says closing is non-destructive and does not remove `EpicIssueRow`. This appears pre-existing and was not introduced by the candidate docs/copy changes, but it is adjacent Epic lifecycle copy that can mislead users.
  SuggestedAction: Update the Close Epic confirmation copy and any pinned Web tests to reflect that closing does not change linked issue workflow state and does not remove historical links, if that is the intended product contract.
  Status: pre-existing

## Verification Summary

- Read issue 282 details with `mo issue show 282 --project-id proj_f6c141d63b6243bfbb481737b2243b87` and checked acceptance criteria against the post-build branch snapshot.
- Reviewed changed files under `docs/README.md`, `docs/cli-reference.md`, `docs/concepts.md`, `docs/epics.md`, `docs/getting-started.md`, `docs/web-ui.md`, and `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Docs/EpicDocumentationSpecs.cs`, plus workflow artifacts under `openspec/changes/issue-282/`.
- Cross-checked CLI commands in `packages/cli/Mohist.Cli/MohistCliCommands.Epic.cs`, API routes in `packages/server/src/Mohist.Server/Api/EpicRoutes.cs`, lifecycle/reconciliation behavior in `Epic.Transitions.cs`, `EpicGrain.cs`, `EpicProgress.cs`, and Web labels in the Epic list/detail pages.
- `git diff --check master...HEAD` passed.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter FullyQualifiedName~EpicDocumentationSpecs` passed: 6 passed, 0 failed.
- `npm run test:run -w packages/web -- EpicDetailPage.test.tsx EpicListPage.test.tsx` passed: 209 passed, 0 failed.
- Full `npm test` passed on the second run: .NET tests plus workspace test suites completed successfully. The first `npm test` attempt exceeded the 120s tool timeout after build/test startup, so it was rerun with a longer timeout.

<promise>FAIL</promise>
