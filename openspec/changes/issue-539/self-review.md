# Self-Review (round 2) — Issue 539 (WorkflowRun status ETag cache)

Reviewer role: reviewer, not fixer. Round 1 raised one blocker + two minors;
this round verifies the fixes and re-checks the whole plan.

## Verdict: PASS

The round-1 blocker (spec/design contradiction on State reads during
definition resolution) is resolved and now consistent across spec, design, and
tasks. The two remaining observations are non-blocking polish/uncertainty items
that do not prevent building.

---

## Artifacts reviewed

- `openspec/changes/issue-539/proposal.md`
- `openspec/changes/issue-539/specs/workflow-run-status-cache/spec.md`
- `openspec/changes/issue-539/design.md`
- `openspec/changes/issue-539/tasks.json`
- Against issue #539 body and the live codebase.

## Round-1 findings — resolution verified

1. **BLOCKER (spec↔design contradiction on State reads on a hit).** Fixed and
   verified consistent:
   - Spec Requirement 1 now guarantees only "MUST NOT execute the full typed
     deserialization of `State` (`JSON.Deserialize<WorkflowRun>`)" and adds an
     explicit scope paragraph acknowledging definition resolution's residual
     `State` reads (profile-id extraction) as out-of-scope-accepted.
   - Design D1 carries a matching scope note with evidence
     (`WorkflowDefinitionResolver.cs:220-221` full-row load; `:234-241`
     `Select(x => x.State)` + `JsonDocument.Parse`); Open Questions lists the
     follow-up (bypass via cached `WorkflowProfileId`).
   - tasks.json description + hit/miss/artifact acceptance criteria all say
     "full typed deserialization" consistently. No artifact still claims "MUST
     NOT read the State column".
2. **MINOR (ETag is a save-count, not a content hash).** Fixed in design D3
   ("ETag is a save-count, not a content hash … bumps `ETag` unconditionally on
   every update save, even when byte-identical") and D1 rationale. Correctness
   framing updated ("ETag differs → deserialize once" still holds; hit-rate
   ceiling depends on non-redundant saves).
3. **MINOR (no-mutation guard absent from task criteria).** Fixed: tasks.json
   now has a criterion asserting the cached `WorkflowRun` is not mutated by
   `BuildStatusView`/`AttachArtifactSummariesAsync`/per-call paths.

## Re-check of the whole plan

- Capability boundary intact: one capability (`workflow-run-status-cache`),
  one spec file, one cohesive task; no over-granular split.
- Spec format compliant: requirements `###`, scenarios exactly `####`, each
  requirement has ≥1 scenario, no `ADDED/MODIFIED/REMOVED` headers, normative
  MUST/SHALL language throughout.
- Issue-required regression coverage present: ETag-unchanged → zero full
  deserializes (Req 1); ETag-changed → exactly one (Req 2); both reflected in
  task acceptance criteria.
- Proposal wording ("完整反序列化" / "仅消除重复反序列化开销") already matches the
  spec's scoped "full typed deserialization" guarantee — no proposal↔spec gap.
- Decision rationale (D1–D5) is internally consistent; D2 singleton lifetime is
  correct for the cross-request poll; D4 bounding prevents the cache from
  regressing memory (the epic's purpose); no schema migration, additive code,
  safe rollback.

## Non-blocking observations (do not block building)

### N1 — Context § repeats the round-1 "actual State write" phrasing

`design.md:15` still says the ETag is "incremented on every actual State write"
— the same imprecision round-1 finding-2 flagged. It is harmless because (a)
the authoritative decision D3 states the correct save-count semantics
explicitly and unambiguously, and (b) the citation
(`WorkflowRunStore.cs:156,166`) leads an implementer straight to the
unconditional increment. A future tidy-up can align the background sentence
with D3; it will not mislead a builder today.

### N2 — Net LOH benefit is uncertain until profiling

On a cache hit the typed deserialize is skipped, but definition resolution still
materializes the `State` string twice per call (`ResolveRunContextAsync`
full-row load + `LoadBoundProfileIdAsync` `Select(x => x.State)`); a ~325 KB
string is LOH-eligible, so the residual LOH may be non-trivial. The design
acknowledges this honestly (D1 scope note + Open Questions #3, which scopes
eliminating those reads as a follow-up gated on post-implementation profiling).
This aligns with the issue's explicit scope ("仅消除重复反序列化开销") and its
`effort=small` label, so deferring is defensible — but the team should treat
the follow-up profiling as a real commitment, not a nice-to-have, since the
headline memory win partially depends on it.

<promise>PASS</promise>
