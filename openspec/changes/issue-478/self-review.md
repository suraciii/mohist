# Self-Review — issue-478 (unified Project/Issue/Run Variables read/write CLI)

Reviewed artifacts: `proposal.md`, `design.md`, `tasks.json`, `specs/variable-commands/spec.md`,
`specs/variable-resources/spec.md`, against the issue body and the current codebase.

## Summary

The plan is coherent and the specs are well-formed: both capability specs use exactly
`### Requirement:` / `#### Scenario:` (4 hashtags), every requirement has at least one scenario,
language is normative (SHALL/MUST), and there are no `ADDED/MODIFIED/REMOVED` delta headers.
All nine issue acceptance criteria map onto a spec requirement and a task. The task graph is a
clean DAG (T-001 → T-002 → T-003), each task bundles its tests, and dependencies point only to
strictly lower-priority tasks.

However, there is one blocker that makes the plan not ready to build as written.

## Finding 1 — BLOCKER: the `workflow-profile/variables` → `variables` rename omits the Web UI's production API clients

T-001 renames the Project/Issue/Run variable routes and removes the `workflow-profile/variables`
routes, but the plan only accounts for the Runner (`connection.ts`) as a caller to update.
The Web frontend has **production** API clients that call the removed paths:

- `packages/web/src/entities/settings/api/client.ts:64` — `getProjectVariables` GETs
  `/api/projects/{projectId}/workflow-profile/variables`
- `packages/web/src/entities/settings/api/client.ts:68` — patches the same path (Settings → AI
  variables)
- `packages/web/src/entities/issue/api/client.ts:266,274,281` — Issue workflow variables
  GET/PUT/PATCH on `/issues/{number}/workflow-profile/variables` (Issue detail page)

T-001 acceptance criterion ("GET/PUT/PATCH on `.../workflow-profile/variables` for Project,
Issue, and Run are no longer mapped") therefore ships a change that makes the Settings page and
the Issue-detail page 404 at runtime. The omission is systemic across all three plan artifacts:

- `proposal.md` Impact lists CLI, Server, Runner, Tests, Docs — Web is absent.
- `design.md` D5 and Migration say "Update the single Runner caller" and drop the redundant
  routes; Web is not mentioned.
- `tasks.json` T-001 output and acceptance criteria name server + runner + tests only.

Required fix (for the fixer, not me): the route-rename task must also update the two Web
production clients (`entities/settings/api/client.ts`, `entities/issue/api/client.ts`) to the
`/variables` path, their MSW handlers (`pages/issue-detail/ui/_issueDetailMsw.tsx`,
`pages/settings/ui/AiSettingsSectionTestSupport.tsx`), the Web tests
(`SettingsPage.spec.tsx`, `IssueDetailPage.spec.tsx`, and the browser specs under
`packages/web/tests/browser/*.spec.ts` that route-intercept `workflow-profile/variables`), and
the server path-contract regression test `PathContractRegressionSpecs.cs:320-378` plus the other
server specs asserting the old path (`IssueWorkflowProfileApiConsistencySpecs.cs`,
`IssueWorkflowProductLoopSpecs.cs`, `RuntimeSettingsSpecs.cs`,
`AgentSessionLaunchRoutesSpecs.cs`). T-001 acceptance criteria should add: Web Settings and
Issue-detail variable reads/writes target `/variables` and `npm run typecheck`/`npm run test:run`
in `packages/web` pass.

The design's rejection of alias routes (D5) is correct; the fix is to update the Web clients, not
to keep dual paths.

## Minor observations (not blockers)

- `specs/variable-commands/spec.md` "Stage selection via --stage" clearly scopes `set`/`get`/`unset`
  to Stage Variables, but does not state what `list --stage` returns for a scope-local list. The
  design lists this as an Open Question (raw stage slice, no merge). Worth a one-line scenario to
  remove ambiguity, but the design already resolves the intent.
- `proposal.md`/`tasks.json` treat the Web update as out of scope only because it was overlooked
  (see Finding 1); once the Web clients are added to T-001, the proposal Impact section should list
  Web too.

## Verdict

Finding 1 is a must-fix: the plan as written would build a breaking route removal without updating
the Web UI clients that depend on it. The plan is not ready to build until T-001 (and the proposal
Impact section) cover the Web client migration.

<promise>FAIL</promise>
