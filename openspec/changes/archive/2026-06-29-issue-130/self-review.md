# Self Review Report

## Result: PASS

The plan is well-aligned with issue #130, internally consistent, and feasible. All
issue "Product Shape" questions map to a spec requirement and at least one task.
The dependency graph is acyclic, every non-first task has a valid `dependsOn`
chain to lower-priority tasks, and task granularity is appropriate (each task is
a complete feature slice; tests are inlined; no over-decomposed "define interface
/ register DI / extract class" tasks). Code references in `proposal.md` and
`design.md` were spot-checked against the tree
(`AgentSessionQuery.cs:105` switch with 8 existing keys including `SourceKind`;
`AgentSessionRow.cs:16` with 8 `Label*` columns; `GenericAgentSessionMetadata.cs`
with the 6 agent-launch key constants; `AgentSessionQuerier.cs:293/382/627/826`;
`WorkflowActivityQuerier.cs:44` blank-skip; `ActivityCardDto` at
`AgentSessionReadModels.cs:192`; `AgentSessionLaunchRoutes.cs:115`
`ResolveAgentAsync`; existing summary + transcript routes registered in
`AgentSessionFollowupRoutes.cs:31/45`) and match.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: T-005 description said "Add GET /api/projects/{projectRef}/agent-sessions/{sessionId}/transcript"
    and listed "new transcript route handler" in its output, but that route was already
    registered in `AgentSessionFollowupRoutes.cs:45` by #129 and is served by the existing
    `AgentSessionQuerier.GetGenericSessionTranscriptAsync` (line 302). Following the
    description literally could yield a duplicate route registration. Updated T-005
    description to state the route already exists from #129, that this task verifies it
    satisfies the spec and adds integration coverage rather than re-registering it, and
    adjusted the `output` field from "new transcript route handler" to
    "verified/covered transcript route handler". Acceptance criteria were already correct
    and are unchanged.
  Verification: Read `AgentSessionFollowupRoutes.cs:45-57` confirming the route and its
    `GetGenericSessionTranscriptAsync` handler; read `AgentSessionQuerier.cs:302-310`
    confirming the method reuses `LoadTranscriptAsync` and `FindGenericSessionAsync`
    (which already enforces project + `source-kind=agent-launch`, satisfying the
    "never return a workflow session" invariant). Re-read the edited `tasks.json` to
    confirm the description and output fields are coherent with the acceptance criteria.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: The `Generic AgentSession summary enrichment` requirement
    (`specs/agent-session-visibility/spec.md:97`) lists the context-reference envelope as
    "(issue, epic, project, repository, workspace path)", but design D4 and T-003's
    description define the `contextRefs` envelope as `(issueNumber, epicNumber,
    repository, workspacePath)` — i.e. without `project`. The proposal's "What Changes"
    only commits to "workspace/repository/context refs", and project is already the URL
    scope of every read path, so omitting it is a defensible design choice. The
    implementer should pick one shape and align spec wording with the design (or add
    `projectId` to the envelope) before T-003 lands, to avoid a spec-vs-implementation
    drift in the #132 Web contract.
  SuggestedAction: Either drop "project" from the spec's contextRefs wording with a note
    that project is the route scope, or add an explicit `projectId` field to the D4/T-003
    `contextRefs` envelope.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: completeness
  Evidence: T-002's illustrative method signature
    `ListAgentSessionsAsync(projectId, agentId, statusSet, limit)` does not show how a
    context-reference label filter is accepted, but the `agent-session-visibility` spec
    requires status + context-reference filters to compose
    (`specs/agent-session-visibility/spec.md:63-66`, "Combine status and context filters")
    and T-002's acceptance criteria explicitly test that combination. The http-api
    `?status=`-only list endpoint does not surface context-ref filtering, so the
    composition is a query-layer capability that the method signature should make
    explicit. This is an ambiguity, not a gap — the in-memory status filter from D2 will
    compose with any `ListByLabelsAsync` label set — but the implementer should either
    extend the signature with an optional context-ref labels parameter or document that
    callers compose via `ListByLabelsAsync` + the in-memory status filter.
  SuggestedAction: Clarify T-002's description to state how context-reference labels
    enter the agent-scoped list query (e.g. an optional `contextRefLabels` parameter),
    so the status+context-ref acceptance criterion has an obvious implementation path.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: feasibility
  Evidence: D5 keeps the existing non-nullable `IssueId`/`IssueNumber`/`IssueTitle`/
    `IssueStage` fields on `ActivityCardDto` and synthesizes `agent_{agentId}` into
    `IssueId` for generic sessions (with `AgentId`/`AgentName` added as nullable). That
    satisfies the spec literally (no `issue_{projectId}_0`), but it leaves
    workflow-shaped fields populated with synthetic values on generic cards, which #132
    renderers must learn to ignore. The design calls this out as additive-only and the
    spec permits it, so it is not blocking; flagging in case the Web contract should
    instead null those fields for generic cards.
  SuggestedAction: Consider, when implementing T-004, whether the synthetic
    `IssueId = agent_{agentId}` is acceptable for #132 or whether those workflow-shaped
    fields should be nulled for generic cards (which would require widening them to
    nullable and updating the regression spec).
  Status: follow-up

<promise>PASS</promise>
