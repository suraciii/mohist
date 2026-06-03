## ADDED Requirements

### Requirement: ACP agent tasks accept unified prompt specs
The `mohist/acp-agent` task handler SHALL resolve `with.prompt` as a unified prompt specification before sending the prompt to the ACP agent. A string prompt SHALL be used byte-for-byte as provided. A plain object prompt SHALL be rendered through the default structured prompt renderer. An object prompt containing `uses` SHALL be resolved through the registered prompt loader with that name, and the loader result SHALL then be used directly when it is a string or rendered through the default structured prompt renderer when it is an object.

#### Scenario: String prompt remains unchanged
- **WHEN** an ACP agent task runs with `with.prompt` set to a string
- **THEN** the resolved task prompt SHALL equal that string byte-for-byte before the existing Mohist issue context wrapper is applied

#### Scenario: Plain object prompt is rendered
- **WHEN** an ACP agent task runs with `with.prompt` set to an object that does not contain `uses`
- **THEN** the task handler SHALL render that object to stable XML-like prompt text
- **AND** it SHALL apply the existing Mohist issue context wrapper around the rendered prompt

#### Scenario: Loader-backed prompt is resolved
- **WHEN** an ACP agent task runs with `with.prompt.uses` set to a registered prompt loader name
- **THEN** the task handler SHALL invoke that prompt loader with the loader `with` configuration, resolved workflow variables, work directory, work id, title, stage, and issue number context
- **AND** it SHALL resolve the loader result to final prompt text before applying the existing Mohist issue context wrapper

#### Scenario: Missing prompt uses legacy fallback
- **WHEN** an ACP agent task runs without `with.prompt`
- **THEN** the task handler SHALL continue to build the legacy fallback prompt from the task context

### Requirement: Structured prompt renderer produces stable LLM-friendly text
The runner SHALL provide a default structured prompt renderer for prompt objects. The renderer SHALL produce stable XML-like text for LLM consumption and SHALL NOT be treated as a strict XML serializer or round-trip-safe interchange format.

#### Scenario: Object root and attributes are rendered
- **WHEN** a structured prompt object contains a root block with `attrs`
- **THEN** the renderer SHALL emit an opening root tag with stable attributes
- **AND** it SHALL emit a matching closing root tag

#### Scenario: Nested text and lists are rendered predictably
- **WHEN** a structured prompt object contains nested blocks, string values, or list values
- **THEN** the renderer SHALL emit stable whitespace and ordering suitable for unit-test assertions
- **AND** list values SHALL be represented as readable prompt content rather than embedded JSON template input

### Requirement: Built-in OpenSpec task prompt loader composes selected task prompts
The runner SHALL register a built-in `mohist/openspec-task-prompt` prompt loader. The loader SHALL read a JSON task file relative to the work directory, locate the task array using a dotted `items` path that defaults to `tasks`, select a task by `taskId` when present or by zero-based `index` otherwise, and return a composed prompt containing optional base instructions and the selected task data.

#### Scenario: Select task by taskId
- **WHEN** the OpenSpec task prompt loader is invoked with `taskId`
- **THEN** it SHALL select the first task whose `id` or `taskId` matches that value
- **AND** it SHALL prefer this selection over any provided `index`

#### Scenario: Select task by index
- **WHEN** the OpenSpec task prompt loader is invoked without `taskId` and with `index`
- **THEN** it SHALL select the task at that zero-based array index

#### Scenario: Missing selector fails clearly
- **WHEN** the OpenSpec task prompt loader is invoked without both `taskId` and `index`
- **THEN** it SHALL fail with an error that clearly states a task selector is required

#### Scenario: Missing file fails clearly
- **WHEN** the OpenSpec task prompt loader is invoked with a `file` path that does not exist relative to the work directory
- **THEN** it SHALL fail with an error that clearly identifies the missing prompt task file

#### Scenario: Missing items path fails clearly
- **WHEN** the OpenSpec task prompt loader is invoked with an `items` path that does not resolve to a task array in the JSON file
- **THEN** it SHALL fail with an error that clearly identifies the missing or invalid task array path

#### Scenario: Missing selected task fails clearly
- **WHEN** the OpenSpec task prompt loader is invoked with a selector that does not match any task
- **THEN** it SHALL fail with an error that clearly identifies the missing selected task

#### Scenario: Task JSON content remains opaque until prompt loading
- **WHEN** a selected task field contains a literal template expression such as `${{ prompts.xxx }}`
- **THEN** the task JSON content SHALL be preserved as data until the OpenSpec task prompt loader reads and composes it
- **AND** the task loader SHALL NOT template-render that task JSON content while generating runtime tasks

#### Scenario: Base instructions are embedded
- **WHEN** the OpenSpec task prompt loader is invoked with `base`
- **THEN** the composed prompt SHALL include that base prompt alongside the selected task content
