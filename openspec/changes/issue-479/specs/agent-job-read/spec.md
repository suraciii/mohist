### Requirement: List AgentJobs for an Agent

`mo agent job list <agent>` MUST return the AgentJobs launched for the resolved Agent profile, backed by an HTTP GET endpoint scoped to that agent. The list SHALL be ordered by recency and each entry SHALL expose the job id and its current status.

#### Scenario: List returns jobs for the agent
- **WHEN** `mo agent job list <agent>` runs for an agent that has launched jobs
- **THEN** each job's id and status are shown, most recent first

#### Scenario: Agent with no jobs
- **WHEN** the agent has never launched a job
- **THEN** the list is empty

#### Scenario: Unknown agent
- **WHEN** the agent reference does not resolve to a known agent
- **THEN** the response is `404`

### Requirement: View a single AgentJob by id

`mo agent job view <job-id>` MUST return the AgentJob's current status and, once the job is terminal, its terminal result. The backing HTTP GET endpoint SHALL accept the same job id that launch returns.

#### Scenario: View a completed job
- **WHEN** the caller views a job that reached `completed`
- **THEN** the status is `completed` and the terminal result is shown

#### Scenario: View a failed job
- **WHEN** the caller views a job that reached `failed`
- **THEN** the status is `failed` and the failure reason is shown

#### Scenario: View a non-terminal job
- **WHEN** the caller views a job that is still `pending` or `running`
- **THEN** the current status is shown and no terminal result is asserted

#### Scenario: Unknown job id
- **WHEN** the caller views a job id that does not exist
- **THEN** the response is `404`

### Requirement: Terminal result fields

The AgentJob terminal result SHALL expose: status (`completed` or `failed`), message, output, artifact upload ids, failure reason, and exit code. A field with no value for a given terminal job SHALL be absent (null) rather than fabricated.

#### Scenario: Completed job exposes result fields
- **WHEN** a completed job is viewed
- **THEN** the result includes status, message, output, artifact upload ids, and exit code

#### Scenario: Failed job exposes failure fields
- **WHEN** a failed job is viewed
- **THEN** the result includes status `failed`, the failure reason, and the exit code when one is available

### Requirement: AgentJob is the sole work-result read path

A launch's work outcome MUST be read from `agent job`, not inferred from any AgentSession field. The CLI SHALL NOT present a competing terminal verdict on the AgentSession read surface.

#### Scenario: Result read from the job, not the session
- **WHEN** a caller wants to know whether a launch's work succeeded or failed
- **THEN** it reads `mo agent job view <job-id>`, and the session read surface does not present a separate job-result verdict
