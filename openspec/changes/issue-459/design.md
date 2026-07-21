## Context

Issue-scoped session metadata and transcript routes delegate to `AgentSessionQuerier`. Their current resolver first reads the issue's active `workflowRunId`, then resolves the logical `AgentSession` by project, issue number, workflow run, and session name. Workflow termination clears the issue's active run reference, but the persisted session retains project, issue, workflow-run, source-kind, and session-name labels, so the resolver returns not found while the audit record still exists.

The Session context owns session lookup and transcript projection. Web and CLI consumers already use the issue-scoped API and require no response-shape changes. Session command routes also use the current active-run resolver, so the read fallback must not broaden command targeting.

## Goals / Non-Goals

**Goals:**

- Keep issue-scoped metadata and transcript reads available after the issue has no active workflow run.
- Preserve exact active-run resolution and not-found behavior for in-progress issues.
- Preserve project and issue isolation and existing runtime-session transcript filtering.
- Reuse persisted session labels and current indexes without a schema migration or backfill.

**Non-Goals:**

- Enabling follow-up, compact, reset, or cancel against a historical session through issue-scoped command routes.
- Changing AgentSession identity, WorkflowRun lifecycle, issue terminal transitions, transcript projection, or API response shapes.
- Defining a deterministic tie-breaker for multiple same-name historical sessions with identical creation times.
- Changing workflow-run-scoped or generic AgentSession routes.

## Decisions

### Separate readable-session resolution from command resolution

Add a read-specific issue session resolver inside `AgentSessionQuerier` and use it only from `GetSessionMetadataAsync` and `GetSessionTranscriptAsync`. Keep `ResolveIssueSessionIdAsync` and `ResolveFollowupTargetAsync` on the existing active-run-only resolver so command routes retain their current ownership guard.

Alternative considered: change `FindCurrentSessionAsync` in place. This is smaller mechanically, but that helper is shared by metadata, transcript, follow-up, compact, reset, and cancel paths; adding fallback there would make historical sessions command targets and exceed the change contract.

### Branch on the issue's active workflow-run reference

The read resolver first loads the requested issue's current workflow-run reference. When present, it performs the existing exact label lookup using project ID, issue number, workflow run ID, and session name. It does not fall back if the requested name is absent from that active run.

When the active reference is absent, the resolver queries persisted sessions by project ID, issue number, `source-kind = workflow`, and session name, ordered by creation time descending with a limit of one. The source-kind predicate prevents an issue-associated generic Agent launch from being treated as a Workflow session. The resulting record's stable `sessionId` remains the canonical identity used to load metadata and transcript content.

Alternative considered: preserve the terminal WorkflowRun ID on the Issue. That changes Issue lifecycle semantics and risks treating history as an active binding. Another alternative is to reconstruct run history from WorkflowRun records before querying sessions; the session already carries the required issue correlation labels, so that adds cross-context coupling without improving correctness.

### Reuse existing query and persistence structures

Use `AgentSessionQuery.FirstByLabelsAsync` with `CreatedDescending` for the historical branch. `AgentSessions` already exposes stored computed columns for project ID, issue number, source kind, session name, and creation time, with an index beginning with project ID and issue number. No new repository, read model, schema field, or migration is needed.

Alternative considered: add a dedicated historical-session table or a new Issue history field. Both duplicate existing Session facts and introduce write synchronization for a read-only correlation problem.

### Verify behavior through the issue-scoped API

Add focused server specs for completed and cancelled issue history, active-run precedence, a genuinely missing session, and runtime-session filtering on a historical transcript. The tests should seed or drive persisted sessions through in-memory test infrastructure and exercise the metadata/transcript HTTP routes; no Web change or browser test is required because routing and response rendering are unchanged.

Alternative considered: test only the private resolver through a new abstraction. That would add an interface solely for testing and would not verify the user-visible route that currently returns 404.

## Risks / Trade-offs

- [Historical lookup could accidentally expose a session from another issue or source] -> Require project ID, issue number, workflow source kind, and session name in the fallback query.
- [Changing a shared resolver could enable commands on terminal history] -> Keep historical fallback in a read-specific resolver and retain active-run-only command resolution.
- [Multiple matching sessions can share the same creation time] -> Accept the existing creation-order limitation; deterministic tie-breaking remains outside this issue.
- [Historical lookup scans more candidates than run-scoped lookup] -> Use the existing project/issue/creation index, descending order, and `LIMIT 1`; add an index only if measured data shows a real regression.
- [Older or malformed records without correlation labels remain unreachable] -> Treat missing required labels as not found rather than weakening project/issue isolation; no compatibility backfill is part of this change.

## Migration Plan

1. Deploy the server query change and focused server specs. Web and CLI consumers pick up the behavior through their existing API calls.
2. No database migration, data rewrite, configuration change, or dependency update is required; existing correctly labelled sessions become readable immediately.
3. Roll back by deploying the previous server version. The change performs no writes and introduces no persisted format, so rollback requires no data action.

## Open Questions

No blocking questions remain. A deterministic policy for same-name historical sessions with identical creation times is intentionally deferred to the separate backlog item identified by issue 459.
