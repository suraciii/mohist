**Findings**

1. Warning: `POST /api/issues` can return `stageModels: {}` even though persistence normalizes an empty map to `NULL` / no override.
File: `packages/cli/src/api/issues.ts:235`
File: `packages/cli/src/db/issue-repo.ts:107-109`
File: `packages/cli/src/db/issue-repo.ts:117-128`
Why it matters: the local-issue-store design and update path both treat an empty override map as cleared state, but create returns the pre-normalized input object. A caller can see `stageModels: {}` in the create response even though a subsequent read returns no `stageModels`. That is a response/persistence mismatch and can briefly mislead the Web UI or any API consumer using the create response optimistically.
Suggested fix: normalize `stageModels` before calling `issueService.create()` in `packages/cli/src/api/issues.ts`, or normalize the returned `Issue` object in `IssueRepo.create()` so an empty object comes back as `undefined`.

2. Warning: discovery coverage is source-text based, not behavior based.
File: `packages/cli/tests/model-override-regression.test.ts:563-589`
Why it matters: the spec requires 30-minute caching and failure behavior for `opencode models`. Current tests only assert that the source file contains `'models'`, does not contain `'acp'`, and includes the TTL literal. They do not verify that `execFile` is called once during a warm cache window, that errors are surfaced, or that no models cause rejection.
Suggested fix: add unit tests for `OpencodeDiscoveryService` that mock `child_process.execFile`, assert one spawn across two `getAvailableModels()` calls within TTL, assert refresh invalidates cache, and assert empty/failed stdout rejects.

3. Warning: there is no direct test coverage for issue-aware model resolution in recovery sessions.
File: `packages/cli/src/services/conflict-resolution.ts:37-55`
File: `packages/cli/src/server/index.ts:164-188`
Why it matters: the workflow path is tested, but the spec also requires conflict resolution and build-error-fix sessions to resolve with build-stage policy plus issue overrides. The implementation looks correct, but there is no dedicated test proving those paths actually pass the resolved build model into `AgentSession.create()`.
Suggested fix: add focused tests that mock `AgentSession.create` and assert `model === resolveStageModel(Stage.Build, config, issue)` for both `resolveConflictsViaAgent()` and the merge-queue build-fix callback.

**Correctness**

1. PASS: model precedence is implemented in one place and follows the specified fallback order.
Evidence: `packages/cli/src/config/model-resolution.ts:24-39`

2. PASS: workflow, conflict-resolution, and build-fix paths all use issue-aware resolution.
Evidence: `packages/cli/src/workflow/workflow-engine.ts:69-83`
Evidence: `packages/cli/src/services/conflict-resolution.ts:48-55`
Evidence: `packages/cli/src/server/index.ts:180-187`

3. WARNING: create-path normalization for empty `stageModels` is inconsistent with persisted/read behavior.
Evidence: `packages/cli/src/db/issue-repo.ts:107-109`
Evidence: `packages/cli/src/db/issue-repo.ts:117-128`

**Complexity**

1. PASS: newly added focused functions are small and simple.
Evidence: `packages/cli/src/config/model-resolution.ts:18-40`
Evidence: `packages/cli/src/services/opencode-discovery-service.ts:17-67`

2. WARNING: `createIssueRoutes` remains a very large route module, although this change only added small validation branches.
Evidence: `packages/cli/src/api/issues.ts` total size is 2933 lines.

**Test Coverage**

1. PASS: targeted regression tests pass locally.
Evidence: `npm test -- model-override-regression.test.ts`
Evidence: `npm test -- workflow/workflow-engine.test.ts per-issue-model.test.ts config/model-resolution.test.ts`

2. WARNING: discovery tests validate source text rather than runtime behavior.
Evidence: `packages/cli/tests/model-override-regression.test.ts:563-589`

