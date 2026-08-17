# Review

This is a re-review verifying the disposition of the prior review's only
must-fix finding (MF-1). The issue details were read with `mo issue view 555
--project proj_f6c141d63b6243bfbb481737b2243b87`; its rendered body is empty,
so the acceptance contract was checked against `proposal.md`, `design.md`, the
five capability specs, and `tasks.json`.

## Disposition of prior findings

### MF-1: Follow-up replay bypasses canonical Project membership — FIXED

**Prior finding:** in `DirectApiRoutes.FollowupAsync`, the durable
`(sessionId, Idempotency-Key)` mapping was read before any canonical
Project-membership check, so a PAT granted for Project B could replay or admit
a follow-up mapping created under Project A (violating `external-agent-caller-auth`
"Project authorization precedes resource lookup", `external-write-idempotency`
"durable keyed mappings are scoped per command", and T-005 AC1).

**Current state (commit `22c3883f8`):** the route now calls
`AgentSessionQuerier.ResolveGenericFollowupTargetAsync(projectId, sessionId, ct)`
immediately before `idempotency.FindAsync` and returns
`404 session_not_found` when the result is null:

```csharp
if (await sessions.ResolveGenericFollowupTargetAsync(projectId, sessionId, ct) is null)
{
    return DirectApiResults.ResourceNotFound(DirectApiErrorCodes.SessionNotFound);
}
```

I verified the resolver (`AgentSessionQuerier.cs:438`) returns null only when
the Session record is missing **or** its `LabelProjectId` does not match the
route's `projectId` — an existence-and-membership-only check that ignores the
mutable Runner/source target, exactly as MF-1's fix instruction required. It
runs before the mapping read, so both replay of a completed mapping and
admission of a pending mapping through a foreign Project are blocked; the
intentional replay-after-mutable-target-invalidation path is preserved.

The regression test `DirectApiFollowupSpecs.ExistingFollowupMappingCannotBeReplayedThroughAnotherProject`
mints a follow-up mapping in Project A and replays the same session/key/body
through Project B with a PAT granted for both: it asserts `404 session_not_found`
and that the original mapping remains `Completed`. I confirmed the test uses
the same caller and the same key (scope key `sessionId|key` carries no caller
or project), so it exercises the exact attack surface, and that
`CompletedFollowupReplaySurvivesSessionTargetInvalidation` still passes.

I also adversarially re-checked the sibling routes for the same class of bug:
launch scope keys embed `projectId` (project-scoped by construction); the stop
route resolves `ResolveCanonicalTurnStopTargetAsync(projectId, turnId)` (which
filters on `LabelProjectId`) before any mapping read; and the events/read paths
resolve the canonical `LabelProjectId` before serving. No other cross-project
replay path exists. MF-1 is properly fixed.

## Dimension checks (re-review)

- **Disposition of prior must-fix:** MF-1 fixed correctly; fix matches the
  required ordering and preserves replay-after-target-invalidation. No
  won't-fix justifications needed.
- **Regression check:** the fix touches only `DirectApiRoutes.FollowupAsync`,
  the follow-up specs, and `progress.txt`. Focused verification:
  `DirectApiFollowupSpecs` 9/9 pass (includes the new regression and the
  replay-survival test); `DirectApiStopSpecs` + `DirectApiAuthPipelineSpecs` +
  `DirectApiIdempotencySpecs` 23/23 pass; `PublicExecutionProjectorHostingSpecs`
  passes in isolation. A full Server SpecTests run passed 3,984/3,985; the
  single failure (`NudgedHostedProjector_CatchesUpFromTheCheckpoint`, a
  nudge-timing wait) is a known transient flake — it passes in isolation and is
  unrelated to this fix, which does not touch the projector. No regression
  attributable to the fix.
- **Coverage:** the previously missing case — an existing `(sessionId, key)`
  mapping submitted through a different selected Project — is now covered by
  a dedicated regression test.
- **Correctness / consistency with the surrounding codebase:** checked, no
  additional issue. The added check reuses the existing querier and error
  code, matches the design's required ordering, and is consistent with the
  stop route's canonical-check-first shape.
- **Tests:** the green focused suites and the near-full green full suite verify
  the change; the only red test is the unrelated, reproducible-as-green
  projector timing flake.

## Observations

- `20260909000000_AddPublicApiCursorSecret.cs` rebuilds the existing
  `StoredSecrets` table while copying rows to extend its constraints.
  Deployment testing against populated secret stores remains advisable, but
  this is not a must-fix for the issue criteria.
- Several retryable internal queue or capacity conditions intentionally map to
  the single safe public reason `queue_full`; this is consistent with the
  specified public vocabulary.
- The new cross-Project regression covers the completed-mapping replay branch;
  the pending-mapping admission branch is blocked by the same placement of the
  check (before the mapping read) but has no dedicated test. Structurally
  covered, so recorded as an observation only.

## Verdict

**PASS** — the prior must-fix finding (MF-1) is fixed correctly with regression
coverage, no regression was introduced, and no new must-fix problem was found.

<promise>PASS</promise>
