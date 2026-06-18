# workflow-prompt-assembly Specification

### Requirement: Prompt assembly is governed by a single type-driven contract

The system SHALL assemble every workflow task prompt for LLM consumption through a single resolution entry point (`resolvePrompt`). The assembly format SHALL be determined exclusively by the type of the task's declared `prompt` input — no separate configuration, flag, or code path SHALL alter the format. The contract is: a string `prompt` SHALL pass through verbatim as text; an object `prompt` SHALL be rendered as XML; a loader `prompt` (`uses` + `with`) SHALL dispatch to a registered `PromptLoader` whose return value follows the same rule (string → text, object → XML). XML is NOT mandatory; it applies only when the `prompt` input is already an object.

#### Scenario: Text prompt passes through verbatim

- **WHEN** a task declares `prompt` as a string
- **THEN** the assembled prompt SHALL equal the input string byte-for-byte
- **AND** the system SHALL NOT wrap, prefix, suffix, or transform the text
- **AND** markdown fences, headers, or envelope sections SHALL NOT be injected

#### Scenario: Text prompt containing markdown is still passed through verbatim

- **WHEN** a task declares a `prompt` string that itself contains markdown (`#`, `##`, code fences, etc.)
- **THEN** the assembled prompt SHALL equal the input string unchanged
- **AND** the system SHALL NOT interpret or rewrap that markdown

#### Scenario: Object prompt is rendered as XML

- **WHEN** a task declares `prompt` as a JSON object
- **THEN** the system SHALL render the object into a single-root XML tag tree
- **AND** the assembled prompt SHALL be XML text, not a markdown envelope around the object

#### Scenario: Loader prompt dispatches to a registered PromptLoader

- **WHEN** a task declares `prompt` with `uses` (a loader name) and `with` (loader inputs)
- **THEN** the system SHALL resolve the loader by name from the `PromptLoaderRegistry`
- **AND** when the loader returns a string, that string SHALL be used verbatim as text
- **AND** when the loader returns an object, that object SHALL be rendered as XML

### Requirement: All prompt-building code paths route through the single entry point

No code path that produces a prompt for LLM consumption SHALL independently construct a markdown-wrapped or ad-hoc assembled prompt. Every path SHALL route through `resolvePrompt`, including the agent action entry point. The system SHALL NOT provide a markdown-envelope post-processing step that wraps an already-resolved prompt.

#### Scenario: Agent action does not post-wrap the resolved prompt

- **WHEN** the agent action resolves a task prompt through `resolvePrompt`
- **THEN** the resolved prompt SHALL be delivered to the agent session unchanged
- **AND** no `## Mohist Issue Context` / `## Task Prompt` markdown envelope SHALL be appended around it

#### Scenario: No ad-hoc fallback prompt synthesis

- **WHEN** a task does not declare a `prompt`
- **THEN** the system SHALL NOT synthesize a prompt from loose fields such as `title`, `description`, `acceptanceCriteria`, `dependsOn`, `output`, or `notes`
- **AND** the task SHALL be rejected with a clear error stating that a `prompt` is required

### Requirement: Built-in text prompt templates continue to work unchanged

The system SHALL preserve the behavior of existing text-based `.prompt` template bodies and inline YAML string prompts. They SHALL continue to be delivered as plain text with no transformation, independent of the assembly contract. The `.prompt` template authoring format and frontmatter SHALL NOT change.

#### Scenario: Existing .prompt template body is delivered as plain text

- **WHEN** a task references a built-in `.prompt` template whose rendered body is a string
- **THEN** the assembled prompt SHALL be that string verbatim
- **AND** no XML rendering SHALL be applied to it

#### Scenario: Inline YAML string prompt is delivered as plain text

- **WHEN** a workflow declares an inline `prompt` as a YAML string
- **THEN** the assembled prompt SHALL be that string verbatim
- **AND** it SHALL NOT be wrapped in markdown or rendered as XML

### Requirement: Structured prompts render as a single-root XML tree

When the `prompt` input is an object, the system SHALL render it via `renderStructuredPrompt` into a single-root XML tag tree. The object SHALL have exactly one root key that is a valid XML tag name. The renderer SHALL support nested elements, an `attrs` object for element attributes, arrays of primitive items, and primitive (string, number, boolean) leaf values. Tag names SHALL match `^[A-Za-z_][A-Za-z0-9_-]*$`.

#### Scenario: Single root key renders as root element

- **WHEN** a structured prompt object has exactly one root key
- **THEN** the rendered XML SHALL have that key as its single root element
- **AND** all other keys SHALL render as nested child elements

#### Scenario: Multiple or zero root keys are rejected

- **WHEN** a structured prompt object has zero root keys or more than one root key
- **THEN** the system SHALL raise an error naming the offending key count
- **AND** no partial XML SHALL be emitted

#### Scenario: Invalid root tag name is rejected

- **WHEN** the single root key is not a valid XML tag name
- **THEN** the system SHALL raise an error naming the invalid key
- **AND** no XML SHALL be emitted

#### Scenario: attrs object renders as element attributes

- **WHEN** a child element object contains an `attrs` map
- **THEN** each `attrs` entry SHALL render as an attribute on that element
- **AND** attribute values SHALL be string-quoted and `&` / `"` SHALL be escaped

#### Scenario: Arrays render as primitive list items

- **WHEN** a child value is an array
- **THEN** each item SHALL render as a `- ` list line inside the element
- **AND** non-primitive items SHALL be rejected with an error

### Requirement: PromptLoader registry resolves loaders by name

The system SHALL maintain a `PromptLoaderRegistry` that maps loader names (case-insensitive) to async loader functions returning `string | JsonObject`. A loader `prompt` SHALL be resolved by looking up its `uses` name in the registry and invoking the loader with the declared `with` inputs merged into the loader context. Unknown loader names and non-string `uses` values SHALL be rejected with clear errors.

#### Scenario: Registered loader is invoked with its inputs

- **WHEN** a loader prompt declares `uses: "my-loader"` and `with: { ... }`
- **AND** a loader named `my-loader` is registered
- **THEN** the system SHALL invoke that loader with the `with` inputs as the loader context
- **AND** the loader's return value SHALL be normalized per the type-driven contract

#### Scenario: Unknown loader name is rejected

- **WHEN** a loader prompt declares `uses: "missing-loader"`
- **AND** no loader is registered under that name
- **THEN** the system SHALL raise an error identifying the unknown loader name
- **AND** no prompt SHALL be assembled

#### Scenario: Non-object loader return value is rejected

- **WHEN** a registered loader returns a value that is neither a string nor a JSON object
- **THEN** the system SHALL raise an error naming the loader
- **AND** no prompt SHALL be assembled
