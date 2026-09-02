# Skills

A Skill is a reusable description of an Agent capability. Mohist supports
External Agents that install Skills and Mohist Agents that select Skills in
configuration. The Skill changes what an Agent may do; it does not create a
second execution path.

## Product Commitments

- An External Agent uses Mohist Skills to inspect or operate Mohist from its
  own environment. It is not a Mohist resource.
- A Mohist Agent selects Skills in its configuration. The same Skills apply
  when Web, CLI, an Agent Connection, or an event starts the Agent.
- An entry point cannot add or remove capability for one launch.
- Exploration asks only unresolved questions and produces a ready Issue that
  can enter a Workflow.
- Skills use the `mo` command surface and cannot write the Mohist database or
  replace Workflow execution.
- `mo skill list` exposes discovery descriptions. `mo skill view` exposes
  complete content.
- A Project or user Skill can override a built-in Skill. The installed catalog
  is the final fallback.

## External Agent and Mohist Agent

An External Agent interacts with the user outside Mohist and operates Mohist
through a Skill. A Mohist Agent is a stable Project resource executed by
Mohist. A Slack Bot is a client identity for one Mohist Agent through an Agent
Connection, not an External Agent.

Interactive requirement discovery can run in an External Agent or in a
Mohist Agent configured with `mohist-explore` through Web or Slack. It accepts
an idea or a finished requirement and asks only about missing parts. It does
not reopen settled decisions.

## Why Daily Interaction Is External

Mohist does not duplicate the user's continuous context in Slack, an IDE, or
another environment. It exposes Mohist Agents through Agent Connections and
lets External Agents use Skills to call the same execution surface. Daily
interaction is real-time, interactive, and attended; Workflow execution can
then continue in Mohist.

```text diagram
+------------------+    +-----------------------+
| User + Slack Bot |    | User + External Agent |
+---------+--------+    +-----------+-----------+
          +------------+------------+
                       vAgent Connection
           +-----------------------+
           | Mohist Agent + Skills |
           +-----------+-----------+
                       |
                       v
            +--------------------+
            | Issue / Workflow / |
            |      AgentJob      |
            +--------------------+
```

## Skills Distributed by Mohist

`mo skill list` shows distributable Skills. Mohist Agents resolve Skills in
this order:

1. The execution Workspace.
2. The user's External Agent directory and explicitly configured Skill roots.
3. The installed Mohist catalog, refreshed by `mo update`.

A Project or user override can replace a built-in Skill without copying the
installed catalog into an interaction Workspace.

### `mohist`

This Skill operates Mohist from an External Agent. It can:

- Create, inspect, start, and approve Issues, and advance Epics.
- Summarize Project progress, pending decisions, blocks, and anomalies.
- Inspect logs and execution evidence, then select a recovery action.
- Invoke the `mo` CLI.
- Load a specialized Skill for a matching scenario.

Use it for daily inspection and operation, such as asking which Issues need
attention or delegating a requirement to Mohist.

### `mohist-explore`

This Skill clarifies a requirement from a product perspective. It can:

- Compare input with its question list and skip answered questions.
- Ask only about user value, product boundary, and domain constraints.
- Choose one Issue or an Epic with several independently valuable Issues.
- Record dependencies in explicit order.

Use it for a rough idea, boundary clarification, or Issue decomposition.

### `mohist-create-issue`

This execution Skill selects a template, prepares content, recommends
Workflow, risk, and labels, confirms with the user, and runs `mo issue create`.
Every Issue must deliver value independently, regardless of its origin.

### `mohist-create-epic`

This execution Skill writes the milestone description, links Issues, sets
prerequisites, and advances the Epic lifecycle.

## Installing Skills for an External Agent

```bash
mo skill install
```

The command synchronizes Skill content into the External Agent configuration
directory. See `mo skill install --help` for its location. The External Agent
then loads each Skill through its normal mechanism:

- OpenCode selects a Skill from its description.
- Claude Code matches its Skill description.
- Other Agent tools use their own Skill-loading mechanism.

## Reading Complete Skill Content

`mo skill list` returns a discovery stub with a short description. Read the
complete content with:

```bash
mo skill view mohist
mo skill view mohist-explore
mo skill view mohist-create-issue
mo skill view mohist-create-epic
```

The output matches the current Mohist version. Every `mo skill install`
refreshes it.

## Example: From Idea to Issue

Assume the user wants search in a task list but has not decided the details.

1. Talk to an External Agent in the current work environment.

   ```text literal
   I want search in the task list. Help me explore the product behavior.
   ```

2. The External Agent loads `mohist-explore` and asks only unresolved
   questions.

3. Exploration produces a structured Issue body.

   ```markdown
   ## Background
   Users cannot find older tasks and need search.

   ## Goal
   Filter tasks by title as the user types.

   ## Non-goals
   - Do not search descriptions.
   - Do not add advanced filters.

   ## Acceptance Criteria
   - Filter on input with a response under 100 ms.
   - Match without case sensitivity.
   ```

4. Use the `mohist` Skill to create the Issue.

   ```text literal
   Create this Issue in the mohist-local Project.
   ```

   The Agent passes the exploration result as the body to `mo issue create`.

5. Ask the External Agent to start and track the Issue.

   ```text literal
   Start this Issue and tell me when I need to act.
   ```

   The External Agent runs `mo issue start`, and the Workflow takes over. The
   user stays in the existing environment. Open the Web UI for complete state,
   detailed evidence, or manual intervention.

## Example: Is the Project Advancing?

Ask directly in an External Agent:

```text literal
@mohist Which Issues are advancing, and does any need attention?
```

The External Agent uses the Mohist Skill to read Project, Issue, Workflow, and
Runner state. It separates progress, decisions, blocks, and anomalies, then
returns a conclusion and concrete next actions.

## Direct Mohist Agent, CLI, and Web Use

An external environment is the default interaction surface, not the only one.
The user can launch a configured Mohist Agent through Web or CLI and can invoke
the same `mo` commands directly. Use the Web UI for a global view, detailed
evidence, or manual intervention.

For direct Issue creation, use the Explore structure of Background, Goal,
Non-goals, and Acceptance Criteria. This structure affects Plan quality.

## Skill Boundary

A Skill can:

- Use `mo` to operate Issues, Workflows, and other Mohist resources.
- Write ordinary files such as exploration notes and Issue body drafts.
- Read Project state.

A Skill cannot:

- Write the Mohist database directly.
- Depend on an internal Mohist Runtime Session.
- Replace Mohist Workflow execution.

An Agent Connection cannot change Skills. It transmits messages and presents
results. Web and CLI use the same configured Skills.

## Custom Skills

Users can write Skills for a Project requirement template, a review checklist,
or a specific Issue exploration process. A custom Skill is an ordinary file in
an External Agent's Skill directory. Use `mo skill view mohist-explore` as a
structural example.

Distributing a custom Skill currently requires manual copying into the
External Agent directory.

## Implementation Gaps

`mo skill install` and `mo skill view` serve External Agents. Mohist snapshots
an Agent's configured Skills with its execution definition, and Runner injects
the same set for direct launches, Workflow Tasks, and follow-up Turns. CLI,
Web, and Slack entry points therefore do not change Agent capability. Custom
Skill distribution remains manual.

---

Implementation source: `packages/go/mohist-cli/skill-data/`. See
[`design/architecture.md`](../design/architecture.md) for the Agent Skill
boundary.
