# Workflow Profile

A Workflow Profile is a Project resource that defines how a ready Issue moves
from Plan to Done. A Project can own multiple Profiles and select one as its
default. An Issue can inherit that default or select another Profile from the
same Project.

Variables and Prompts are separate resources. A Profile consumes them through
`${{ vars.* }}` and `${{ prompts.* }}` but does not own their values or bodies.
See [Workflow Definition Reference](workflow-definition.md) for Definition
syntax.

## Product Commitments

- A Profile contains one complete Workflow Definition and no hidden stages or tasks.
- An Issue uses one Profile or explicitly runs without a Workflow.
- A WorkflowRun binds the effective Profile and complete Definition at start.
- A Profile edit cannot change an active WorkflowRun.
- Built-in Profiles are release-managed and cannot be edited or deleted.

## Select a Profile

An Issue may explicitly select a Profile from its Project when it is created or
updated. Without an explicit selection, it inherits the Project default.
Clearing an explicit selection restores this inheritance. An Issue may opt out
of the production line with `mo issue create --no-workflow`; see [Issue
Management](issues.md).

Mohist resolves the effective Profile when the WorkflowRun starts. It captures
the Profile ID and complete validated Definition in that Run. The Run uses
that snapshot for every Stage, Approval Feedback behavior, and recovery rule.
Later changes affect only future Runs.

```text diagram
                +-------------+
                | Issue start |
                +------+------+
                       |
                       v
             +------------------+
             | Explicit Profile |
             |    selected?     |
             +---------+--------+
           +-----------+-----------+
           vyes                    vno
+--------------------+    +-----------------+
| same Project check |    | Project default |
+----------+---------+    +--------+--------+
           +-----------+-----------+
                       v
             +-------------------+
             | Effective Profile |
             +---------+---------+
                       |
                       v
            +---------------------+
            |  Run binds ID and   |
            | complete Definition |
            +---------------------+
```

A Profile selection must resolve within the Issue's Project. Profiles do not
inherit from or merge with one another.

