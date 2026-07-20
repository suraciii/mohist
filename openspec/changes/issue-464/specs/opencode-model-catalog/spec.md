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

For each model entry in verbose output, the runner SHALL retain the model's full identifier. When the entry's JSON metadata contains a `variants` object, the runner SHALL expose every key of that object as that model's variant list, preserving each key's exact text. Missing, empty, or non-object `variants` data SHALL produce no variants for that model and MUST NOT remove the model from the catalog.

#### Scenario: A model reports reasoning variants
- **WHEN** a model entry has `variants` keys `low`, `medium`, `high`, and `max`
- **THEN** the discovered model SHALL expose `low`, `medium`, `high`, and `max` as its variants

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

If the command cannot start, exits unsuccessfully, is aborted, or produces no parseable model entries, discovery SHALL log the failure and return an empty model list and empty variant map without throwing to its caller. A malformed metadata block for one identifiable model SHALL yield no variants for that model and MUST NOT prevent later valid model entries from being parsed.

#### Scenario: Command execution fails
- **WHEN** the selected command is missing or exits unsuccessfully
- **THEN** discovery SHALL return an empty model list and empty variant map
- **AND** it SHALL report the failure diagnostically without throwing to the caller

#### Scenario: Output has no parseable model entries
- **WHEN** the command succeeds but its stdout contains no parseable model entries
- **THEN** discovery SHALL return an empty model list and empty variant map

#### Scenario: One model has malformed metadata
- **WHEN** one identifiable model entry has malformed JSON metadata and a later entry is valid
- **THEN** discovery SHALL retain the malformed entry's model identifier without variants
- **AND** it SHALL continue parsing the later valid entry and its variants
