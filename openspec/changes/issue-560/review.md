# Review: issue-560

## Verdict
FAIL. The implementation has must-fix gaps against the issue acceptance criteria.

## Must-Fix Findings

### MF-1: Web and CLI do not present the effective execution scope consistently

Violates AC2 and AC6. The server now resolves a Project default for Readiness and launch (`packages/server/src/Mohist.Server/Agent/Services/AgentReadinessService.cs:46-55`, `packages/server/src/Mohist.Server/Agent/Services/AgentLauncher.cs:742-766`), but the read surfaces still expose only the raw Agent definition. Web `readAgentModelAndVariant` defaults a missing runtime to OpenCode and has no Project-default input (`packages/web/src/entities/agent/api/client.ts:162-176`); the detail page consequently shows `Model Default` and the fallback runtime (`packages/web/src/pages/agent-detail/ui/AgentDetailPage.tsx:347-354`, `544-558`). The CLI renderer also reads only `agentConfig` (`packages/cli/Mohist.Cli/TableRenderer.Entities.cs:109-119`). Thus an existing Agent with `{}` and a Project default of `pi/provider/model` is Ready and launches with Pi, while Web and CLI show no effective model/runtime. Separately, a task-first Agent materialized with `runtime: pi` is displayed as OpenCode in the Web list because `getAgentType` still reads the legacy `agentConfig.type` (`packages/web/src/pages/agent-list/ui/AgentListPage.tsx:25-38`, `51-90`). Project or Agent read projections need one server-authoritative effective execution view, and both list/detail clients need to consume it; add coverage for both default-resolved and materialized-Pi Agents.

### MF-2: Launch does not provide the required pre-launch scope confirmation

Violates AC4. The Web composer sends the task immediately from `handleLaunch` (`packages/web/src/pages/agent-session-composer/ui/AgentSessionComposerPage.tsx:389-447`). It only renders context chips that were already present in URL parameters; it does not show the resolved repository, default or selected workspace, Issue/Epic context, permission scope, or expected impact in a confirmation step. The CLI likewise uploads inputs and immediately POSTs `/agent-tasks` (`packages/cli/Mohist.Cli/MohistCliCommands.Agent.Start.cs:111-149`); its launch table contains only the workspace identity and does not show repository, Issue/Epic context, or permission scope. The server resolves an implicit workspace only during request handling (`packages/server/src/Mohist.Server/Api/AgentTaskRoutes.cs:89-100`, `376-404`), after the caller has had no opportunity to confirm it. Add a shared preflight/projection and make Web and CLI display the actual frozen scope before allowing the launch.

### MF-3: Crash-window adoption is not stable when the Project default changes

Violates the task-first replay requirement in `specs/agent-task-launch/spec.md` and the issue's idempotent create-and-launch contract. If the first request creates the deterministic Agent after resolving default A and crashes before the coordinator plan is persisted, a replay with the same key after the Project default is changed or cleared reads the current default again through `AgentTaskDefinitionFactory.CreateAsync` (`packages/server/src/Mohist.Server/Agent/Services/AgentTaskDefinitionFactory.cs:62-76`). The adopted branch then compares that newly derived definition with the already materialized Agent and returns `launch_idempotency_conflict` (`packages/server/src/Mohist.Server/Api/AgentTaskRoutes.cs:256-289`, `496-503`), or `execution_config_unresolvable` if the default was removed. It does not adopt and launch the original definition as required. Adoption must use durable first-attempt definition facts, or otherwise avoid re-deriving mutable Project defaults during replay; add a test that mutates the default between the create and replay steps.

### MF-4: Concurrent first requests can overwrite the deterministic Agent

Violates AC1 and AC5's exactly-one/original-outcome idempotency requirements. The task route performs `ShowAsync` followed by `CreateAsync` (`packages/server/src/Mohist.Server/Api/AgentTaskRoutes.cs:245-342`) with no per-key admission fence. `AgentGrain.CreateAsync` does not reject an already populated grain and unconditionally replaces and saves `_agent` (`packages/server/src/Mohist.Server/Agent/Grains/AgentGrain.cs:46-72`). Two simultaneous first requests can both observe no Agent; after the first name is occupied, the second factory chooses a disambiguated name and writes it to the same deterministic Agent id. One coordinator plan can then refer to the first definition while the persisted Agent has been overwritten, and the other request returns a conflict. The deterministic Agent identity must have an atomic first-writer/adoption path, with a concurrent same-key test proving the original definition and launch identities remain stable.

### MF-5: The task-first product does not cover collaborators and concurrency intent in Web

Violates AC1. The task request is closed to `prompt`, `attachments`, `context`, `name`, `runtime`, `model`, and `variant` only (`packages/server/src/Mohist.Server/Api/AgentTaskRoutes.cs:26-35`), and task-first creation hard-codes `Skills: []` and `MaxConcurrentRuns: null` (`packages/server/src/Mohist.Server/Api/AgentTaskRoutes.cs:342-349`). The Web profile editor has no controls or save fields for `AllowedSubagentAgentIds` or `MaxConcurrentRuns`; the detail page only displays the max-concurrency value and has no collaborator/permission view (`packages/web/src/widgets/agent-profile-editor/ui/AgentProfileEditor.tsx:70-126`, `packages/web/src/pages/agent-detail/ui/AgentDetailPage.tsx:543-584`). The separate definition-first CLI create/edit flags do not make these capabilities available in the task-first Web experience. The task-first/refinement surfaces need to let the user configure and view the allowed collaborators and concurrency intent, with the applicable launch permission scope represented explicitly.

## Review Dimensions

- Issue acceptance criteria reread before the diff: checked.
- Coverage: FAIL. The core route, defaults, Web composer, CLI command, and rollback paths are present, but MF-1, MF-2, and MF-5 leave acceptance criteria incomplete.
- Correctness: FAIL. MF-3 and MF-4 produce incorrect replay or persisted-definition outcomes in crash and concurrent-request cases.
- Consistency with surrounding code: FAIL. The server's effective default/runtime behavior disagrees with Web and CLI Agent projections (MF-1); the task route also lacks the atomic create/adopt boundary used by the idempotency contract (MF-4).
- Tests: FAIL for completeness. Verification is green for the covered paths: Server SpecTests 3950/3950, Server UnitTests 2701/2701, CLI tests 1860/1860, and the affected Web files 103/103. No test covers default mutation during crash adoption, concurrent deterministic creation, effective default projection in Agent list/detail/CLI, or pre-launch scope confirmation. `npm run verify` was not rerun in this review.

## Observations

- The current repository and plan artifacts have no separate preflight contract or test scenario for AC4; the Web and CLI specs cover forwarding context, not displaying and confirming the resolved permission scope.
- The task route's workspace repository snapshot filters names that are absent from the Project repository list instead of rejecting an unknown repository context (`packages/server/src/Mohist.Server/Api/AgentTaskRoutes.cs:505-532`). This compounds the missing scope confirmation and should be addressed by the preflight contract.
- The existing Web composer represents a `?ws=` value as `workspacePath` rather than the named `context.workspace` field (`packages/web/src/pages/agent-session-composer/ui/AgentSessionComposerPage.tsx:392-405`); the current tests lock in that behavior, so its intended meaning should be clarified while implementing the scope projection.

<promise>FAIL</promise>
