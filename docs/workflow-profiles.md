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
- `mohist/github-pr`: Uses Project Agents named `planner`, `coder`, and
  `reviewer`, then delivers through one GitHub Pull Request and Auto-merge.

Profiles under `mohist/*` are updated with Mohist releases and must not be edited
or deleted. An update affects an active Workflow at the same point as any other
Profile edit described above. Create a new Project Profile when you need to
change a built-in flow.

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
configurable Stage with Tasks. `mohist/local` waits for Approval after Plan and
Check. `mohist/github-pr` waits only after Plan; its named reviewer is the Check
gate. Both advance automatically after Build and Integrate.

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

`mohist/github-pr` keeps the Plan -> Build -> Check -> Integrate stage model but
assigns each judgment to a named Project Agent:

| Stage | Agent | Working directory | Durable result |
|---|---|---|---|
| Plan | `planner` | `${{ workspace.path }}` | `PLANS/issue-<number>-DESIGN.md` and `PLANS/issue-<number>-PLAN.md` |
| Build | `coder` | `${{ repository.path }}` | Commits published to `${{ repository.branch }}` and one draft Pull Request |
| Check | `reviewer` | `${{ workspace.path }}` | `PLANS/issue-<number>-REVIEW.md` with a `PASS` or `FAIL` decision |
| Integrate | none | `${{ repository.path }}` | The same Pull Request confirmed as `MERGED` by GitHub |

The Profile requires an active Agent named `planner`, `coder`, and `reviewer` in
the Project. Their definitions select Runtime, model, Instructions, and Skills.
The Profile does not fall back to an Inline Agent or let Variables select
another name. A missing or archived role fails its task with
`agent_not_found`.

Plan is the only default Approval point. The planner reads the Issue and target
Repository, writes the design and executable plan at the Workspace root, and
updates those same files when Plan feedback is rejected. Both files are
required captures: Approval is not offered until both have been recorded in
Mohist's Artifact Store. They are not committed to the target Repository merely
because the Workflow uses GitHub.

Build reads the approved plan, changes only the checkout, runs the Project's
verification command, publishes the Repository Workflow branch, and creates or
reuses one draft Pull Request. Check gives the reviewer the Issue, plan,
checkout, and diff.
`FAIL` schedules bounded coder repair, verification, and publish work before the
reviewer runs again. `PASS` marks the Pull Request ready and waits for required
checks. Check does not add a second human Approval point; the reviewer decision
is its gate. A review decision is bound to the exact Pull Request head commit.
Any later push invalidates it and requires the reviewer and required checks to
pass again before Integrate.

Integrate never pushes directly to the base branch. It enables GitHub Auto-merge
with squash and remains active until GitHub reports that exact Pull Request as
`MERGED`. Base movement, conflicts, and failed checks use declared recovery;
exhausted recovery blocks the Issue with the external state visible. Only a
confirmed `MERGED` result completes the Workflow. Recovery that changes content
first cancels queued Auto-merge, then returns through review and required checks
for the new head.

After its first publish, the remote Workflow branch is the recovery source for
Repository contents. Before that branch exists, rematerialization recreates the
local Workflow branch from the target Repository's current locked base branch.
Successfully recorded files under `PLANS/` are the recovery source for
Workspace-level artifacts, including a failed review report needed by repair.
`RESEARCH/` is durable only when a task declares its files as artifacts, and
`.scratch/` is never a recovery source.

The Runner host must have GitHub CLI installed and authenticated for the target
Repository, and the Repository must allow GitHub Auto-merge.

## Common Customizations

### Require Approval after Build

Set Build's `requiresApproval` value to `true`.

### Remove Check

Remove the Check Stage. This shortens the flow but removes the independent
review before Integrate.

### Add Deploy

Add a Stage after Integrate:

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
a custom Profile Definition, and select the Project default. The Issue details
page selects or changes a Profile. It does not edit the Profile Definition.

Editing a Definition affects later Stages in active Workflows that use the
Profile. Before saving, confirm that the change is also valid for those runs.

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
- The bundled `mohist/github-pr` Definition still uses Inline Agents and
  OpenSpec artifacts, and still requires human Approval after Check. It has not
  yet converged on the named-Agent roles, reviewer-only Check gate,
  Workspace-level plan files, Repository child checkout, or Auto-merge contract
  specified above. OpenSpec Actions remain available for custom Profiles; this
  Profile simply does not use them.
