### Requirement: A test task can be started from the Agent detail page

From the Agent detail page, a user SHALL be able to initiate a test task that opens the launch composer pre-bound to that Agent. The composer SHALL accept a real task (visible prompt text) and optional Issue, Epic, Repository and workspace context references. The composer SHALL NOT accept Runtime, Model, Variant, Skills, or Max concurrent runs overrides; execution-definition fields are owned by the Agent and are not editable at launch time.

#### Scenario: Launching from detail is pre-bound to the Agent

- **WHEN** a user starts a test task from an active Agent's detail page
- **THEN** the launch composer SHALL open bound to that Agent, and SHALL not require the user to re-select the Agent

#### Scenario: The composer accepts task and context but not configuration overrides

- **WHEN** a user submits a test task with a prompt and an Issue context reference
- **THEN** the launch request SHALL carry the prompt and the context reference, and SHALL NOT carry any Runtime/Model/Variant/Skills/concurrency override

### Requirement: Launch is gated by Readiness

Launch SHALL be blocked when the server reports Readiness as Needs setup, with the blocking reason and gaps surfaced to the user. Launch SHALL be allowed when Readiness is Ready. Launch SHALL be allowed when Readiness is Unknown, with an explicit caveat that the task will wait for the server to validate execution. Archived Agents SHALL NOT be launchable.

#### Scenario: Needs setup blocks launch and shows the gaps

- **WHEN** the selected Agent's server Readiness is Needs setup
- **THEN** launch SHALL be disabled, and the page SHALL surface the gaps and the place to fix them

#### Scenario: Unknown remains launchable with a validation caveat

- **WHEN** the selected Agent's server Readiness is Unknown
- **THEN** launch SHALL remain enabled, and the page SHALL state that the task will wait for the server to validate execution

#### Scenario: An archived Agent cannot launch

- **WHEN** the selected Agent is archived
- **THEN** launch SHALL be disabled, and the page SHALL state that the Agent is archived

### Requirement: Launch is idempotent per intent

A launch SHALL carry an idempotency key tied to the user's current launch intent. If the response is lost and the user retries the same intent, the retry SHALL reuse the same key and SHALL return the work that the original intent produced, without creating a duplicate AgentJob or AgentSession. A new, distinct launch intent SHALL use a new key.

#### Scenario: A lost response retried with the same key returns the original work

- **WHEN** a launch response is lost and the user retries the same task intent
- **THEN** the retry SHALL reuse the original idempotency key, and SHALL NOT create a second AgentJob or AgentSession for that intent

### Requirement: A successful launch lands on a pointer to the resulting work

On a successful launch, the user SHALL be navigated to a pointer to the resulting work (the AgentSession that was created). The pointer SHALL identify the AgentSession created by this launch so the user can follow up on the test task from a single, stable entry.

#### Scenario: Success navigates to the created session

- **WHEN** a launch succeeds and returns the created AgentSession identifier
- **THEN** the user SHALL be navigated to the AgentSession created by this launch
