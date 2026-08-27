# Routing Rule Agent Reference

## Requirements

### Requirement: Routing-rule create resolves Agent name and id identically

`mo routing rule create --agent <reference>` MUST resolve `<reference>` through the existing project-scoped CLI Agent resolver before posting the routing rule. The request MUST contain the resolver's stable `agentId`, whether the input is an Agent name or an Agent id.

#### Scenario: Create resolves an Agent name

- **WHEN** `mo routing rule create` receives `--agent reviewer` and the project resolver identifies `reviewer` as Agent id `agent_123`
- **THEN** the CLI MUST resolve the name before the routing-rule POST
- **AND** the POST body MUST contain `agentId: "agent_123"`
- **AND** the raw string `reviewer` MUST NOT be sent as `agentId`

#### Scenario: Create preserves an Agent id

- **WHEN** `mo routing rule create` receives `--agent agent_123`
- **THEN** the CLI MUST use the existing resolver for that id
- **AND** the POST body MUST contain `agentId: "agent_123"`
- **AND** the resulting Agent reference MUST match the name form

### Requirement: Routing-rule edit resolves a supplied Agent reference

`mo routing rule edit <rule> --agent <reference>` MUST resolve the supplied reference through the existing project-scoped CLI Agent resolver before patching the routing rule. The PATCH body MUST contain the resolver's stable `agentId`.

#### Scenario: Edit resolves an Agent name

- **WHEN** `mo routing rule edit rule_1 --agent reviewer` resolves `reviewer` to `agent_123`
- **THEN** the CLI MUST resolve the name before the routing-rule PATCH
- **AND** the PATCH body MUST contain `agentId: "agent_123"`

#### Scenario: Edit resolves an Agent id

- **WHEN** `mo routing rule edit rule_1 --agent agent_123`
- **THEN** the CLI MUST resolve the id through the same resolver
- **AND** the PATCH body MUST contain `agentId: "agent_123"`
- **AND** the result MUST be identical in Agent identity to the name form

#### Scenario: Edit without Agent leaves the field unchanged

- **WHEN** `mo routing rule edit rule_1` omits `--agent`
- **THEN** the CLI MUST preserve the existing omission semantics for `agentId`
- **AND** it MUST NOT perform an Agent lookup solely for the omitted option

### Requirement: Unknown Agent input fails before routing mutation

When the existing resolver cannot identify an Agent for a supplied routing-rule `--agent` value, the CLI MUST return a non-zero exit code, MUST identify the original input in its diagnostic, and MUST NOT issue the routing-rule POST or PATCH.

#### Scenario: Unknown name on create

- **WHEN** `mo routing rule create` receives `--agent missing-agent` and the project contains no matching Agent
- **THEN** the command MUST fail before POST
- **AND** its diagnostic MUST identify `missing-agent` exactly as supplied
- **AND** no routing rule mutation request MUST be sent

#### Scenario: Unknown name on edit

- **WHEN** `mo routing rule edit rule_1 --agent missing-agent` and the project contains no matching Agent
- **THEN** the command MUST fail before PATCH
- **AND** its diagnostic MUST identify `missing-agent` exactly as supplied
- **AND** no routing rule mutation request MUST be sent

### Requirement: Project resolution and routing contracts remain unchanged

Agent reference resolution MUST occur within the already resolved Project. The CLI MUST preserve existing project selection, routing-rule request paths, rule fields, position queries, output selection, routing DSL, stored rule references, and Server validation. No backend or routing-engine change is part of this capability.

#### Scenario: Name resolution uses the selected project

- **WHEN** a command selects project `project_a` and supplies an Agent name
- **THEN** the existing resolver MUST query and resolve that name in `project_a`
- **AND** the routing mutation MUST use `project_a`'s project-scoped endpoint
- **AND** no Agent from another project may be substituted

#### Scenario: Rule references remain server-owned

- **WHEN** a routing rule is created or edited after its Agent reference has been resolved
- **THEN** only the stable `agentId` value changes at the CLI boundary
- **AND** the command MUST NOT rewrite rule ids, match expressions, position references, or routing DSL semantics
