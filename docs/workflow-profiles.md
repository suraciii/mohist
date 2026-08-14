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
default. Clearing an explicit selection also restores this inheritance.

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
any other Profile edit described above. A Project may configure a binding that
the built-in source explicitly exposes, such as the GitHub PR Profile's Agent
Action, without copying or editing that source. Create a new Project Profile
when you need to change the Stage graph or other built-in behavior.

## Profile Contents

A Profile contains:

- A name and a description of its intended use.
- An optional default Agent Action when the Profile exposes an Agent Action
  binding.
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
        uses: mohist/opencode
        with:
          session: ${{ stage.name }}
          prompt: ${{ prompts.apply-feedback }}
          options: ${{ vars.agent }}

stages:
  - stage: plan
    requiresApproval: true
    tasks:
      - id: proposal
        title: Generate proposal
        uses: mohist/opencode
        with:
          session: plan
          prompt: ${{ prompts.proposal }}
          options: ${{ vars.agent }}
        expect:
          files:
            - path: openspec/changes/issue-${{ issue.number }}/proposal.md
      - id: specs
        # ...
      - id: design
        # ...
      - id: tasks
        # ...
      - id: self-review
        # ...
    checks:
      - id: plan-artifacts
        with:
          changeDir: openspec/changes/issue-${{ issue.number }}

  - stage: build
    requiresApproval: false
    tasks:
      # Execute tasks.json.

  - stage: check
    requiresApproval: true
    tasks:
      - id: review
        # The Inline Agent reviews its own output.

  - stage: integrate
    requiresApproval: false
    tasks:
      - id: merge
        # Merge into the base branch.
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

### Agent Action Binding

The `mohist/github-pr` Profile exposes one Agent Action binding for all of its
inline Agent work. Its source declares the default and uses the restricted
Profile expression wherever an Agent Action is required:

```yaml
id: mohist/github-pr
name: GitHub PR
agentAction: mohist/opencode

stages:
  - stage: plan
    tasks:
      - id: proposal
        uses: ${{ profile.agentAction }}
        with:
          session: plan
          prompt: ${{ prompts.proposal }}
          options: ${{ vars.agent }}
```

Project settings may select another compatible concrete Action, such as
`mohist/pi`, for this Profile. The selected Action must provide the `agent-turn`
capability and accept every Agent task input in the materialized Profile. Mohist
validates the whole Profile before saving the change. If no current Action
catalog is available, the change is rejected instead of saving an unvalidated
Action choice.

The binding is not a Variable. `${{ profile.agentAction }}` is valid only as a
complete `uses` value and cannot be used in `with`, `expect`, or a string. A
WorkflowRun fixes the effective Action when it starts. Changing the Project
binding later affects only new Runs; later Stages of the active Run continue to
use the Action already bound to that Run.

While a bound Run is active, the Issue model selector uses that Run's Runtime
for both Workflow-wide and Stage-specific model controls. After the Run becomes
terminal, it uses the Runtime projected from the Profile selected for the next
Run. Changing the binding does not rewrite or clear `vars.agent`; a configured
model that is absent from the selected Runtime remains visible until it is
changed or cleared explicitly.

### Variable References

A Profile can read Variables through expressions such as `${{ vars.agent }}`.
It does not declare their values.

Mohist merges Variables in this order: Project -> Issue -> Run. A later scope
overrides an earlier scope with the same key. Project and Issue Variables can
define Workflow-wide or per-Stage values. A Task's `setVars` writes
Workflow-wide Variables for the current Run so that later Tasks can use them.

A Variable affects execution only when the Profile binds it to Action Input,
`expect`, a Check, or another field that supports expressions.

### Prompt References

A Profile references a Project Prompt by key, such as
`${{ prompts.proposal }}`. Prompt bodies are configured only on the Project.
Issues must not override them. Mohist uses the built-in Prompt when the Project
does not configure a built-in key.

## GitHub PR Profile

`mohist/github-pr` uses the same Plan -> Build -> Check -> Integrate path and
approval points as `mohist/local`, but it delivers the result differently:

- Project settings can run all inline Agent tasks through the Profile's default
  `mohist/opencode` Action or another compatible Action such as `mohist/pi`.
  Approval feedback, recovery, and OpenSpec-generated Build tasks use the same
  Run-bound Action.

- The remote Workflow branch preserves completed work between Stages. A Runner
  workspace can be rebuilt and is not responsible for preserving completed
  work.
- After Plan self-review passes, Mohist publishes the current work and then
  creates or reuses a draft pull request.
- After Build validation passes, Mohist publishes the current work.
- After Check fixes are complete, Mohist publishes the current work and then
  marks the pull request ready.
- After Integrate publishes the archived work, Mohist waits for pull request
  checks and performs a squash merge.
- Mohist rebases automatically when the base branch advances and applies the
  declared Profile recovery when pull request checks fail.
- When automatic recovery is exhausted, Mohist stops and exposes the failure.
  The user can fix the cause and retry.

The pull request is the review surface for the published Workflow branch. It is
not responsible for publishing code. A publish failure retries only the
publish. A pull request create or update failure leaves the remote work intact
and retries only that pull request operation. When approval feedback changes
the work, Mohist publishes the result before it reaches approval again.

The Runner host must have GitHub CLI installed and authenticated for the target
repository.

## Common Customizations

### Require Approval after Build

Set Build's `requiresApproval` value to `true`. The Profile must also declare
non-empty `approval.feedback.tasks`; built-in Profiles already do.

### Remove Check

Remove the Check Stage. This shortens the flow but removes the independent
review before Integrate.

### Add Deploy

Add a Stage after Integrate:

The Profile must declare non-empty top-level `approval.feedback.tasks` because
Deploy waits for approval.

```yaml
- stage: deploy
  requiresApproval: true
  tasks:
    - id: deploy
      uses: core/script
      with:
        run: ./scripts/deploy.sh
```

### Pin a Model for One Task

A fixed value used by only one Task can be written directly in its Action Input:

```yaml
- id: proposal
  uses: mohist/opencode
  with:
    session: plan
    prompt: ${{ prompts.proposal }}
    options:
      model: anthropic/claude-sonnet-4
      variant: high
```

When the Project or Issue must control the value, use
`options: ${{ vars.agent }}` instead and provide the value through the separate
Variables settings.

## Manage Profiles

In Settings > Workflows, manage the current Project's Profile collection, edit
a custom Profile Definition, configure bindings exposed by a Profile, and
select the Project default. The Issue details page selects or changes a Profile.
It does not edit the Profile Definition or its Project binding.

Editing a Definition affects later Stages in active Workflows that use the
Profile. Mohist validates the edit with every Agent Action bound to an active
Run and rejects an edit that removes the binding or is incompatible with one of
those Actions.

Before saving, run `mo workflow validate --file <path>` to check the Definition
structure, field types, and template expressions locally. Use `--file -` to
read from stdin. The save operation then checks Action availability and Action
Input against contracts from the current Runner.

See [CLI Reference](cli-reference.md#workflow-profile) for the commands. A
Profile ID must be unique within its Project; global uniqueness is not
required. It may contain `/`. Pass the complete ID as one CLI argument. Built-in
Profiles use `mohist/<name>`. Custom Profiles must use an ID that describes
their purpose and remains stable.

## Current Status and Remaining Gaps

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
- Some built-in Tasks still use legacy Action Input. The target interfaces are
  defined by the Action documentation.
- Profile Agent Action binding, Run-bound materialization, and the configurable
  `mohist/github-pr` Profile are planned in the current implementation change.
