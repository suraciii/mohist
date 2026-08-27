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
- **THEN** the CLI MUST use the existing resolver for that id before POST
- **AND** the POST body MUST contain `agentId: "agent_123"`
- **AND** the resulting Agent reference MUST match the name form

### Requirement: Routing-rule edit resolves a supplied Agent reference and omits absent options

`mo routing rule edit <rule> --agent <reference>` MUST resolve the supplied reference through the existing project-scoped CLI Agent resolver before patching the routing rule. The PATCH body MUST contain the resolver's stable `agentId`. Every other edit option MUST be included only when that CLI option was provided; an omitted option MUST be absent from JSON rather than serialized as `null`.

#### Scenario: Edit resolves an Agent name

- **WHEN** `mo routing rule edit rule_1 --agent reviewer` resolves `reviewer` to `agent_123`
- **THEN** the CLI MUST resolve the name before the routing-rule PATCH
- **AND** the PATCH body MUST contain `agentId: "agent_123"`
- **AND** the raw string `reviewer` MUST NOT be sent as `agentId`

#### Scenario: Edit resolves an Agent id

- **WHEN** `mo routing rule edit rule_1 --agent agent_123`
- **THEN** the CLI MUST resolve the id through the same resolver before PATCH
- **AND** the PATCH body MUST contain `agentId: "agent_123"`
- **AND** the result MUST be identical in Agent identity to the name form

#### Scenario: Edit sends only supplied fields

- **WHEN** `mo routing rule edit rule_1 --name renamed`
- **THEN** the PATCH JSON object MUST contain exactly `name`
- **AND** it MUST NOT contain `match`, `agentId`, `responsePrompt`, or `continue`
- **AND** the stored fields other than `name` MUST remain unchanged

#### Scenario: Edit without Agent leaves the field unchanged

- **WHEN** `mo routing rule edit rule_1` omits `--agent`
- **THEN** the CLI MUST preserve the existing omission semantics for `agentId`
- **AND** it MUST NOT perform an Agent lookup solely for the omitted option
- **AND** the PATCH body MUST NOT contain `agentId`

### Requirement: Server applies present PATCH fields using canonical JSON vocabulary

The Server routing-rule PATCH contract MUST use these exact JSON field names for editable properties: `name`, `match`, `agentId`, `responsePrompt`, and `continue`. `Raw`, `Fields`, and the store's presence checks MUST use the same canonical names and casing. A present field MUST be applied; an absent field MUST preserve its stored value. A present JSON `null` is still present and is handled by the existing validation/normalization rules.

#### Scenario: Name, match, AgentId, and prompt updates apply end to end

- **WHEN** a PATCH body contains any one or more of `name`, `match`, `agentId`, and `responsePrompt` with valid values
- **THEN** Server `Fields` MUST contain those exact lower camel-case JSON names
- **AND** the corresponding values MUST be applied to the stored routing rule
- **AND** a field absent from the body MUST remain unchanged

#### Scenario: Continue presence uses the canonical JSON name

- **WHEN** a PATCH body contains `continue: true` or `continue: false`
- **THEN** Server `Fields` MUST contain `continue`
- **AND** the stored Continue value MUST be updated to the supplied boolean
- **AND** it MUST NOT rely on a C# member name such as `Continue`

#### Scenario: Present null is not omission

- **WHEN** a PATCH body contains one of the editable JSON properties with `null`
- **THEN** Server `Fields` MUST contain that property's canonical JSON name
- **AND** the existing validation/normalization rules MUST handle the present null
- **AND** the property MUST NOT be silently treated as absent

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

### Requirement: Project resolution and broader routing contracts remain unchanged

Agent reference resolution MUST occur within the already resolved Project. The CLI MUST preserve existing project selection, routing-rule request paths, required create fields, position queries, output selection, routing DSL, stored rule references, and Server stable-id validation. The minimum Server PATCH presence correction above is part of this capability; no broader backend Agent lookup, fallback routing, or routing-engine change is allowed.

#### Scenario: Name resolution uses the selected project

- **WHEN** a command selects project `project_a` and supplies an Agent name
- **THEN** the existing resolver MUST query and resolve that name in `project_a`
- **AND** the routing mutation MUST use `project_a`'s project-scoped endpoint
- **AND** no Agent from another project may be substituted

#### Scenario: Rule references remain server-owned

- **WHEN** a routing rule is created or edited after its Agent reference has been resolved
- **THEN** only the stable `agentId` value changes at the CLI boundary
- **AND** the command MUST NOT rewrite rule ids, match expressions, position references, or routing DSL semantics

### Requirement: Focused contract tests prove the value end to end

The implementation MUST add deterministic fake-HTTP CLI tests and focused Server PATCH contract tests. The CLI tests MUST prove name/id equivalence, project-scoped lookup-before-mutation order, exact omission of absent fields, and no mutation after unknown input. The Server tests MUST prove canonical `Fields` names and application of present `name`, `match`, `agentId`, `responsePrompt`, and `continue` fields.
