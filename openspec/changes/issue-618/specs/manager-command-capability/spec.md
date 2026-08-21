### Requirement: Manager exposes only the approved management operations
The Manager execution capability SHALL expose exactly the approved logical operations `list`, `view`, `create`, `claim-owner`, `edit`, `enable`, `disable`, `transfer-owner`, and `diagnostics`. `list` SHALL provide workspace Manager status, `view` SHALL inspect Agent or Connection state, `create` SHALL create or mount the requested Agent using the existing Manager creation semantics, `edit` SHALL apply only supported routine Connection settings, `enable` and `disable` SHALL change routine Connection lifecycle state, owner operations SHALL issue the existing one-time owner workflows, and `diagnostics` SHALL report authoritative status and next actions. No operation outside this allowlist SHALL be callable through the Manager capability.

#### Scenario: Status and inspection use the approved command surface
- **WHEN** the Manager Agent requests current workspace status or an Agent/Connection inspection
- **THEN** the capability accepts the corresponding approved operation and returns the current enrollment, managed App, Agent, or Connection facts from the authoritative application service

#### Scenario: Routine lifecycle operations are available
- **WHEN** the Manager Agent requests an approved create, edit, enable, or disable operation with valid arguments
- **THEN** the capability invokes the corresponding routine application operation and returns its authoritative success or failure result

#### Scenario: Owner operations use the existing claim workflow
- **WHEN** the Manager Agent requests `claim-owner` or `transfer-owner` for a valid target
- **THEN** the capability invokes the existing one-time owner workflow and returns only the resulting user instruction and expiry information required to continue that workflow, without exposing protected runtime credentials

#### Scenario: An unknown operation is rejected
- **WHEN** the Manager Agent requests an operation name not in the allowlist
- **THEN** the capability rejects the call with a stable authorization or availability failure and performs no resource mutation

### Requirement: Command results are authoritative and failure preserving
Every approved Manager command SHALL delegate to the existing application service used by the CLI or Web path for that operation. The result SHALL preserve the service's confirmed resource state, next action, idempotent outcome, validation error, conflict, not-found result, or unavailable outcome. The capability MUST NOT claim that an Agent, Connection, or Slack App is ready unless the delegated service confirms that state, and a failed or uncertain operation SHALL not be represented as success.

#### Scenario: A status result reports the current next action
- **WHEN** the Manager requests status while setup or installation is incomplete
- **THEN** the command result reports the persisted state and the service-defined next action, and does not claim readiness

#### Scenario: Invalid arguments produce a command failure
- **WHEN** an approved command omits a required target or supplies an invalid access policy or other invalid argument
- **THEN** the command returns the existing validation error and performs no partial management mutation

#### Scenario: Repeating an idempotent operation preserves the service outcome
- **WHEN** the Manager repeats an approved create, enable, disable, or inspection command for the same target
- **THEN** the command returns the existing application's idempotent or current-state result without creating duplicate Agent, Connection, managed App, or owner records

### Requirement: Every command is reauthorized against the current Manager actor and target
Before each Manager command, the capability SHALL reauthenticate the actor from the current Slack origin and current Workspace enrollment. It SHALL reauthorize the requested target against that actor, the active enrollment, the target Workspace, and the target resource's current existence and ownership boundary. A target in another Workspace or Project scope, a stale actor identity, a disabled or removed enrollment, or a changed target authorization SHALL be rejected even when an earlier command for the same Session was allowed.

#### Scenario: A valid current actor may operate on a same-workspace target
- **WHEN** the current claimed Manager actor requests an approved operation for a target belonging to the current enrolled Workspace
- **THEN** the capability reauthorizes the actor and target and delegates the operation to the existing application service

#### Scenario: Actor authorization changes take effect immediately
- **WHEN** the Manager actor is no longer the current authorized Workspace member or the enrollment is no longer available or ready
- **THEN** the next command is rejected before the application service mutates state, even if an earlier turn issued a valid Manager capability credential

#### Scenario: Cross-workspace target access is rejected
- **WHEN** an otherwise authorized Manager actor names a Project, Agent, Connection, or managed App belonging to another Workspace
- **THEN** the capability returns a not-found or authorization failure and performs no read or write against that foreign target

### Requirement: Protected and unrestricted management operations remain unavailable
The Manager capability MUST NOT submit, rotate, or reveal Slack credentials or other secrets; read credential values; permanently delete a managed App; remove a Connection binding; call arbitrary management API routes; access the database directly; or introduce a new Slack-native command grammar. Protected credential entry and irreversible management operations SHALL remain available only through their existing protected CLI, Web, or control-plane authorization paths.

#### Scenario: Secret submission or credential reads are denied
- **WHEN** Manager text or a command request asks for a Bot token, App-level token, signing secret, credential value, credential reference contents, or credential rotation
- **THEN** the capability rejects the request without reading, storing, returning, or logging the secret

#### Scenario: Irreversible lifecycle operations are denied
- **WHEN** the Manager requests `remove-binding`, `permanent-delete`, deletion, or an equivalent irreversible lifecycle action
- **THEN** the capability returns an unavailable or not-authorized result and leaves the Connection, Agent App, and Agent unchanged

#### Scenario: Arbitrary API access is denied
- **WHEN** Manager execution attempts to select an endpoint, HTTP method, database operation, or management action outside the approved operation set
- **THEN** the capability rejects the request and exposes no unrestricted management transport to the Agent

#### Scenario: No Slack-native command grammar is created
- **WHEN** an operator sends a Slack message intended to invoke a slash command or a new Manager-specific command syntax
- **THEN** the message is handled as ordinary natural-language Manager input and management effects occur only through the allowlisted capability calls
