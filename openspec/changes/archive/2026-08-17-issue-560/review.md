# Review: issue-560

## Verdict

PASS. Both must-fix findings from the previous round (MF-9, MF-10) are now
addressed, properly implemented, and covered by regression tests. The fix
commit `9f295d1bc` ("preserve Variant-only definitions and surface scope-drift
rejection") was then merged with the verified master implementation
(`f8a19552a`, "adopt verified master implementation for issue-560 check
repair"), which carries the same two behaviors in master's form. No regression
was found in the merged state: the full Web suite (4,756), Web typecheck, and
the focused suites all pass against the current working tree, which is clean.

## Dispositions of the previous round's must-fix findings

### MF-9 (explicit Variant-only definition cleared by an unrelated Web edit) — fixed

The Variant-only raw definition is now preserved end to end:

- `packages/web/src/entities/agent/api/client.ts`
  (`readAgentDefinitionModelAndVariant`) returns the raw `variant` even when
  no `model` is set; it no longer short-circuits to `variant: null` on a
  missing Model.
- `writeAgentModelAndVariant` keeps the null-Model branch from collapsing to
  `null`: a Variant-only (or Variant + non-default Runtime / reasoningEffort)
  result is written back as the preserved object, and only an empty
  (null model, null variant, default runtime) result returns `null` — the
  "user explicitly cleared it" case.
- `AgentProfileEditor.tsx` initializes `variant` from the raw-definition
  reader (`readAgentDefinitionModelAndVariant`) and serializes through
  `writeAgentModelAndVariant`, so an unrelated name/description/Instructions
  edit no longer erases a raw Variant that the Project default fills at
  launch. The definition no longer silently changes from `(default Model,
  explicit Variant)` to `(default Model, no Variant)`.
- Coverage: `client.test.ts` "preserves the raw variant when no model is set"
  and "preserves a raw variant-only definition"; `AgentProfileEditor.test.tsx`
  "preserves a variant-only definition when saving an unrelated edit" (and the
  existing "preserves an effective default as unresolved when saving an
  unrelated edit" regression still holds).

### MF-10 (scope-drift rejection silent in the Web composer) — fixed

The `launch_scope_changed` preflight rejection is now surfaced with an
actionable repair path that keeps the composed task:

- `packages/web/src/entities/agent/model/launch-feedback.ts` maps
  `code === 'launch_scope_changed'` to `kind: 'launch-scope-changed'` feedback
  ("Launch scope changed … Review the updated scope, then confirm the launch
  again"), no longer falling through to `return null`.
- `AgentSessionComposerPage.tsx` renders the mapped feedback
  (`error-launch-scope-changed`, `data-feedback-kind="launch-scope-changed"`)
  and, when `lastPreflight` is available, a "Review updated scope" button that
  re-runs the preflight with the same task input, attachments, and
  idempotency key (`handleReviewChangedScope`) — the task and context refs
  stay in the composer throughout.
- Server contract unchanged: both `AgentTaskRoutes.cs:292` and
  `AgentSessionLaunchRoutes.cs:350` still return `409` with
  `launch_scope_changed`, and the Web `ApiError` carries `code`/`status`
  through to the mapper.
- Coverage: `launch-feedback.test.ts` maps the code; composer tests cover the
  task-first path and the existing-Agent path, asserting the rejection block,
  preserved prompt/context, and a re-run that keeps the same key and does not
  start work.

## Prior finding dispositions (unchanged by this round)

- MF-1..MF-5: previously verified fixed; the merged master implementation
  retains the server-authoritative `effectiveExecutionConfig` projections,
  preflight scope projection, idempotency-key durability, first-writer-wins
  grain adoption, and collaborator/concurrency fields.
- MF-6 (replay before mutable preflight scope check): still holds in the
  merged state — `ResumeIdempotentAsync` runs before the `launch_scope_changed`
  comparison in `AgentTaskRoutes.cs`.
- MF-7 (unrelated profile edit must not materialize the Project default):
  still holds; the editor's raw-definition read and the empty-config save
  regression are present.
- MF-8 (numeric `mo agent start --epic`): unchanged server/CLI behavior;
  not touched by this round's Web-only fix or the merge rename
  (`ReadinessRejected` → `ExecutabilityRejected`).

## Review Dimensions

- **Acceptance criteria:** **checked, no issue.** Criterion 5 (saving an
  Agent preserves definition facts that apply to later Jobs) and the pre-launch
  scope criterion (a confirmed scope that becomes invalid is explained, with
  the task preserved) are both satisfied in the current tree.
- **Correctness:** **checked, no issue.** The Variant-only definition is a
  supported case per `agent-creation-defaults` (a Variant without a Model is
  resolved from the Project default and produces no `variant-without-model`
  gap); it survives unrelated saves. Every rejected launch path now maps to
  visible, actionable feedback per `web-agent-task-composer`'s "Failure keeps
  the task" scenario.
- **Consistency with surrounding code and plan:** **checked, no issue.** The
  fixes match design D6's per-field precedence and the no-edit-time-
  materialization rule; the scope-drift feedback follows the same
  feedback-kind pattern as the other launch outcomes.
- **Tests:** **checked, no issue.** Regression coverage exists for both
  findings (helper units, editor save, composer task-first and existing-Agent
  scope drift) and the full Web suite is green.

## Observations

- `openspec/changes/issue-560/specs/agent-task-launch/spec.md` still says the
  task body accepts exactly seven top-level fields, while the product route
  additionally accepts `allowedSubagentAgentIds` and `maxConcurrentRuns` for
  the issue's collaborator/concurrency requirement. The implementation and
  issue-level behavior are aligned; the workflow spec artifact should be
  synchronized in a later artifact-only update.
- The preflight fingerprint check is not an atomic compare-and-create with the
  Project default/workspace reads. A change after the check and before
  definition derivation can make the actual launch use a newly resolved scope;
  the launch plan still records the actual values, so this is a concurrency
  hardening concern below the must-fix bar for the stated acceptance criteria.
- CLI `mo agent start` has an additional `--yes` confirmation requirement for
  current-server preflight flows, while the abbreviated CLI documentation
  examples do not call it out. The command help and non-interactive behavior
  provide the requirement; this is documentation polish, not a merge blocker.
- The `check`-stage merge adopted the verified master implementation into the
  run branch; its terminology rename (Readiness → Executability for the
  preflight rejection) is consistent with the branch's own executability
  work and does not change the reviewed behavior.

## Verification

- HEAD `f8a19552a` (merge of the verified master implementation), working tree
  clean; the working tree was re-settled by the concurrent check-repair merge
  during this review and was re-verified after it stabilized.
- Web suite: 4,756 passed (378 files), including the MF-9/MF-10 focused
  suites (87 targeted tests).
- Web typecheck (`tsc -b`): passed.
- Server routes still return `launch_scope_changed` from both the task-first
  and session-launch preflight gates; MF-6's replay-before-scope ordering is
  intact.

<promise>PASS</promise>
