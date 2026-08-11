# Workflow Definition Reference

A Workflow Profile Definition is a YAML document. It declares the Stages that
an Issue follows, the initial Tasks, Checks, approval points, and the rules that
produce follow-up Tasks. This document is the complete syntax reference for a
Definition. See [Workflow Profile](workflow-profiles.md) for Profile selection
and management.

During a run, retry, recovery, approval feedback, and control commands such as
`mo issue rebase` can produce additional Tasks. These Tasks belong to the
current WorkflowRun and do not rewrite the Definition.

## Top-Level Structure

A Definition has only two top-level sections:

```yaml
approval:      # Optional. Feedback repair Tasks after an approval rejection.
  feedback:
    tasks:     # An ordered Task list.
      - <Task>

stages:        # Required. An ordered Stage list.
  - <Stage>
```

After an approval rejection, Mohist runs `approval.feedback.tasks` in order to
apply the feedback. The first Task usually continues the rejected Stage's
session. Later Tasks can publish the repaired work. When all feedback Tasks
finish, Mohist runs the Stage Checks again. The approver then sees the current,
published work.

## Stage

```yaml
- stage: integrate          # Required. The Stage name.
  requiresApproval: true    # Optional. Default: false. Wait for approval after the Stage.
  lockBehavior: sequential  # Optional. Run this Stage serially. Requires resources.
  resources:
    - project-integration   # A lock name. Only one Stage can hold a named lock at a time.
  tasks:                    # Required. An ordered Task list.
    - <Task>
  checks:                   # Optional. Validation before the Stage completes.
    - <Check>
```

