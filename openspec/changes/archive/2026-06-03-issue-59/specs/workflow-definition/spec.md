## MODIFIED Requirements

### Requirement: Stage definitions preserve existing stage semantics

The declarative definitions for Plan, Build, Check, and Integrate SHALL preserve the existing user-visible workflow semantics while moving stage differences into configuration and registries. The built-in Build stage definition SHALL express OpenSpec task prompt composition through a prompt-loader specification on generated `mohist/acp-agent` tasks instead of requiring the OpenSpec task loader to compose literal prompt strings.

#### Scenario: Plan definition preserves planning contract

- **WHEN** Plan executes through the config-driven runner
- **THEN** it SHALL generate proposal, specs, design, tasks, and self-review work as Plan stage tasks
- **AND** it SHALL retain Plan approval, artifact validation checks, health check behavior, and checkpoint compatibility

#### Scenario: Check definition preserves review contract

- **WHEN** Check executes through the config-driven runner
- **THEN** it SHALL execute AI review as stage work before review and merge readiness checks
- **AND** it SHALL retain user approval, repair policy, stale review invalidation, and merge readiness behavior

#### Scenario: Build definition preserves Ralph contract

- **WHEN** Build executes through the config-driven runner
- **THEN** it SHALL consume Ralph dynamic tasks as Build stage tasks
- **AND** it SHALL retain checkpoint resume, task materialization, aggregate single task execution, and health gate repair behavior
- **AND** generated OpenSpec task prompts SHALL be resolved by `mohist/openspec-task-prompt` when no caller prompt override is supplied

#### Scenario: Integrate definition preserves integration contract

- **WHEN** Integrate executes through the config-driven runner
- **THEN** it SHALL execute spec sync, change archive, and branch merge as ordered stage tasks
- **AND** it SHALL run the Integrate health check only after those tasks succeed

## ADDED Requirements

### Requirement: OpenSpec task loader emits ACP agent tasks with loader-backed prompts
The `mohist/openspec-tasks` loader SHALL remain responsible for reading `tasks.json` and expanding each task item into one runtime `mohist/acp-agent` task. When the caller has not supplied `task.with.prompt`, the loader SHALL inject a `with.prompt` object that uses `mohist/openspec-task-prompt` with configuration for the task file, items path, base prompt, and per-task selector. The loader SHALL NOT compose or set a literal prompt string in that default path.

#### Scenario: Loader injects prompt loader spec when prompt is absent
- **WHEN** `mohist/openspec-tasks` expands a task item and the configured task template does not provide `task.with.prompt`
- **THEN** the generated runtime task SHALL use `mohist/acp-agent`
- **AND** its `with.prompt.uses` SHALL be `mohist/openspec-task-prompt`
- **AND** its prompt loader configuration SHALL include the task file, items path, base prompt when configured, and a per-task `taskId` selector when available

#### Scenario: Caller prompt override is preserved
- **WHEN** `mohist/openspec-tasks` expands a task item and the configured task template provides `task.with.prompt`
- **THEN** the loader SHALL preserve that prompt value whether it is a string, plain object, or loader-backed object
- **AND** it SHALL NOT overwrite it with the built-in OpenSpec task prompt loader

#### Scenario: Loader does not template-render task JSON content
- **WHEN** `mohist/openspec-tasks` reads task data containing literal template syntax inside task fields
- **THEN** the loader SHALL NOT embed those task fields into generated `with` data for template rendering
- **AND** the task content SHALL remain in the JSON file for `mohist/openspec-task-prompt` to read at prompt-resolution time

### Requirement: Default OpenSpec build workflow delegates prompt composition
The built-in default OpenSpec build workflow SHALL configure `mohist/openspec-tasks` so default Build-stage agent prompts are composed lazily by `mohist/openspec-task-prompt`. Existing `mohist/openspec-tasks` configuration keys SHALL remain backward compatible, and explicit `task.with.prompt` values SHALL take precedence over the default injected prompt loader spec.

#### Scenario: Default build workflow uses prompt loader shape
- **WHEN** the system loads the built-in default workflow definition
- **THEN** the Build stage OpenSpec task loading configuration SHALL be expressible with `task.uses: mohist/acp-agent`
- **AND** `task.with.prompt.uses` SHALL be `mohist/openspec-task-prompt`
- **AND** the prompt loader configuration SHALL pass the task file, items path, and base build prompt

#### Scenario: Existing OpenSpec task loader keys remain valid
- **WHEN** a workflow definition uses the existing `mohist/openspec-tasks` input keys
- **THEN** the loader SHALL continue to read the configured task file and generate runtime tasks
- **AND** only prompt composition SHALL move from the loader action to the prompt loader resolution path
