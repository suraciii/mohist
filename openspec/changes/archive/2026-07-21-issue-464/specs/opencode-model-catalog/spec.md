### Requirement: The OpenCode CLI is the model-catalog authority

The runner SHALL discover available OpenCode models by executing the selected OpenCode command with `models --verbose`. Each discovery invocation MUST execute the command and parse that invocation's output; the runner MUST NOT satisfy discovery from a time-based cache or from the OpenCode SDK v2 model catalog.

#### Scenario: Discovery invokes the verbose models command
- **WHEN** the runner performs model discovery
- **THEN** it SHALL execute the selected command with the arguments `models --verbose`
- **AND** it SHALL build the result from that command's stdout

#### Scenario: Consecutive discoveries execute independently
- **WHEN** model discovery is invoked twice
- **THEN** the underlying command SHALL execute twice
- **AND** each result SHALL reflect its own command output

#### Scenario: SDK catalog differs from the CLI
- **WHEN** the OpenCode SDK v2 catalog reports no variants but `opencode models --verbose` reports variants
- **THEN** the discovered catalog SHALL contain the variants reported by the CLI
- **AND** the runner MUST NOT replace them with SDK catalog data

### Requirement: Model-discovery command overrides retain their precedence

The runner SHALL select the executable for model discovery from the first configured value in this order: `MOHIST_AGENT_MODELS_COMMAND`, `MOHIST_AGENT_COMMAND`, then `opencode`.

#### Scenario: Models-specific command is configured
- **WHEN** both `MOHIST_AGENT_MODELS_COMMAND` and `MOHIST_AGENT_COMMAND` are configured
- **THEN** discovery SHALL execute `MOHIST_AGENT_MODELS_COMMAND`

#### Scenario: Only the general agent command is configured
- **WHEN** `MOHIST_AGENT_MODELS_COMMAND` is not configured
- **AND** `MOHIST_AGENT_COMMAND` is configured
- **THEN** discovery SHALL execute `MOHIST_AGENT_COMMAND`

#### Scenario: No command override is configured
- **WHEN** neither command override variable is configured
- **THEN** discovery SHALL execute `opencode`

### Requirement: Verbose output yields models and reasoning variants

A model entry SHALL begin with a trimmed header matching `provider/modelID`: the provider is one or more non-whitespace, non-`/` characters before the first `/`, and the model ID is the non-whitespace remainder after that slash, including any additional `/` characters. Lines that do not match this grammar outside a metadata block SHALL be ignored. The runner SHALL retain the full trimmed header for every valid entry.

After a valid header, the parser SHALL skip blank lines and inspect the next non-blank line. A line beginning with `{` starts that model's JSON metadata block; the block ends at its balanced closing brace while respecting JSON strings and escapes. If the next non-blank line is another valid header or end-of-output, the prior model is a valid flat-list entry without metadata. This preserves support for flat `provider/modelID` lists even though discovery requests verbose output.

When a complete metadata object contains a `variants` object, the runner SHALL expose every key of that object as that model's variant list, preserving each key's exact text. Missing, empty, or non-object `variants` data SHALL produce no variants for that model and MUST NOT remove the model from the catalog.

#### Scenario: A model reports reasoning variants
- **WHEN** header `openai/gpt-5` is followed by complete JSON metadata with `variants` keys `low`, `medium`, `high`, and `max`
- **THEN** the discovered model SHALL expose `low`, `medium`, `high`, and `max` as its variants

#### Scenario: A model ID contains additional slashes
- **WHEN** verbose output contains header `openrouter/vendor/family/model`
- **THEN** the discovered catalog SHALL retain `openrouter/vendor/family/model` as the complete model identifier

#### Scenario: Flat model list is accepted
- **WHEN** output contains valid headers `openai/gpt-5` and `anthropic/claude-sonnet-4` without JSON metadata
- **THEN** the discovered catalog SHALL contain both models without variants

#### Scenario: Non-model lines are ignored
- **WHEN** output contains only `warning: provider unavailable` and `{ broken output`
- **THEN** neither line SHALL be treated as a model header
- **AND** discovery SHALL return an empty model list and empty variant map

#### Scenario: A model has no variants
- **WHEN** a model entry omits `variants`, contains an empty `variants` object, or contains a non-object `variants` value
- **THEN** the discovered catalog SHALL include the model
- **AND** it SHALL expose no variant entry for that model

#### Scenario: Variant names are provider-defined
- **WHEN** a model reports a variant key not known to Mohist
- **THEN** the runner SHALL report that key unchanged
- **AND** it MUST NOT localize, rename, or filter the key

### Requirement: Discovery parses the complete command output

The command boundary MUST collect all stdout written by `opencode models --verbose`, including data buffered when the child process exits, before parsing or resolving discovery. The parser SHALL support model metadata represented as complete JSON objects spanning one or more lines.

#### Scenario: Output exceeds an intermediate pipe chunk
- **WHEN** the command writes a catalog across multiple stdout chunks and exits after writing the final models
- **THEN** discovery SHALL include models and variants from the complete stdout
- **AND** it MUST NOT silently truncate trailing providers or models

#### Scenario: Model metadata spans multiple lines
- **WHEN** a model's JSON metadata is formatted across multiple lines
- **THEN** discovery SHALL parse the complete JSON object
- **AND** it SHALL extract the `variants` keys from that object

### Requirement: Catalog discovery failures return an empty result

If the command cannot start, exits unsuccessfully, is aborted, or produces no valid `provider/modelID` headers, discovery SHALL log the failure and return an empty model list and empty variant map without throwing to its caller. A valid header remains a model when its metadata is malformed. After a balanced but invalid JSON block, scanning SHALL resume on the line after that block. After an unbalanced block, scanning SHALL resume at the next subsequent line that independently matches the model-header grammar; that header MUST NOT be consumed as part of the malformed entry.

#### Scenario: Command execution fails
- **WHEN** the selected command is missing or exits unsuccessfully
- **THEN** discovery SHALL return an empty model list and empty variant map
- **AND** it SHALL report the failure diagnostically without throwing to the caller

#### Scenario: Output has no valid model headers
- **WHEN** the command succeeds but its stdout contains no line matching the `provider/modelID` header grammar
- **THEN** discovery SHALL return an empty model list and empty variant map

#### Scenario: One model has balanced but invalid metadata
- **WHEN** header `openai/gpt-5` is followed by a balanced block `{ invalid }`
- **AND** a later entry `anthropic/claude-sonnet-4` has valid metadata
- **THEN** discovery SHALL retain the malformed entry's model identifier without variants
- **AND** it SHALL continue parsing the later valid entry and its variants

#### Scenario: One model has an unbalanced metadata block
- **WHEN** header `openai/gpt-5` is followed by an unbalanced JSON block
- **AND** a later line `anthropic/claude-sonnet-4` independently matches the model-header grammar
- **THEN** discovery SHALL retain `openai/gpt-5` without variants
- **AND** it SHALL resume at and parse `anthropic/claude-sonnet-4` as a separate model entry
