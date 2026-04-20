## ADDED Requirements

### Requirement: Program-driven pipeline with fixed stage order
The workflow SHALL be driven by a TypeScript program (pipeline controller) that executes stages in fixed order: plan → gate → build → review → gate → done. No LLM session SHALL be responsible for deciding which stage to execute next. Build stage proceeds directly to review without a gate.

#### Scenario: Pipeline runs stages sequentially
- **WHEN** a user starts an issue through the pipeline
- **THEN** the pipeline SHALL execute plan stage, then pause for user approval, then execute build stage, then automatically proceed to review stage, then pause for user approval, then mark done

#### Scenario: No LLM between stages
- **WHEN** a stage completes
- **THEN** the pipeline program SHALL determine the next stage based on the fixed order and gate approval status, not by asking an LLM

#### Scenario: No gate after build
- **WHEN** build stage completes
- **THEN** the pipeline SHALL automatically proceed to review stage without waiting for user approval

### Requirement: ACP connection supports multi-round prompts
The system SHALL provide an `AcpConnection` abstraction that creates a single opencode ACP session and allows the caller to send multiple `prompt()` calls before closing the connection. This enables the pipeline to generate artifacts round-by-round while reusing the same session context.

#### Scenario: Connection created and reused for multiple rounds
- **WHEN** the pipeline creates an `AcpConnection`
- **THEN** it SHALL be able to call `prompt()` multiple times on the same connection, with each response returned to the caller

#### Scenario: Connection cleanup on close
- **WHEN** the pipeline calls `close()` on the connection
- **THEN** the underlying opencode process SHALL be terminated and resources cleaned up

### Requirement: Plan stage uses multi-round ACP connection
The pipeline SHALL execute the plan stage by creating an `AcpConnection` and sending a sequence of prompts, one per artifact: proposal → specs → design → tasks. After each round, the pipeline SHALL verify that the corresponding artifact was created before sending the next prompt. The planner agent SHALL use `write_file` and `read_file` tools within the shared session.

#### Scenario: Proposal round
- **WHEN** the pipeline sends the proposal prompt
- **THEN** the planner agent SHALL write `proposal.md` to the change directory

#### Scenario: Specs round reuses proposal context
- **WHEN** the pipeline sends the specs prompt (the next round in the same session)
- **THEN** the planner agent SHALL have access to the previously written `proposal.md` content through the session history and `read_file`

#### Scenario: Tasks round reuses all prior artifacts
- **WHEN** the pipeline sends the tasks prompt
- **THEN** the planner agent SHALL generate `tasks.json` informed by proposal, specs, and design content

#### Scenario: Self-review round
- **WHEN** all four artifacts are generated
- **THEN** the pipeline SHALL send a self-review prompt, and the agent SHALL read all artifacts and fix any issues within the same session

#### Scenario: Plan stage failure mid-round
- **WHEN** a round fails or times out
- **THEN** the pipeline SHALL report the failure and the specific artifact that was being generated; on retry, the plan stage SHALL start from a clean change directory

### Requirement: Build stage uses RalphExecutor
The pipeline SHALL execute the build stage using the existing `RalphExecutor` which loops through tasks, calling `runAcpSession()` for each pending task. This is unchanged from current behavior.

#### Scenario: Build stage executes tasks
- **WHEN** the pipeline enters build stage
- **THEN** it SHALL read tasks.json and execute each pending task via `runAcpSession()`

### Requirement: Review stage uses multi-round ACP connection
The pipeline SHALL execute the review stage by creating an `AcpConnection` and sending reviewer prompts. The reviewer agent SHALL produce the review report within the shared session.

#### Scenario: Review stage runs review
- **WHEN** the pipeline enters review stage
- **THEN** it SHALL create an ACP connection, send the reviewer prompt, and the agent SHALL read code changes and produce a review report

### Requirement: Gates pause pipeline for human approval
After plan stage and review stage, the pipeline SHALL pause and wait for human approval before proceeding to the next stage. Gate approval SHALL be handled by the program (e.g., HTTP API endpoint or CLI prompt), not by an LLM agent.

#### Scenario: Plan gate
- **WHEN** plan stage completes successfully
- **THEN** the pipeline SHALL pause, present the generated artifacts to the user, and wait for approval before starting build stage

#### Scenario: Review gate
- **WHEN** review stage completes
- **THEN** the pipeline SHALL pause, present the review report to the user, and wait for approval before marking done

#### Scenario: User rejects at plan gate
- **WHEN** a user rejects at the plan gate
- **THEN** the pipeline SHALL re-execute the plan stage from a clean change directory

#### Scenario: User rejects at review gate
- **WHEN** a user rejects at the review gate
- **THEN** the pipeline SHALL set the issue stage back to build and re-execute from the build stage (build → review), since the most common reason for review rejection is code needing changes

### Requirement: Delete MainAgent and related agent session infrastructure
The `MainAgent` class, `runMainAgent()`, `runAgentLoop()`, and the MainAgent-specific tools (`execute_stage`, `advance_stage`, `submit_approval`, `spawn_coder`, `generate_tasks`, `add_comment`, `get_issue`, `read_workflow`) SHALL be deleted. The pipeline does not need an LLM orchestrator session.

#### Scenario: No MainAgent file
- **WHEN** the refactoring is complete
- **THEN** `main-agent.ts` and `agent-loop.ts` SHALL NOT exist

#### Scenario: No MainAgent tools
- **WHEN** the refactoring is complete
- **THEN** `execute-stage.ts`, `advance-stage.ts`, `submit-approval.ts`, `spawn-coder.ts`, `read-workflow.ts` SHALL NOT exist

### Requirement: Delete PlannerAgent class
The `PlannerAgent` class and `planner-agent.ts` SHALL be deleted. Plan stage is now driven by the pipeline via an `AcpConnection`.

#### Scenario: No PlannerAgent
- **WHEN** the refactoring is complete
- **THEN** `planner-agent.ts` SHALL NOT exist and no code SHALL reference `PlannerAgent` or `createPlannerAgent`

### Requirement: Delete ReviewerAgent class
The `ReviewerAgent` class and `reviewer-agent.ts` SHALL be deleted. Review stage is now driven by the pipeline via an `AcpConnection`.

#### Scenario: No ReviewerAgent
- **WHEN** the refactoring is complete
- **THEN** `reviewer-agent.ts` SHALL NOT exist and no code SHALL reference `ReviewerAgent` or `createReviewerAgent`

### Requirement: Delete programmatic task generation
The `generateTasksFromSpecs()`, `generateTasksFile()`, `createGenerateTasksTool()` functions SHALL be deleted. Task generation is now done by the planner agent within the multi-round ACP session.

#### Scenario: No regex task generation
- **WHEN** the refactoring is complete
- **THEN** no code SHALL extract task definitions from spec file headers via regex
