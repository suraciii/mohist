# Workflow Profile

A Workflow Profile defines how an Issue moves from Draft to Done, including its
Stages, Tasks, Checks, recovery rules, and Approval Points. A Profile is a
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

Mohist determines the Profile when the WorkflowRun starts and binds its
complete Definition to that Run. Changing the Issue selection, Project default,
or Profile later affects only future WorkflowRuns. The bound Definition controls
every Stage, Approval Feedback behavior, and recovery in the current Run.

Variables and Prompt bodies are separate. Mohist resolves them at Task dispatch,
and the dispatched Task input stays fixed for that attempt. See
[Workflow Definition Reference](workflow-definition.md#template-expressions).

Mohist provides these built-in Profiles:

- `mohist/local`: Delivers through a local merge. This is the default and does
  not require a code-hosting platform.
- `mohist/github-pr`: Delivers through one GitHub pull request.

Profiles under `mohist/*` are updated with Mohist releases. Their source must not
be edited or deleted. An update affects only WorkflowRuns that start after the
update. Built-in Profiles run named built-in Agents such as `mohist/planner`,
`mohist/builder`, and `mohist/reviewer`, so a new Project works without manual
Agent creation. A Project Agent with the same
name overrides the built-in definition; create a new Project Profile when you
need to change the Stage graph or other built-in behavior.

## Profile Contents

A Profile contains:

- A name and a description of its intended use.
- Stages and the Tasks in each Stage.
- Stage Checks and Task completion expectations.
- Approval Points and Feedback Tasks.
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
          session: feedback-${{ stage.name }}
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
configurable Stage with Tasks. By default, the Workflow waits at an Approval
Point after Plan and Check and advances automatically after Build and Integrate.

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
cannot override them. The task creates a real AgentJob and AgentSession.
AgentJob owns execution and result. AgentSession owns conversation continuity.
Neither owns Approval Point state. A missing, archived, or not-ready Agent
fails the launch explicitly. See the
[`mohist/agent` Action](actions/agent.md) for the complete input contract.

The optional `session` input explicitly requests named Session reuse. Mohist
reuses that name only when the Agent and Workspace identities also match.
Omitting it requests no named reuse. Every Agent-backed Task explicitly uses
`mohist/agent` and names its Agent. Agent-backed Feedback, recovery, and generated
Build Tasks use this binding. Mechanical Feedback Tasks, such as publication, use
their ordinary Actions, for example `mohist/push`.

A completed or stopped Run is an immutable execution record. It cannot be
retried, rerun, or resumed in place. Starting the Issue again creates a new Run
with the Profile that is effective for that new start.

### Variable References

A Profile can read Variables through expressions such as
`${{ vars.github.pr.number }}`. It does not declare their values.

Built-in Profiles use the separate Project-owned `${{ workflow.verification.command }}`
value for their single verification Task. The command is captured when the WorkflowRun binds and
runs from `REPOS/${{ repository.name }}` with the built-in timeout and recovery contract. It is not
`vars.ci.verify`, and Project Variable edits cannot alter an in-flight Run. Projects needing multiple
verification Tasks should create a custom WorkflowProfile.

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
Approval Points as `mohist/local`, but it delivers the result differently:

- Every Agent-backed Task explicitly uses `mohist/agent` and names its Agent; the
  Agent definition owns the backend and model. Agent-backed Feedback and recovery
  Tasks, and generated Build Tasks, follow the same rule. Mechanical Feedback
  Tasks, such as publication, use their ordinary Actions, for example
  `mohist/push`.

- The remote Workflow branch preserves completed work between Stages. A Runner
  workspace can be rebuilt and is not responsible for preserving completed
  work. Plan and review material under `PLANS/` is preserved as uploaded run
  artifacts.
- After Plan completes, Mohist publishes the Workflow branch and then creates
  or reuses a draft pull request.
- After Build validation passes, Mohist publishes the current work.
- After Check review work is complete, Mohist publishes the current work and
  then marks the pull request ready.
- After an approver selects Approve at the Check Approval Point, Integrate
  enables auto-merge on the pull request and waits until GitHub reports it
  merged.
- Mohist rebases automatically when the base branch advances and applies the
  declared Profile recovery when pull request checks fail.
- When automatic recovery is exhausted, Mohist leaves the Run in `failed` and
  exposes the failure. The user can fix the cause and retry.

The pull request is the review surface for the published Workflow branch. It is
not responsible for publishing code. A publish failure retries only the
publish. A pull request create or update failure leaves the remote work intact
and retries only that pull request operation. When Approval Feedback changes
the work, the Profile declares publication as a Feedback Task before the Run
returns to the Approval Point.

The Runner host must have GitHub CLI installed and authenticated for the target
repository.

## Common Customizations

### Require an Approval Point after Build

Set Build's `requiresApproval` value to `true`. Add non-empty
`approval.feedback.tasks` to make Request Changes available at that Approval
Point. The built-in Profiles advance directly from Build to Check.

### Remove Check

Remove the Check Stage. This shortens the flow but removes the independent
review before Integrate.

### Add Deploy

Add a Stage after Integrate:

To make Request Changes available after Deploy, declare non-empty top-level
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

Editing a Definition affects only future WorkflowRuns. Each active Run keeps
its complete bound Definition, including Stage order, Approval Points, Feedback
Tasks, and recovery behavior.

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

- Settings currently combines the default template, Variables, and Prompts in
  one Workflow configuration. The target UI separates these three resources.
- Legacy Project-template and Issue-inline Definition editors still overlap the
  Profile collection. The Web Issue selector also remains locked during an
  active Run even though a Server-side selection would affect only the next
  Run.
