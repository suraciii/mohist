### Requirement: Complete expression preserves JSON type
When a `${{ path }}` expression occupies an entire field value, the rendered result SHALL preserve the resolved value's JSON type (object, array, number, boolean, string, null). The runner SHALL NOT stringify the value when it stands alone.

#### Scenario: A whole-value expression resolves to an object
- **WHEN** a task input declares `options: ${{ vars.agent }}` and `vars.agent` is `{ "model": "gpt-5", "variant": "high" }`
- **THEN** the rendered `options` is that JSON object, not a serialized string

#### Scenario: A whole-value expression resolves to a number
- **WHEN** a task input declares `count: ${{ vars.replicas }}` and `vars.replicas` is `3`
- **THEN** the rendered `count` is the JSON number `3`

### Requirement: String interpolation accepts only scalars
When a `${{ path }}` expression is embedded in a larger string alongside other text, the resolved value SHALL be converted to text and concatenated only when it is a scalar (string, number, boolean) or null. An embedded expression that resolves to an object or array SHALL fail the task.

#### Scenario: An embedded scalar is concatenated
- **WHEN** a Prompt body contains `openspec/changes/issue-${{ issue.number }}` and `issue.number` is `431`
- **THEN** the rendered text is `openspec/changes/issue-431`

#### Scenario: An embedded object fails
- **WHEN** a string field contains `model is ${{ vars.agent }}` and `vars.agent` resolves to an object
- **THEN** the task fails because an object cannot be embedded in a string

### Requirement: Unresolvable expression fails the task
Any `${{ path }}` expression that does not resolve against the attempt snapshot SHALL fail the task and SHALL identify the offending expression. This applies equally to expressions that occupy an entire field value and to expressions embedded in a larger string. A missing `${{ tasks.<id>.outputs.* }}` path SHALL fail rather than resolving to an empty string, null, or the literal reference text.

#### Scenario: A whole-value unresolvable reference fails
- **WHEN** a task input declares `title: ${{ vars.missing }}` and `vars.missing` does not exist
- **THEN** the task fails and the error identifies `${{ vars.missing }}` as unresolved

#### Scenario: An embedded unresolvable reference fails
- **WHEN** a Prompt body contains `see ${{ vars.missing }} for details` and `vars.missing` does not exist
- **THEN** the task fails rather than leaving the literal `${{ vars.missing }}` text in place

#### Scenario: A missing task output fails
- **WHEN** a task input references `${{ tasks.unknown.outputs.result }}` and no task with id `unknown` has produced output
- **THEN** the task fails; it does not receive an empty string or the literal reference

### Requirement: Escape produces literal opening braces
The sequence `\${{` in a field value or Prompt body SHALL produce the literal text `${{` in the rendered output, suppressing template expansion for that occurrence.

#### Scenario: Escaped braces survive rendering
- **WHEN** a Prompt body contains `use \${{ vars.foo }} to reference a variable`
- **THEN** the rendered output contains the literal text `${{ vars.foo }}` without expansion

### Requirement: Nested expansion has a deterministic stop
When a rendered value itself contains `${{ }}` references, the renderer SHALL continue expanding up to a fixed depth limit. Expansion SHALL fail deterministically if it exceeds the allowed depth or detects a reference cycle. The renderer SHALL NOT leave partially expanded or unexpanded reference text in the output.

#### Scenario: A value chains through another reference
- **WHEN** `vars.alias` resolves to `${{ vars.real }}` and `vars.real` resolves to `done`
- **THEN** the rendered result is `done`

#### Scenario: A reference cycle fails
- **WHEN** `vars.a` resolves to `${{ vars.b }}` and `vars.b` resolves to `${{ vars.a }}`
- **THEN** the task fails rather than expanding indefinitely or leaving unexpanded text

#### Scenario: Exceeding the depth limit fails
- **WHEN** a chain of references exceeds the renderer's maximum expansion depth
- **THEN** the task fails with a message indicating the depth limit was exceeded

### Requirement: Unified rendering behavior across entry points
Task input rendering, live-read Prompt body rendering, and the retained preview or extract entry points SHALL apply the same rendering rules: JSON type preservation for complete expressions, scalar-only string interpolation, fail-fast on unresolvable references, `\${{` escape, and deterministic nested-expansion stop. No entry point SHALL retain a separate set of missing-value, type-coercion, or escape rules.

#### Scenario: Preview matches execution rendering
- **WHEN** a Prompt body containing `openspec/changes/issue-${{ issue.number }}` and an unresolvable `${{ vars.missing }}` is rendered through the preview entry point and through the task execution entry point
- **THEN** both produce the same concatenated path text for the resolvable expression and both fail on the unresolvable expression

#### Scenario: No entry point leaves unresolvable text
- **WHEN** a Prompt body with an unresolvable embedded reference is rendered through any retained entry point
- **THEN** the entry point fails or reports the missing reference; it does not silently leave the literal `${{ }}` text

### Requirement: Inline Agent template parity
The template language SHALL NOT depend on the Action name or the execution Runtime. A task declaring an Inline Agent Action (`mohist/opencode` or `mohist/pi`) SHALL receive identical template rendering, Prompt behavior, completion requirements, and error semantics regardless of which Action `uses` is declared. Switching `uses` between `mohist/opencode` and `mohist/pi` SHALL NOT change the rendered input, the resolved Prompt, the expected artifacts, or the failure semantics.

#### Scenario: Switching uses produces identical rendering
- **WHEN** the same task definition with `uses: mohist/opencode` is changed to `uses: mohist/pi` while all other declaration and snapshot inputs are identical
- **THEN** the rendered `with`, the resolved Prompt body, the `expect` contract, and the error behavior on an unresolvable reference are identical

#### Scenario: Both inline agents share the dispatch validation surface
- **WHEN** the server validates dispatch input for an inline-agent task
- **THEN** `mohist/opencode` and `mohist/pi` SHALL be treated as the same inline-agent Action set; neither bypasses input validation that the other receives

### Requirement: failure namespace is recovery-task-only
The `${{ failure.* }}` namespace SHALL be available only in recovery handler tasks. Expressions referencing `failure.output`, `failure.error.code`, or `failure.error.message` in a non-recovery task SHALL not resolve and SHALL fail the task. The recovery expansion semantics for `failure.*` SHALL remain unchanged: whole-value references preserve JSON type, embedded references stringify scalars, and unresolvable `failure.*` paths fail the recovery task.

#### Scenario: failure in a non-recovery task fails
- **WHEN** a regular (non-recovery) task input references `${{ failure.output }}`
- **THEN** the task fails because the failure context is not available outside recovery tasks

#### Scenario: failure in a recovery task resolves
- **WHEN** a recovery handler task references `${{ failure.error.code }}` and the triggering attempt produced an error
- **THEN** the expression resolves to the triggering error code with unchanged recovery semantics