Variables and Prompt bodies are resolved at Task dispatch. The dispatched input
stays fixed for that attempt. See
[Workflow Definition Reference](workflow-definition.md#template-expressions).

Mohist provides two built-in Profiles:

- `mohist/local` is the default. It delivers through a local merge and does not
  require a code-hosting platform.
- `mohist/github-pr` delivers through one GitHub pull request.

Profiles under `mohist/*` are updated with Mohist releases. Their source cannot
be edited or deleted. An update affects only WorkflowRuns that start after the
update. Built-in Profiles use named built-in Agents such as `mohist/planner`,
`mohist/builder`, and `mohist/reviewer`, so a new Project needs no manual Agent
creation. A Project Agent with the same name overrides the built-in definition.
Create a Project Profile when you need to change the Stage graph or another
built-in behavior.

## Profile Contents

A Profile contains:

- A stable ID, name, and description.
- Stages and ordered Tasks.
- Stage Checks and Task completion expectations.
- Approval Points and Feedback Tasks.
- Failure recovery rules.
- Action inputs and references to Variables and Prompts.

A Profile does not contain:

- Project, Issue, or Run Variable values.
- Prompt bodies.
- Runtime context such as Issue identity or repository state.
- Execution state or Task output from a specific WorkflowRun.

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

Every Agent-backed Task uses the `mohist/agent` Action with a named Agent:

```yaml
- id: proposal
  uses: mohist/agent
  with:
    name: mohist/planner
    session: plan
    prompt: ${{ prompts.proposal }}
```

`name` resolves to a Project Agent, falling back to a built-in Agent for
`mohist/*` names. The Agent definition owns the execution backend, model,
optional Reasoning Effort, true model variant, and Skills. The Task cannot
override them. The Task creates a real AgentJob and AgentSession. AgentJob
owns execution. AgentSession owns conversation continuity. Neither owns
Approval Point state.

A missing, archived, or not-ready Agent fails the launch explicitly. See the
[`mohist/agent` Action](actions/agent.md) for its complete input contract.

The optional `session` input requests named Session reuse. Mohist reuses that
name only when Agent and Workspace identities also match. Omitting it requests
no named reuse. Feedback Tasks, recovery Tasks, and generated Build Tasks use
the same binding. Mechanical Feedback Tasks, such as publication, use their
ordinary Actions, for example `mohist/push`.

A completed or stopped Run is an immutable execution record. It cannot be
retried, rerun, or resumed in place. Starting the Issue again creates a new Run
with the Profile effective at that new start.

### Variable References

A Profile reads Variables through expressions such as
`${{ vars.github.pr.number }}`. It does not declare their values.

Built-in Profiles use the separate Project-owned
`${{ workflow.verification.command }}` value for their single verification
Task. The command is captured when the WorkflowRun binds and runs from
`REPOS/${{ repository.name }}` with the built-in timeout and recovery contract.
It is not `vars.ci.verify`, and Project Variable edits cannot alter an in-flight
Run. A Project that needs multiple verification Tasks should create a custom
WorkflowProfile.

Mohist merges Variables in this order: Project, then Issue, then Run. A later
scope overrides an earlier scope with the same key. Project and Issue
Variables can define Workflow-wide or per-Stage values. A Task's `setVars`
writes Workflow-wide Variables for the current Run so later Tasks can use them.

A Variable affects execution only when the Profile binds it to Action Input,
`expect`, a Check, or another field that supports expressions.

### Prompt References

A Profile references a Project Prompt by key, such as `${{ prompts.plan }}`.
Prompt bodies are configured only on the Project. Issues cannot override them.
Mohist uses the built-in Prompt when the Project does not configure a built-in
key.

## GitHub PR Profile

`mohist/github-pr` uses the same Plan, Build, Check, and Integrate path and
Approval Points as `mohist/local`, but it delivers through one pull request.

```text diagram
         +------+
         | Plan |
         +---+--+
             |
             v
+------------------------+
| publish branch; create |
|   or reuse draft PR    |
+------------+-----------+
             |
             v
 +----------------------+
 |  Build validation:   |
 | publish current work |
 +-----------+----------+
             |
             v
+------------------------+
| Check review: publish, |
|     mark PR ready      |
+------------+-----------+
             |
             v
+------------------------+
| Check approval: enable |
|       auto-merge       |
+------------+-----------+
             |
             v
 +-----------------------+
 | wait for GitHub merge |
 +-----------------------+
```

- The remote Workflow branch preserves completed work between Stages. A Runner
  Workspace can be rebuilt and does not preserve completed work. Plan and review
  material under `PLANS/` is preserved as uploaded run artifacts.
- After Plan completes, Mohist publishes the Workflow branch and creates or
  reuses a draft pull request.
- After Build validation passes, Mohist publishes the current work.
- After Check review completes, Mohist publishes the current work and marks the
  pull request ready.
- After an approver selects Approve at the Check Approval Point, Integrate
  enables auto-merge and waits until GitHub reports the pull request merged.
- Mohist rebases automatically when the base branch advances and applies the
  declared Profile recovery when pull request Checks fail.
- When automatic recovery is exhausted, Mohist leaves the Run in `failed` and
  exposes the failure. The user can fix the cause and retry.

The pull request is the review surface for the published Workflow branch. It
is not responsible for publishing code. A publish failure retries only the
publish. A pull request create or update failure leaves remote work intact and
retries only that pull request operation. When Approval Feedback changes the
work, the Profile declares publication as a Feedback Task before the Run
returns to the Approval Point.

The Runner host must have GitHub CLI installed and authenticated for the target
repository.

## Common Customizations

### Require an Approval Point after Build

Set Build's `requiresApproval` value to `true`. Add non-empty
`approval.feedback.tasks` to make Request Changes available at that Approval
Point. The built-in Profiles advance directly from Build to Check.

### Remove Check

Remove the Check Stage. This shortens the flow but removes independent review
before Integrate.

### Add Deploy

Add a Stage after Integrate. To make Request Changes available after Deploy,
declare non-empty top-level `approval.feedback.tasks`.

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

Every Agent-backed Task names its Agent in the Task input. To use a different
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
Workflow configuration. Change them on the Agent or in Project Agent settings.
A Workflow Variable cannot override them for one Task.

## Manage Profiles

In Settings > Workflows, manage the current Project's Profile collection, edit
a custom Profile Definition, and select the Project default. The Issue details
page selects or changes a Profile. It does not edit the Profile Definition.

Before saving, run `mo workflow validate --file <path>` to check Definition
structure, field types, and template expressions. Use `--file -` to read from
stdin. The save operation then checks Action availability and Action Input
against contracts from the current Runner.

See [CLI Reference](cli-reference.md#workflow-profile) for the commands. A
Profile ID must be unique within its Project. Global uniqueness is not required.
An ID may contain `/`. Pass the complete ID as one CLI argument. Built-in
Profiles use `mohist/<name>`. Custom Profile IDs must describe their purpose
and remain stable.

## Implementation Gaps

- Settings currently combines the default template, Variables, and Prompts in one
  Workflow configuration. The target UI separates these resources.
- Legacy Project-template and Issue-inline Definition editors still overlap the
  Profile collection. The Web Issue selector also remains locked during an active
  Run even though a Server-side selection would affect only the next Run.
