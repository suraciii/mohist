### Requirement: Agent launch accepts an optional first-launch-only startup context
The Agent launch contract SHALL accept an optional bounded external discussion context, supplied by the caller as background for the agent's first launch only. When the caller omits startup context, the launch SHALL behave exactly as it does without this capability — no difference in deduplication fingerprint, initial input, or execution. Startup context SHALL be honored only on the first launch of a session; a follow-up input SHALL NOT carry, replace, or append to startup context.

#### Scenario: Launch without startup context is unchanged
- **WHEN** a caller launches an agent without supplying startup context
- **THEN** the launch, its deduplication fingerprint, the initial session input, and the resulting execution SHALL be identical to a launch performed before this capability existed

#### Scenario: Startup context applies only to the first launch
- **WHEN** a session's first launch carries startup context and a later follow-up input is submitted to the same session
- **THEN** the follow-up SHALL NOT carry, replace, or append any startup context
- **AND** the follow-up SHALL be processed as an ordinary session input

### Requirement: Startup context is untrusted user input, not system instructions
Startup context SHALL be composed into the agent's execution as untrusted user input. It MUST NOT be supplied as, or be permitted to override, the agent's Instructions. It MUST NOT alter the agent's Runtime, Model, Variant, or Skills, and MUST NOT expand the agent's configured capabilities or permissions. The influence of any content in the startup context SHALL be bounded by the capabilities the agent already holds.

#### Scenario: Startup context cannot override agent instructions
- **WHEN** a first launch supplies startup context whose text attempts to change the agent's role, rules, or instructions
- **THEN** the agent's resolved Instructions, Runtime, Model, Variant, and Skills SHALL be the agent's configured execution definition, unchanged by the startup context

#### Scenario: Startup content influence is bounded by existing capabilities
- **WHEN** startup context contains directives that would require capabilities or permissions the agent does not have configured
- **THEN** the agent SHALL NOT gain those capabilities or permissions
- **AND** those directives SHALL have no more effect than identical text supplied as an ordinary user input

### Requirement: Startup context carries explicit provenance and truncation
When a caller supplies startup context, the launch SHALL carry an explicit, bounded description of the external discussion it captured, including whether truncation occurred. This description SHALL flow to the agent's input and to the session input audit record, so that neither the agent nor a later observer is misled about what was or was not read.

#### Scenario: Truncated startup context is marked as truncated
- **WHEN** a first launch supplies startup context that was truncated to fit a bound
- **THEN** the startup context delivered to the agent SHALL explicitly state that truncation occurred and that the oldest content was dropped
- **AND** the session input audit SHALL record that truncation occurred

#### Scenario: Complete startup context is marked as complete
- **WHEN** a first launch supplies startup context that captures its bounded range without truncation
- **THEN** the startup context delivered to the agent SHALL state that the bounded range was captured completely
