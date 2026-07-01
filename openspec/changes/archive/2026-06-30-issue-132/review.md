# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/web/src/pages/agent-list/ui/AgentListPage.tsx, packages/web/src/app/App.tsx
  Evidence: The list page's create actions navigate to `/agents/new` at `AgentListPage.tsx:141` and `AgentListPage.tsx:151`, but the router only declares `agents` and `agents/:agentId` at `App.tsx:68-69`. The candidate therefore treats `/agents/new` as an agent detail route for id `new`, which fails to load instead of opening a create profile editor. This breaks the Agent profile management acceptance criterion that the editor creates a profile and navigates to the new profile's detail page. [disallowed:product-behavior-change]
  SuggestedAction: Add an explicit `agents/new` route before `agents/:agentId` that renders `AgentProfileEditor` in create mode, or change the list page to open the create editor locally. Add a route-level test that clicking both create actions exposes the create form and that successful creation navigates to `/agents/{createdId}`.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but current tests only assert the empty/create buttons exist and do not exercise the route.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/web/src/pages/session/data/useIssueSessionDataSource.tsx, packages/web/src/pages/session/ui/SessionDetailShell.tsx
  Evidence: The issue-scoped session data source returns `sendFollowup: () => {}` and `followupIsPending: false` at `useIssueSessionDataSource.tsx:256-257`. `SessionDetailShell` still renders `SessionFollowupComposer` for running issue-scoped sessions at `SessionDetailShell.tsx:247` and `SessionDetailShell.tsx:296`, so users can type a follow-up and see a local success state, but no `POST /issues/{number}/sessions/{name}/followup` mutation is ever called. This is a regression from the pre-existing issue-scoped session follow-up behavior while refactoring adjacent recovery/session paths. [disallowed:product-behavior-change]
  SuggestedAction: Wire `useFollowupMutation` into `IssueSessionDataSource` using the resolved `issueNumber` and `recoverySessionName`/session name, pass its pending state and mutation callback to the shell, and add a regression test that a running issue-scoped session calls the issue follow-up endpoint/mutation.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but no current test covers issue-scoped follow-up submission after the data-source extraction.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: packages/web/src/pages/session/ui/SessionDetailShell.tsx
  Evidence: The modified `agent-session-ui` spec requires the recovery bar, when present, to render as a sub-region of the session header across render branches. For sessions with one or more turns, `SessionDetailShell` renders `headerWithoutRecovery` at `SessionDetailShell.tsx:269`, then renders `session-recovery-bar` separately as a sticky block inside the scroll container at `SessionDetailShell.tsx:275-283`. This violates the spec and reintroduces the standalone narrow-bar layout for the main transcript branch. [disallowed:product-behavior-change]
  SuggestedAction: Render the recovery region consistently through `SessionHeader` for the main transcript branch as well, preserving sticky behavior within the header/page scroll context. Add a test that the transcript branch places `session-recovery-bar` inside the header region and not as a separate scroll-body sibling.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed. Existing generic-session tests only assert that `ContextHealthBar` appears, not where it is rendered.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: packages/web/src/pages/agent-session-composer/ui/AgentSessionComposerPage.tsx
  Evidence: The composer surfaces no-runner and external-agent-unavailable errors at `AgentSessionComposerPage.tsx:234-256`, but `canLaunch` at `AgentSessionComposerPage.tsx:190` ignores `isNoRunnerError` and `isExternalAgentUnavailable`. After such an error is displayed, a user can keep clicking Launch if the prompt and agent are selected, despite the acceptance criteria saying these states prevent launch until recovery. The test named `prevents launch when error is present` at `AgentSessionComposerPage.test.tsx:330-338` only asserts the error is visible and never verifies the button is disabled or mutation is blocked. [disallowed:product-behavior-change]
  SuggestedAction: Include blocking launch error state, or live runner/runtime availability state, in `canLaunch`; clear stale blocking errors when the selected agent changes if appropriate. Strengthen the test to assert the button is disabled and `launchMutation.mutate` is not called.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but the current tests do not verify the required prevention behavior.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: packages/web/src/pages/agent-list/ui/AgentListPage.tsx
  Evidence: The Agent list requirement says every profile row SHALL display the profile's most recent session. `AgentListPage.tsx:52-83` renders name, agent type/model/variant, and availability only; it never fetches agent-scoped sessions or displays any latest session status/time/link. The corresponding tests in `AgentListPage.test.tsx:95-138` cover only profile summary and Active/Archived availability. [disallowed:acceptance-criteria-gap]
  SuggestedAction: Add a data source for each profile's latest session or consume an aggregate API if available, render the latest session summary/link per row, and cover populated and empty latest-session states in tests.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but this acceptance criterion is unimplemented.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: packages/web/src/pages/agent-list/ui/AgentListPage.tsx
  Evidence: Availability is hard-coded to `Active` for every non-archived profile at `AgentListPage.tsx:19-24`. The spec requires availability to reflect whether the profile can currently be launched, including no available runner. The page does not consume `useAgentStatus` or any per-runtime availability signal, so profiles with no runner or unavailable external runtime are shown as active/launchable. [disallowed:product-behavior-change]
  SuggestedAction: Derive availability from the runner/runtime status data available to the workbench, show no-runner/runtime-unavailable states distinctly, and add tests for active, archived, no-runner, and unavailable-runtime rows.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but current tests encode the incomplete Active/Archived-only behavior at `AgentListPage.test.tsx:127-138`.
  Status: open

