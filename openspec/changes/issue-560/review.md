# Review: issue-560

## Verdict

FAIL. Two must-fix problems remain in the current implementation.

## Must-Fix Findings

### MF-9: An explicit variant-only definition is cleared by an unrelated Web edit

Violates acceptance criterion 5 (saving an Agent must preserve the definition
facts that apply to later Jobs), the `agent-creation-defaults` precedence
contract, and the scenario where a definition sets a Variant while the Project
default supplies the Model.

A definition with `agentConfig: { "variant": "high" }` is valid under the
current schema. With a Project default Model, the resolver correctly combines
the definition Variant with that default Model, so Readiness does not report
`variant-without-model`. The Web editor loses that explicit Variant anyway:

- `packages/web/src/entities/agent/api/client.ts:173-179`
  (`readAgentDefinitionModelAndVariant`) returns `variant: null` whenever the
  raw definition has no Model, even if the raw definition contains a Variant.
- `packages/web/src/widgets/agent-profile-editor/ui/AgentProfileEditor.tsx:60`
  initializes the form from that projection, and `:95` serializes the form on
  every save.
- `packages/web/src/entities/agent/api/client.ts:207-210`
  (`writeAgentModelAndVariant`) returns `null` when the form Model is null, so
  an unrelated description/name/Instructions edit sends `agentConfig: null`.

Therefore an Agent that currently resolves to `(default Model, explicit
Variant)` is changed to `(default Model, no Variant)` merely by saving an
unrelated field. This is the same raw-definition preservation boundary that
MF-7 addressed for an empty config, but the existing regression only covers
`agentConfig: null` and misses the valid Variant-only case. Preserve the raw
Variant through the editor unless the user explicitly changes execution
configuration, and add a save regression for Variant-only definitions.

### MF-10: Scope-drift rejection is silent in the Web composer

Violates the `web-agent-task-composer` requirement that every rejected launch
show its rejection reason and repair path, especially the failure scenario
that keeps the composed task visible.

The new preflight gate deliberately returns `409 launch_scope_changed` when
the confirmed scope no longer matches the scope at launch:

- `packages/server/src/Mohist.Server/Api/AgentTaskRoutes.cs:287-293`
- `packages/server/src/Mohist.Server/Api/AgentSessionLaunchRoutes.cs:346-351`

The Web client has no feedback mapping for this code. The feedback union and
mapper in `packages/web/src/entities/agent/model/launch-feedback.ts:3-13,61-`
handle idempotency conflict, pending convergence, configuration failure, and
runtime availability, but not `launch_scope_changed`. The composer stores the
failed mutation at `packages/web/src/pages/agent-session-composer/ui/AgentSessionComposerPage.tsx:559-564`,
but renders the rejection block only when `launchFeedback` is non-null at
`:618`; the unmapped error therefore produces no visible reason or repair
path. A Project default or workspace repository can change while the
confirmation dialog is open, so this is an exercised rejection path rather
than an unreachable response.

Map scope drift to actionable feedback (re-run/review preflight while keeping
the task and context) and add task-first and existing-Agent Web coverage. The
previous review's MF-2 verification checked that the server rejected scope
drift and that the normal confirmation dialog displayed scope, but did not
exercise the client handling of the resulting error code; this is why the
miss remains relevant in this re-review.

## Prior Finding Dispositions

- **MF-6 (replay before mutable preflight scope checking): fixed.**
  `packages/server/src/Mohist.Server/Api/AgentTaskRoutes.cs:231-274` now calls
  `ResumeIdempotentAsync` before the `launch_scope_changed` comparison at
  `:277-294`. The accepted-replay regression
  `TaskLaunch_ReplaysAcceptedOutcomeBeforeCheckingDriftedPreflightScope`
  verifies original Agent, Session, and Job identities after a Project default
  changes.
- **MF-7 (unrelated profile edit materializes the Project default): fixed for
  the reported empty-config case.** The editor now uses the raw-definition
  reader, and `AgentProfileEditor.test.tsx` verifies that a default-resolved
  Agent with no raw config still saves `agentConfig: null`. MF-9 is a separate
  raw-definition preservation case that the fix did not cover.
- **MF-8 (`mo agent start --epic` serialized a string): fixed.** The start and
  definition-first CLI options are `int?`, `BuildLaunchContext` emits numeric
  `epicNumber`, and the CLI plus task-route coverage exercises the numeric
  contract.

## Review Dimensions

- **Acceptance criteria:** **FAIL.** The Project-default resolution and
  task-first identity/scope paths are present, but MF-9 changes a supported
  execution definition during an unrelated edit and MF-10 leaves a valid
  preflight rejection unexplained in Web.
- **Correctness:** **FAIL.** The server-side replay and Epic fixes are correct;
  the two findings above remain observable behavior failures.
- **Consistency with surrounding code and plan:** **FAIL.** MF-9 contradicts
  design D6's per-field precedence and no-edit-time-materialization rule;
  MF-10 contradicts design D9's state-preserving actionable launch feedback.
- **Tests:** **FAIL for completeness.** The existing suites cover the reported
  prior fixes, but there is no Variant-only profile-save test and no Web test
  for `launch_scope_changed`. The full gate is green, so this is missing
  behavioral coverage rather than a failing build.

## Acceptance-Criterion Checks

- **Purpose, description, Instructions, permissions, collaborators, and
  concurrency intent:** checked; no additional must-fix gap found in the
  current surfaces. MF-9 still affects preservation of an execution field
  while refining a definition.
- **Readiness states:** checked, no separate issue found. Server-authoritative
  Ready / Unknown / Needs setup projections are present in list and detail.
- **Model recommendation and full options:** checked, no separate issue found.
  Web uses the labeled Project default and catalog-backed adjustment; CLI
  points unresolved configuration at `mo agent model list`.
- **Pre-launch context and permission scope:** checked with MF-10 above. The
  normal preflight projection is visible, but one of its rejection outcomes is
  not actionable in Web.
- **Save timing and in-flight facts:** checked with MF-9 above. The existing
  save-timing copy and launch snapshots are present, but the editor can erase
  a valid raw Variant during an unrelated save.
- **CLI/Web identity and execution scope:** checked; no separate issue found.
  The numeric Epic context path and effective execution projections are
  consistent across the tested surfaces.

## Observations

- `openspec/changes/issue-560/specs/agent-task-launch/spec.md` still says the
  task body accepts exactly seven top-level fields, while the product route
  additionally accepts `allowedSubagentAgentIds` and `maxConcurrentRuns` for
  the issue's collaborator/concurrency requirement. The implementation and
  issue-level behavior are aligned, but the workflow spec artifact should be
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
