## Why

Workflow completion policy is currently mixed into Action Input: agent Actions privately evaluate `expect`, built-in task-level expectations can be silently discarded, and model variables can alter execution through hidden fallback behavior. This makes task success and execution configuration unreliable and must be corrected before the native OpenCode runtime replaces the current backend.

## What Changes

- **BREAKING**: Make `expect` a task-level completion contract beside `with`, `artifacts`, `setVars`, and `recovery`; dispatch it separately so the selected Action receives only its declared `with` input.
- Apply expected-file, marker, and `failIf` checks as Workflow task completion behavior after the Action returns, while preserving recovery matching against the resulting task output.
- Support marker `path: _output` against the turn's final assistant text carried as a private Action-result fact; the text does not become Action Output or task output.
- For `mohist/opencode` tasks, project a matched promise marker into that Action's minimal `{ "promise": "..." }` output so downstream expressions and `promise=FAIL` recovery handlers can use it; preserve every other Action's own output contract.
- **BREAKING**: Reject legacy agent-task shapes such as `with.expect` and `with.agent` with actionable profile errors during loading or validation; do not ignore, rewrite, or maintain a compatibility path for them.
- **BREAKING**: Remove implicit injection or same-key merging of Workflow Variables into Action Input, including the hidden `vars.agent` fallback. Project, Issue, Run, and Stage Variables keep their existing merge semantics, but configuration affects an Action only when the task explicitly binds it, such as `options: ${{ vars.agent }}`.
- Preserve the original JSON type when a variable reference occupies an entire value, so an explicit `options` binding receives an object rather than a string.
- Define `mohist/opencode` options as a `provider/model` model string plus an optional sibling `variant`; accept model IDs containing additional `/` characters by separating provider from model ID only at the first `/`, without implementing the native OpenCode process or SDK behavior in this change.
- Migrate all built-in profiles and every generated agent task to the canonical task contract, including approval feedback, recovery/self-retry tasks, and OpenSpec-expanded Build tasks. Built-in `variables.agent` defaults retain only model/variant semantics, approval feedback explicitly binds `options`, and previously ignored task-level expectations become effective while existing stages, approvals, artifacts, and recovery flows otherwise keep their product behavior.

## Capabilities

- `workflow-task-contract`: The canonical task declaration and Action-aware validation rules shared by built-in, custom, persisted, feedback, recovery, retry, and dynamically generated tasks, including top-level `expect` and actionable rejection of legacy agent-task `with.expect` and `with.agent` forms without banning fields that genuinely belong to another selected Action's input contract.
- `workflow-action-input`: The explicit Action Input expansion and dispatch contract, including separation from task completion policy, whole-value JSON type preservation, and absence of hidden variable injection or same-key input merging while retaining the existing merge semantics within the Variables hierarchy itself.
- `workflow-task-completion`: Workflow-owned post-Action completion behavior for required files, markers, `_output`, `failIf`, task success/failure, preservation of Action-owned outputs, and recovery matching without exposing private turn facts as Action Output.
- `opencode-action-contract`: The `mohist/opencode` Action's explicit `prompt`/`session`/`options` input and minimal `null | { promise }` output contract, including model/variant shape, first-slash model parsing, no hidden Variables fallback, and promise projection from Workflow completion without defining native OpenCode runtime behavior.

## Impact

- **Server Workflow domain and persistence** (`packages/server/src/Mohist.Server/Workflow/`): task definitions and runs, retry/runtime task reconstruction, YAML and persisted-profile loading, validation, Orleans surrogates, work-item translation, and runner dispatch DTOs gain a distinct completion contract.
- **Server model configuration** (`packages/server/src/Mohist.Server/Issue/`): model validation accepts IDs containing additional `/` segments while retaining a required provider prefix.
- **Runner protocol and execution** (`packages/runner/src/`): work/result types, template expansion, task execution, expectation evaluation, Action-specific output normalization, recovery ordering, the current agent Action boundary, and OpenSpec Build-task generation change so completion policy is no longer interpreted inside the Action or embedded in generated Action Input.
- **Built-in and generated workflows**: both built-in profile YAML files, approval-feedback tasks, recovery/self-retry tasks, and dynamically generated agent tasks use the same explicit input and task-level completion structure.
- **Model configuration surfaces** (`packages/web/src/` and corresponding Server APIs): Project and Issue writers persist only the model/variant meaning of `vars.agent` and stop adding execution-backend legacy keys.
- **Compatibility**: existing custom profiles or in-flight definitions using the old shape are not migrated automatically; loading or dispatch fails with an actionable migration error, and affected runs may require the profile to be updated and the stage rerun.
- **Dependencies and scope**: no new external dependency is required. OpenCode process, Session, and SDK behavior remain out of scope and are implemented by the subsequent runtime change.
