---
purpose: "Cross-cutting naming and identity conventions."
include:
  - "EntityId, GrainKey, and ResourceKey rules."
  - "WorkflowRun ownership metadata."
  - "Runtime context vs profile variables."
exclude:
  - "Database schema details."
  - "HTTP payload field lists."
  - "Temporary migration code."
style:
  - "Prefer rules and examples over prose."
  - "Keep names stable and explicit."
---

# Conventions

本文记录跨模块约定。新的设计文档和代码命名优先遵守这里。

## Identity Terms

| Term | Meaning | Example |
|------|---------|---------|
| EntityId | 领域实体自己的稳定身份 | `issueId`, `workflowRunId` |
| GrainKey | Orleans actor address | `workflowRunId` for `WorkflowGrain` |
| ResourceKey | RESTful resource path | `/projects/{projectId}/issues/{issueId}` |

Rules:

- `EntityId` is stable and singular.
- `GrainKey` follows the entity owned by the grain.
- `ResourceKey` is only for URL/resource-path naming in the current migration.
- Parent scope belongs in metadata or `ResourceKey`, not in `EntityId`.
- Display numbers and route aliases are lookup keys, not entity identity.
- Avoid ad hoc keys like `projectId:issueNumber` in new designs.

## Role Names

| Suffix | Use For | Example |
|--------|---------|---------|
| `Querier` | Read-only projection/query boundary | `IssueQuerier` |
| `Resolver` | Translate aliases or external keys to canonical identity | `IssueIdentityResolver` |
| `Manager` | Owns configuration or lifecycle policy | `WorkflowProfileManager` |
| `Store` | Persistence boundary for one state shape | `WorkflowRunStore` |

Rules:

- Do not introduce new `*QueryService` names.
- Use `Querier` when the caller asks for read models or projections.
- Use `Resolver` when the caller asks "what is the canonical id for this route/display key?"
- Keep resolvers narrow. They should not enrich DTOs or compute workflow state.

## ResourceKey Format

```text
/projects/{projectId}
/projects/{projectId}/issues/{issueId}
/workflow-runs/{workflowRunId}
/projects/{projectId}/workflow-backlog
```

Rules:

- Use leading slash.
- Use plural resource names.
- Use URL path segments, not colon-delimited strings.
- No trailing slash.
- Encode path segments when they are not already safe ids.

## Entity Map

| Concept | EntityId | GrainKey | ResourceKey |
|---------|----------|----------|-------------|
| Project | `projectId` | `projectId` | `/projects/{projectId}` |
| Issue | `issueId` | `issueId` | `/projects/{projectId}/issues/{issueId}` |
| WorkflowRun | `workflowRunId` | `workflowRunId` | `/workflow-runs/{workflowRunId}` |
| Runner | `runnerId` | `runnerId` | `/projects/{projectId}/runners/{runnerId}` |
| WorkflowBacklog | none | `projectId` | `/projects/{projectId}/workflow-backlog` |
| StageLock | none | internal lock id | `/projects/{projectId}/workflow-stage-locks/{resource}` |
| AgentSession | `sessionId` | `sessionId` | `/projects/{projectId}/workflow-runs/{workflowRunId}/sessions/{sessionName}` |
| Event | `eventId` | none | `/events/{eventId}` |

## WorkflowRun Metadata

`WorkflowGrain` is keyed by `workflowRunId`.

`WorkflowRun` owns a run instance, not an issue slot. It should keep owner identity in metadata:

```text
WorkflowRun.Metadata
  ProjectId
  IssueId
```

`IssueNumber` is a display or route lookup value. It does not need to be stored in workflow metadata.

Event append, event query, locks, and scheduling should use `workflowRunId` and `issueId`. If an API route still receives issue number, resolve it to `issueId` at the boundary.

New workflow runs write `issueId` to metadata. Existing runs may still have legacy `issueKey = projectId:issueNumber`; read paths may use that only as fallback.

For now, `ResourceKey` is only a URL/resource-path convention. Do not force it into event rows, locks, or audit data until those surfaces are explicitly migrated.

Route identity flow:

```text
/projects/{projectId}/issues/{number}
  -> IssueIdentityResolver.GetIdAsync(projectId, number)
  -> issueId
  -> GrainKey.Issue(issueId)
```

## Runtime Context

Use `WorkflowRuntimeContext`, not `WorkflowRuntimeVariables`.

Runtime context is a run-start snapshot used to render dispatch payloads:

- issue title/body snapshot
- repository/workspace snapshot
- prompt and template input snapshot
- workflow run facts needed by the runner

Runtime context is not identity. Identity belongs in `WorkflowRun.Metadata`.

Runtime context is not profile configuration. Project/issue profile variables are managed by `WorkflowProfileManager`.

## Profile Variables

Profile variables are configurable workflow inputs:

```text
template embedded variables
  < project workflow profile variables
  < issue workflow profile variables
  < dispatch injection
```

They answer "how should this workflow run work be parameterized?"

Runtime context answers "what concrete run facts does this dispatch need?"

Keep those lifecycles separate.
