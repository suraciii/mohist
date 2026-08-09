# Skills

A Skill is a reusable description of Agent capability. Mohist supports two
forms of use:

- A third-party external Agent installs Mohist Skills. In Slack, an IDE, or
  another Agent host, it identifies the scenario and uses `mo` to inspect state,
  delegate work, or perform an operation. Its conversation remains in its own
  host.
- A Mohist Agent selects Skills in its configuration. The same Skills apply
  whether Web, CLI, an Agent Connection, or an event starts the Agent. An entry
  point cannot add or remove capability for one launch.

An external Agent is not a Mohist Agent. The first interacts with the user
outside Mohist. The second is a stable Project resource executed by Mohist. A
Slack Bot is not an external Agent either; it is a client identity representing
one Mohist Agent through an Agent Connection. A Workflow invokes an Inline
Agent directly. See [Core Concepts](concepts.md) for all terms.

Interactive work such as requirement discovery and product exploration can be
performed by an external Agent through a Skill, or by a Mohist Agent configured
with `mohist-explore` through Web or Slack. Exploration must produce a ready
Issue that can enter a Workflow.

Exploration accepts input of any maturity: a one-sentence idea, conclusions
from a discussion, or a complete requirement. The Skill asks only about missing
parts and does not reopen settled decisions.

## Why Daily Interaction Is External

Users already have continuous conversation and work context in Slack, an IDE,
or another environment. Mohist does not duplicate those environments. It
exposes a Mohist Agent through Agent Connections and lets an external Agent use
Skills to call the execution surface. Exploration especially needs to be:

- **Real-time:** The user speaks while the Agent reasons.
- **Interactive:** The Agent asks, confirms, and adjusts.
- **Attended:** Unlike a Workflow, it cannot be submitted and ignored.

The daily interaction can remain external while Mohist executes the Agent:

```text
User + Slack Bot ---- Agent Connection ---- Mohist Agent + configured Skills
                                                  |
User + external Agent ---- Mohist Skill + mo -----+
                                                  |
                                                  v
                              Issue / Workflow / AgentJob execution and record
```

## Skills Distributed by Mohist

`mo skill list` shows distributable Skills. Four are currently available.

### `mohist`

This Skill operates Mohist and dispatches to scenario-specific Skills. It lets
an external Agent:

- Create, inspect, start, and approve Issues, and advance Epics.
- Summarize Project progress, pending decisions, blocks, and anomalies.
- Inspect logs and execution evidence, then select a definite recovery action.
- Invoke the `mo` CLI.
- Load the following specialized Skills for a matching scenario.

Use it for daily inspection and operation from an external Agent, such as
asking which Issues are advancing correctly or delegating a requirement to
Mohist.

### `mohist-explore`

This Skill clarifies a requirement from a product perspective. It accepts a
vague sentence or a finished requirement and guides the user to:

- Compare the input against its question list, mark what is already answered,
  and avoid asking those questions again.
- Ask only about gaps in user value, product boundary, and domain constraints.
- Decide whether to create one Issue or an Epic with several Issues. Each Issue
  must deliver value independently, with dependencies in explicit order.

Use it to clarify boundaries and acceptance criteria for a rough idea, or to
divide existing requirement material into independently deliverable Issues.

### `mohist-create-issue`

This execution Skill selects a template, prepares content, recommends Workflow
and risk, applies labels, confirms with the user, and runs `mo issue create`.
Before creation, it verifies that every Issue delivers value independently,
regardless of where the requirement originated.

### `mohist-create-epic`

This execution Skill writes the milestone description, links Issues, sets
prerequisites, and advances the autopilot lifecycle.

## Installing Skills for an External Agent

```bash
mo skill install
```

The command synchronizes Skill content into the external Agent configuration
directory. See `mo skill install --help` for its location.

After installation, an external Agent can load Skills through its normal
mechanism:

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

1. **Talk to an external Agent in the current work environment.**

   ```text
   I want search in the task list. Help me explore the product behavior.
   ```

2. **The external Agent loads `mohist-explore`.**

   It asks only unresolved questions, such as:

   - Search title, description, labels, or another field?
   - Highlight matches?
   - Keep search history?
   - Support 100 items or 10,000?

3. **Exploration produces a structured Issue body.**

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

4. **Use the `mohist` Skill to create the Issue.**

   ```text
   Create this Issue in the mohist-local Project.
   ```

   The Agent passes the exploration result as the body to `mo issue create`.

5. **Ask the external Agent to start and track the Issue.**

   ```text
   Start this Issue and tell me when I need to act.
   ```

   The external Agent runs `mo issue start`, and the Workflow takes over. The
   user remains in the existing environment. Open the Web UI only for complete
   state, detailed evidence, or manual intervention.

## Example: Is the Project Advancing?

Ask directly in an external Agent:

```text
@mohist Which Issues are advancing, and does any need attention?
```

The external Agent uses the Mohist Skill to read Project, Issue, Workflow, and
Runner state. It separates normal progress, waiting decisions, blocks, and
anomalies, then returns a conclusion and concrete next actions. The user does
not need to open the Web UI or combine several state fields manually.

## Direct Mohist Agent, CLI, and Web Use

An external environment is the default interaction surface, not the only one.
The user can launch a Skills-configured Mohist Agent through Web or CLI and can
invoke the same `mo` domain commands directly. Use the Web UI for a global view,
detailed evidence, or manual intervention. Its critical operations remain
complete.

For direct Issue creation, use the explore Skill structure of Background, Goal,
Non-goals, and Acceptance Criteria. This structure directly affects Plan
quality.

## Skill Boundary

A Skill **can**:

- Use `mo` to operate Issues, Workflows, and other Mohist resources.
- Write ordinary files such as exploration notes and Issue body drafts.
- Read Project state.

A Skill **cannot**:

- Write the Mohist database directly.
- Depend on an internal Mohist Runtime Session.
- Replace Mohist Workflow execution.

An Agent Connection cannot change Skills either. It transmits messages and
presents results. An Agent has the same configured Skills when tested in Web
and when reached through Slack.

## Custom Skills

Users can write their own Skills, for example:

- A Project-specific requirement template.
- A team code-review checklist.
- An exploration process for a specific Issue type.

A Skill is an ordinary file in an external Agent's Skill directory. Use the
output of `mo skill view mohist-explore` as a structural example.

Distributing a custom Skill currently requires manual copying into the external
Agent directory. Unified management through `mo skill` remains on the roadmap.

## Implementation Gaps

`mo skill install` and `mo skill view` serve external Agents. Skills can be
configured on a Mohist Agent, but Runner does not yet load them from the
AgentJob snapshot. Therefore, one Agent using the same Skills from every entry
point remains target behavior. Implement execution semantics before Slack
integration.

---

Source: `packages/cli/Mohist.Cli/skill-data/`. See
[`design/architecture.md`](../design/architecture.md) for the Agent Skill
boundary.
