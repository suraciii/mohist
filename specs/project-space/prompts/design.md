# Prompts Design

## Design Drivers

- Store one complete Prompt body per Project key. Do not merge bodies across
  scopes.
- Bind a Prompt body to an attempt at dispatch. Later edits affect only later
  attempts.
- Keep Prompt resolution independent from WorkflowProfile, Issue, and
  WorkflowRun ownership.
- Use the same closed template namespace and rendering rules as `with` and
  `expect`.
- Keep builtin content portable across managed repositories and technology
  stacks.

## Model

WorkflowProfile stores only a Prompt key reference, such as
`${{ prompts.plan }}`. The Project owns configured Prompt bodies. The product
owns builtin fallback bodies. The attempt snapshot owns the body selected at
dispatch.

```text diagram
 +-----------------+   +-------------------------+   +-------------------------+
 | WorkflowProfile |   | Project Prompts: key -> |   | Builtin Prompts: key -> |
 |  prompts.<key>  |   |          body           |   |          body           |
 +--------+--------+   +------------+------------+   +------------+------------+
          +-------------------------+-+---------------------------+
                                      vprojectId + key
                             +-----------------+
                             | Prompt Resolver |
                             +--------+--------+
                                      |
                                      vload body by key at dispatch
                            +------------------+
                            | Attempt Snapshot |
                            +---------+--------+
                                      |
                                      vRunner renders before Action
                             +-----------------+
                             | Rendered Prompt |
                             +-----------------+
```

At dispatch, Server loads the Project Prompt or builtin fallback by key and
freezes the selected body in the immutable attempt snapshot. If neither source
has the key, dispatch fails with an actionable domain error. The Action receives
only the rendered Prompt text and cannot read Prompt or Variable resources.

The Prompt model has no revision or body-snapshot field. Redelivery reuses the
attempt snapshot. Retry and rerun-from-stage create new attempt snapshots. A
later Prompt change never changes an existing attempt.
