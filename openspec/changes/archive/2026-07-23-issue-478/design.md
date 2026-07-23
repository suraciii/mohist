## Context

Prerequisites #474 and #475 are landed. #474 separated Variables from the WorkflowProfile: Project, Issue, and WorkflowRun each own a `VariableBundle` (`Workflow/Domain/VariableBundle.cs`), and Effective Variables merge (Project → Issue → Run) already resolves at dispatch. #475 established the shared CLI contract (`--project`, `--json` field selection, stdout/stderr split, exit codes, `ResourceDescriptor`/`JsonSelection`, `RunCommands.ResolveRunTargetAsync`) that all new commands must follow.

What is missing is the **unified agent-facing command surface** and the **clean resource API**. Today:

- No `variable` command exists. Reads/writes go through scope-specific `project/issue workflow config set|clear` flags (`--var k=v`, `--stage-var <stage>.k=v`, `--vars-file`) with flat-string values and ad-hoc `<stage>.k` parsing (`MohistCliCommands.ProjectWorkflow.cs`, `MohistCliCommands.Issue.WorkflowConfigSet.cs`).
- Server variable routes still live under `workflow-profile/variables` (`ProjectRoutes.cs`, `IssueRoutes.WorkflowProfile.cs`, `WorkflowRoutes.cs`); the design target is clean `/variables`. The Run **effective** read routes are already on the clean path (`WorkflowRoutes.cs:26-47`).
- Write boundaries do not yet uniformly reject non-object `vars`/`stages.<stage>.vars` roots (`variables.md:256` gap). The Run scope already uses an ETag-guarded read-modify-write that clears covered initialization defaults (`WorkflowRunProfileManager.MutateVariablesAsync`); Project applies a `ProjectVariablesFilter` that converges `vars.agent` to `{model, variant}`.

Constraint: this is an actively-developed project with no version-compatibility obligation (`AGENTS.md`), so route renames and legacy-flag removals are in-place, not parallel aliases.

## Goals / Non-Goals

**Goals:**
- One `variable list/get/set/unset` command group under `project`, `issue`, and `run`, with identical key-path, `--stage`, and value-type rules, built on the #475 contract.
- Map the four verbs onto the existing server merge/null semantics (deep-merge PATCH; `null` as a delete instruction) without inventing a second write surface.
- Expose the existing Run-only effective merge through `run variable list/get --effective [--stage]`.
- Move Project/Issue/Run variable routes to clean `/variables` and harden write-boundary validation, leaving the original value unchanged on rejection.
- Keep the attempt-snapshot invariant intact (accepted attempts immutable; not-yet-dispatched tasks/retries use latest).

