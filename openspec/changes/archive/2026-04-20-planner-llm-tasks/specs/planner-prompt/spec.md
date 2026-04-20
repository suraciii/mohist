## ADDED Requirements

### Requirement: Per-artifact prompt assembled from instruction files
The pipeline SHALL assemble a separate prompt for each artifact round by loading the corresponding instruction file from `src/agents/prompts/artifacts/`. Each prompt SHALL contain: issue information (title, body, number), the change directory path, the current artifact type as the generation target, the artifact-specific instruction content, and a reminder that the agent can use `read_file` to review previously generated artifacts.

#### Scenario: Prompt contains single artifact instruction
- **WHEN** the pipeline assembles the prompt for the proposal round
- **THEN** the prompt SHALL contain issue info, change directory path, and the proposal instruction loaded from `artifacts/proposal.md`, but SHALL NOT contain instructions for specs, design, or tasks

#### Scenario: Later rounds include read_file reminder
- **WHEN** the pipeline assembles the prompt for the specs round (or any round after the first)
- **THEN** the prompt SHALL remind the agent that previously generated artifacts exist on disk and can be read via `read_file`

### Requirement: Instruction files are plain Markdown
Each artifact instruction file SHALL be a plain Markdown file located in `src/agents/prompts/artifacts/`. Required files: `proposal.md`, `specs.md`, `design.md`, `tasks.md`, `self-review.md`. A separate `src/agents/prompts/review.md` SHALL exist for the review stage.

#### Scenario: Instruction files exist
- **WHEN** the pipeline loads instruction files
- **THEN** `artifacts/proposal.md`, `artifacts/specs.md`, `artifacts/design.md`, `artifacts/tasks.md`, `artifacts/self-review.md`, and `review.md` SHALL all exist and be readable

### Requirement: Tasks instruction includes LLM-generation guidance
The `artifacts/tasks.md` instruction SHALL contain guidance on task granularity (completable in one agent session), dependency ordering, AFK/HITL classification (`mode` field), task types (`type` field: WRITE/TEST/MIGRATE/CONFIG/REVIEW), `output` field, `dependsOn` field, and acceptance criteria semantics.

#### Scenario: Tasks instruction quality
- **WHEN** the tasks instruction is loaded
- **THEN** it SHALL contain guidance derived from OpenSpec's prd artifact instruction, adapted with mohist-specific fields (mode, type, output, dependsOn)

### Requirement: Self-review prompt sent as separate round
After all four artifacts are generated, the pipeline SHALL send a self-review prompt (loaded from `artifacts/self-review.md`) as a separate round in the same ACP session. The agent SHALL read all generated artifacts and evaluate completeness, consistency, and feasibility. If issues are found, the agent SHALL fix them within the same session.

#### Scenario: Agent self-reviews
- **WHEN** all four artifacts are generated and the self-review prompt is sent
- **THEN** the agent SHALL read all files, evaluate them, and fix any issues before the session ends

### Requirement: Change directory path included in prompt
Every per-artifact prompt SHALL include the exact output directory path (e.g., `openspec/changes/42-add-logs/`) so the agent knows where to write files.

#### Scenario: Agent writes to correct directory
- **WHEN** the planner agent session runs
- **THEN** all artifacts SHALL be written to the specified change directory
