### Requirement: Project-scoped routing table

A routing rule SHALL belong to exactly one project. Each project SHALL have exactly one routing table that contains all of its rules. A rule SHALL reference a response Agent but SHALL NOT be owned by that Agent; cross-Agent ordering (fallback and overtake) SHALL be expressed as table-level position, not as an Agent attribute. Every rule operation SHALL be scoped to its project.

#### Scenario: Rule is scoped to a project

- **WHEN** a rule is created in project P
- **THEN** the rule SHALL be stored against project P
- **AND** SHALL NOT be visible or addressable from any other project

#### Scenario: Agent does not own rules

- **WHEN** an Agent referenced by a rule is archived
- **THEN** the rule SHALL remain in the table at its current position
- **AND** the table ordering SHALL be unchanged

### Requirement: Ordered rules with append-default and explicit reordering

Each rule SHALL carry a Position that defines its place in the table. Listing and evaluation SHALL present rules in ascending Position order. A newly created rule SHALL be appended to the end of the table by default. `mo routing rule move --before <rule>` SHALL move the target rule to the position immediately before the referenced rule; `mo routing rule move --after <rule>` SHALL move it to the position immediately after. After any create or move, the positions SHALL form a single strict ascending sequence with no gaps and no ties.

#### Scenario: New rule appends to the end

- **WHEN** a project already contains rules at positions 1, 2, and 3 and a new rule is created without ordering options
- **THEN** the new rule SHALL receive position 4

#### Scenario: Move before reorders the table

- **WHEN** an operator moves rule C to `--before` rule A in a table ordered A, B, C
- **THEN** the resulting order SHALL place C immediately before A
- **AND** positions SHALL form a single strict ascending sequence with no ties

#### Scenario: Move after reorders the table

- **WHEN** an operator moves rule A to `--after` rule C in a table ordered A, B, C
- **THEN** the resulting order SHALL place A immediately after C
- **AND** positions SHALL form a single strict ascending sequence with no ties

### Requirement: Active and archived lifecycle

A rule SHALL be in exactly one status, `active` or `archived`. Only `active` rules SHALL participate in dispatch and dry-run evaluation. `mo routing rule archive` SHALL transition a rule to `archived`. Archiving SHALL NOT delete the rule and SHALL NOT change its position. Archiving SHALL be idempotent: archiving an already-archived rule SHALL succeed without changing its state or position.

#### Scenario: Archived rule is excluded from evaluation

- **WHEN** an event arrives that would match an archived rule
- **THEN** the archived rule SHALL NOT be evaluated
- **AND** SHALL NOT trigger its Agent

#### Scenario: Archive is idempotent

- **WHEN** an operator archives a rule that is already archived
- **THEN** the operation SHALL succeed
- **AND** the rule's status and position SHALL remain unchanged

### Requirement: Rule fields

A rule SHALL carry: a name that is unique within the project table, a match expression, a reference to a response Agent, a response prompt template, and a `continue` flag. The match expression SHALL use the event envelope expression grammar. The response prompt template SHALL accept `{{event.<attr>}}` placeholders. The `continue` flag SHALL default to `false` when not specified.

#### Scenario: Continue defaults to false

- **WHEN** a rule is created without specifying the continue flag
- **THEN** the rule's continue flag SHALL be `false`

### Requirement: Write-time validation rejects invalid rules

Creating or updating a rule SHALL be rejected, with an error that names the cause, when any of the following holds: the match expression does not compile; the referenced Agent does not exist in the project; the referenced Agent is not `active`; the response prompt is blank. A rejected create SHALL NOT store a rule. A rejected update SHALL NOT change the stored rule's fields or position.

#### Scenario: Non-compiling expression is rejected

- **WHEN** an operator creates a rule whose match expression is `(event.type == "x"`
- **THEN** the create SHALL be rejected with an error identifying the expression as invalid
- **AND** no rule SHALL be stored

#### Scenario: Non-existent Agent is rejected

- **WHEN** an operator creates a rule that references an Agent that does not exist in the project
- **THEN** the create SHALL be rejected with an error identifying the Agent as the cause
- **AND** no rule SHALL be stored

#### Scenario: Archived Agent is rejected

- **WHEN** an operator creates or updates a rule that references an Agent whose status is `archived`
- **THEN** the operation SHALL be rejected with an error identifying the Agent as archived
- **AND** no rule SHALL be stored or changed

#### Scenario: Blank response prompt is rejected

- **WHEN** an operator creates or updates a rule whose response prompt is blank
- **THEN** the operation SHALL be rejected with an error identifying the response prompt as the cause
- **AND** no rule SHALL be stored or changed

#### Scenario: Rejected update leaves rule unchanged

- **WHEN** an operator updates an existing rule with a non-compiling match expression
- **THEN** the update SHALL be rejected
- **AND** the stored rule's fields and position SHALL remain as they were before the attempt

### Requirement: Routing rule command surface

The `mo routing rule` command SHALL expose `create`, `list`, `show`, `update`, `archive`, and `move` subcommands. `create` SHALL accept `--name`, `--match`, `--agent`, `--response-prompt`, optional `--continue`, and optional `--before <rule>` / `--after <rule>`. `list` SHALL enumerate the rules of the selected project in ascending Position order. `show` SHALL display a single rule. `update` SHALL accept the same mutable fields as `create`. `move` SHALL accept `--before <rule>` or `--after <rule>`. All rule commands SHALL resolve the project from the active project or from `--project` / `--project-id`, and SHALL fail locally when no project is resolved.

#### Scenario: List shows rules in table order

- **WHEN** an operator runs `mo routing rule list` against a project with rules A, B, C in that table order
- **THEN** the output SHALL list A, then B, then C

#### Scenario: Create with explicit position

- **WHEN** an operator runs `mo routing rule create --name N --match <expr> --agent <agent> --response-prompt <p> --before <existing-rule>`
- **THEN** the new rule SHALL be inserted immediately before the referenced rule
- **AND** the table SHALL remain a single strict ascending sequence

#### Scenario: No project resolves to a local failure

- **WHEN** an operator runs a `mo routing rule` command and no active project is set and no `--project` / `--project-id` is supplied
- **THEN** the command SHALL fail without contacting the server
- **AND** SHALL report that no project was selected

### Requirement: Legacy Agent subscription surface removed

The prior Agent subscription resource and its command surface SHALL NOT exist after this change. `mo agent subscription` and its subcommands SHALL NOT resolve. The `/api/projects/{project}/agents/{agent}/subscriptions` API surface SHALL NOT exist. The management surface SHALL NOT carry a priority field, a three-field subscription filter, or priority-based selection. No automatic migration of prior subscriptions into rules SHALL be performed.

#### Scenario: Legacy subscription command is gone

- **WHEN** an operator runs `mo agent subscription create` or `mo agent subscription list`
- **THEN** the command SHALL NOT resolve

#### Scenario: Legacy subscription API is gone

- **WHEN** a client sends a request to `/api/projects/{project}/agents/{agent}/subscriptions`
- **THEN** the server SHALL NOT provide the subscription resource

#### Scenario: No automatic migration

- **WHEN** the system starts after this change
- **THEN** prior subscription rows SHALL NOT be migrated into rules
- **AND** operators SHALL be responsible for re-authoring rules
