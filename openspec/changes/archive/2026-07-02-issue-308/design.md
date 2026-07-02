# Design — Split `github-pr` action to reduce single-module complexity (issue #308)

## Context

`packages/runner/src/actions/github-pr.ts` is the most complex hand-written file in
the repo (scc Complexity 384 / 1379 lines). It packs three unrelated concerns into
one module:

1. **Three action orchestrators** (`create-github-pr` / `merge-github-pr` /
   `mark-github-pr-ready`) — the only public surface, registered in `registry.ts:58-60`.
2. **The merge-pipeline state machine** (`waitChecksAndMergePr` + `waitForPrChecks`:
   PR-state short-circuit → check-rollup poll loop → `mergeStateStatus` re-confirm →
   `gh pr merge --squash` → post-merge `MERGED` confirm). ~250 lines of branching.
3. **The `gh` failure-text classifier matrix** (`classifyGhFailure` + five
   `looksLike*` matchers) plus the `gh` output parsers, check-rollup classification,
   issue-field bridge, git-ref probes, and output adapters.

Every change to PR-check polling or failure triage forces edits deep in this
monolith and risks tangling unrelated stages. The file sits on the hot path of the
standard PR workflow (real `git push` + `gh pr merge`), so a cross-stage regression
is costly.

**Current public import surface** (must be preserved verbatim):

- `registry.ts:9-13` imports the three `*Action` entry points from `./github-pr.js`.
- The three specs import the three actions **plus** the four test-injection stubs
  (`setGitHubPrGitRunnerForTest`, `setGitHubPrGhRunnerForTest`,
  `setGitHubPrChecksTimingForTest`, `setGitHubPrTransientRetryForTest`) from
  `../src/actions/github-pr.js`.
- No other `src/` consumer imports anything from this module.

**Established precedent:** `github-pr-status.ts` was already split out of the
monolith and owns its own `setGitHubPrStatusGhRunnerForTest` + `GhRunner` mutable
singleton — this change follows the same pattern at finer granularity.

