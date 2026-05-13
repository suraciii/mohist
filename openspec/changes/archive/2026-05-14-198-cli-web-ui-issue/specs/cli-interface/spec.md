## MODIFIED Requirements

### Requirement: REQ-CLI-198-001 CLI issue create supports model on initial creation

`mo issue create` SHALL accept `--model <provider/model>` and send that value in the initial `POST /api/issues` request alongside title, body, labels, and priority when provided.

#### Scenario: Create issue with model
- **WHEN** the user runs `mo issue create "Fix login bug" --model anthropic/claude-sonnet`
- **THEN** the CLI sends `model: "anthropic/claude-sonnet"` in the create request body
- **AND** the created issue is shown as created successfully

#### Scenario: Create issue with body source and model
- **WHEN** the user runs `mo issue create "Fix login bug" --body @body.md --model anthropic/claude-sonnet`
- **THEN** the CLI resolves the body source before sending the request
- **AND** the same create request includes the resolved body text and `model`

#### Scenario: Invalid model format from create path
- **WHEN** the user runs `mo issue create "Fix login bug" --model invalid-model`
- **THEN** the CLI surfaces the API error clearly
- **AND** exits with status code 1