3. WARNING: no direct recovery-session tests were found for conflict resolution or build-error-fix model selection.
Evidence: implementation at `packages/cli/src/services/conflict-resolution.ts:37-55` and `packages/cli/src/server/index.ts:164-188`; no matching targeted tests found in `packages/cli/tests`.

**Security**

1. PASS: API boundary validation rejects invalid `model` and `stageModels` shapes before persistence.
Evidence: `packages/cli/src/api/issues.ts:211-224`
Evidence: `packages/cli/src/api/issues.ts:492-505`

2. PASS: discovery child process strips inherited server auth environment variables.
Evidence: `packages/cli/src/services/opencode-discovery-service.ts:29-38`

**Spec Compliance**

1. `agent-runtime/spec.md` - Scenario: Discover models through lightweight CLI
PASS
Evidence: `packages/cli/src/services/opencode-discovery-service.ts:26-28` runs `execFile(binPath, ['models'])`.
Evidence: `packages/cli/src/services/opencode-discovery-service.ts:51-55` parses `provider/model` lines with `isValidModelId`.
Evidence: `packages/cli/tests/model-override-regression.test.ts:566-579` checks the implementation no longer references ACP or `newSession`.

2. `agent-runtime/spec.md` - Scenario: Discovery cache is fresh for 30 minutes
PASS
Evidence: `packages/cli/src/services/opencode-discovery-service.ts:8` sets `30 * 60 * 1000`.
Evidence: `packages/cli/src/services/opencode-discovery-service.ts:72-79` returns cached models without probing when the cache is fresh.
Residual risk: behavior is implemented but only source-inspected in tests, not runtime-verified.

3. `agent-runtime/spec.md` - Scenario: Discovery command fails
PASS
Evidence: `packages/cli/src/services/opencode-discovery-service.ts:42-48` rejects and logs on `execFile` error.
Evidence: `packages/cli/src/services/opencode-discovery-service.ts:56-61` rejects and logs when no parseable model list is returned.

4. `http-api/spec.md` - Scenario: Create issue with model metadata
PASS
Evidence: `packages/cli/src/api/issues.ts:193-235` accepts `model` and `stageModels` and passes them to creation.
Evidence: `packages/cli/tests/model-override-regression.test.ts:332-343` asserts response includes both fields.

5. `http-api/spec.md` - Scenario: Update issue stage model overrides
PASS
Evidence: `packages/cli/src/api/issues.ts:482-513` accepts `stageModels` on patch.
Evidence: `packages/cli/tests/model-override-regression.test.ts:355-374` verifies replacement semantics.

6. `http-api/spec.md` - Scenario: Clear issue stage model overrides
PASS
Evidence: `packages/cli/src/api/issues.ts:513` forwards `null` stageModels.
Evidence: `packages/cli/src/db/issue-repo.ts:278-283` normalizes null/empty to `NULL`.
Evidence: `packages/cli/tests/model-override-regression.test.ts:376-385` verifies clearing with `null`.

7. `http-api/spec.md` - Scenario: Reject invalid model metadata
PASS
Evidence: `packages/cli/src/api/issues.ts:211-224` and `packages/cli/src/api/issues.ts:492-505` reject malformed `model` and `stageModels`.
Evidence: `packages/cli/tests/model-override-regression.test.ts:435-559` covers create/update validation failures and non-persistence.

8. `local-issue-store/spec.md` - Scenario: Store per-issue stage model overrides
PASS
Evidence: `packages/cli/src/db/issue-repo.ts:107-114` persists `stage_models` JSON.
Evidence: `packages/cli/src/db/issue-repo.ts:44-54` parses JSON back into `Issue.stageModels`.
Evidence: `packages/cli/tests/model-override-regression.test.ts:179-200` verifies create/update round-trip.

9. `local-issue-store/spec.md` - Scenario: Clear per-issue stage model overrides
PASS
Evidence: `packages/cli/src/db/issue-repo.ts:278-283` converts empty/null to `NULL`.
Evidence: `packages/cli/tests/model-override-regression.test.ts:217-237` verifies clear with `null` and `{}`.