**Constraints / invariants the refactor must hold** (these are what the specs
assert and the server's `mohist/github-pr` workflow profile depends on):

- Action IDs, the three `*Output` JSON field names **and their insertion order**
  (`JSON.stringify(output)` serializes in interface-property order), and the full
  `GitHubPrErrorCode` value set.
- The exact git/gh command strings and their sequence, and every step-recorder
  step name (`gh-precheck`, `git-push`, `gh-pr-list`, `gh-pr-create`, `gh-pr-checks`,
  `gh-pr-merge`, `gh-pr-view-confirm`, …).
- The four `setGitHubPr*ForTest` names and signatures (specs call them unchanged).

## Goals / Non-Goals

**Goals:**

- Decompose `github-pr.ts` into cohesive, singly-focused modules so that adjusting
  check-polling strategy or failure triage no longer requires editing a 1379-line
  file alongside unrelated stages.
- Keep the refactor **strictly behavior-preserving**: identical external surface,
  identical git/gh side-effect ordering, identical output JSON.
- Strengthen coverage: add direct unit tests for each `looksLike*` matcher's phrase
  set (today only `looksLikeRetrySafe` has dense ~15-phrase coverage; the other four
  are exercised only sparsely through the orchestrators).

**Non-Goals** (carried over from the proposal):

- No change to execution semantics, fault-tolerance/retry strategy, or git/gh
  side-effect ordering.
- No change to action IDs, output JSON contract, `GitHubPrErrorCode` values, or the
  server workflow-profile references.
- No performance work; no new action registered, none removed.

## Decisions

### D1 — Module layout: 11 focused modules + a barrel

Split into a strict dependency DAG. Leaf modules are pure (no internal deps, no
mutable state); infra/modules sit above them; orchestrators sit at the top.

| Module | Owns | Deps |
|---|---|---|
| `github-pr-types.ts` | shared wire types: `GitHubPrErrorCode`, `GitHubPrStep`, the three `*Output` interfaces | — |
| `github-pr-classify.ts` | `classifyGhFailure`, `classifyPushFailure`, the five `looksLike*` | types |
| `github-pr-parse.ts` | `parsePrList`, `parsePrListWithDraft`, `parsePrView`/`WithDraft`/`Internal`, `extractPrNumberFromUrl`, `combinedGhOutput`, `errorMessage` | — |
| `github-pr-checks.ts` | `PrCheckEntry`, `parsePrStatusCheckRollup`(±result), `classifyRollupBucket`, `classifyPrChecks`, `formatFailedCheck` | — |
| `github-pr-runtime.ts` | mutable `git`/`gh` runner singletons + getters + `setGitHubPrGitRunnerForTest` / `setGitHubPrGhRunnerForTest`; the shared `runGhPrecheck` | parse (for `combinedGhOutput`) |
| `github-pr-merge.ts` | the merge-pipeline state machine: `waitChecksAndMergePr`, `waitForPrChecks`, `mergeStateStatusFailure`, `runGhReadWithRetry`, `delayWithSignal`, `WaitChecksAndMergeOk/Failure`; **owns** `setGitHubPrChecksTimingForTest` + `setGitHubPrTransientRetryForTest` (these knobs are consumed only here) | runtime, classify, parse, checks, types |
| `github-pr-issue-fields.ts` | `resolveCreatePrText`, `resolveMergeSubject`, `validateIssueFieldSource`, `loadIssueFields`, `resolveIssueFieldValue`, `requiredIssueFields` | issue-fields, parse (`errorMessage`) |
| `create-github-pr.ts` | `createGitHubPrAction`, `openOrReusePr`, `resolveCurrentBranch`, `resolveBaseSha`, `BaseShaOk/Failure`, `buildCreateGitHubPrOutput` | runtime, classify, parse, issue-fields, types |
| `merge-github-pr.ts` | `mergeGitHubPrAction`, `resolvePrNumberForMerge`, `buildMergeGitHubPrOutput` | runtime, merge, classify, parse, issue-fields, types |
| `mark-github-pr-ready.ts` | `markGitHubPrReadyAction`, `markReadyOutput` | runtime, classify, parse, types |
| `github-pr.ts` | **barrel only** — re-exports the 3 actions + the 4 `setGitHubPr*ForTest` | the three orchestrators + runtime + merge |

**Rationale.** Grouping follows the proposal's concern split (G/L/M/I/D/J/K). Leaf
purity makes each piece trivially unit-testable and removes hidden global reads.
The git-ref probes (`resolveCurrentBranch`/`resolveBaseSha`) fold into
`create-github-pr.ts` because **only the create path uses `git`** — merge resolves
its PR via `gh pr list`, never touching git.

**Alternatives considered.**

- *Coarser split (e.g. one `github-pr-helpers.ts` for all pure fns).* Rejected: keeps
  the classifier matrix and the parsers in one file, so the original "two unrelated
  branch-heavy concerns in one file" pain persists.
- *Finer split (one file per `looksLike*` matcher).* Rejected: five 1-function files
  add navigation cost without extra cohesion; the matchers share the
  `text.toLowerCase()` idiom and belong together.

### D2 — Single source of truth for the mutable runners (new `github-pr-runtime.ts`)

Today `let git` / `let gh` live at the top of the monolith and are mutated by the two
runner setters. After the split, **multiple modules need the same runner instance**
(`gh` is used by create, merge, and mark paths). Duplicating the mutable binding per
module would force specs to call N setters with the same runner and would break the
"one `setGitHubPrGhRunnerForTest` sets the single gh runner" contract.

**Decision:** a single `github-pr-runtime.ts` owns the two runner singletons and
exposes them via getter functions (`getGitHubPrGit()` / `getGitHubPrGh()`). Helpers
keep their existing `git`/`gh` **parameter** signatures (they already take the runner
as a param, e.g. `openOrReusePr(gh, …)`, `waitChecksAndMergePr(gh, …)`); only the
orchestrators call the runtime getter **once** to obtain the runner and thread it in.

**Why getters, not exported `let` bindings:** an exported mutable binding captured
into a helper at module-load would go stale after `setGitHubPrGhRunnerForTest(null)`
resets it. A getter always reads the current value, matching today's behavior where
helpers read the live `gh`/`git` at call time.

**Alternatives considered.**

- *Per-module setters (relocate each setter to "the module that owns the knob").*
  Works cleanly for the timing/retry knobs (consumed in exactly one module each →
  they move to `github-pr-merge.ts`). It does **not** work for the runners, which are
  shared → the runtime module is the unavoidable single owner.
- *Dependency-inject runners into the action via `ActionContext`.* Out of scope: it
  changes the action contract and the `ActionContext` shape — a Non-Goal.

### D3 — `github-pr.ts` stays as a barrel; **spec import paths do not change**

`registry.ts` imports the 3 actions from `./github-pr.js`, and the 3 specs import the
actions + setters from `../src/actions/github-pr.js`. Rather than rewrite those import
sites to point at the new owner modules (the proposal lists this as an option),
`github-pr.ts` becomes a **pure re-export barrel**: `export { createGitHubPrAction }
from "./create-github-pr.js"` and `export { setGitHubPrGitRunnerForTest,
setGitHubPrGhRunnerForTest } from "./github-pr-runtime.js"`, etc.

**Rationale.** Because the runner setters mutate a singleton in `github-pr-runtime.ts`
that **every** module reads, a spec importing the setter via the barrel and a helper
reading the getter in runtime see the same state — the barrel is transparent. Keeping
the barrel means **zero spec churn**: `create-github-pr.spec.ts`, `merge-github-pr.spec.ts`,
and `mark-github-pr-ready.spec.ts` are not edited at all, which is the lowest-risk way
to satisfy "behavior preserved." The three orchestrator entry points also remain
importable from the same path the registry already uses.

**Alternative considered.** Update spec imports to point directly at owner modules
(closer to the proposal's wording). Rejected as the primary path: it is purely
cosmetic, adds review surface, and risks a stale-path typo — for no behavioral gain.
It remains a *possible* follow-up cleanup, not part of this change.

### D4 — Move interface definitions byte-for-byte; pin output JSON field order

`JSON.stringify(output)` serializes properties in **interface declaration order**. To
guarantee byte-identical output JSON, the three `*Output` interfaces move into
`github-pr-types.ts` **with property order preserved exactly**, and the output
adapters (`buildCreateGitHubPrOutput` / `buildMergeGitHubPrOutput` / `markReadyOutput`)
move verbatim into their respective orchestrator files. The existing specs already
assert on parsed JSON values; preserving order also keeps any byte-level assertions
intact.

### D5 — New direct unit tests for the classifier matrix

Add `packages/runner/tests/github-pr-classify.spec.ts` with direct, exhaustive
phrase-set coverage for each of `looksLikeBaseMoved`, `looksLikeProtectionConflict`,
`looksLikePrStateConflict`, `looksLikeAuthFailure`, `looksLikeRetrySafe` (the last
already has ~15 phrases covered indirectly — promote them to direct assertions), plus
`classifyGhFailure` precedence (auth → protection → base-moved → pr-state → retry-safe)
and the empty-text → `retry-safe` fallback. These are pure-function unit tests (no
git/gh/network) and run in the default `npm test -w packages/runner` suite.

**Optional strengthening (not required by AC):** mirror unit specs for
`github-pr-parse.ts` and `github-pr-checks.ts`, which today are exercised only through
the orchestrators. Trivial to add once the modules exist; included if time permits.

## Risks / Trade-offs

- **[Stale mutable-runner binding after split] →** single `github-pr-runtime.ts`
  owner + getter accessors (D2). Helpers keep runner *params*, so no hidden reads.
- **[Output JSON field-order drift] →** move interfaces verbatim with order preserved;
  output adapters move verbatim into orchestrators (D4). Existing parsed-JSON + any
  byte assertions guard this.
- **[Import cycles] →** strict DAG (D1): types/classify/parse/checks are pure leaves;
  runtime depends only on parse; merge depends on leaves+runtime; orchestrators depend
  on all; barrel depends on orchestrators+runtime+merge. `tsc --noEmit`
  (`npm run typecheck -w packages/runner`) surfaces any cycle.
- **[Spec breakage from setter relocation] →** barrel re-export keeps all spec imports
  at `github-pr.js` unchanged (D3); zero spec edits.
- **[Behavioral drift in phrase matching] →** matchers move verbatim; new direct unit
  tests pin every phrase (D5).
- **[Over-splitting / 11 files] →** accepted trade-off: the explicit goal is lowering
  per-file complexity, and each leaf is cohesive. Coalescing parse+checks was
  considered and rejected (different concerns: gh output *shape* vs check-rollup
  *semantics*).
- **[Hot-path regression unnoticed] →** `merge-github-pr.spec.ts` contains exact
  git/gh command-string + step-name + sequence assertions; these are the regression
  guard and must stay green at every phase.

## Migration Plan

Behavior-preserving, phased by dependency layer (leaves first). After **each** phase,
run `npm run typecheck -w packages/runner` and `npm test -w packages/runner`; both
must be green before starting the next. Commit per phase.

1. **Pure leaves.** Create `github-pr-types.ts`, `github-pr-classify.ts`,
   `github-pr-parse.ts`, `github-pr-checks.ts`; move functions/types verbatim.
   `github-pr.ts` re-exports them so all current callers keep compiling.
2. **Classifier tests.** Add `github-pr-classify.spec.ts` (D5). Suite green.
3. **Runtime extraction.** Create `github-pr-runtime.ts` with the runner singletons +
   getters + `runGhPrecheck`; move `setGitHubPrGitRunnerForTest` /
   `setGitHubPrGhRunnerForTest`. Orchestrators obtain runners via getters.
4. **Merge pipeline.** Create `github-pr-merge.ts`; move the state machine +
   `setGitHubPrChecksTimingForTest` / `setGitHubPrTransientRetryForTest`.
5. **Issue-field bridge + orchestrator split.** Create `github-pr-issue-fields.ts`,
   then `create-github-pr.ts` / `merge-github-pr.ts` / `mark-github-pr-ready.ts`;
   `github-pr.ts` collapses to the barrel (D3). Fold git-ref probes into create,
   output adapters into their orchestrators.
6. **Final gate.** Full `npm run typecheck -w packages/runner` +
   `npm test -w packages/runner`. Confirm no spec file's import line changed.

**Rollback.** Each phase is an independent commit on the feature branch; revert the
commit to roll back a phase. Because the refactor changes no persisted data, no action
contract, and no external surface, rollback is purely source-level — no data migration
and no server/runner coordination is involved. The change lands behind the existing
`mohist/github-pr` workflow profile unchanged.

## Open Questions

- **Spec import paths.** Keep the barrel (D3, recommended: zero churn) or follow the
  proposal's lean and repoint spec imports at owner modules? Default: barrel.
- **`runGhPrecheck` placement.** Housed in `github-pr-runtime.ts` (framed as the
  git/gh *execution context*). It is a slight conceptual stretch (a command, not a
  singleton). If review prefers isolation, promote it to a dedicated
  `github-pr-gh.ts`. No behavioral difference either way.
- **Optional parse/checks unit specs (D5).** Include in this change or defer to a
  follow-up? Default: include classify (required by AC); add parse/checks only if
  inexpensive.
