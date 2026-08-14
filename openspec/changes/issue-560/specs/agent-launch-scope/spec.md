### Requirement: One confirmed-scope projection before dispatch

The Server SHALL provide one confirmed-scope projection for a prospective launch of an Agent with a given set of context references. The projection SHALL resolve and return the repository, the workspace, the Issue or Epic context when one is referenced, and the Agent's permission scope, exactly as they will apply to that launch. The Web composer and the CLI launch path SHALL consume the same projection; neither surface derives its own resolution.

#### Scenario: Resolving the scope for a referenced Issue

- **WHEN** a caller requests the confirmed scope for launching an Agent with an Issue context reference
- **THEN** the projection SHALL return the resolved repository, workspace, and Issue context that will apply to this launch
- **AND** the projection SHALL include the Agent's declared permission scope

#### Scenario: The same projection serves both surfaces

- **WHEN** the Web composer and the CLI launch path prepare a launch of the same Agent with the same context references
- **THEN** both SHALL consume the same Server confirmed-scope projection
- **AND** the scope they present SHALL be identical

#### Scenario: An unresolvable reference fails the scope

- **WHEN** a context reference cannot be resolved — for example an unknown Issue or Epic, or an archived Workspace
- **THEN** the confirmed scope SHALL fail with an actionable error naming the reference
- **AND** no AgentJob or AgentSession SHALL be created

### Requirement: The caller confirms the scope before dispatch

The Web session composer SHALL show the confirmed scope — the resolved repository, workspace, Issue or Epic context, and permission scope — before dispatch, and SHALL dispatch only after the caller confirms it. The CLI launch path SHALL obtain the same confirmed scope for the launch it performs. A launch MUST NOT dispatch a scope the caller has not seen.

#### Scenario: The composer shows the scope before launching

- **WHEN** a user prepares a launch in the Web composer with context references and submits it
- **THEN** the composer SHALL present the confirmed scope before dispatch
- **AND** dispatch SHALL occur only after the user confirms the presented scope

#### Scenario: The caller sees the workspace that will run the work

- **WHEN** the confirmed scope names a workspace for the launch
- **THEN** the caller SHALL be able to read which workspace the execution will run in before confirming
- **AND** dispatch SHALL bind the execution to that named workspace

### Requirement: The confirmed scope is persisted as per-launch execution facts

On dispatch, the resolved repository, workspace, Issue or Epic context, and permission scope SHALL be persisted as explicit facts of that launch, owned by that launch's AgentJob and AgentSession — not as loose session metadata. The recorded facts SHALL be readable from the launch's read and observation surfaces, SHALL be immutable for the life of the launch, and later definition edits MUST NOT rewrite them.

#### Scenario: The recorded scope is readable after launch

- **WHEN** a launch has been dispatched and the caller reads the launch's observation surface
- **THEN** the repository, workspace, Issue or Epic context, and permission scope recorded for that launch SHALL be present as explicit facts

#### Scenario: Definition edits do not rewrite launch facts

- **WHEN** an Agent's definition, including its permission declaration, is edited after a launch
- **THEN** the earlier launch's recorded scope SHALL remain unchanged
- **AND** the next launch SHALL record its own newly confirmed scope

#### Scenario: Idempotent replay returns the recorded scope

- **WHEN** the same launch is replayed with the same idempotency key
- **THEN** the response SHALL surface the already-recorded scope of the original launch
- **AND** no second launch with a different scope SHALL be created

### Requirement: Confirmation is not a configuration override surface

Launch-scope confirmation SHALL NOT become a launch-time configuration surface. The launch input remains prompt, context references, and attachments; the confirmed scope is a Server-derived projection of the Agent definition and project facts. A caller MUST NOT be able to alter the Agent's execution configuration, instructions, or permission scope through the launch path, and any attempt SHALL be rejected before dispatch with an actionable error.

#### Scenario: An execution override is rejected at the boundary

- **WHEN** a launch body carries a field that would override the Agent's execution definition, such as a model or runtime
- **THEN** the Server SHALL reject the request before creating any session or job
- **AND** the error SHALL name the unsupported field and the accepted launch fields

#### Scenario: Permissions are echoed, not chosen

- **WHEN** the confirmed scope is displayed to a caller or recorded for a launch
- **THEN** the permission scope SHALL be the Agent definition's declared permission scope
- **AND** the launch path SHALL offer no input that changes it
