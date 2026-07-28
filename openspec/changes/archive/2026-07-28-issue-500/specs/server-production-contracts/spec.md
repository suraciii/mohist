### Requirement: Production grain contracts exclude test controls
The production contracts and implementations of Issue, IssueRepositoryCoordinator, AgentSession, Workflow, Runner, and WorkflowProfileReferenceCoordinator grains SHALL expose only operations required by production behavior. They MUST NOT expose a test-only deactivation operation. Agent, Epic, Issue, and Workflow grain implementations MUST derive their identity from their authoritative Orleans grain key and MUST NOT expose a test-only key override.

#### Scenario: A test requires a fresh grain activation
- **WHEN** a server specification needs to verify state rehydration after an activation ends
- **THEN** it MUST use the cluster lifecycle management surface, and the production grain interface contains no test-only deactivation operation

#### Scenario: A grain processes a command in production
- **WHEN** an Agent, Epic, Issue, or Workflow grain resolves its identity
- **THEN** it MUST use its authoritative Orleans grain key without a test-only override path

### Requirement: Always-registered services are required dependencies
Components that require workflow profile resolution, event push delivery, background task launch, AgentJob storage, or AgentJob dispatch observation SHALL declare those dependencies as required. The production composition root MUST register an implementation for each required dependency, including an explicit no-op event push implementation when push delivery is unavailable. Components MUST NOT select a fallback implementation or alternate behavior because one of these required dependencies is absent.

#### Scenario: A workflow profile is resolved
- **WHEN** Issue, workflow-profile, or profile-reference behavior requires a workflow profile
- **THEN** it MUST use the registered workflow profile provider and MUST NOT use a legacy template fallback caused by a missing provider

#### Scenario: Event dispatch runs without live UI push delivery
- **WHEN** event dispatch is composed in an environment without a live push queue
- **THEN** the composition root MUST supply the explicit no-op event push implementation and dispatch behavior remains available without a nullable constructor fallback

#### Scenario: A mandatory service is omitted from a direct composition
- **WHEN** a test or alternate composition constructs a component that depends on one of the required services
- **THEN** it MUST provide an explicit real implementation or fake rather than exercising an absent-dependency branch

### Requirement: Genuine optional infrastructure remains explicit
Dependencies that are genuinely optional because they only provide caching, diagnostics, or another non-authoritative side channel MAY remain nullable. Their absence MUST NOT change workflow, issue, session, AgentJob, persistence, or event-dispatch decisions, and the dependency declaration MUST state why absence is valid.

#### Scenario: An optional diagnostic sink is unavailable
- **WHEN** a non-authoritative optional sink is not registered
- **THEN** the owning component MUST continue its authoritative behavior without treating the missing sink as a production error

### Requirement: External behavior is preserved
Removing test-only controls and unreachable dependency fallbacks MUST NOT alter external API or CLI contracts, workflow transitions, profile-resolution outcomes for valid production configuration, event-dispatch decisions, or persistence ordering.

#### Scenario: A supported production request is handled
- **WHEN** a client invokes an existing API or CLI operation under a valid production composition
- **THEN** the operation MUST retain its existing externally observable result and workflow behavior
