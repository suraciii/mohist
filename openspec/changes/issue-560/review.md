# Review: issue-560

## Verdict

FAIL. Both must-fix findings from the previous review round remain
unaddressed. No implementation commit landed after the previous review
(`f1d932bf3` is the review commit itself; the working tree is clean and HEAD
is unchanged), so this re-review verifies the dispositions against identical
code: MF-9 and MF-10 are still reproducible as written.

## Must-Fix Findings

### MF-9: An explicit variant-only definition is cleared by an unrelated Web edit

Violates acceptance criterion 5 (saving an Agent must preserve the definition
facts that apply to later Jobs, and must not silently alter execution facts),
design D6's per-field precedence (definition field, then Project default),
and the supported scenario where a definition sets a Variant while the
Project default supplies the Model.

A definition with `agentConfig: { "variant": "high" }` is valid. The resolver
is explicitly designed to let such a Variant survive and be filled with the
Project-default Model (`ExecutionConfigResolver.FromAgentConfig` preserves a
Variant without a Model "so the precedence rule can fill the missing Model
from the Project default"), and `AgentReadinessService.StructuralGaps` does
not report `variant-without-model` once a default Model resolves it. The Web
editor still erases that explicit Variant on an unrelated save:

- `packages/web/src/entities/agent/api/client.ts:168-185`
  (`readAgentDefinitionModelAndVariant`) returns `variant: null` whenever the
  raw definition has no Model (`:178 if (!model) return { model: null,
  variant: null, runtime }`), even if the raw definition carries a Variant.
- `packages/web/src/widgets/agent-profile-editor/ui/AgentProfileEditor.tsx:60`
  initializes the form from that projection (`:66-67`), so `variant` state is
  null for a Variant-only definition.
- `packages/web/src/widgets/agent-profile-editor/ui/AgentProfileEditor.tsx:95`
  serializes the form on every save; `writeAgentModelAndVariant`
  (`client.ts:201-213`, `:208 if (model === null) return ... : null`) returns
  null when the form Model is null, so an unrelated description/name/
  Instructions edit sends `agentConfig: null`.

The Agent therefore changes from `(default Model, explicit Variant)` to
`(default Model, no Variant)` merely by saving an unrelated field — a later
Job resolves differently than the user's definition states. This is the same
raw-definition preservation boundary that MF-7 addressed for an empty config;
the existing save regression (`AgentProfileEditor.test.tsx` "preserves an
effective default as unresolved when saving an unrelated edit", `:255-269`)
only covers `agentConfig: null` and misses the valid Variant-only case.
Preserve the raw Variant through the editor unless the user explicitly
changes execution configuration, and add a save regression for Variant-only
definitions.

**Re-review status: not addressed.** Code and test coverage unchanged since
the previous review; the finding reproduces exactly as reported.

### MF-10: Scope-drift rejection is silent in the Web composer

Violates the `web-agent-task-composer` requirement that every rejected launch
show its rejection reason and repair path while preserving the composed task
("Failure keeps the task" scenario), and the issue's pre-launch scope
criterion (a confirmed scope that becomes invalid is left unexplained).

The preflight gate deliberately returns `409 launch_scope_changed` when the
confirmed scope no longer matches the scope at launch:

- `packages/server/src/Mohist.Server/Api/AgentTaskRoutes.cs:277-294`
  (comparison at `:288`, response at `:292`)
- `packages/server/src/Mohist.Server/Api/AgentSessionLaunchRoutes.cs:346-351`
  (response at `:350`)

The Web client has no feedback mapping for this code. The feedback union and
mapper in `packages/web/src/entities/agent/model/launch-feedback.ts` handle
idempotency conflict, pending convergence, configuration failure, and runtime
availability, but not `launch_scope_changed`; the mapper falls through to
`return null` (`:193`). The composer stores the failed mutation at
`packages/web/src/pages/agent-session-composer/ui/AgentSessionComposerPage.tsx:562`
(`launchFeedback = getAgentLaunchErrorFeedback(launchError, ...)`) and renders
the rejection block only when `launchFeedback` is non-null (`:618`), so an
unmapped error produces no visible reason or repair path.

This rejection is reachable from the composer: it submits the preflight
fingerprint on the real launch (`handleConfirmPreflight`, `:457-474`), so the
server's `launch_scope_changed` check runs for every confirmed Web launch. A
Project default or workspace repository can change while the confirmation
dialog is open (another actor, or the preflight read racing an edit), making
this an exercised path, not an unreachable response. Map scope drift to
actionable feedback (re-run/review preflight while keeping the task and
context) and add task-first and existing-Agent Web coverage for the
`launch_scope_changed` code.

**Re-review status: not addressed.** No `launch_scope_changed` / scope-drift
mapping exists anywhere under `packages/web/src`; the rejection remains
silent in the composer.

## Prior Finding Dispositions

- **MF-6 (replay before mutable preflight scope checking): fixed and still
  holds.** `AgentTaskRoutes.cs:231-274` calls `ResumeIdempotentAsync` before
  the `launch_scope_changed` comparison at `:277-294`; the accepted-replay
  regression `TaskLaunch_ReplaysAcceptedOutcomeBeforeCheckingDriftedPreflightScope`
  verifies original Agent, Session, and Job identities after a Project
  default changes. No regression observed.
- **MF-7 (unrelated profile edit materializes the Project default): fixed for
  the reported empty-config case, still holds.** The editor uses the
  raw-definition reader, and `AgentProfileEditor.test.tsx:255-269` verifies a
  default-resolved Agent with no raw config still saves `agentConfig: null`.
  MF-9 is the separate raw-definition preservation case that remains
  uncovered (see above).
- **MF-8 (`mo agent start --epic` serialized a string): fixed and still
  holds.** The start and definition-first CLI options are `int?`,
  `BuildLaunchContext` emits numeric `epicNumber`, and the CLI plus
  task-route coverage exercises the numeric contract.
- **MF-9 and MF-10: not addressed** — see Must-Fix Findings above.

## Review Dimensions

- **Acceptance criteria:** **FAIL.** MF-9 still silently changes a supported
  execution definition during an unrelated edit (criterion 5); MF-10 still
  leaves a valid preflight rejection unexplained in Web (pre-launch scope
  criterion and the composer's failure-keeps-task contract).
- **Correctness:** **FAIL.** The server-side replay, Epic, and raw-config
  fixes verified for the prior round remain correct; the two findings above
  remain observable behavior failures in the shipped surfaces.
- **Consistency with surrounding code and plan:** **FAIL.** MF-9 contradicts
  design D6's per-field precedence and no-edit-time-materialization rule;
  MF-10 contradicts the `web-agent-task-composer` spec's "Failure keeps the
  task" scenario.
- **Tests:** **FAIL for completeness.** The suites cover the prior round's
  fixes (replay, Epic, empty-config preservation) and remain green, but there
  is still no Variant-only profile-save test and no Web test exercising
  `launch_scope_changed`. This is missing behavioral coverage, not a failing
  build.

## Acceptance-Criterion Checks

- **Task-oriented configuration (purpose, description, Instructions,
  permissions, collaborators, concurrency intent):** checked; no additional
  must-fix gap found in the current surfaces beyond MF-9's effect on an
  execution field during an unrelated edit.
- **Readiness states (not configured / unknown / executable / insufficient):**
  checked, no separate issue found. Server-authoritative Ready / Unknown /
  Needs setup projections are present in list and detail.
- **Model recommendation and full options:** checked, no separate issue
  found. Web uses the labeled Project default and catalog-backed adjustment;
  CLI points unresolved configuration at `mo agent model list`.
- **Pre-launch context and permission scope:** checked with MF-10 above. The
  normal preflight projection is visible, but one of its rejection outcomes
  (`launch_scope_changed`) is not actionable in Web.
- **Save timing and in-flight facts:** checked with MF-9 above. The save
  timing copy and launch snapshots are present, but the editor can still erase
  a valid raw Variant during an unrelated save.
- **CLI/Web identity and execution scope consistency:** checked, no separate
  issue found. The numeric Epic context path and effective execution
  projections are consistent across the tested surfaces.

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

## Verification

- No implementation commits after the previous review: `HEAD` is the review
  commit (`f1d932bf3`), working tree clean, so dispositions were verified
  against unchanged code.
- `npm run verify`: passed.
- Server specification assembly: 3,954 passed.
- Server unit tests: 2,701 passed.
- CLI tests: 1,860 passed.
- Web tests: 4,741 passed.
- Runner tests: 1,639 passed.
- Slack tests: 70 passed.
- Workflow definition tests: 178 passed.
- Server architecture tests, Web FSD/test-boundary checks, docs, format, and
  file-size checks passed.
- Focused task-first route specs: 14/14 passed; default/readiness/storage
  specs: 19/19 passed; targeted Web tests: 109 passed.

<promise>FAIL</promise>
