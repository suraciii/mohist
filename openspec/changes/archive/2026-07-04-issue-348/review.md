# Review Report

## Result: PASS

The post-build candidate implements option (a), true reversible archive, across server and web. Evidence checked: `IAgentGrain.UnarchiveAsync` and `AgentGrain.UnarchiveAsync` reverse archived status and preserve the defined active no-op behavior; `POST /api/projects/{projectRef}/agents/{id}/unarchive` returns the unarchived `AgentInfo`; `useAgents` requests `{ all: true }`; the list renders active and archived groups separately; the detail page offers direct Archive with confirmation for active agents and Unarchive for archived agents; the profile-editor confirmation text no longer contains the old false `remain visible` / `can be reversed` wording. I also reviewed adjacent session-launch paths: the composer filters archived agents out of the picker, and the server launch endpoint rejects archived agents before creating a session or job.

Verification performed:
- `dotnet test Mohist.sln -p:SkipWebBuild=true` - passed: 3759 passed, 13 skipped.
- `npm run typecheck -w packages/web` - passed.
- `npm run test:run -w packages/web` - passed: 259 files, 4101 passed, 1 skipped.
- `npm run test:ci --workspaces --if-present` - passed, including runner workspace tests.
- `git diff --check master...HEAD` - passed with no whitespace errors.

## Repaired Items

None.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/web/src/pages/agent-session-composer/ui/AgentSessionComposerPage.tsx`
  Evidence: A deep link such as `/agent-sessions/new?agent=<archived-id>` still resolves the archived agent for the page-level guard (`selectedAgent` from the full `agents` list at lines 186-190) and shows the archived warning at lines 271-274, but the `AgentSelector` receives only `launchableAgents` at lines 265-267. Inside `AgentSelector`, the trigger label is derived from the passed option list at line 55, so the archived selected id cannot render its name and the trigger falls back to `Select an agent...` at lines 82-86. This is not a correctness failure because launch remains disabled and archived agents are intentionally omitted from the picker, but it is a small UX inconsistency for archived-agent deep links.
  SuggestedAction: Allow the selector trigger to display the currently selected archived agent name while keeping archived agents out of the selectable option list, or clear archived deep-link selections explicitly.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: info
  Scope: verification
  Evidence: The first `npm test` wrapper invocation exceeded the 120000 ms tool timeout while the server suite was still running. The same wrapper steps were then executed with usable timeouts as `dotnet test Mohist.sln -p:SkipWebBuild=true` and `npm run test:ci --workspaces --if-present`; both completed successfully.
  SuggestedAction: Use a longer timeout when invoking the full wrapper in this repository.
  Status: out-of-scope

<promise>PASS</promise>
