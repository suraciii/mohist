# Workflow Profile

A Workflow Profile defines how an Issue moves from Draft to Done, including its
Stages, Tasks, Checks, recovery rules, and approval points. A Profile is a
Project resource. A Project may own multiple Profiles and select one as its
default.

Variables and Prompts are separate resources. They do not belong to a Workflow
Profile. A Profile only consumes them through `${{ vars.* }}` and
`${{ prompts.* }}`.

## Select a Profile

You may explicitly select a Profile from the same Project when you create or
update an Issue. Without an explicit selection, the Issue inherits the Project
default. Clearing an explicit selection also restores this inheritance. The
third selection is no Workflow: `mo issue create --no-workflow` produces an
Issue that runs no production line; see [Issue Management](issues.md).

Mohist determines the Profile for a Workflow when that Workflow starts.
Changing the Issue selection or the Project default later affects only the next
run. It does not move an active Workflow to another Profile.

Profile content is not a runtime snapshot. When you edit the selected Profile,
its new Definition applies to Stages that the current run enters later. It does
not change a Stage that has already started or a Task that is already running.
Variables are resolved again before each Task starts. A Prompt is read when its
Task executes. The Task input is then fixed for the duration of that Task.

Mohist provides these built-in Profiles:

- `mohist/local`: Delivers through a local merge. This is the default and does
  not require a code-hosting platform.
- `mohist/github-pr`: Delivers through one GitHub pull request.

Profiles under `mohist/*` are updated with Mohist releases. Their source must not
be edited or deleted. An update affects an active Workflow at the same point as
any other Profile edit described above. Built-in Profiles run named built-in
Agents such as `mohist/planner`, `mohist/builder`, and `mohist/reviewer`, so a
new Project works without manual Agent creation. A Project Agent with the same
name overrides the built-in definition; create a new Project Profile when you
need to change the Stage graph or other built-in behavior.

## Profile Contents

A Profile contains:

- A name and a description of its intended use.
- Stages and the Tasks in each Stage.
- Stage Checks and Task completion expectations.
- Approval points.
- Failure recovery rules.
- Action Input and references to Variables and Prompts.

A Profile does not contain:

- Project, Issue, or Run Variable values.
- Prompt bodies.
- Runtime context such as Issue identity or repository state.
- Execution state or Task output from a specific Workflow.

The structure of `mohist/local` can be simplified as follows:

```yaml
approval:
  feedback:
    tasks:
      - id: apply-feedback
        uses: mohist/agent
        with:
          name: mohist/builder
          session: ${{ stage.name }}
          prompt: ${{ prompts.apply-feedback }}

stages:
  - stage: plan
    requiresApproval: true
    tasks:
      - id: plan
        title: Plan the change
        uses: mohist/agent
        with:
          name: mohist/planner
          session: plan
          prompt: ${{ prompts.plan }}
        expect:
          files:
            - path: PLANS/PLAN.md
            - path: PLANS/DESIGN.md
            - path: PLANS/tasks.json

  - stage: build
    requiresApproval: false
    tasks:
      # Expand the task list and verify each increment.

  - stage: check
    requiresApproval: true
    tasks:
      - id: review
        # A separate Agent session records review evidence for the approver.

  - stage: integrate
    requiresApproval: false
    tasks:
      - id: enable-auto-merge
        # Enable auto-merge and wait for the merge.
```

## Key Fields

### Definition Syntax

See the [Workflow Definition Reference](workflow-definition.md) for the complete
syntax of Stages, Tasks, `expect`, `artifacts`, `setVars`, recovery, Checks, and
template expressions.

The built-in Profiles use the execution Stages `plan`, `build`, `check`, and
`integrate`. `done` is a terminal state after the Workflow finishes, not a
configurable Stage with Tasks. By default, the Workflow waits for approval
after Plan and Check and advances automatically after Build and Integrate.

### Agent Tasks

Every Agent-backed task uses the `mohist/agent` Action with a named Agent:

```yaml
- id: proposal
  uses: mohist/agent
  with:
    name: mohist/planner
    session: plan
    prompt: ${{ prompts.proposal }}
```

`name` resolves to a Project Agent, falling back to the built-in Agents for
`mohist/*` names. The Agent definition owns the execution backend (OpenCode or
Pi), model, optional Reasoning Effort, true model variant, and Skills; the task
cannot override them. The task creates a real AgentJob and AgentSession, and
AgentJob owns execution and result. A missing, archived, or not-ready Agent
fails the launch explicitly. See the
[`mohist/agent` Action](actions/agent.md) for the complete input contract.

The optional `session` name continues one logical Session across tasks in the
same Run when the Agent and Workspace identities also match. Approval feedback,
recovery tasks, and generated Build tasks use the same `mohist/agent` binding.

A completed or stopped Run is an immutable execution record. It cannot be
retried, rerun, or resumed in place. Starting the Issue again creates a new Run
with the Profile that is effective for that new start.

### Variable References

A Profile can read Variables through expressions such as
`${{ vars.github.pr.number }}`. It does not declare their values.

Mohist merges Variables in this order: Project -> Issue -> Run. A later scope
overrides an earlier scope with the same key. Project and Issue Variables can
define Workflow-wide or per-Stage values. A Task's `setVars` writes
Workflow-wide Variables for the current Run so that later Tasks can use them.

