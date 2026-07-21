## Why

Completed or cancelled issues lose their active workflow-run reference, so their persisted workflow session transcripts become unreachable from the issue page even though the session records still exist. Historical conversations must remain available for review and audit after workflow execution ends.

## What Changes

- Allow an issue-scoped session read to fall back to the issue's historical workflow sessions when the issue has no active workflow run.
- Resolve the historical workflow session with the requested name so its metadata and transcript remain viewable from `/issues/<number>/workflow/sessions/<name>`.
- Preserve the current active-workflow-run lookup behavior while an issue is in progress.
- Continue returning "not found" when the issue has no matching session record.
- Preserve runtime-session filtering when reading transcript content.

## Capabilities

- `issue-workflow-session-history`: Resolves an issue's named workflow session for viewing, including persisted historical sessions after the issue no longer has an active workflow run.

## Impact

- Server Session query behavior for issue-scoped workflow session metadata and transcripts.
- Issue-scoped session API reads consumed by the AgentSession page and `mo issue session transcript`.
- Server behavior specifications covering terminal issues, active-run precedence, missing sessions, and runtime-session transcript filtering.
- No API shape, database schema, external dependency, or Workflow execution behavior changes are expected.
