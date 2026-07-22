### Requirement: Builtin profiles use only the closed namespace
The built-in workflow profiles (`mohist/local`, `mohist/github-pr`) SHALL reference only the ten documented template roots in their task `with`, `expect`, `artifacts`, and check inputs. They SHALL NOT use `openspecChangeDir`, `openspecChangeName`, `project`, `mohist`, `workspace.changeDir`, or a bare `approvalFeedback` root.

#### Scenario: OpenSpec paths use the literal issue-number template
- **WHEN** a builtin profile task needs an OpenSpec change path (e.g. an `expect.files` path, an artifact path, or an Action `changeDir` input)
- **THEN** the path is expressed as the literal template `openspec/changes/issue-${{ issue.number }}` (or a sub-path of it), not as `${{ openspecChangeDir }}`

#### Scenario: Resolved paths match the pre-migration locations
- **WHEN** a builtin profile is executed against an issue with number N
- **THEN** the resolved OpenSpec paths are identical to those produced before the migration (e.g. `openspec/changes/issue-N/proposal.md`)

### Requirement: Builtin prompts use only the closed namespace
All builtin Prompt bodies SHALL reference only the ten documented template roots. They SHALL NOT use `openspecChangeDir`, `openspecChangeName`, `project.id`, `approvalFeedback.id` as a bare root, or `approvalFeedback.command`. Literal `${{ }}` text that is meant as documentation or syntax examples SHALL be escaped with `\${{` so it is not treated as a template expression.

#### Scenario: Project identity uses issue.projectId
- **WHEN** a builtin Prompt needs the project identity (e.g. in a `mo issue show` invocation)
- **THEN** it uses `${{ issue.projectId }}` with the `--project` / `--project-id` flag, not `${{ project.id }}`

#### Scenario: Feedback prompts build the command from primitives
- **WHEN** the apply-feedback Prompt references the feedback identity
- **THEN** it uses `${{ work.approvalFeedback.id }}`, `${{ issue.number }}`, and `${{ issue.projectId }}` to construct any invocation, not `${{ approvalFeedback.command }}` or `${{ approvalFeedback.id }}`

#### Scenario: Literal brace text is escaped
- **WHEN** a builtin Prompt body contains text that shows `${{ }}` syntax as documentation or an example rather than as a live reference
- **THEN** that occurrence is escaped as `\${{` so rendering produces the literal `${{` text rather than failing or expanding

### Requirement: Existing Workflow end-to-end behavior is preserved
The migration SHALL NOT change the observable end-to-end behavior of existing workflows. The Plan-stage artifact locations (proposal, design, tasks, specs), recovery handling, approval-feedback application, retry, and GitHub PR delivery SHALL produce the same outcomes as before the migration.

#### Scenario: Plan artifacts land in the expected location
- **WHEN** a builtin profile's Plan stage runs for issue N
- **THEN** the proposal, design, tasks, and specs artifacts are produced under `openspec/changes/issue-N/` as before the migration

#### Scenario: Approval feedback application works end-to-end
- **WHEN** a user submits approval feedback and the apply-feedback task runs
- **THEN** the task reads the feedback through `work.approvalFeedback` and applies the feedback successfully

#### Scenario: Recovery continues to work
- **WHEN** a task fails and a recovery handler fires
- **THEN** the recovery task dispatches, renders, and executes with the same behavior as before the migration

#### Scenario: GitHub PR delivery is unchanged
- **WHEN** the `mohist/github-pr` profile runs through the integrate stage
- **THEN** branch push, PR creation, PR merge, and PR status checks complete as before the migration
