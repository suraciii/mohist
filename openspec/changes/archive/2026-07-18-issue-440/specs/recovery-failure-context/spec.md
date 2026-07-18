### Requirement: Failure context is sourced from the triggering task's structured output

The runner SHALL build a failure context for each recovery task exclusively from the structured `output` of the task that triggered the recovery (the same parsed JSON object the recovery handler's `when` matcher reads). `failure.output` SHALL resolve to that parsed object; `failure.output.<field>` SHALL resolve to the named field of that object, preserving the field's JSON type. When the triggering action emitted no `output`, a non-object `output`, or an unparseable `output`, the failure context SHALL be empty and every `${{ failure.* }}` path SHALL be treated as unresolved.

#### Scenario: failure.output field resolves to triggering output value

- **WHEN** a recovery task references `${{ failure.output.prNumber }}`
- **AND** the triggering action emitted `output: {"errorCode":"pr-checks-failed","prNumber":42,"prUrl":"https://example/pr/42","message":"..."}`
- **THEN** the expanded recovery task SHALL carry the value `42` in place of `${{ failure.output.prNumber }}`

#### Scenario: Whole-string failure.output preserves JSON type

- **WHEN** a recovery task references `${{ failure.output }}` as the entire value of a field
- **AND** the triggering action emitted a structured object `output`
- **THEN** the expanded recovery task SHALL carry that object with its JSON type preserved (object, array, number, or boolean)
- **AND** SHALL NOT flatten the object to its serialized string form

#### Scenario: Non-object triggering output yields empty failure context

- **WHEN** the triggering action emitted `output` that is missing, `null`, a non-JSON string, or a JSON scalar
- **THEN** the failure context SHALL be considered empty
- **AND** every `${{ failure.* }}` reference in any recovery handler task SHALL be treated as an unresolved path

### Requirement: Recovery tasks reach the engine with `${{ failure.* }}` fully expanded

The runner SHALL expand every `${{ failure.* }}` reference in a recovery handler task — including references that appear inside the body of a `${{ prompts.<key> }}` reference the recovery task declares — against the failure context before the recovery task is delivered to the engine. The `addTasks` entry the runner returns for the recovery handler SHALL NOT contain any literal `${{ failure.* }}` expression in any field, including the rendered prompt body. Template references in the recovery task under any other namespace (`vars.*`, `workspace.*`, `stage.*`, `repository.*`, `prompts.<key>`, etc.) SHALL pass through the failure-context expansion unchanged and SHALL continue to follow the same dispatch-time and execution-time rules that apply to ordinary tasks.

#### Scenario: failure reference inside prompt body is expanded before engine delivery

- **WHEN** a recovery handler task declares `with.prompt: ${{ prompts.fix-pr-checks }}`
- **AND** the named prompt body contains the text `failed for PR #${{ failure.output.prNumber }} (${{ failure.output.prUrl }})`
- **AND** the triggering action emitted `output.prNumber = 42` and `output.prUrl = "https://example/pr/42"`
- **THEN** the recovery task delivered to the engine SHALL carry the resolved prompt body with both `${{ failure.output.* }}` references replaced by their actual values
- **AND** the delivered task SHALL NOT contain any residual `${{ failure.* }}` text in its prompt body or any other field

#### Scenario: failure reference in a direct with field is expanded

- **WHEN** a recovery handler task declares `with.targetPr: ${{ failure.output.prNumber }}`
- **AND** the triggering action emitted `output.prNumber = 42`
- **THEN** the expanded recovery task's `with.targetPr` SHALL equal `42`

#### Scenario: Non-failure template references pass through unchanged

- **WHEN** a recovery handler task declares `with.options: ${{ vars.agent }}` and `with.session: ${{ stage.name }}`
- **THEN** the failure-context expansion SHALL leave both placeholders byte-for-byte unchanged in the recovery task delivered to the engine
- **AND** the placeholders SHALL be resolved later by the same dispatch-time rules that apply to ordinary tasks

### Requirement: Unresolved failure paths fail dispatch with an actionable diagnostic

When any recovery handler task references a `${{ failure.* }}` path that is absent from the failure context — whether the reference occupies an entire field value or is embedded inside a larger string — the runner SHALL produce a failure outcome carrying a diagnostic that names the unresolvable reference and the recovery task it appears in. The runner SHALL NOT deliver the recovery task to the engine carrying literal `${{ failure.* }}` text, SHALL NOT substitute an empty value silently, and SHALL NOT invoke the recovery action with unresolved `${{ failure.* }}` text in its prompt body. This strictness SHALL apply only to the `failure.*` namespace.

#### Scenario: Missing failure.output sub-path in prompt body fails dispatch

- **WHEN** a recovery handler task's prompt body references `${{ failure.output.prNumber }}`
- **AND** the triggering action's structured `output` does not contain a `prNumber` field
- **THEN** the runner SHALL fail the recovery task with a diagnostic that names `${{ failure.output.prNumber }}` as unresolvable
- **AND** SHALL NOT deliver the recovery task to the engine with the literal reference text in its prompt body
- **AND** SHALL NOT invoke the recovery action

#### Scenario: Missing failure.output root fails dispatch

- **WHEN** a recovery handler task references `${{ failure.output.message }}`
- **AND** the triggering action emitted no structured `output` (or a non-object `output`)
- **THEN** the runner SHALL fail the recovery task with a diagnostic that names `${{ failure.output.message }}` as unresolvable
- **AND** SHALL NOT deliver the recovery task to the engine

#### Scenario: Embedded unresolved failure reference fails dispatch

- **WHEN** a recovery handler task's prompt body embeds the reference inside larger text (e.g. `PR #${{ failure.output.prNumber }}`)
- **AND** the referenced path is absent from the failure context
- **THEN** the runner SHALL fail the recovery task with a diagnostic that names the unresolvable reference
- **AND** SHALL NOT leave the literal `${{ failure.output.prNumber }}` text in the delivered prompt body

### Requirement: Non-recovery task rendering is unchanged

Tasks that are not recovery handler tasks (ordinary stage tasks, approval-feedback tasks, runtime-generated subtasks, `retry: self` attempts of a previously-failed task) SHALL NOT receive a failure context, and `${{ failure.* }}` SHALL NOT resolve in their template rendering. Template rendering for every namespace other than `failure.*` SHALL preserve its existing behavior, including the rule that an embedded reference whose path cannot be resolved is left as literal text rather than producing an error.

#### Scenario: Ordinary task does not resolve failure references

- **WHEN** a non-recovery task's `with` or prompt body contains the text `${{ failure.output.prNumber }}`
- **THEN** the runner SHALL NOT attempt to substitute a value for that reference during the non-recovery task's rendering
- **AND** the existing definition-validation rule that rejects `failure.*` outside recovery handler tasks SHALL continue to apply

#### Scenario: Embedded unresolved non-failure references remain literal text

- **WHEN** a non-recovery task embeds a reference under a non-`failure` namespace whose path cannot be resolved against the dispatch context (e.g. `see ${{ docs.somewhere }}`)
- **THEN** the runner SHALL leave the literal `${{ docs.somewhere }}` text in place
- **AND** SHALL NOT fail dispatch, preserving the pre-existing tolerance rule for embedded unresolved references outside the `failure.*` namespace
