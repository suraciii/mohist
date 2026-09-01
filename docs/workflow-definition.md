# Workflow Definition Reference

A Workflow Definition is the YAML content of a Workflow Profile. It declares
ordered Stages, Tasks, Checks, Approval Points, and recovery rules for a
WorkflowRun. See [Workflow Profile](workflow-profiles.md) for Profile selection
and management.

A Profile source may also contain `id`, `name`, and `description`. Those fields
belong to the Profile resource. Mohist removes them before it parses the
Definition, whose top level contains only `approval`, `stages`, and
`recoveries`.

During a run, retry, recovery, Approval Feedback, and control commands such as
`mo issue rebase` may create Tasks. These Tasks belong to the current
WorkflowRun and do not rewrite its Definition.

## Product Commitments

A Definition is explicit and ordered. A WorkflowRun snapshots it at start.
Validation returns all structural and template errors together.

## Top-Level Structure

A Definition has only these three top-level sections:

```yaml
approval:      # Optional. Configures Approval Feedback work.
  feedback:
    tasks:     # Required when feedback is configured; non-empty and ordered.
      - <Task>

stages:        # Required. An ordered Stage list.
  - <Stage>

recoveries:    # Optional. Named recovery declarations reused by Tasks.
  <name>: <Recovery>
```

