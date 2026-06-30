### Requirement: Built-in issue templates are file assets

The three built-in issue templates — Feature, Bug, and Refactor — SHALL be sourced exclusively from file assets at `Issue/Services/IssueTemplates/templates/*.md`. Each file SHALL consist of YAML frontmatter (carrying `name` and `description`) followed by a markdown body. The hardcoded `MohistDefaultIssueTemplate.cs` SHALL be deleted, and `IssueTemplateRegistry` SHALL load built-ins from these files at runtime. The server project file SHALL preserve the template files in the build output (`CopyToOutputDirectory="PreserveNewest"`) so the loader can read them in the deployed location.

#### Scenario: Built-ins loaded from files

- **WHEN** the issue-template registry initializes
- **THEN** the Feature, Bug, and Refactor templates SHALL be discovered from `templates/*.md`
- **AND** no built-in template content SHALL originate from a hardcoded C# class

#### Scenario: Files preserved in build output

- **WHEN** the server project is built
- **THEN** the `templates/*.md` files SHALL be copied to the output directory
- **AND** the deployed server SHALL be able to read them at runtime

### Requirement: Two-stage on-demand template loading

Template loading SHALL be split into two stages mirroring the skill discovery mechanism. Discovery (the `list` surface) SHALL parse frontmatter only and SHALL NOT parse the body. Detail retrieval (the `get` surface) SHALL parse both frontmatter and body. Both stages SHALL reuse `PromptFrontmatterParser` for frontmatter parsing. The `list` path SHALL NOT pay the cost of deserializing every template's full body.

#### Scenario: List parses frontmatter only

- **WHEN** a client requests the template directory
- **THEN** the server SHALL read each template file's frontmatter
- **AND** the server SHALL NOT parse or return template bodies
- **AND** the response SHALL contain only metadata for each template

#### Scenario: Get parses frontmatter and body

- **WHEN** a client requests a specific template by id
- **THEN** the server SHALL parse that template's frontmatter and body
- **AND** the response SHALL include the full body sections

### Requirement: Template metadata is name + description only

Every issue-template metadata representation — `IssueTemplateInfo` (list entries) and `IssueTemplateDetail` (the get response) — SHALL expose exactly two metadata fields: `name` and `description`. The fields `suitableFor`, `defaults`, `isDefault`, and the previous `about` field SHALL NOT appear in the template model. The `/api/issue-templates` response shape and the CLI/Web consumers SHALL be updated in lockstep with this trimmed metadata.

#### Scenario: List response shape

- **WHEN** a client calls `GET /api/issue-templates`
- **THEN** each entry SHALL expose only `name` and `description`
- **AND** the entry SHALL NOT include `suitableFor`, `defaults`, `isDefault`, or `about`

#### Scenario: Detail response shape

- **WHEN** a client calls `GET /api/issue-templates/:id`
- **THEN** the response SHALL expose `name`, `description`, and the parsed body
- **AND** the response SHALL NOT include `suitableFor`, `defaults`, `isDefault`, or `about`

### Requirement: Template selection is agent/human judgment, not programmatic matching

Template selection SHALL be an agent or human judgment made by reading each template's `description`; the system SHALL NOT programmatically match a template against an issue. The `SuitableForMatcher.Matches()` path SHALL NOT be invoked on the template path. The workflow-profile `Matches()` path SHALL remain unchanged.

#### Scenario: No programmatic matching on templates

- **WHEN** an agent or user chooses a template for an issue
- **THEN** the choice SHALL be made by reading template descriptions
- **AND** the server SHALL NOT run `SuitableForMatcher.Matches()` against templates

#### Scenario: Workflow-profile matching unaffected

- **WHEN** a workflow profile is resolved for an issue
- **THEN** `SuitableForMatcher.Matches()` SHALL continue to apply on the workflow-profile path
- **AND** the matcher's behavior on profiles SHALL be unchanged by this change

### Requirement: list and get surfaces are exposed via HTTP and CLI

The two-stage loading model SHALL be exposed symmetrically through HTTP endpoints and CLI commands. `GET /api/issue-templates` SHALL return the metadata-only list; `GET /api/issue-templates/:id` SHALL return the full detail. `mo issue template list` SHALL render only `name` + `description` per entry and SHALL NOT render body sections. `mo issue template get <name>` SHALL render the template's full sections.

#### Scenario: HTTP list endpoint

- **WHEN** a client calls `GET /api/issue-templates`
- **THEN** the server SHALL return one entry per built-in template
- **AND** each entry SHALL contain only `name` and `description`

#### Scenario: HTTP get endpoint

- **WHEN** a client calls `GET /api/issue-templates/feature`
- **THEN** the server SHALL return the Feature template's `name`, `description`, and parsed body

#### Scenario: CLI list command

- **WHEN** a user runs `mo issue template list`
- **THEN** the output SHALL show each template's name and description
- **AND** the output SHALL NOT include body sections

#### Scenario: CLI get command

- **WHEN** a user runs `mo issue template get feature`
- **THEN** the output SHALL render the Feature template's full sections

### Requirement: mohist/default is superseded by feature with a compatibility alias

The canonical built-in Feature template id SHALL be `feature`, superseding `mohist/default`. Existing references to `mohist/default` SHALL continue to resolve to the Feature template via a compatibility alias/shim, so callers that still reference `mohist/default` SHALL NOT break. The `IssueTemplates.DefaultId = "mohist/default"` constant SHALL be replaced by the `feature` alias as the source of truth.

#### Scenario: Feature resolves by canonical id

- **WHEN** a client requests the template whose canonical id is `feature`
- **THEN** the server SHALL return the Feature template

#### Scenario: Legacy mohist/default keeps resolving

- **WHEN** a client requests the template by the legacy id `mohist/default`
- **THEN** the server SHALL resolve it to the Feature template via the alias
- **AND** the response SHALL be identical to requesting `feature`
