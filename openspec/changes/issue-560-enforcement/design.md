# Design: Launch Task Scope Admission

## Authority and order

`AgentTaskScopeAdmission` is a pure Server-owned policy. It consumes the
resolved Agent profile and a launch context, and returns either a frozen
`AgentTaskScopeSnapshot` or one stable rejection code. `AgentLauncher` calls it
after prompt validation and before minting/opening a Session, binding an
attachment, or invoking an AgentJob grain. Replays that already resolve to a
durable idempotent plan keep the original accepted snapshot.

The shared launcher covers manual, mention, routed, and Slack connection
launches. Routed preflight records are not accepted work and must not bypass
the gate when the routed launcher is called directly.

## Deterministic rules

- A user-defined Agent must have a non-empty `purpose` at launch. The exact
  trimmed value is frozen in the snapshot; no semantic or substring test is
  attempted against the prompt.
- A non-empty repository, workspace name/path, or workspace repository
  binding requires `repo:read` or `repo:write`.
- An Issue context requires `issue:read` or `issue:write`.
- An Epic context requires `epic:read` or `epic:write`.
- A write permission implies the corresponding read permission for this
  context-admission check. An empty context has no inferred resource scope and
  therefore does not require a declaration beyond purpose.
- Built-in system Agents use an explicit system exception and supply their
  own profile snapshot; this does not make the exception available to project
  Agents.

The rejection is `agent_task_scope_rejected`, with a stable reason (`purpose_missing`
or `permission_missing`) and the missing terms. No Session, Job, attachment
binding, Runner claim, or external effect may exist after rejection.

## Frozen launch fact

`AgentTaskScopeSnapshot` is appended to `AgentExecutionDefinition`,
`AgentJobInput`, the manual launch coordinator plan, and the routed plan. The
Session settings and Runner `WorkDispatch.AgentDefinition` therefore retain
the purpose, declared permissions, and inferred required permissions for the
accepted launch. Existing records deserialize with a null snapshot and are
not retroactively re-evaluated.

## Owner boundary

`WorkflowItemTranslator.ResolveAgentTaskAsync` resolves an Agent snapshot and
builds a Workflow-owned `WorkDispatch` directly. It does not own a Session or
AgentJob and cannot call the shared launcher without changing Workflow task
identity and settlement. The follow-up contract must add the same
`AgentTaskScopeSnapshot` to the Workflow dispatch identity and reject before
claim/Runner submission. This slice records that boundary instead of adding a
second, divergent gate.