`approval.feedback.tasks` declares the ordered Feedback Tasks for Approval
Feedback. A non-empty list enables Request Changes for WorkflowRuns that bind
this Definition. Each Task must declare the work it needs, including its Agent,
prompt, named Session, timeout, or publication step. Mohist does not add omitted
work. See [Core Concepts: Approval Point](concepts.md#approval-point) for the
execution order and decision rules.

## Stage

```yaml
- stage: integrate          # Required. The Stage name.
  requiresApproval: true    # Optional. Default: false. Wait at an Approval Point after the Stage.
  lockBehavior: sequential  # Optional. Run this Stage serially. Requires resources.
  resources:
    - project-integration   # A lock name. Only one Stage can hold a named lock at a time.
  tasks:                    # Required. An ordered Task list.
    - <Task>
  checks:                   # Optional. Validation before the Stage completes.
    - <Check>
```

Stage names must be non-empty and unique. `tasks` must be non-empty and
ordered. `requiresApproval` defaults to `false` and waits at an Approval Point
when `true`. `lockBehavior` accepts `sequential` only and requires non-empty
`resources`; `resources` cannot appear alone. A Stage completes after all its
Checks pass.

See [Core Concepts: Approval Point](concepts.md#approval-point) for available
decisions and who can submit them.

## Task

```yaml
- id: enable-auto-merge                   # Required. The Task identifier within the Stage.
  title: Enable auto-merge         # Optional. A user-facing name.
  uses: mohist/enable-github-pr-auto-merge   # Required. Selects an Action.
  with:                          # Optional. Action Input. Supports template expressions.
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
  expect: <Expect>               # Optional. Completion requirements for this Task.
  artifacts:                     # Optional. Artifacts to collect.
    files:
      - path: <path>
  setVars:                       # Optional. Write Task output to Variables for this Run.
    github.pr.number: output.prNumber
  recovery: <Recovery>           # Optional. Failure recovery declaration.
```

`id` must be non-empty and unique within its task list. `uses` is required and
must contain a literal concrete Action name. It cannot contain a template.

The selected Action contract validates `with`. Each Action declares its input
names, required fields, defaults, outputs, and error codes. Mohist rejects
unknown fields, missing required fields, and invalid types. It does not ignore
invalid input. See [Action Contracts](actions/README.md) for Action inputs and
outputs.

`working-directory` is the one engine-reserved `with` key. It sets the
Workspace-relative directory before Action validation. Repository-only Tasks
use `working-directory: REPOS/${{ repository.name }}` to address the checkout;
see [Workspace](workspaces.md#layout). Runner derives the same Repository
independently for branch stability and clean-worktree enforcement, including
when an Agent runs from the Workspace root. A path that escapes the Workspace
fails the Task.

### `expect`: Completion Requirements

```yaml
expect:
  files:                    # These files must exist.
    - path: <path>
  markers:                  # The content must match one of the oneOf values or the Task fails.
    - path: <path>          # Or use _output to inspect the Agent's final response text.
      oneOf:
        - <promise>PASS</promise>
        - <promise>FAIL</promise>
      failIf: <promise>FAIL</promise>   # Optional. This text makes the Task fail.
```

`expect` defines completion requirements. It is not Action Input. An Action
failure fails the Task. After the Action succeeds, unmet `expect` requirements
also fail the Task.

List a required output file in both `expect.files` and `artifacts.files`. List
an optional output file only in `artifacts`.

### `artifacts`: Artifact Collection

Artifact collection is best effort. Mohist skips a file that does not exist
and does not fail the Task. Collected artifacts are stored permanently and are
available in Task details.

### `setVars`: Write Output to Variables

The left side is a path under `vars`. The right side is a field path in the
Task output. After the Task succeeds, Mohist writes these values to the current
Run's Variables. Later Tasks can read them through `${{ vars.* }}`. If any
write fails, the Task fails and Variables remain unchanged. A recovery Task can
overwrite the same value.

### `recovery`: Failure Recovery

```yaml
recovery:
  budget: 2                 # Optional. Default: 0. Limit for one automatic recovery cycle.
  handlers:                 # Ordered. Mohist uses the first matching handler.
    - when: error.code=conflict  # Optional. Match path=value in the result context.
      tasks:                # Optional. Recovery Tasks can have their own recovery rules.
        - <Task>
      retrySelf: true       # Optional. Default: false. Retry the original Task afterward.
```

- A handler must declare `tasks`, `retrySelf`, or both.
- A handler with `when` matches the result context in declaration order, regardless
  of Task success or failure. A successful Task with `output.promise=FAIL` can
  match `when: output.promise=FAIL`. A failed Task can match `when: error.code=...`.
- A handler without `when` is the default handler. A recovery declaration has at
  most one default handler, and that handler must be last. It runs only when the
  Task fails and no earlier explicit handler matches.
- Recovery Tasks are real Workflow Tasks and appear in progress and timeline views.
- After the budget is exhausted, automatic recovery stops and the Task fails with
  its cause. A manual retry starts a new cycle with the full budget.

## Check

```yaml
- id: merge-verified        # Required. The Check identifier within the Stage.
  title: Merge verified     # Optional. A user-facing name.
  uses: mohist/github-pr-status
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
```

A Check `id` must be non-empty and unique within its Stage. `uses` is required.

## Template Expressions

Mohist supports `${{ }}` expressions in fields under `with` and `expect`.
These root namespaces are valid:

- `workflow.runId`: the current Run identifier.
- `workflow.verification.command`: the Project-owned verification command frozen into
  the Run at binding. Built-in Profiles use it as the `core/script` `run` input.
- `stage.name`: the current Stage name.
- `work.*`: current work information, such as `work.id`, `work.type`, `work.title`,
  and `work.attempt`.
- `work.approvalFeedback.*`: information that triggered Feedback work, including
  `id`, `stage`, `createdAt`, and `summary`. It is available only to Feedback Tasks.
- `issue.*`: Issue information, such as `issue.projectId`, `issue.number`,
  `issue.title`, and `issue.body`.
- `repository.*`: target repository information, such as `repository.baseBranch`.
- `workspace.*`: Workspace information, such as `workspace.branch`.
- `vars.*`: merged Variables. See [Variable References](workflow-profiles.md#variable-references).
- `tasks.<id>.outputs.*`: output from a previous Task.
- `prompts.<key>`: a Project Prompt whose body is read when the Task executes.
- `failure.output`: output from the Task that triggered recovery. It is available
  only to recovery Tasks.
- `failure.error.code`: the triggering error code. It is available only to recovery
  Tasks.
- `failure.error.message`: the triggering error message. It is available only to
  recovery Tasks.

Evaluation follows this boundary:

```text diagram
     +--------------+
     | Profile YAML |
     +-------+------+
             |
             v
  +--------------------+
  | parse and validate |
  +----------+---------+
             |
             v
  +--------------------+
  | Run binds complete |
  |     Definition     |
  +----------+---------+
             |
             v
+------------------------+
| task dispatch resolves |
|         inputs         |
+------------+-----------+
             |
             v
  +---------------------+
  | attempt input stays |
  |        fixed        |
  +---------------------+
```

- Mohist expands templates before a Task starts. Input for a started Task stays fixed
  when Variables change later.
- `${{ prompts.<key> }}` is an exception. Mohist reads the Prompt body when the Task
  executes. Expressions in that body use the same namespaces, missing-value behavior,
  and interpolation rules.
- A Task fails when any expression cannot resolve a value. This includes a missing
  `${{ tasks.<id>.outputs.* }}` path.
- When an expression occupies a complete value, replacement retains its original
  type, including object, array, or number.
- An expression embedded in a string, such as `PLANS/tasks.json`, is converted to
  text. The Task fails when the expression cannot resolve or its value is an object
  or array.
- Write `\${{` when literal text `${{` is required.
- Effective Variables are exposed only through `${{ vars.* }}`. A Variable key is not
  a top-level name. Mohist also does not copy `workflow`, `stage`, `work`, `issue`,
  `repository`, `workspace`, `tasks`, `prompts`, or `failure` into `vars`.
- `workspace` describes Workspace facts, such as `workspace.path` and
  `workspace.branch`. It does not provide plan-artifact path conventions. A Profile
  or Prompt must write a path such as `PLANS/PLAN.md` explicitly.

## Validate a Definition

When a Profile is saved, Mohist validates Definition structure, field types,
template expressions, and concrete Action contracts. It returns all problems
in one response. You can validate Profile composition and Definition syntax
locally without a running Server:

```bash
mo workflow validate --file workflow.yaml
mo workflow validate --file -
```

The local command cannot check current Action availability. The save operation
uses Action contracts from the current Runner to determine whether each
concrete `uses` is available and whether `with` satisfies the selected Action's
input contract.

## Complete Example

The following minimal Profile delivers through a GitHub pull request and uses
each construct once:

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
      - id: proposal
        uses: mohist/agent
        with:
          name: mohist/planner
          session: plan
          prompt: ${{ prompts.proposal }}
        expect:
          files:
            - path: PLANS/PLAN.md
            - path: PLANS/DESIGN.md
            - path: PLANS/tasks.json
        artifacts:
          files:
            - path: PLANS/PLAN.md
            - path: PLANS/DESIGN.md
            - path: PLANS/tasks.json
      - id: publish-plan
        uses: mohist/push
        with:
          working-directory: REPOS/${{ repository.name }}
          source: HEAD
          target: ${{ workspace.branch }}
          remote: origin
          force: true
      - id: open-draft-pr
        uses: mohist/create-github-pr
        with:
          repositoryUrl: ${{ repository.gitUrl }}
          source: ${{ workspace.branch }}
          target: ${{ repository.baseBranch }}
          draft: true
          titleFrom: issue.title
          bodyFrom: issue.body
          working-directory: REPOS/${{ repository.name }}
        setVars:
          github.pr.number: output.prNumber
          github.pr.url: output.prUrl
    checks:
      - id: health
        uses: core/script
        with:
          run: git diff --check
          timeout: 300000
          working-directory: REPOS/${{ repository.name }}

  - stage: integrate
    lockBehavior: sequential
    resources:
      - project-integration
    tasks:
      - id: enable-auto-merge
        uses: mohist/enable-github-pr-auto-merge
        with:
          repositoryUrl: ${{ repository.gitUrl }}
          prNumber: ${{ vars.github.pr.number }}
          method: squash
        recovery:
          budget: 2
          handlers:
            - when: error.code=pr-checks-failed
              tasks:
                - id: recover:fix-pr-checks
                  uses: mohist/agent
                  with:
                    name: mohist/builder
                    session: integrate
                    prompt: ${{ prompts.fix-pr-checks }}
                - id: recover:push
                  uses: mohist/push
                  with:
                    working-directory: REPOS/${{ repository.name }}
                    source: ${{ workspace.branch }}
                    target: ${{ workspace.branch }}
                    remote: origin
                    force: true
              retrySelf: true
    checks:
      - id: merge-verified
        uses: mohist/github-pr-status
        with:
          repositoryUrl: ${{ repository.gitUrl }}
          prNumber: ${{ vars.github.pr.number }}
          expect: merged
```
