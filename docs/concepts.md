# Core Concepts

Mohist advances product work through Issues, Workflows, Agents, and Runners.
A Project isolates one product and its execution resources. This document gives
the mental model that connects those concepts.

## Product Commitments

- A Project isolates its Issues, configuration, repositories, and execution data.
- An Issue is one unit of work with one Project-scoped identity and one target Repository.
- A Workflow advances a ready Issue from Plan to Done through its configured stages.
- An Epic groups Issues for one product goal and supplies one ready Issue at a time.
- A Mohist Agent has one reusable configuration and uses the same AgentJob launch boundary from every entry point.
- An AgentSession records continuing conversation context. An AgentJob owns each top-level execution.
- A WorkflowRun owns orchestration state and binds one complete Workflow Definition when it starts.
- An Agent Connection exposes one Agent in an external interaction location without copying its configuration.
- An Approval Point waits for Approve or Request Changes. Feedback Tasks apply requested changes before the same Approval Point is shown again.
- A Runner executes resolved Agent work and reports facts. It does not decide product state.

## How the Concepts Fit Together

```text diagram
      +-------+    +----------------+
      | Slack |    | External Agent |
      +---+---+    +--------+-------+
          |                 |
          v                 v
+------------------+    +-------+
| Agent Connection |    | Skill |
+---------+--------+    +---+---+
          +--------+--------+
                   v
           +--------------+
           | Mohist Agent |
           +-------+------+
                   |
                   v
              +---------+
              | Project |
              +---------+
```

Within one Project, an Epic supplies Issues. Each Issue moves through its
Workflow. Agent-backed tasks run a Mohist Agent, and mechanical Actions remain
Workflow orchestration:

```text diagram
                      +---------+
                      | Project +------------------+
                      +----+----+                  |
                           |                       |
                           v                       |
                       +------+                    |
                       | Epic |                    |
                       +---+--+                    |
                           |                       |
                           v                       |
                       +-------+                   |
                       | Issue |<------------------+
                       +---+---+
                           |
                           v
              + Workflow (mohist/local) +
              |        +------+         |
              |        | Plan |         |
              |        +---+--+         |
              |            |            |
              |            v            |
              |        +-------+        |
              |        | Build |        |
              |        +---+---+        |
              |            |            |
              |            v            |
              |        +-------+        |
              |        | Check |        |
              |        +---+---+        |
              |            |            |
              |            v            |
              |      +-----------+      |
              |      | Integrate |      |
              |      +-----+-----+      |
              |            |            |
              |            v            |
              |        +------+         |
              |        | Done |         |
              |        +------+         |
              +------------+------------+
              +------------+------------+
              v                         v
      +--------------+         +-----------------+
      | Mohist Agent |         | User repository |
      +-------+------+         +--------+--------+
              |                         |
              v                         v
 +-------------------------+  +------------------+
 | AgentJob + AgentSession |  | Product advances |
 +-------------------------+  +------------------+
```

Keep one mental model: a Project is the product and execution boundary; an Epic
owns a goal and supplies work; an Issue is the workpiece; a Workflow is the
production line; Mohist Agents are the workers; AgentJob owns each execution;
and AgentSession records continuing conversation. The Web UI is the fallback
operations and visualization plane.