- [ID: item-7]
  Severity: warning
  Scope: packages/web/src/widgets/agent-profile-editor/ui/AgentProfileEditor.tsx
  Evidence: The profile editor validation only requires name and instructions at `AgentProfileEditor.tsx:62-67`. It allows create/edit submission with `model === null` and `variant === null`, producing `agentConfig: null` via `writeAgentModelAndVariant` at `AgentProfileEditor.tsx:74-78`, even though the issue/task acceptance criteria require configuring agentConfig model + variant through `ModelSelect` and inline validation for missing required fields. Tests at `AgentProfileEditor.test.tsx:95-121` treat name/instructions-only submission as valid. [disallowed:product-behavior-change]
  SuggestedAction: Decide the required model/variant rules and enforce them in `validate()` with inline field errors. Update create/edit tests to cover missing model and missing variant if variants are mandatory for selected models.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but tests currently validate the weaker contract.
  Status: open

- [ID: item-8]
  Severity: test-gap
  Scope: packages/web/src/pages/agent-list, packages/web/src/pages/agent-session-composer, packages/web/src/pages/session
  Evidence: Several required behaviors are either untested or tested only superficially: create-profile routing is not exercised (`AgentListPage.test.tsx` stops at button presence), no-runner prevention is not asserted (`AgentSessionComposerPage.test.tsx:330-338`), issue-scoped follow-up regression is not covered after the data-source seam, and recovery-bar placement is not asserted for the main transcript branch. These gaps allowed the candidate to pass `213` web test files despite multiple acceptance failures. [disallowed:test-coverage]
  SuggestedAction: Add route/integration tests for `/agents/new`, negative launch tests for runner/runtime blocking, issue-scoped follow-up mutation tests, and DOM-structure tests for header-contained recovery in the transcript branch.
  Verification: `npm run test:run -w packages/web` passed with `213 passed`, `3226 passed`, `1 skipped`; the pass is not sufficient because the named scenarios are absent or incomplete.
  Status: open

## Follow-up Items

- [ID: item-9]
  Severity: follow-up
  Scope: packages/web/src/pages/session/data/buildGenericSessionMetadata.ts, packages/web/src/pages/session/data/useGenericSessionDataSource.ts
  Evidence: Generic status mapping logic is duplicated in `buildGenericSessionMetadata.ts:4-23` and `useGenericSessionDataSource.ts:12-29`. This is not the cause of a current failure, but it increases the chance of status drift as new lifecycle states are added.
  SuggestedAction: Consolidate generic status-kind derivation into one shared helper once the blocking behavior issues are fixed.
  Status: follow-up

## Pre-existing or Out-of-scope Items

_None identified._

<promise>FAIL</promise>
