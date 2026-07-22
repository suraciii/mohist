### Requirement: Closed template root set
The template context exposed to task input, live-read Prompt bodies, and retained preview entry points SHALL contain exactly the roots `workflow`, `stage`, `work`, `issue`, `repository`, `workspace`, `vars`, `tasks`, `prompts`, and `failure`. The server dispatch context builder and the runner variable assembly SHALL NOT inject the off-table bare roots `mohist`, `project`, `openspecChangeName`, `openspecChangeDir`, a bare `approvalFeedback` root, or a runner-local `runner` root.

#### Scenario: An off-table bare root reference fails
- **WHEN** a task input or Prompt body contains `${{ openspecChangeDir }}`, `${{ openspecChangeName }}`, `${{ project.id }}`, `${{ mohist.runId }}`, or `${{ approvalFeedback.id }}`
- **THEN** the expression does not resolve and the task fails with a message identifying the offending expression

#### Scenario: The dispatch context carries only the ten roots
- **WHEN** the server builds a task dispatch payload
- **THEN** the variable roots are limited to `workflow`, `stage`, `work`, `issue`, `repository`, `workspace`, `vars`, `tasks`, `prompts`, and `failure`; none of `mohist`, `project`, `openspecChangeName`, `openspecChangeDir`, bare `approvalFeedback`, or `runner` is present

#### Scenario: The runner does not add off-table roots
- **WHEN** the runner assembles the template variable context from the dispatch payload
- **THEN** it SHALL NOT add a `runner` root or re-introduce any off-table root that the dispatch context no longer carries

### Requirement: Effective Variables resolve only through vars
Effective Variables SHALL be reachable in template expressions exclusively through `${{ vars.* }}`. The dispatch context builder SHALL NOT copy each Effective Variable key to a top-level bare name, and the runner SHALL NOT hoist variable keys to the top level. Runtime context (`workflow`, `stage`, `work`, `issue`, `repository`, `workspace`, `tasks`, `prompts`, `failure`) SHALL NOT be copied, merged, or aliased into `vars`.

#### Scenario: A variable key is not reachable as a bare name
- **WHEN** an Effective Variable `foo` exists and a task input or Prompt body references `${{ foo }}`
- **THEN** the expression does not resolve and the task fails, because `foo` is only available as `${{ vars.foo }}`

#### Scenario: vars contains only merged Variables
- **WHEN** the dispatch context snapshot is inspected
- **THEN** `vars` contains only the merged Effective Variables; it does not contain runtime context, `tasks`, `prompts`, `failure`, or any field that duplicates another root

#### Scenario: A variable resolves through vars
- **WHEN** an Effective Variable `agent.model` exists and a task input references `${{ vars.agent.model }}`
- **THEN** the expression resolves to the merged variable value

### Requirement: Workspace carries facts without computed paths
The `workspace` root SHALL expose workspace facts (`path`, `branch`) only. It SHALL NOT provide `changeDir` or any field that computes an OpenSpec change directory. The template context SHALL NOT provide `openspecChangeName` or `openspecChangeDir`; OpenSpec change paths are expressed by profiles and prompts as the literal template `openspec/changes/issue-${{ issue.number }}`.

#### Scenario: workspace.changeDir is absent
- **WHEN** a task input or Prompt body references `${{ workspace.changeDir }}`
- **THEN** the expression does not resolve and the task fails

#### Scenario: The runner resolves the workspace without off-table roots
- **WHEN** the runner prepares a workspace for a task whose issue number is N
- **THEN** workspace resolution succeeds and produces `workspace.path` and `workspace.branch`, without reading `openspecChangeDir` or `mohist.runId` from the template variable context

#### Scenario: Workspace identity uses the documented run root
- **WHEN** the runner validates workspace identity for a dispatch
- **THEN** the authoritative run identity is read from `workflow.runId`, not from an off-table `mohist.runId` bare root
