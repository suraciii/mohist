### Requirement: Launch returns both the AgentJob and AgentSession identities

A successful manual launch (`POST /api/projects/{projectRef}/agents/{agentRef}/sessions`) MUST return a `201` response carrying both a non-empty `jobId` (the AgentJob identity) and a non-empty `sessionId` (the AgentSession identity), alongside the resolved agent id, agent name, and a link to read the session transcript. The `jobId` MUST be the same identifier the AgentJob read surface accepts — there is no translation gap between the id returned at launch and the id used to read the job.

#### Scenario: Successful launch returns both identities
- **WHEN** a caller submits a non-empty prompt for a known, non-archived agent
- **THEN** the `201` response includes a non-empty `jobId` and a non-empty `sessionId`, the agent id and name, and a transcript URL

#### Scenario: Launched job id is accepted by the job read surface
- **WHEN** the caller reads the AgentJob using the `jobId` from the launch response
- **THEN** the job is found by that exact id

### Requirement: The launcher propagates the AgentJob key

`AgentLaunchResult` MUST carry the AgentJob key the launcher mints, so every launch caller can surface the job identity instead of discarding it. Surfacing the identity MUST NOT change how many entities a launch creates or how dispatch happens: a launch still creates exactly one AgentJob and exactly one AgentSession and issues exactly one dispatch.

#### Scenario: Manual launch result carries the job key
- **WHEN** the launcher completes a manual launch
- **THEN** the returned `AgentLaunchResult` carries the job key minted for that launch

#### Scenario: Exactly one job and one session per launch
- **WHEN** a launch succeeds
- **THEN** exactly one AgentJob and exactly one AgentSession are created and one dispatch is issued

### Requirement: CLI launch command returns both identities

`mo agent launch <agent>` MUST create an AgentJob and an AgentSession and print both the Job ID and the Session ID. The command SHALL live directly under `agent`, not under an `agent session` subgroup.

#### Scenario: CLI prints both identities
- **WHEN** `mo agent launch <agent> --prompt "..."` completes successfully
- **THEN** stdout shows both a job id and a session id

#### Scenario: Command relocated directly under agent
- **WHEN** a user runs `mo agent launch` (with no intermediate `session` subcommand)
- **THEN** the launch command is invoked

### Requirement: Launch domain gates are preserved

Relocating the command and surfacing the job identity MUST NOT weaken the existing pre-creation gates: a whitespace prompt is rejected with `400` before any session or job is created; an unknown agent is rejected with `404`; an archived agent is rejected with `409`; a non-existent referenced issue or epic is rejected.

#### Scenario: Whitespace prompt rejected before entity creation
- **WHEN** the request prompt is empty or whitespace
- **THEN** the response is `400` and no AgentSession or AgentJob is created

#### Scenario: Unknown agent rejected
- **WHEN** the agent reference does not resolve to a known agent
- **THEN** the response is `404` and no session or job is created

#### Scenario: Archived agent rejected
- **WHEN** the resolved agent is archived
- **THEN** the response is `409` and no session or job is created
