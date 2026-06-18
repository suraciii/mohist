## ADDED Requirements

### Requirement: Resolved prompts carry no code-injected issue context

The resolved prompt delivered to a workflow agent SHALL consist solely of the prompt template body together with any declared `PromptLoader` output. No code path SHALL inject the issue title or issue body into the prompt text — neither as a markdown preamble, an XML section, a wrapper envelope, nor any other assembled addition sourced from issue fields. Issue context SHALL be obtained by the agent at runtime by executing `mo issue show` (or equivalent CLI) instructions embedded in the prompt template itself, so the template remains the single source of truth for what context the agent fetches. Interpolation of the `issue.number` and `project.id` variables used solely to construct those CLI commands is permitted and is not considered issue-context injection.

#### Scenario: Issue title and body are absent from the resolved prompt

- **WHEN** a workflow task runs against an issue that has a title and body
- **AND** the task's prompt is resolved through `resolvePrompt`
- **THEN** the prompt text delivered to the agent SHALL NOT contain the issue title or the issue body
- **AND** no code path SHALL prepend, append, or embed a context preamble assembled from issue fields

#### Scenario: Issue context is fetched via a CLI instruction embedded in the template

- **WHEN** a built-in `.prompt` template requires issue context to perform its task
- **THEN** the template body SHALL include a `mo issue show` instruction (or equivalent CLI) that references the issue
- **AND** the agent SHALL obtain issue context by executing that instruction at runtime
- **AND** the prompt-assembly layer SHALL NOT supply that context by injecting it into the resolved prompt

#### Scenario: Issue number interpolation for CLI construction is permitted

- **WHEN** a prompt template interpolates `issue.number` or `project.id` solely to build a CLI command (for example `mo issue show ${{ issue.number }} --project-id ${{ project.id }}`)
- **THEN** that interpolation SHALL be allowed
- **AND** it SHALL NOT be treated as prohibited issue-context injection, because it constructs a command rather than injecting issue title or body content
