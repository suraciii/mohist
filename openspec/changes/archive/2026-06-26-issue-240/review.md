# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs:1241`
  Evidence: `config set --var` and `config clear --var` use `TableShape.WorkflowVariables` while `config set --template` and `config clear --template` use `TableShape.WorkflowProfile`. The `WorkflowProfile` shape renders the full three-section profile but its prompts section may be empty since the template endpoints return the profile without merged prompt data (unlike `config get`, which fetches prompts separately and merges them). This is a cosmetic display issue — the template and variables are correctly shown.
  SuggestedAction: If a "show-everything" `-o table` output is desired after template mutations, use the two-fetch approach from `config get` (profile + prompts merge) in the output path. Not required for v1.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Workflow/Domain/VariableBundle.cs:259`
  Evidence: `MergeStages` creates a stage entry for overlay-only stages even when all stage vars are null-cleared by `CloneOverlay`. The resulting stage has an empty vars object (`{}`) rather than being absent from the stages dict. This does not affect correctness — empty-vars stage entries are inert and invisible in resolution — but is a minor bookkeeping artifact.
  SuggestedAction: Consider skipping stage insertion when the resulting `StageVariables` has no vars after null filtering, or add a post-merge cleanup pass. Not required for v1.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: warning
  Scope: dependency audit output
  Evidence: Server test build reported existing `npm audit` findings (9 vulnerabilities). These are pre-existing and not introduced by this change.
  SuggestedAction: Track dependency audit cleanup separately.
  Status: out-of-scope

## Verification

- `dotnet build Mohist.sln` — 0 warnings, 0 errors.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --filter CliIssueWorkflowConfigSpecs` — 61 passed, 0 failed.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter VariableBundleSpecs` — 32 passed, 0 failed.

### Previous review items (from 3d62baf0) — all resolved

| Item | Original finding | Resolution |
|---|---|---|
| item-1 | Empty-base null-clear bug in VariableBundle | `CloneOverlay`/`CloneOverlayObject` filter nulls on clone path; tests cover `Patch_EmptyBase_Null*` |
| item-2 | Variable writes used WorkflowProfile table shape | Changed to `WorkflowVariables` shape; tests assert separate variable-bundle rendering without profile fields |
| item-3 | Prompt writes/deletes ignored output mode | Changed to `PrintPutWithOutputAsync`/`PrintDeleteWithOutputAsync` with `WorkflowProfilePrompt` shape; `-o json` / `-o table` tests added |
| item-4 | Preview missing slash-key rejection | Added `key.Contains('/')` validation with zero-request exit; test added |
| item-5 | Test-gap for above cases | Tests added for: empty-base null-clear, variable table modes, prompt output modes, preview slash rejection |

### Acceptance criteria coverage

| Criterion | Evidence |
|---|---|
| `config get` returns three sections, `-o table|json` | `ConfigGet_TableMode_*`, `ConfigGet_JsonMode_*` |
| `config set --template @wf.yaml`, `get` reflects it | `ConfigSet_TemplateAtFile_*` |
| `config clear --template`, falls back to default | `ConfigClear_Template_*` |
| `config set --var/--stage-var`, no template/prompt side-effects | `ConfigSet_VarAndStageVar_*`, `ConfigSet_OnlyVar_*` |
| `config clear --var foo --prompt greeting`, only specified removed | `ConfigClear_VarAndPrompt_*`, `ConfigClear_Var_OnlyAffectsVars_*` |
| `config set --prompt` inline and @file | `ConfigSet_PromptInline_*`, `ConfigSet_PromptAtFile_*` |
| `config preview <key>` prints rendered text | `ConfigPreview_TableMode_*`, `ConfigPreview_JsonMode_*` |
| All subcommands support `--project`/`--project-id` and `-o` | `ConfigGet_AcceptsProjectAndProjectIdAlias`, `ConfigPreview_AcceptsProjectAndProjectIdAlias`, `*_InvalidOutputMode_*` |
| `--help` lists get/set/clear/preview | `ConfigHelp_ListsFourSubcommands` |
| Server error passthrough per subcommand | `ConfigSet_ServerRejectsTemplate_*`, `ConfigClear_ServerRejectsVarPatch_*`, `ConfigClear_ServerRejectsTemplateDelete_*`, `ConfigPreview_ServerError_*`, `ConfigGet_ServerError_*` |

<promise>PASS</promise>