A Variable affects execution only when the Profile binds it to Action Input,
`expect`, a Check, or another field that supports expressions.

### Prompt References

A Profile references a Project Prompt by key, such as `${{ prompts.plan }}`.
Prompt bodies are configured only on the Project.
Issues must not override them. Mohist uses the built-in Prompt when the Project
does not configure a built-in key.

## GitHub PR Profile

`mohist/github-pr` uses the same Plan -> Build -> Check -> Integrate path and
approval points as `mohist/local`, but it delivers the result differently:

- All Agent tasks run named Mohist Agents through `mohist/agent`; the Agent
  definition owns the backend and model. Approval feedback, recovery, and
  generated Build tasks follow the same rule.

- The remote Workflow branch preserves completed work between Stages. A Runner
  workspace can be rebuilt and is not responsible for preserving completed
  work. Plan and review material under `PLANS/` is preserved as uploaded run
  artifacts.
- After Plan completes, Mohist publishes the Workflow branch and then creates
  or reuses a draft pull request.
- After Build validation passes, Mohist publishes the current work.
- After Check review work is complete, Mohist publishes the current work and
  then marks the pull request ready.
- After Check approval, Integrate enables auto-merge on the pull request and
  waits until GitHub reports it merged.
- Mohist rebases automatically when the base branch advances and applies the
  declared Profile recovery when pull request checks fail.
- When automatic recovery is exhausted, Mohist leaves the Run in `failed` and
  exposes the failure. The user can fix the cause and retry.

The pull request is the review surface for the published Workflow branch. It is
not responsible for publishing code. A publish failure retries only the
publish. A pull request create or update failure leaves the remote work intact
and retries only that pull request operation. When approval feedback changes
the work, Mohist publishes the result before it reaches approval again.

The Runner host must have GitHub CLI installed and authenticated for the target
repository.

## Common Customizations

### Require Approval after Build

Set Build's `requiresApproval` value to `true`. Add
`approval.feedback.tasks` when rejected approvals should create follow-up work.
The built-in Profiles leave Build unapproved and advance directly to Check.

### Remove Check

Remove the Check Stage. This shortens the flow but removes the independent
review before Integrate.

### Add Deploy

Add a Stage after Integrate:

To send rejected Deploy approvals back to an agent, declare non-empty top-level
`approval.feedback.tasks`.

```yaml
- stage: deploy
  requiresApproval: true
  tasks:
    - id: deploy
      uses: core/script
      with:
        run: ./scripts/deploy.sh
```

### Configure the Agent for a Task

Every Agent-backed task names its Agent in the task input. To use a different
role, edit the Profile to reference another Agent, or create a Project Agent
that overrides the built-in definition of the same name:

```yaml
- id: proposal
  uses: mohist/agent
  with:
    name: mohist/planner
    session: plan
    prompt: ${{ prompts.proposal }}
```

The model, Reasoning Effort, variant, and Skills are Agent configuration, not
Workflow configuration. Change them on the Agent or in Project Agent settings;
a Workflow Variable cannot override them for one task.

## Manage Profiles

In Settings > Workflows, manage the current Project's Profile collection, edit
a custom Profile Definition, and select the Project default. The Issue details
page selects or changes a Profile. It does not edit the Profile Definition.

Editing a Definition affects later Stages in active Workflows that use the
Profile. An edit must retain every Stage used by an active Run. The active Run
keeps its original Stage order and Approval points, so an edit must retain
valid Approval feedback while that Run can still request it. Added or
reordered Stages apply only to future Runs.

Before saving, run `mo workflow validate --file <path>` to check the Definition
structure, field types, and template expressions locally. Use `--file -` to
read from stdin. The save operation then checks Action availability and Action
Input against contracts from the current Runner.

See [CLI Reference](cli-reference.md#workflow-profile) for the commands. A
Profile ID must be unique within its Project; global uniqueness is not
required. It may contain `/`. Pass the complete ID as one CLI argument. Built-in
Profiles use `mohist/<name>`. Custom Profiles must use an ID that describes
their purpose and remains stable.

## Implementation Gaps

- Project-scoped Profile collections, Project defaults, explicit Issue
  selection, and clearing back to the default are implemented. Server accepts a
  selection change during an active Run for the next Run. Each active Run keeps
  its Profile ID while later Stages read the current Definition.
- Settings currently combines the default template, Variables, and Prompts in
  one Workflow configuration. The target UI separates these three resources.
- Legacy Project-template and Issue-inline Definition editors still overlap the
  Profile collection. The Web Issue selector also remains locked during an
  active Run even though a Server-side selection would affect only the next
  Run.
- Profile changes take effect at future Stage and Task boundaries so a
  configuration fix can help later work without changing an attempt that is
  already running. A retry rebuilds the declared Task before it reads current
  Variables and Prompts; it never treats one attempt's resolved input as future
  configuration. This boundary preserves both live fixes and accepted-attempt
  stability. See
  [Workflow Recovery Design](../design/workflow/recovery.md).
- Agent tasks run through the unified AgentJob model: `mohist/agent` creates a
  real AgentJob and AgentSession, and AgentJob owns execution, retry, and
  result.