10. `local-issue-store/spec.md` - Scenario: Malformed stored stage model JSON
PASS
Evidence: `packages/cli/src/db/issue-repo.ts:45-53` catches parse failures and returns no overrides.
Evidence: `packages/cli/tests/model-override-regression.test.ts:246-259` covers malformed and non-object JSON.

11. `web-ui/spec.md` - Scenario: Configure issue default model
PASS
Evidence: `packages/cli/web/src/components/IssueModelSelector.tsx:131-155` updates `model` through the issue API.
Evidence: `packages/cli/web/src/components/IssueModelSelector.tsx:351-354` shows override-active UI state.

12. `web-ui/spec.md` - Scenario: Configure issue stage model override
PASS
Evidence: `packages/cli/web/src/components/IssueModelSelector.tsx:158-170` patches `stageModels` through the issue API.
Evidence: `packages/cli/web/src/components/IssueDetailPage.tsx:565` passes `issue.stageModels` back into the selector after refresh.

13. `web-ui/spec.md` - Scenario: Clear issue model overrides
PASS
Evidence: `packages/cli/web/src/components/IssueModelSelector.tsx:145-155` clears issue default model with `null`.
Evidence: `packages/cli/web/src/components/IssueModelSelector.tsx:172-180` clears a stage override by omitting the stage or sending `null` when the map becomes empty.

14. `web-ui/spec.md` - Scenario: Stage lists match executable pipeline stages
PASS
Evidence: `packages/cli/web/src/components/AiSettingsSection.tsx:11` uses `['explore', 'plan', 'build', 'check', 'integrate']`.
Evidence: `packages/cli/web/src/components/IssueModelSelector.tsx:10` uses the same list.
Evidence: `packages/cli/tests/model-override-regression.test.ts:130-134` verifies `integrate` included and `fix` excluded in shared resolver constants.

15. `web-ui/spec.md` - Scenario: Create issue with default model
PASS
Evidence: `packages/cli/web/src/components/CreateIssueDialog.tsx:180-188` includes `model` in the create request when selected.
Evidence: `packages/cli/web/src/lib/api.ts:49-53` supports create payloads with `model`.

16. `workflow-engine/spec.md` - Scenario: Issue stage model overrides all lower levels
PASS
Evidence: `packages/cli/src/config/model-resolution.ts:29-39` implements precedence.
Evidence: `packages/cli/tests/model-override-regression.test.ts:65-71` verifies build-stage issue override wins.

17. `workflow-engine/spec.md` - Scenario: Issue default model applies when stage override is unset
PASS
Evidence: `packages/cli/src/config/model-resolution.ts:32-39` checks `issueOverride.model` before global settings.
Evidence: `packages/cli/tests/model-override-regression.test.ts:73-76` verifies issue default wins.

18. `workflow-engine/spec.md` - Scenario: Global configuration remains fallback
PASS
Evidence: `packages/cli/src/config/model-resolution.ts:35-39` falls back to global stage/default config.
Evidence: `packages/cli/tests/model-override-regression.test.ts:78-89` verifies global and undefined fallback behavior.

19. `workflow-engine/spec.md` - Scenario: Recovery sessions use build-stage policy
PASS
Evidence: `packages/cli/src/services/conflict-resolution.ts:54` resolves with `Stage.Build`.
Evidence: `packages/cli/src/server/index.ts:186` resolves build-fix sessions with `Stage.Build`.
Residual risk: no dedicated automated test was found for these recovery paths.

**Overall**

PASS with warnings.

The implementation appears functionally aligned with the spec, and the targeted regression suites pass. The main issue I found is a create-response normalization mismatch for empty `stageModels`; the remaining items are test coverage gaps rather than confirmed behavior bugs.

<promise>PASS</promise>