See [Core Concepts: Approval](concepts.md#approval) for who can approve and how
the decision is submitted.

## Task

```yaml
- id: merge-pr                   # Required. The Task identifier within the Stage.
  title: Merge GitHub PR         # Optional. A user-facing name.
  uses: mohist/merge-github-pr   # Required. Selects an Action.
  with:                          # Optional. Action Input. Supports template expressions.
    working-directory: ${{ repository.path }}  # Optional engine-reserved execution directory.
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

See [Action Contracts](actions/README.md) for the available Actions and their
inputs and outputs. Each Action declares its input names, required fields,
default values, output fields, and error codes. Mohist validates `with` against
that declaration. It rejects unknown fields, missing required fields, and
invalid types instead of ignoring them.

`working-directory` is an engine-reserved field accepted under `with` for every
Action. It is not part of an Action's own input contract. An omitted value uses
`${{ workspace.path }}`. A relative value resolves from that root, and an
absolute value must still be the root or one of its descendants. Runner rejects
a path that escapes the Workspace, including a path whose existing ancestor is
a symbolic link outside it. Use `${{ repository.path }}` for tasks that operate
on the target checkout and `${{ workspace.path }}` for tasks that produce
Workspace-level plans, research, or reviews.

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

`expect` defines the Workflow's completion requirements for a Task. It is not
Action Input. An Action failure makes the Task fail. After the Action succeeds,
an unmet `expect` also makes the Task fail. File and marker paths under `expect`
resolve from the selected `working-directory`.

List a required output file in both `expect.files` and `artifacts.files`. Mohist
compares their normalized absolute paths, so the two declarations can use
different relative paths when their anchors differ. List an optional output
file only in `artifacts`.

### `artifacts`: Artifact Collection

Declared artifact paths resolve from `${{ workspace.path }}` even when the
Action uses another `working-directory`. After template expansion, Mohist
normalizes each path to a Workspace-root relative key. Absolute paths,
backtracking, symbolic links, and paths outside the Workspace are invalid.
Action-produced dynamic artifacts resolve from the selected
`working-directory` and are normalized to the same key space.

An artifact whose normalized file is also required by `expect` is a required
capture: missing content or a capture, upload, or recording failure fails the
Task. Other declared and dynamic artifacts are optional; Mohist records a
warning when one cannot be captured. Successfully recorded captures remain
available in Task details according to Artifact Store retention. Only captures
under `PLANS/` and `RESEARCH/` participate in Workspace rematerialization;
`REPOS/` is restored from Git and `.scratch/` is disposable.

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

- A handler must declare at least one of `tasks` or `retrySelf`.
- A handler with `when` matches the result context in declaration order. This
  match does not depend on Task success or failure. For example, a successful
  Task with `output.promise=FAIL` triggers
  `when: output.promise=FAIL`. A failed Task can use
  `when: error.code=...`.
- A handler without `when` is the default handler. A recovery declaration can
  have at most one default handler, and it must be last. It runs only when the
  Task fails and no earlier explicit handler matches.
- Recovery Tasks are real Workflow Tasks. They appear in progress and timeline
  views.
- After the budget is exhausted, automatic recovery stops. The Task fails and
  exposes the cause. A manual retry starts a new cycle with the full budget.

## Check

```yaml
- id: merge-verified        # Required. The Check identifier within the Stage.
  title: Merge verified     # Optional. A user-facing name.
  uses: mohist/github-pr-status
  with:
    working-directory: ${{ repository.path }}
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
```

A Stage completes only after all its Checks pass. If a Check fails, the
Workflow does not enter the next Stage.

## Template Expressions

Fields under `with`, `expect`, and `artifacts` can use `${{ }}` expressions.
The following table lists every available namespace. A root reference not
listed here is invalid.

| Expression | Meaning |
|---|---|
| `workflow.runId` | The current Run identifier |
| `stage.name` | The current Stage name |
| `work.*` | Current work information, such as `work.id`, `work.type`, `work.title`, and `work.attempt` |
| `work.approvalFeedback.*` | Available only to approval feedback Tasks. Information that triggered this work, such as `id`, `stage`, `createdAt`, and `summary` |
| `issue.*` | Issue information, such as `issue.projectId`, `issue.number`, `issue.title`, and `issue.body` |
| `repository.*` | Target Repository information. Resource facts include `name`, `gitUrl`, and `baseBranch`; Runner facts include the checkout `path` and Workflow `branch` |
| `workspace.*` | Workspace information: `workspace.name` and the materialized root `workspace.path` |
| `vars.*` | Merged Variables. See [Variable References](workflow-profiles.md#variable-references) |
| `tasks.<id>.outputs.*` | Output from a previous Task |
| `prompts.<key>` | A Project Prompt whose body is read when the Task executes |
| `failure.output` | Available only to recovery Tasks. Output from the Task that triggered recovery |
| `failure.error.code` | Available only to recovery Tasks. The triggering error code |
| `failure.error.message` | Available only to recovery Tasks. The triggering error message |

- Mohist expands templates before a Task starts. Input for a started Task stays
  fixed when Variables change later.
- `${{ prompts.<key> }}` is an exception: Mohist reads the Prompt body when the
  Task executes. Expressions in the Prompt body use the same namespaces,
  missing-value behavior, and interpolation rules.
- A Task fails when any expression cannot resolve a value. This includes a
  missing `${{ tasks.<id>.outputs.* }}` path.
- When an expression occupies the complete value, the replacement retains its
  original type, including object, array, or number.
- An expression can be embedded in a string, for example
  `openspec/changes/issue-${{ issue.number }}`. Mohist converts the value to
  text. The Task fails when the expression cannot resolve or its value is an
  object or array.
- Write `\${{` when the literal text `${{` is required.
- Effective Variables are exposed only through `${{ vars.* }}`. A Variable key
  does not also become a top-level name. Mohist also does not copy `workflow`,
  `stage`, `work`, `issue`, `repository`, `workspace`, `tasks`, `prompts`, or
  `failure` into `vars`.
- `workspace` describes only Workspace facts. It does not provide OpenSpec path
  conventions. A Profile or Prompt must write a path such as
  `openspec/changes/issue-${{ issue.number }}` explicitly.
- `repository.path` and `repository.branch` exist only after Runner materializes
  the target checkout. A Profile must not construct the checkout path from
  `workspace.path` or read a branch from `workspace` or `vars`.

## Validate a Definition

When you save a Profile, Mohist validates the Definition structure, field
types, and template expressions and returns all problems in one response. You
can also validate a file locally without a running Server:

```bash
mo workflow validate --file workflow.yaml
mo workflow validate --file -
```

The local command only validates the Workflow Definition language. The save
operation must use Action contracts from the current Runner to determine
whether a `uses` value is available and whether `with` satisfies the selected
Action's input contract.

## Complete Example

The following minimal Profile delivers through a GitHub pull request and uses
each construct once:

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
        uses: mohist/opencode
        with:
          working-directory: ${{ workspace.path }}
          session: plan
          prompt: ${{ prompts.proposal }}
          options: ${{ vars.agent }}
        expect:
          files:
            - path: PLANS/issue-${{ issue.number }}-PLAN.md
          markers:
            - path: _output
              oneOf:
                - <promise>done</promise>
                - <promise>unfinished</promise>
              failIf: <promise>unfinished</promise>
        artifacts:
          files:
            - path: PLANS/issue-${{ issue.number }}-PLAN.md
      - id: publish-plan
        uses: mohist/push
        with:
          working-directory: ${{ repository.path }}
          source: HEAD
          target: ${{ repository.branch }}
          remote: origin
          force: true
      - id: open-draft-pr
        uses: mohist/create-github-pr
        with:
          working-directory: ${{ repository.path }}
          repositoryUrl: ${{ repository.gitUrl }}
          source: ${{ repository.branch }}
          target: ${{ repository.baseBranch }}
          draft: true
          titleFrom: issue.title
          bodyFrom: issue.body
        setVars:
          github.pr.number: output.prNumber
          github.pr.url: output.prUrl
      - id: record-reviewed-head
        uses: mohist/github-pr-status
        with:
          working-directory: ${{ repository.path }}
          repositoryUrl: ${{ repository.gitUrl }}
          prNumber: ${{ vars.github.pr.number }}
          expect: open,ready
        setVars:
          github.reviewedHead: output.headSha
    checks:
      - id: health
        uses: core/script
        with:
          working-directory: ${{ repository.path }}
          run: git diff --check
          timeout: 300000

  - stage: integrate
    lockBehavior: sequential
    resources:
      - project-integration
    tasks:
      - id: merge-pr
        uses: mohist/merge-github-pr
        with:
          working-directory: ${{ repository.path }}
          repositoryUrl: ${{ repository.gitUrl }}
          prNumber: ${{ vars.github.pr.number }}
          expectedHeadSha: ${{ vars.github.reviewedHead }}
          method: squash
        recovery:
          budget: 2
          handlers:
            - when: error.code=retry-safe
              retrySelf: true
    checks:
      - id: merge-verified
        uses: mohist/github-pr-status
        with:
          repositoryUrl: ${{ repository.gitUrl }}
          prNumber: ${{ vars.github.pr.number }}
          expect: merged
```

## Validation Boundary

The authoritative validator checks the Definition structure, field types, and
template expressions when a Profile is saved. `mo workflow validate --file`
provides the same language validation locally. CI continuously validates the
built-in Profiles and the complete example in this document. The selected
Action contract still decides which `with` keys are valid, required, and of the
correct type. That check is outside the Definition validator's responsibility.
