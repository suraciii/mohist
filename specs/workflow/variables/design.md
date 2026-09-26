# Variables Design

## Design Drivers

- Project, Issue, and Run values use one resource shape and one merge algorithm.
- More specific scopes override less specific scopes. Stage values override
  Workflow values regardless of scope.
- Effective Variables are derived and read-only. They are never stored as a
  second source of truth.
- A dispatched attempt uses an immutable Variables snapshot. Later changes
  affect only future dispatches and new attempts.
- `setVars` writes only Run Workflow Variables. It cannot alter execution
  context or Stage Variables.

## Model

Project, Issue, and WorkflowRun Variables use the same shape:

```json
{
  "vars": { "agent": { "model": "model-a" } },
  "stages": {
    "check": { "vars": { "agent": { "variant": "variant-a" } } }
  }
}
```

- `vars` applies to all Stages.
- `stages.<stage>.vars` applies only to that Stage.
- Project Variables provide shared values for Workflows in the Project.
- Issue Variables override or add to Project Variables for one Issue.
- Run Variables store dynamic values for one WorkflowRun. A task `setVars`
  writes here.

A Workflow Profile may reference Variables, but it does not own, declare, or
restrict their keys. A Variable affects execution only when a Profile, task,
check, recovery, or Prompt references it.

The diagram abbreviates Project, Issue, and Run as `P`, `I`, and `R`; `WF` and
`ST` mean Effective Workflow and Effective Stage Variables:

```text diagram
+ Workflow merge --------------------------------+
|                                                |
|+--------+    +--------+    +--------+    +----+|
|| P.vars +--->| I.vars +--->| R.vars +--->| WF ||
|+--------+    +--------+    +--------+    +----+|
+------------------------------------------------+

+ Stage merge ------------------------------------------------+
|                                                             |
|+----+    +---------+    +---------+    +---------+    +----+|
|| WF +--->| P.stage +--->| I.stage +--->| R.stage +--->| ST ||
|+----+    +---------+    +---------+    +---------+    +----+|
+-------------------------------------------------------------+
```

Later sources override earlier sources. Effective Workflow Variables merge
`vars` in Project, Issue, Run order. Effective Stage Variables start with that
result and merge `stages.<stage>.vars` in the same order. Both results are
read-only and derived.
