# Conventions

## Identity

| Term | Meaning | Example |
|---|---|---|
| EntityId | domain entity identity | `issueId` |
| GrainKey | Orleans actor address | `workflowRunId` for WorkflowGrain |
| ResourceKey | REST path | `/projects/{id}/issues/{id}` |

- `EntityId` is stable and singular.
- `GrainKey` follows the owned entity.
- `ResourceKey` stays in URLs. Never in events, locks, or audit.
- Parent scope lives in metadata or ResourceKey, not EntityId.
- Display numbers and route aliases are lookup keys, not identity.
- No ad hoc keys (`projectId:issueNumber`).

## Role suffixes

| Suffix | Scope | Example |
|---|---|---|
| Querier | single-domain read projection | IssueQuerier |
| Assembler | cross-domain read assembly (AgentOps) | AgentActivityFeedAssembler |
| Reporter | cross-domain metrics (AgentOps) | AgentUsageReporter |
| Resolver | alias → canonical identity | IssueIdentityResolver |
| Manager | config or lifecycle policy | WorkflowProfileManager |
| Store | persistence boundary for one shape | WorkflowRunStore |

- No new `*QueryService` names.
- Assembler/Reporter belong to AgentOps. Never in leaf domains like Session.

## ResourceKey

```
/projects/{projectId}
/projects/{projectId}/issues/{issueId}
/workflow-runs/{workflowRunId}
```

Leading slash. Plural nouns. URL path segments. No trailing slash.

## Entity map

| Concept | EntityId | GrainKey | ResourceKey |
|---|---|---|---|
| Project | projectId | projectId | /projects/{projectId} |
| Issue | issueId | issueId | /projects/{projectId}/issues/{issueId} |
| WorkflowRun | workflowRunId | workflowRunId | /workflow-runs/{workflowRunId} |
| Runner | runnerId | runnerId | /projects/{projectId}/runners/{runnerId} |
| WorkflowBacklog | — | projectId | /projects/{projectId}/workflow-backlog |
| StageLock | — | internal id | /projects/{projectId}/workflow-stage-locks/{resource} |
| AgentSession | sessionId | sessionId | /projects/{projectId}/workflow-runs/{runId}/sessions/{name} |
| Event | eventId | — | /events/{eventId} |

## WorkflowRun metadata

```
WorkflowRun.Metadata
  ProjectId
  IssueId
```

`IssueNumber` is a display key. Never in workflow metadata.
Events, locks, scheduling: always use `workflowRunId` / `issueId`.

Route boundary: resolve issue number to issueId at entry.

## Runtime context vs profile

| | Runtime context | Profile variables |
|---|---|---|
| question | what facts does this dispatch need? | how is this run parameterized? |
| content | title, repo, prompt inputs, run facts snapshot | template < project < issue < dispatch injection |
| owner | run-start snapshot | WorkflowProfileManager |

Runtime context is not identity. Not profile config. Lifecycles are separate.
