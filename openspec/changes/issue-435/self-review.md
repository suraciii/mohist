# Self-Review — issue-435

Reviewer reviewed `proposal.md`, `specs/runner-model-discovery/spec.md`, `design.md`, and `tasks.json` against issue #435 and the existing code in `packages/runner/`. All cited file:line references in the artifacts were checked against the codebase and verified accurate (host.ts:318 single `connectRunner` call; host.ts:383 `onDispatchReconnected` does not re-enter `connectRunner`; host.ts:397–405 `sendImmediateHeartbeat`; host.ts:794–802 `registrationState`; host.ts:807 the only production call site of `discoverOpencodeModels`; opencode-models.ts:9–42 the TTL guard; types.ts:267 `RunnerOptions`; opencode-models.spec.ts:137–153 the TTL caching assertion; runner-host-lifecycle.spec.ts:90–92 and :115 the mock + `vi.useFakeTimers()` pattern). All 7 of the issue's Acceptance Criteria are traceable into the spec.

## Blocking problem

### B-001. Spec "Time is injectable" requirement and design D7 contradict each other on whether a clock object is introduced

`specs/runner-model-discovery/spec.md` "Time is injectable" requirement paragraph states:

> Every time-driven decision in the rediscovery path … SHALL read from an injected clock, not from `Date.now()` or any other wall-clock source. **The runner SHALL accept this clock via its construction/option surface** so that tests can drive it.

`design.md` Decision D7 explicitly chooses the opposite:

> **No new clock abstraction**: the only time source in the new path is `setInterval` itself, which `vi.useFakeTimers()` … intercepts natively. … introducing a clock object just for this feature would diverge from the established pattern and add a constructor parameter for no behavioral benefit.

The spec scenarios themselves ("Tests drive rediscovery via fake timers", "No Date.now in the rediscovery path") are satisfiable either way — D7 satisfies them via `vi.useFakeTimers()` intercepting `setInterval` plus D4 removing the only `Date.now()`. But the **requirement paragraph** mandates a clock on the construction surface, which D7 refuses to add.

An implementing agent will have to pick one artifact over the other. This is a real plan-level inconsistency that must be reconciled before building — either soften the spec requirement paragraph (the design's rationale is consistent with how the rest of `host.ts:325–328` already works under fake timers) or actually thread a clock object through `RunnerHost` per the spec's literal text.

## Non-blocking concerns (worth noting, do not block the build)

### N-001. Empty-result corner case technically violates a convergence scenario

Design D6 step 2 (skip state update when `discovered.models.length === 0`) preserves the existing "don't cache empty result" product semantic and is explicitly accepted by the issue body. However, in the corner case where the user removes **all** opencode providers, the runner will keep reporting the last non-empty set forever (never converging to empty). This corner case technically violates the spec scenario "Removed provider disappears within one interval" (`specs/runner-model-discovery/spec.md:172–175`), which is worded without qualifying "all vs. some". Partial removal works correctly because the remaining set is non-empty and `opencodeModelSetsEqual` detects the change. Acceptable per the issue, but the spec scenario wording could note the empty-set carve-out.

### N-002. T-001 alone is a refactor that does not fix the bug and slightly regresses `connectRunner` retry cost

`tasks.json` T-001 removes the TTL guard without adding the periodic timer (which lands in T-002). Shipped alone, T-001 (a) does not fix issue #435 (no post-startup trigger exists yet) and (b) makes `connectRunner`'s connection-retry loop (`host.ts:804–822`) spawn `opencode models --verbose` once per retry instead of hitting the 30-min cache. Design D4 acknowledges this cost. Not a defect — the task split is reasonable and the two tasks will typically ship together — but worth knowing that T-001 in isolation is a net cost without the compensating benefit.

### N-003. T-001 description omits migrating `beforeEach`/`afterEach` calls to the removed `clearOpencodeModelsCacheForTesting`

`packages/runner/tests/opencode-models.spec.ts:88–95` calls `clearOpencodeModelsCacheForTesting()` in both `beforeEach` and `afterEach`. T-001's description says to drop the export but does not explicitly call out that these two call sites must also be removed. The implementing agent will hit a compile error and fix it, but the task description could be more explicit.

### N-004. T-002 acceptance criteria name only 2 of 6+ existing runner-host specs that must stay green

Six runner-host spec files mock `discoverOpencodeModels` today: `runner-host-lifecycle.spec.ts`, `runner-host-reporting.spec.ts`, `runner-host-cleanup-config.spec.ts`, `runner-host-liveness.spec.ts`, `runner-host-convergence.spec.ts`, `runner-host-task-log.spec.ts`. T-002's criterion "existing `runner-host-lifecycle.spec.ts` / `runner-host-reporting.spec.ts` still green" is illustrative, not exhaustive — the full `npm test -w packages/runner` run will catch any regression — but a more thorough criterion would list (or say "all existing") the affected specs.

### N-005. Proposal's "BREAKING (internal)" label is internally contradictory

`proposal.md:9` says "**BREAKING (internal):** … No external behavior breaks; this is a code-structure change documented for maintainers." Calling something BREAKING and then immediately saying nothing breaks is confusing. The substance is correct (no external behavior change; purely an internal trigger-model cleanup), but the label overstates it. Recommend dropping "BREAKING" or rephrasing as "Internal cleanup" / "Maintainer-visible change".

### N-006. T-002 has `passes: true` while the artifact instruction says "Always start as `false`"

The "tasks" artifact's `field_guidance` says `passes`: "Always start as `false`." Both tasks should start `false`. Archive examples (e.g. `openspec/changes/archive/2026-07-17-issue-427/tasks.json`) use `passes: true` on leaf tasks, so the convention is genuinely ambiguous; the literal instruction says `false`. Trivial to reconcile either way; flagging for awareness.

## Coverage check

- All 9 spec requirements have ≥1 scenario, all scenarios use exactly 4 hashtags, all use WHEN/THEN format. ✓
- All 7 of the issue's Acceptance Criteria trace to spec requirements and task acceptance criteria. ✓
- Dependency graph (T-002 → T-001) is acyclic and references only lower-priority tasks. ✓
- `tasks.json` is valid JSON. ✓
- Every task has test-related verification in its acceptance criteria. ✓
- All code references in `proposal.md` / `design.md` / `tasks.json` were spot-checked against the codebase and are accurate. ✓

## Verdict

The plan is close to ready and the rest of the artifacts are tight and internally consistent. The single blocking item (B-001) is a real spec-vs-design disagreement that will force the implementing agent to choose between two contradictory authoritative documents. It must be reconciled before building.

<promise>FAIL</promise>