**Non-Goals:**
- No change to the `VariableBundle` shape, merge algorithm, precedence, or the `DefaultVars`/`DefaultStages` initialization-default mechanism (#474 owns these).
- No Variables revision/history/rollback; no separate persistence of the attempt snapshot.
- No new Profile Definition, Prompt, or root-config Variables modeling.
- No change to Profile selection or Definition CRUD.

## Decisions

### D1 — One shared command builder parameterized by scope

Build one partial class `VariableCommands` exposing a `BuildVariableGroup(api, scope)` that returns a `Command("variable")` carrying the four leaves, parameterized by a small `VariableScope` descriptor (Project / Issue / Run) that supplies path construction, project resolution, and (Run only) target resolution + the `--effective` flag. Each root (`project`/`issue`/`run`) registers this group.

- *Alternative:* three separate command classes per scope. *Rejected:* it triples 12 near-identical leaves and invites drift on exactly the shared rules (dotted path, `--stage`, string-vs-`--value-json`) that the reliability check requires to be identical across scopes.

### D2 — Dotted key path becomes a nested PATCH document client-side

The CLI converts `<key> <value>` into the nested object the server already deep-merges: `set agent.model openai/gpt-5` emits `PATCH { "vars": { "agent": { "model": "openai/gpt-5" } } }` (positional value serialized as a JSON string). `--stage <stage>` wraps the leaf under `stages.<stage>.vars` instead of `vars`. `unset <key>` emits the same nested path with a `null` leaf, which the existing merge treats as a delete instruction (`VariableJsonMerge`). A shared `VariableKeyPath` helper builds the nesting; it rejects empty segments and any segment that must traverse a non-object (arrays are not addressable by key path).

- *Alternative:* a server-side single-key endpoint `PATCH .../variables/<keyPath>`. *Rejected:* it adds a parallel write surface beside resource PUT/PATCH, duplicates validation, and the nested deep-merge already exists and is exercised by `setVars`.

### D3 — Value typing: positional string vs mutually-exclusive `--value-json`

`--value-json <json>` is parsed with `JsonDocument.Parse`; a parse failure is a **local** usage error (exit 2, no service call). The positional value and `--value-json` are mutually exclusive and exactly one is required for `set`, enforced by a `System.CommandLine` validator that runs before any HTTP call. The positional value is always serialized as a JSON string (no inference). `--json` remains output-only and is never accepted as a value input.

- *Alternative:* infer type from the positional token. *Rejected by the issue itself* — implicit coercion is the ambiguity being removed.

### D4 — Write-boundary validation in the managers (single authority)

Add a shared `VariableBundleShapeValidator` invoked at the top of each manager's `Set`/`Patch` (`ProjectWorkflowProfileManager`, `IssueWorkflowProfileManager`, `WorkflowRunProfileManager`) that rejects any bundle whose `vars` root or any `stages.<stage>.vars` root is not a JSON object, before any save, so the original value is unchanged. Because managers are the write authority for both HTTP and grain callers, validating there covers every entry point and keeps the invariant local to the aggregate. The existing `ProjectVariablesFilter` (agent convergence) and Run ETag read-modify-write stay as-is, layered after shape validation.

- *Alternative:* route-handler or ASP.NET model-level validation only. *Rejected:* HTTP-only checks leave grain callers unguarded and split the authority.

### D5 — In-place route rename, no aliases

Rename Project/Issue/Run variable routes from `workflow-profile/variables` to `variables` in `ProjectRoutes`, `IssueRoutes.WorkflowProfile`, and `WorkflowRoutes`. Update every caller of the removed paths in the same change: the Runner caller (`server/connection.ts:282`) and the **Web production API clients** — `entities/settings/api/client.ts:64,68` (Settings AI variables GET/PATCH) and `entities/issue/api/client.ts:266,274,281` (Issue workflow variables GET/PUT/PATCH) — which currently call `workflow-profile/variables`. Drop the now-redundant `workflow-profile` GET/PUT/PATCH variable routes. Effective read routes are already clean and unchanged.

- *Alternative:* keep both paths as aliases during transition. *Rejected:* the reliability check forbids synonymous command/resource paths, and no compat obligation exists. The correct response to the Web dependency is to migrate the clients, not to retain dual routes.

### D6 — Effective reads reuse existing read-only routes

`run variable list/get --effective` hit the already-clean routes: `GET /api/workflow-runs/{id}/variables/effective[?stage=]` for list, and `GET .../variables/effective/{keyPath}[?stage=]` for get. `--effective` is attached only to the Run variant's `list`/`get` and is rejected on `set`/`unset` and on project/issue (local usage error, exit 2). Scope-local `list`/`get` read the scope's own document and never merge.

- *Alternative:* compute the merge in the CLI. *Rejected:* control plane owns state and decisions; the CLI must not interpret cross-scope facts.

### D7 — Reads use the shared output contract

`list`/`get` declare `ResourceDescriptor` over the `WorkflowVariables` shape (`["vars","stages"]`, already in `ResourceOutputCatalog`) and render through `api.PrintResourceAsync`/`WriteJsonSelectionResult`, identical to the Run reads. Mutations render through `PrintMutationResourceAsync` returning the resulting bundle. Diagnostics go to stderr; results to stdout.

## Risks / Trade-offs

- `[Route rename breaks Runner/Web/test callers]` → Mitigation: the rename touches every caller of `workflow-profile/variables` — Runner (`server/connection.ts:282`), Web production clients (`entities/settings/api/client.ts`, `entities/issue/api/client.ts`), Web MSW/test handlers, and the server path-contract regression specs (`PathContractRegressionSpecs.cs` and the other server specs asserting the old path). All are updated in one change; CLI variable tests, Runner `setVars` specs, and Web specs assert the clean `/variables` path.
- `[Dotted key path through a non-object is ambiguous]` → Mitigation: `VariableKeyPath` rejects segments that cannot traverse an object at write and read boundaries with an actionable domain error; original value unchanged.
- `[Project/Issue `vars.agent` convergence silently drops non-{model,variant} keys]` → Mitigation: `ProjectVariablesFilter` remains the agent write authority; document that `project variable set agent.<other>` is not persisted. This is pre-existing behavior, surfaced — not introduced — by the new command.
- `[Concurrent `run variable set` and `setVars` race]` → Mitigation: Run already uses ETag read-modify-write (`MutateVariablesAsync`); a lost write raises `DbUpdateConcurrencyException` rather than silently clobbering. CLI surfaces transport-vs-domain outcomes per #475.
- `[Removing legacy `--var`/`--stage-var`/`--vars-file` disrupts existing scripts]` → Trade-off accepted (no compat obligation). Mitigation: the new `variable` verbs cover every prior capability; docs (`cli-reference.md`) migrate examples.

## Migration Plan

No schema or data change, so migration is code-only and atomic within this change:

1. **Server**: add `VariableBundleShapeValidator`; wire it into the three managers' Set/Patch.
2. **Server routes**: rename variable routes to `/variables` in `ProjectRoutes`, `IssueRoutes.WorkflowProfile`, `WorkflowRoutes`; remove the redundant `workflow-profile/variables` PUT/PATCH/GET for variables.
3. **Runner**: update `server/connection.ts:282` URL to `/variables`.
4. **Web**: migrate `entities/settings/api/client.ts` and `entities/issue/api/client.ts` to `/variables`; update MSW handlers (`_issueDetailMsw.tsx`, `AiSettingsSectionTestSupport.tsx`) and Web tests (`SettingsPage.spec.tsx`, `IssueDetailPage.spec.tsx`, browser specs under `packages/web/tests/browser`); update server path-contract specs (`PathContractRegressionSpecs.cs`, `IssueWorkflowProfileApiConsistencySpecs.cs`, `IssueWorkflowProductLoopSpecs.cs`, `RuntimeSettingsSpecs.cs`, `AgentSessionLaunchRoutesSpecs.cs`).
5. **CLI**: add `VariableCommands`; register under `project`/`issue`/`run`; remove `--var`/`--stage-var`/`--vars-file` from `MohistCliCommands.ProjectWorkflow.cs` and the Issue workflow-config commands.
6. **Tests**: CLI specs (shared rules across scopes, Run `--effective`, target resolution, local usage failures, no-remote paths); server specs (write-boundary rejection, clean routes, effective reads); Runner `setVars` spec on the clean path; Web specs on the clean path.
7. **Docs**: align `docs/cli-reference.md`, `design/cli.md`, and close the `workflow-profile` path and string-only-value gaps in `design/workflow/variables.md`.

**Rollback**: revert the change set; persisted `VariableBundle` JSON is unaffected because storage columns and the bundle shape are unchanged.

## Open Questions

- `list --stage`: show the scope's raw `stages.<stage>.vars` slice only (no merge)? Design assumes yes — scope-local raw read, consistent with `get`.
- Whether `project/issue workflow config get/set/clear` should retain prompt/default-template handling after the variable flags are removed, or whether prompt/template moves to their own command in a later slice. This change removes only the variable flags; prompt/template handling is untouched here.
- Whether to surface a domain error (not a silent drop) when a `project variable set` targets a converged `agent` key outside `{model, variant}`; current filter silently strips. Left as a follow-up unless the write-boundary validation pass makes it cheap.
