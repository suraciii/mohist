# Node test cleanup plan

> grug plan. one file. no plan number. no extra plan documents.
>
> future agent read whole file before edit. gates run top to bottom. each gate
> leave repo green. complexity demon not allowed to hide in giant rewrite.

## Goal

grug want green test mean real green.

- Runner fake match production contract.
- Runner test TypeScript checked.
- unexpected console error fail test.
- Web shared globals always restored.
- default tests use fake time, not sleep hope.
- jsdom not pretend to know layout.
- default Runner test never run real Git or child process.
- real platform tests live in explicit integration track.
- temp directories and child processes always cleaned.
- Web HTTP test use MSW, one boundary.
- giant test files split by behavior.
- CI keep one Node job unless measured time prove impossible.

Product behavior must not change.

## Base truth

Plan written from commit `a50cd9775` on 2026-07-10.

Known baseline:

- Web: 294 files, 4495 tests.
- Runner: 72 files, 1031 tests.
- fixed shuffle seed `20260710` passed both suites.
- active Node tests had no `.skip(` or `.only(`.
- `AwaitingAck_RetriesReportUntilAcked` passed while console printed
  `uploadTaskLog is not a function`.
- Runner test typecheck prototype found 111 errors in 17 files.
- `build-info.test.ts` imported executable `write-build-info.mjs`; import ran
  `main()`, spawned real Git, and wrote `dist/build-info.json`.
- unrelated dirty file may exist:
  `packages/server/tests/Mohist.Server.SpecTests/Support/CostDescendingCollectionOrderer.cs`.
  Do not touch it.

Run drift check first:

```bash
git diff --stat a50cd9775..HEAD -- \
  package.json package-lock.json packages/runner packages/web \
  .github/workflows/ci.yml .github/workflows/web-test-shuffle.yml \
  design/testing.md
```

If named bug already fixed, mark gate item `SKIPPED: fixed by drift`. If module
boundary changed enough that plan no longer fits, stop. Do not rebuild old bug
shape.

## Work ledger

Update table after every gate. Another agent should resume without archaeology.

| Gate | Status | Commit or note |
|---|---|---|
| Baseline | DONE | Web 296/4527; Runner 72/1031; fixed shuffle passed; hidden `uploadTaskLog` TypeError reproduced |
| Runner truth | DONE | Runner production/test typecheck, target retry regression, 73/1036 suite, fixed shuffle seed 20260710, and build passed |
| Shared state | TODO | |
| Fake time | TODO | |
| Browser truth | TODO | |
| Temp ownership | TODO | |
| Platform split | TODO | |
| Web boundaries | TODO | |
| File size | TODO | |
| Final proof | TODO | |

Status is `TODO`, `IN PROGRESS`, `DONE`, `SKIPPED: reason`, or
`BLOCKED: command + error`.

Gate is verification point, not forced one-commit shape. Baseline has no commit.
Other gates use small logical commits. File-split commit touches at most ten test
source files.

## Scope

Allowed files:

- `package.json`
- `package-lock.json`
- `scripts/check-node-test-boundaries.mjs`
- `scripts/compare-vitest-results.mjs`
- `scripts/node-test-file-budget-baseline.json`
- `scripts/fixtures/node-test-boundaries/**`
- `scripts/fixtures/vitest-results/**`
- `packages/runner/package.json`
- `packages/runner/tsconfig*.json`
- `packages/runner/vitest*.ts`
- `packages/runner/tests/**`
- `packages/runner/scripts/write-build-info.mjs`
- `packages/runner/src/runtime/build-manifest.ts`
- `packages/runner/src/runtime/host.ts`, only for narrow timer signal
- `packages/runner/src/system/process-policy.ts`
- `packages/runner/src/system/process.ts`, only for process-policy calls
- `packages/runner/src/runtime/acp-connection.ts`, only for process-policy calls
- `packages/runner/src/runtime/opencode-models.ts`, only for process-policy calls
- `packages/runner/src/actions/acp/process.ts`, only for process-policy calls
- `packages/runner/src/runtime/workspace.ts`, only for narrow Git runner seam
- `packages/runner/src/actions/openspec.ts`, only for injected date
- `packages/runner/src/server/liveness-probe.probe.test.ts`
- `packages/runner/src/server/liveness-probe.reconnect.test.ts`
- `packages/web/package.json`
- `packages/web/vite.config.ts`
- `packages/web/playwright*.config.ts`
- `packages/web/tests/**`
- `packages/web/src/**/*.test.ts`
- `packages/web/src/**/*.test.tsx`
- paired Web API production module only when pure serializer or adapter must be
  extracted from named HTTP test
- `packages/web/scripts/**`
- `.github/workflows/ci.yml`
- `.github/workflows/web-test-shuffle.yml`
- `design/testing.md`

Not allowed:

- C# file.
- product contract or UI redesign.
- broad scheduler framework.
- new test framework.
- new CI job before timing evidence.
- edit to unrelated plan file.
- archived OpenSpec change.

## Small tools

grug accept few tools. each tool trap one complexity demon.

### Runner test config

Create `packages/runner/tsconfig.tests.json`:

```json
{
  "extends": "./tsconfig.json",
  "compilerOptions": {
    "noEmit": true,
    "rootDir": ".",
    "types": ["node", "vitest/globals"]
  },
  "include": ["src/**/*.ts", "tests/**/*.ts"],
  "exclude": ["dist", "node_modules"]
}
```

Base Runner `tsconfig.json` must exclude `src/**/*.test.ts` from production
build. Test config above includes it. This lets real unit tests live near source
without shipping test code.

Final Runner scripts:

```json
{
  "test:run": "vitest run",
  "test:ci": "npm run typecheck:tests && npm run check:test-boundaries && npm run test:run",
  "test:integration": "vitest run --config vitest.integration.config.ts",
  "typecheck:tests": "tsc -p tsconfig.tests.json --noEmit",
  "check:test-boundaries": "node ../../scripts/check-node-test-boundaries.mjs --scope runner"
}
```

### Shared boundary checker

Create one root script: `scripts/check-node-test-boundaries.mjs`.

- use TypeScript AST, not regex.
- root `package.json` declares TypeScript range `^5.3.2`, same as Web and Runner.
- lockfile updated.
- `--scope web` checks Web.
- `--scope runner` checks Runner.
- `--self-test` runs small fixtures.
- `--budget-base-ref <git-ref>` compares budget baseline with trusted Git tree.
- errors print file, line, rule, simple fix.

Rules enter when gate below says so. Do not build plugin system. One file. Simple
rule functions. Complexity demon stay trapped in small crystal.

### Web scoped property support

Create `packages/web/tests/support/scoped-property.ts`:

```ts
setScopedProperty(target, key, descriptor): void
setScopedValue(target, key, value): void
restoreScopedProperties(): void
```

Helper remembers own, inherited, or absent property. Restore in reverse order.
Web global `afterEach` calls `restoreScopedProperties()`.

### Runner temp support

Create `packages/runner/tests/support/temp-dir.ts`:

```ts
createTestTempDir(prefix: string): Promise<string>
createTestTempDirSync(prefix: string): string
cleanupRegisteredTempDirs(): Promise<void>
```

Register path immediately. Remove reverse order. Ignore only already-absent
path. Other cleanup error fail test.

Create `packages/runner/tests/support/child-process.ts` for integration tests:

```ts
registerTestChild(child): void
cleanupRegisteredChildren(): Promise<void>
```

Register PID at spawn return. Cleanup kills and awaits close. Teardown fails if
registry not empty.

### Vitest result compare

Create `scripts/compare-vitest-results.mjs`.

- read before and after Vitest JSON reports.
- use multiset of `assertionResults[].fullName`, not only count.
- every after result must be `passed`.
- additions allowed.
- missing or renamed old identity fails unless manifest names exact change.
- manifest shape:

```json
{
  "renames": [{ "from": "old fullName", "to": "new fullName" }],
  "removals": [{ "fullName": "removed fullName", "reason": "duplicate" }]
}
```

- `--self-test` covers equal set, added test, duplicate identity, rename,
  removal, failed test, skipped test, and missing test.

No count-only escape. Big brain can add useless test and delete useful test;
grug say no.

## Gate: Baseline

Run before edit:

```bash
npm run test:ci -w packages/web
npm run test:ci -w packages/runner
TZ=UTC npm run test:run -w packages/web -- --sequence.shuffle --sequence.seed=20260710 --maxWorkers=1
npm test -w packages/runner -- --sequence.shuffle --sequence.seed=20260710 --maxWorkers=1
npm test -w packages/runner -- runner-host.spec.ts \
  -t AwaitingAck_RetriesReportUntilAcked \
  --disableConsoleIntercept
git status --short
```

Record file counts, test counts, hidden error text, dirty files. If normal or
shuffle suite fail twice on untouched branch, stop.

## Gate: Runner truth

### Fix old type debt first

Do not enable CI type gate while 111 errors still exist. Fix debt, then turn gate
on. No `as never`, no broad `any`, no double cast, no production interface widen.

Work item model errors, 76 diagnostics:

- `packages/runner/tests/artifact-capture.spec.ts`
- `packages/runner/tests/check-verdict.test.ts`
- `packages/runner/tests/executor-artifacts.spec.ts`
- `packages/runner/tests/executor-branch-stability.spec.ts`
- `packages/runner/tests/executor-task-log.spec.ts`
- `packages/runner/tests/executor-workspace-boundary.spec.ts`
- `packages/runner/tests/executor-write-vars.spec.ts`
- `packages/runner/tests/issue-112-regression.spec.ts`
- `packages/runner/tests/workspace-prepare-workflow.spec.ts`

Fix factory at source. Executor fixture returns exact `RenderedWorkItem`.
Direct action fixture builds exact `ActionContext`. Keep discriminated union.

Vitest mock signature errors, 14 diagnostics:

- `packages/runner/tests/acp/session-strategies.spec.ts`
- `packages/runner/tests/prompt-renderer.spec.ts`
- `packages/runner/tests/workspace-registry-integration.spec.ts`

Use function signature generic:

```ts
vi.fn<(context: PromptLoaderContext) => Promise<Result>>()
```

Stale fixture or call errors, 15 diagnostics:

- `packages/runner/tests/openspec-archive-change.spec.ts`
- `packages/runner/tests/runner-host-cleanup-config.spec.ts`
- `packages/runner/tests/runner-signalr.spec.ts`

Match current contract. Omit unsupported `undefined` JSON values. Return `null`
when contract says `null`. Remove stale extra args.

Dead or untyped boundary errors, six diagnostics:

- `packages/runner/tests/openspec-artifacts.spec.ts`
- `packages/runner/tests/build-info.test.ts`

Delete dead copied helpers in `openspec-artifacts.spec.ts`.

For build info:

- create typed pure `src/runtime/build-manifest.ts`.
- postbuild script imports compiled `dist/runtime/build-manifest.js`.
- test imports typed source module.
- test never imports executable `.mjs`.
- real Git and file write stay inside executable script.

Add test config and standalone `typecheck:tests`. Get zero errors before wiring
to `test:ci`.

### Make console honest

Create three setup files:

- `packages/runner/tests/setup.common.ts` owns console, timer, env, temp cleanup.
- `packages/runner/tests/setup.default.ts` owns deny process policy.
- `packages/runner/tests/setup.integration.ts` owns allow-and-register process
  policy.

Default Vitest loads common then default. Integration Vitest loads common then
integration. Do not select safety mode from mutable env.

- capture `console.error` and `console.warn` per test.
- unexpected call fails in `afterEach`.
- expected error test captures and asserts locally.
- no global string allowlist.
- always restore real timers.
- unstub envs.
- reset module test seams symmetrically.

Put console state logic in
`packages/runner/tests/support/unexpected-console.ts`. Add
`unexpected-console.test.ts` that calls helper directly:

- empty guard passes.
- recorded error throws with message.
- recorded warning throws with message.
- locally asserted expected call does not reach global guard.

Normal suite must not rely on accidental console noise to prove guard works.

Fix `runner-host.spec.ts` fake:

- add hoisted `uploadTaskLog` mock.
- expose method on fake `ServerConnection`.
- normal path resolves.
- targeted test prints no hidden TypeError.

Also fix:

- `opencode-log-diagnostics.spec.ts` uses `vi.stubEnv`, not delete.
- `openspec-archive-change.spec.ts` resets both rename seam and Git seam.

Enable Runner `test:run`, `typecheck:tests`, and temporary `test:ci`:

```text
typecheck:tests && test:run
```

Boundary checker joins Runner `test:ci` after Fake time gate adds first Runner
rule.

Verify:

```bash
npm run typecheck -w packages/runner
npm run typecheck:tests -w packages/runner
npm test -w packages/runner -- runner-host.spec.ts \
  -t AwaitingAck_RetriesReportUntilAcked \
  --disableConsoleIntercept
npm run test:ci -w packages/runner
npm run test:run -w packages/runner -- --sequence.shuffle --sequence.seed=20260710 --maxWorkers=1
```

## Gate: Shared state

Create boundary checker here. Add Web rule: active Vitest file cannot directly
mutate `window`, `document`, `navigator`, `Element.prototype`, or
`HTMLElement.prototype` with assignment, `Object.defineProperty`,
`Reflect.defineProperty`, or `Reflect.deleteProperty`.

Allowed:

- scoped property support.
- central setup.
- `vi.spyOn`.
- auto-restored `vi.stubGlobal`.
- property change on local element instance.

Migrate exact files:

- `packages/web/src/app/providers/ThemeProvider.test.tsx`
- `packages/web/src/features/create-epic/ui/EpicCreateDialog.test.tsx`
- `packages/web/src/features/edit-epic/ui/EditEpicDialog.test.tsx`
- `packages/web/src/pages/epic-detail/ui/EpicDetailPage.summaryArchitecture.test.tsx`
- `packages/web/src/pages/epics/ui/EpicListPage.test.tsx`
- `packages/web/src/pages/issue-changed-files/ui/IssueChangedFilesPage.test.tsx`
- `packages/web/src/pages/issue-detail/ui/IssueDetailPage.cross-tier.test.tsx`
- `packages/web/src/pages/issue-detail/ui/IssueDetailPage.narrow-action-bar.test.tsx`
- `packages/web/src/pages/issue-detail/ui/IssueDetailPage.reading-flow.test.tsx`
- `packages/web/src/pages/issue-detail/ui/IssueDetailPage.test.tsx`
- `packages/web/src/pages/logs/model/useLogs.test.ts`
- `packages/web/src/pages/session/ui/SessionPage.sticky.test.tsx`
- `packages/web/src/pages/settings/ui/SettingsPage.test.tsx`
- `packages/web/src/pages/settings/ui/WorkflowProfilesSection.test.tsx`
- `packages/web/src/shared/lib/theme/theme.test.ts`
- `packages/web/src/shared/ui/markdown-reader/MarkdownReader.test.tsx`
- `packages/web/src/widgets/issue-event-timeline/ui/ActivityDialog.test.tsx`
- `packages/web/src/widgets/issue-event-timeline/ui/EventTimelinePanel.test.tsx`
- `packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx`
- `packages/web/src/widgets/issue-workflow/ui/WorkflowArtifacts.test.tsx`
- `packages/web/src/widgets/issue-workflow/ui/WorkflowView.test.tsx`
- `packages/web/src/widgets/session-transcript/ui/CopyFullTextButton.test.tsx`
- `packages/web/src/widgets/session-transcript/ui/SessionTranscriptLayout.integration.test.tsx`
- `packages/web/src/widgets/session-transcript/ui/SessionTranscriptView.test.tsx`
- `packages/web/tests/IssueDetailPage.spec.tsx`
- `packages/web/tests/SessionLiveUpdates.spec.tsx`
- `packages/web/tests/SessionPage.endpoints.spec.tsx`
- `packages/web/tests/SessionPage.followup-composer.spec.tsx`
- `packages/web/tests/SessionPage.live-transcript.spec.tsx`
- `packages/web/tests/SessionPageHeader.spec.tsx`
- `packages/web/tests/ToolRegistryAndRefetch.spec.tsx`

Move common successful clipboard fake to scoped support. Delete module-load
clipboard mutation. Delete bespoke restore bookkeeping after helper owns it.

Add adjacent regression tests for own property, inherited property, absent
property, and next-test restoration.

Web `test:ci` now runs boundary checker and old `vi.mock` check. Old check dies
later, after replacement rule exists.

Verify:

```bash
node scripts/check-node-test-boundaries.mjs --self-test
npm run check:test-boundaries -w packages/web
npm run typecheck -w packages/web
TZ=UTC npm run test:run -w packages/web -- --sequence.shuffle --sequence.seed=20260710 --maxWorkers=1
```

## Gate: Fake time

Extend checker.

Reject in default tests:

- awaited Promise that schedules real `setTimeout`.
- awaited local sleep or delay backed by timer.
- correctness assert from elapsed `Date.now()` or `performance.now()`.
- `vi.waitFor` in `runner-host*.spec.ts`.

Date used only as fixture is okay.

Add small Runner test support:

- typed `deferred<T>()`.
- signals for connect, poll, report, upload, shutdown.
- fake timer advance for explicit product interval.

No helper that claims fixed number of microtasks means idle. That big brain lie
feed complexity demon.

Migrate exact files:

- `packages/runner/tests/runner-host.spec.ts`
- `packages/runner/tests/runner-host-convergence.spec.ts`
- `packages/runner/tests/runner-host-cleanup-config.spec.ts`
- `packages/runner/tests/runner-host-task-log.spec.ts`
- `packages/runner/tests/workspace-prepare.spec.ts`
- `packages/runner/tests/acp/liveness.spec.ts`
- `packages/runner/tests/acp/session-strategies.spec.ts`
- `packages/runner/tests/acp/session-strategies-liveness.spec.ts`
- `packages/runner/tests/openspec-archive-change.spec.ts`
- `packages/web/src/pages/settings/ui/ProjectDefaultWorkflowControl.test.tsx`
- `packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.liveAppend.test.tsx`
- `packages/web/tests/events-hub-subscription.spec.tsx`

Important changes:

- retry test advances interval twice, no real ten-second wait.
- negative timer tests advance fake time and assert no extra call.
- workspace prepare asserts calls and cache reuse, not elapsed speed.
- ACP abort tests advance fake time, no real abort timer.
- Web loading test uses deferred MSW response.
- subscription tests await fake event signal.
- archive date fixed with fake system time.

Add Runner boundary script and final `test:ci` shape.

Verify:

```bash
node scripts/check-node-test-boundaries.mjs --self-test
npm run check:test-boundaries -w packages/runner
npm run check:test-boundaries -w packages/web
npm run test:ci -w packages/runner
npm run test:ci -w packages/web
```

## Gate: Browser truth

Fix false absence assertions:

- `EpicCreateDialog.test.tsx`: first observe failed request or visible error,
  then assert no success state.
- `LogsPage.test.tsx`: first observe loaded empty state, then assert no `File:`
  row.

Remove direct jsdom geometry comparisons from:

- `packages/web/src/features/create-epic/ui/EpicCreateDialog.test.tsx`
- `packages/web/src/features/edit-epic/ui/EditEpicDialog.test.tsx`
- `packages/web/src/pages/epics/ui/EpicListPage.test.tsx`
- `packages/web/src/shared/ui/markdown-reader/MarkdownReader.test.tsx`

Keep semantic class and interaction asserts. Do not claim jsdom measured page.

Extend checker: active Web Vitest cannot compare DOM `scrollWidth` with
`clientWidth`, compare `offsetWidth` for page-fit claims, or compare bounding
boxes with viewport size. Playwright may. Keep `scrollTop`, `scrollHeight`, and
`clientHeight` behavior tests legal; those test scrolling logic, not browser
layout.

Browser files:

- create `packages/web/tests/e2e/epic-dialog-mobile-overflow.spec.ts`.
- extend `epic-list-mobile-overflow.spec.ts`.
- strengthen `epic-detail-mobile-overflow.spec.ts`.
- extend `workflow-sessions-responsive.spec.ts` with long Markdown code line.

Use widths 320, 390, 430. Assert document no horizontal overflow. Assert boxes
inside viewport for:

- create: `epic-create-scroll-region`, footer, cancel, submit.
- edit: `edit-epic-scroll-region`, footer, cancel, submit.
- list: toolbar, visible `Start next issue`, running and next labels.
- detail: edit button and visible lifecycle action.
- transcript: code block and nearest horizontal scroll owner.

Split a11y commands:

```json
{
  "test:a11y:unit": "vitest run --config vitest.a11y.config.ts",
  "test:a11y:browser": "playwright test -c playwright.a11y.config.ts",
  "test:a11y": "npm run test:a11y:unit && npm run test:a11y:browser"
}
```

Test script never installs browser. CI installs once:

```bash
npm exec -w packages/web -- playwright install --with-deps chromium
```

Keep one Node CI job. Build Web once. Under CI, Playwright serves existing
`dist`; it does not rebuild for each config. Run default Web, a11y unit, browser
install, E2E, a11y browser. Upload trace only on failure.

Verify:

```bash
npm exec -w packages/web -- playwright install --with-deps chromium
npm run check:test-boundaries -w packages/web
npm run test:ci -w packages/web
npm run test:a11y:unit -w packages/web
npm run test:e2e -w packages/web
npm run test:a11y:browser -w packages/web
```

## Gate: Temp ownership

Add Runner temp support and central cleanup. Migrate exact files:

- `packages/runner/tests/expectations.spec.ts`
- `packages/runner/tests/workspace.spec.ts`
- `packages/runner/tests/openspec-archive-change.spec.ts`
- `packages/runner/tests/openspec-tasks.spec.ts`
- `packages/runner/tests/openspec-artifacts.spec.ts`
- `packages/runner/tests/acp-tool-noise.spec.ts`

Use `mkdtemp` or `mkdtempSync`. No Date plus random path name. File already
touched by earlier gate may adopt helper, but no repo-wide cleanup hunt.

Verify:

```bash
set -euo pipefail
tmp_root="$(mktemp -d)"
trap 'rm -rf "$tmp_root"' EXIT
TMPDIR="$tmp_root" npm run test:ci -w packages/runner
test -z "$(find "$tmp_root" -mindepth 1 -print -quit)"
rmdir "$tmp_root"
trap - EXIT
```

## Gate: Platform split

Default Runner test must not touch real Git or child process. Keep small real
integration track for platform truth.

Exact candidate files:

- `packages/runner/tests/acp-tool-noise.spec.ts`
- `packages/runner/tests/build-info.test.ts`
- `packages/runner/tests/executor-branch-stability.spec.ts`
- `packages/runner/tests/executor-cleanup.spec.ts`
- `packages/runner/tests/executor-task-log.spec.ts`
- `packages/runner/tests/executor-workspace-boundary.spec.ts`
- `packages/runner/tests/git-sink.spec.ts`
- `packages/runner/tests/issue-112-regression.spec.ts`
- `packages/runner/tests/process.spec.ts`
- `packages/runner/tests/system-process-timeout.spec.ts`
- `packages/runner/tests/workspace-prepare.spec.ts`
- `packages/runner/tests/workspace-registry-integration.spec.ts`
- `packages/runner/tests/workspace.spec.ts`

Ledger records each as `HERMETIC`, `INTEGRATION`, or `SPLIT`, with final path.
Build-info should already be `HERMETIC` from Runner truth gate.

Integration layout:

- real tests only in `packages/runner/tests/integration/**/*.spec.ts`.
- default config excludes directory.
- `vitest.integration.config.ts` includes only directory.
- both tracks load common setup for console, timers, env, temp, child cleanup.
- default loads deny policy setup.
- integration loads allow-and-register policy setup.

Keep real coverage only for:

- minimal Git clone, branch, rebase smoke.
- process stdout and stderr.
- timeout and abort termination.
- POSIX process group kill where supported.

Use `it.skipIf` for unsupported platform. Never early return. Checker bans
`.skip`, `.only`, and `.todo`; integration `skipIf` is allowed.

### Runtime guard

Static scan alone weak. Production call can spawn process behind clean test
source. Add one tiny unmocked registry:

`packages/runner/src/system/process-policy.ts` exposes only:

```ts
assertExternalProcessAllowed(label: string): void
registerExternalProcess(child): void
setExternalProcessPolicyForTest(policy): void
```

Production default allows and does not register. Test setup changes policy.

These real spawn sites call policy immediately before spawn and register child
immediately after spawn:

- `system/process.ts`.
- `runtime/acp-connection.ts`.
- `runtime/opencode-models.ts`.
- `actions/acp/process.ts`.

Default setup imports only `process-policy.ts`, never modules commonly replaced
by hoisted `vi.mock`. It installs deny policy at setup load, before each test,
and after each test. Deny throws `external process forbidden in default test`.

Integration setup imports same policy registry and installs allow-and-register
policy. Direct integration `child_process` use calls
`registerTestChild(child)` itself.

Existing local factory setters stay. Resetting local factory to `null` may
restore real factory, but real factory still consults current track policy.
Default track therefore stays deny during teardown window. Test needing fake
uses existing local setter; fake does not spawn.

No setup imports `acp-connection`, `opencode-models`, or ACP process module.
Hoisted RunnerHost mocks remain hoisted.

Add `packages/runner/tests/support/external-process-guard.test.ts`:

- direct guard call throws before spawn.
- one test uses existing local process fake and sees fake sentinel.
- next adjacent test resets local fake and sees default deny policy again.
- integration track test proves real factory selected only with integration
  setup.
- focused RunnerHost tests prove existing hoisted module replacements still win.

No-op guard must fail this focused test even when default suite has no platform
candidate left.

No generic service container. One policy module and four call sites. grug keep
club small.

Extend checker for default Runner:

- no direct `node:child_process` import.
- no `process.execPath` launch.
- no real Git command call.
- no import of executable package script.
- no `vi.mock` of `system/process-policy`.
- no active `.skip`, `.only`, `.todo`.

Real Git test uses dedicated HOME, XDG config, explicit user identity, explicit
branch name, disabled hooks, `GIT_CONFIG_NOSYSTEM=1`, and dedicated global config.
Never user repo. Never user credential helper.

Verify default and integration:

```bash
set -euo pipefail
bin_dir="$(mktemp -d)"
default_home="$(mktemp -d)"
integration_home="$(mktemp -d)"
integration_tmp="$(mktemp -d)"
cleanup_dirs() { rm -rf "$bin_dir" "$default_home" "$integration_home" "$integration_tmp"; }
trap cleanup_dirs EXIT

mkdir -p "$default_home/xdg" "$integration_home/xdg"
: > "$integration_home/gitconfig"
ln -s "$(command -v node)" "$bin_dir/node"
ln -s "$(readlink -f "$(command -v npm)")" "$bin_dir/npm"
ln -s /bin/sh "$bin_dir/sh"

env -i \
  PATH="$bin_dir" \
  HOME="$default_home" \
  XDG_CONFIG_HOME="$default_home/xdg" \
  CI=1 \
  "$bin_dir/npm" run test:ci -w packages/runner

env -i \
PATH="$PATH" \
HOME="$integration_home" \
XDG_CONFIG_HOME="$integration_home/xdg" \
GIT_CONFIG_NOSYSTEM=1 \
GIT_CONFIG_GLOBAL="$integration_home/gitconfig" \
TMPDIR="$integration_tmp" \
CI=1 \
npm run test:integration -w packages/runner

test -z "$(find "$integration_tmp" -mindepth 1 -print -quit)"
rmdir "$integration_tmp"
rm -rf "$bin_dir" "$default_home" "$integration_home"
trap - EXIT
```

Integration teardown fails if child registry not empty.

## Gate: Web boundaries

### One HTTP boundary

Move direct fetch tests to MSW:

- `packages/web/src/entities/agent/api/client.test.ts`
- `packages/web/src/entities/agent/api/subscriptions.test.ts`
- `packages/web/src/entities/epic/api/client.test.ts`
- `packages/web/src/entities/inbox/api/client.test.ts`
- `packages/web/src/entities/issue-templates/api/client.test.ts`
- `packages/web/src/entities/issue/api/client.test.ts`
- `packages/web/src/entities/issue/api/create-issue-api-client.test.ts`
- `packages/web/src/entities/issue/api/task-log-client.test.ts`
- `packages/web/src/entities/label-catalog/api/client.test.ts`
- `packages/web/src/entities/settings/api/settings-client.test.ts`
- `packages/web/src/shared/api/api-client.test.ts`

Use request capture to assert method, URL, headers, body. If conversion logic
need unit test, extract pure function only in paired production module. Paired
module is exact same path with `.test` removed. No new HTTP wrapper.

After migration, simplify MSW support. Keep relative URL normalization and fail
unhandled request. Delete repair code only needed because tests replaced fetch.

Checker rejects:

- `vi.stubGlobal('fetch', ...)`.
- assignment or spy on global fetch.
- Web `vi.mock(...)`.

Delete old `check-vi-mock-ratchet.mjs` and zero baseline JSON only after new AST
rule passes.

### One environment naming rule

Final discovery:

- node: `src/**/*.test.ts`, excluding `*.dom.test.ts`.
- jsdom: `src/**/*.test.tsx`, `src/**/*.dom.test.ts`, `tests/**/*.spec.tsx`.
- browser and a11y dirs stay outside default.
- no `@vitest-environment` directive.
- no central filename allowlist.

Rename exact DOM-without-JSX files to `*.dom.test.ts`:

- `src/app/providers/LiveTaskProvider.inbox.test.ts`
- `src/app/providers/LiveTaskProvider.lifecycle.test.ts`
- `src/app/providers/LiveTaskProvider.transcript.test.ts`
- `src/entities/issue/lib/completion-snapshot.test.ts`
- `src/entities/settings/model/updateOutcome.test.ts`
- `src/features/settings-search/keyboard-shortcuts.test.ts`
- `src/pages/issue-detail/model/useConfirmOutsideClick.test.ts`
- `src/pages/issue-detail/model/useIssueDetailMutations.test.ts`
- `src/pages/logs/model/useLogs.test.ts`
- `src/pages/settings/lib/sections.test.ts`
- `src/shared/lib/theme/theme.test.ts`
- `src/widgets/coder-session/model/activity-cards.test.ts`
- `src/widgets/coder-session/model/useSessionTimeline.test.ts`
- `src/widgets/issue-changed-files/model/diffModel.test.ts`
- `src/widgets/issue-event-timeline/useEventTimeline.test.ts`
- `src/widgets/issue-workflow/model/useWorkflowSessionFiltering.test.ts`
- `src/widgets/kanban-board/ui/StatusPill.contrast.test.ts`
- `src/widgets/session-transcript/model/serialize-transcript.test.ts`

Paths above are under `packages/web/`.

Remove redundant jsdom directives from TSX/spec files. Remove explicit node
directive from `factory-status.test.ts`.

Checker rejects DOM globals or `@testing-library/react` import from ordinary
`.test.ts`. Checker rejects any environment directive.

### Source-reading tests

Resolve exactly:

- `settings-consistency.test.tsx`: durable import, icon, token rule moves to AST
  checker; user behavior becomes render test; historical styling regex deleted.
- `kanban-board-containment.test.tsx`: keep render behavior; App source regex
  becomes App render or AST rule only if real architecture invariant.

After this, checker rejects `node:fs` source reading in Web Vitest.

Final Web scripts:

```json
{
  "test:ci": "npm run check:test-boundaries && npm run test:run",
  "check:test-boundaries": "node ../../scripts/check-node-test-boundaries.mjs --scope web"
}
```

Verify:

```bash
npm run check:test-boundaries -w packages/web
npm run typecheck -w packages/web
npm run test:ci -w packages/web
TZ=UTC npm run test:run -w packages/web -- --sequence.shuffle --sequence.seed=20260710 --maxWorkers=1
```

## Gate: File size

Extend checker with simple budget:

- new or compliant `*.test.*`: max 300 lines.
- new or compliant `*.spec.*`: max 800 lines.
- current offenders stored in `scripts/node-test-file-budget-baseline.json`.
- baseline file may shrink, never grow.
- renamed offender cannot escape limit.
- trusted-base compare rejects new baseline key and higher old number.

Only mandatory splits in this plan:

- `packages/runner/tests/runner-signalr.spec.ts`
- `packages/web/src/widgets/session-transcript/ui/SessionTranscriptView.test.tsx`
- `packages/web/src/pages/issue-changed-files/ui/IssueChangedFilesPage.test.tsx`
- `packages/web/tests/ToolRegistryAndRefetch.spec.tsx`

Split by current top-level behavior. Shared stateless data builder uses narrow
`*-test-utils.ts`. Lifetime owner uses `Fixture` under test support. No new
generic layer.

`packages/runner/tests/liveness-probe.spec.ts` is 388 lines and says it is direct
unit. Do not only rename; that would break 300-line budget. Split into:

- `packages/runner/src/server/liveness-probe.probe.test.ts`
- `packages/runner/src/server/liveness-probe.reconnect.test.ts`

Each file below 300 lines. Delete old spec.

Create initial baseline once:

```bash
base_ref="$(git merge-base HEAD origin/master)"
node scripts/check-node-test-boundaries.mjs \
  --write-budget-baseline \
  --source-ref "$base_ref"
```

Command scans test files from trusted source ref, not mutable working tree. It
must fail if baseline file already exists. Later work edits baseline only to
lower limits or remove compliant paths.

Normal checker with `--budget-base-ref` does this:

- if trusted tree has baseline file, compare current JSON to trusted JSON.
- if trusted tree has no baseline yet, compute expected bootstrap values from
  trusted tree files.
- reject new offender, higher number, missing old path that still violates
  absolute budget, or current file longer than allowed value.

Node CI checkout fetches enough history to read PR base. CI passes PR base SHA;
push run passes previous commit. Workflow dispatch uses `HEAD^` when available.
Self-test covers raised value and new offender.

Before each split batch:

```bash
npm run test:run -w packages/web -- --reporter=json --outputFile=/tmp/web-before.json
npm run test:run -w packages/runner -- --reporter=json --outputFile=/tmp/runner-before.json
```

After relevant batch, create after report and compare:

```bash
npm run test:run -w packages/web -- --reporter=json --outputFile=/tmp/web-after.json
npm run test:run -w packages/runner -- --reporter=json --outputFile=/tmp/runner-after.json
node scripts/compare-vitest-results.mjs --before /tmp/web-before.json --after /tmp/web-after.json
node scripts/compare-vitest-results.mjs --before /tmp/runner-before.json --after /tmp/runner-after.json
```

If test name changes or false test removed, pass explicit manifest. Ledger names
each change and reason. No silent identity loss.

```bash
node scripts/compare-vitest-results.mjs \
  --before /tmp/web-before.json \
  --after /tmp/web-after.json \
  --manifest /tmp/web-test-identity-changes.json
```

Checker bans active `.skip`, `.only`, `.todo`. Integration `skipIf` still okay.

Verify every batch:

```bash
node scripts/compare-vitest-results.mjs --self-test
npm run check:test-boundaries -w packages/web -- --budget-base-ref "$(git merge-base HEAD origin/master)"
npm run check:test-boundaries -w packages/runner -- --budget-base-ref "$(git merge-base HEAD origin/master)"
npm run typecheck -w packages/web
npm run typecheck:tests -w packages/runner
npm run test:ci -w packages/web
npm run test:ci -w packages/runner
```

Other old over-budget files stay in no-growth baseline. They are future work.
grug not boil ocean.

## Gate: Final proof

Keep one Node CI job. Order:

- install once.
- checker and result-comparator self-tests.
- build once.
- Runner default.
- Runner integration.
- Web default.
- Web a11y unit.
- install Chromium once.
- Web E2E.
- Web a11y browser.

Weekly Web shuffle:

- use random seed.
- pass `--pool=forks --maxWorkers=1`.
- write seed, SHA, pool, workers, exact command to job summary.

PR fixed shuffle only if measured Node job still comfortable under 15 minutes.
No new job without timing proof.

Run final local commands:

```bash
npm run build --workspaces --if-present
node scripts/check-node-test-boundaries.mjs --self-test
node scripts/compare-vitest-results.mjs --self-test
base_ref="$(git merge-base HEAD origin/master)"
npm run check:test-boundaries -w packages/runner -- --budget-base-ref "$base_ref"
npm run test:ci -w packages/runner
npm run test:integration -w packages/runner
npm run check:test-boundaries -w packages/web -- --budget-base-ref "$base_ref"
npm run test:ci -w packages/web
npm exec -w packages/web -- playwright install --with-deps chromium
npm run test:a11y:unit -w packages/web
npm run test:e2e -w packages/web
npm run test:a11y:browser -w packages/web
```

Run Web shuffle:

```bash
TZ=UTC npm run test:run -w packages/web -- --sequence.shuffle --sequence.seed=20260710 --pool=forks --maxWorkers=1
TZ=UTC npm run test:run -w packages/web -- --sequence.shuffle --sequence.seed=74109 --pool=forks --maxWorkers=1
TZ=UTC npm run test:run -w packages/web -- --sequence.shuffle --sequence.seed=313005 --pool=forks --maxWorkers=1
```

Run Runner shuffle:

```bash
TZ=UTC npm run test:run -w packages/runner -- --sequence.shuffle --sequence.seed=20260710 --pool=forks --maxWorkers=1
TZ=UTC npm run test:run -w packages/runner -- --sequence.shuffle --sequence.seed=74109 --pool=forks --maxWorkers=1
TZ=UTC npm run test:run -w packages/runner -- --sequence.shuffle --sequence.seed=313005 --pool=forks --maxWorkers=1
```

Repeat restricted PATH, isolated integration HOME, default TMPDIR, and
integration TMPDIR proofs from earlier gates.

When push and Actions access allowed, record first Node job URL, duration,
conclusion. If no live run allowed, ledger says `NOT LIVE-VERIFIED`. Do not claim
15-minute budget passed from local guess.

Finish:

```bash
git diff --check
git status --short
```

## Done

- [ ] ledger complete.
- [ ] Runner source and test typecheck pass.
- [ ] no hidden Runner console error.
- [ ] no unscoped Web process-global mutation.
- [ ] no real-time synchronization in default tests.
- [ ] no jsdom geometry claim.
- [ ] false absence assertions wait for positive completion.
- [ ] browser and a11y tests run in CI entry point.
- [ ] default and integration TMPDIR empty.
- [ ] default Runner runtime guard blocks external process.
- [ ] all 13 platform candidates have disposition.
- [ ] integration uses isolated HOME and Git config.
- [ ] Web fetch tests use MSW.
- [ ] Web environment comes from suffix only.
- [ ] no Web `vi.mock`, source regex test, or old ratchet.
- [ ] file budget checker passes and baseline never grows.
- [ ] named giant files split.
- [ ] Vitest identity compare shows no silent coverage loss.
- [ ] three Web and three Runner fixed seeds pass.
- [ ] no C# or unrelated file changed.

## Stop

grug stop and report when:

- untouched baseline fails twice.
- typecheck errors have new category outside listed debt.
- fix needs file outside scope.
- deterministic host test needs broad scheduler API.
- platform fake would delete only real semantic coverage.
- hoisted mock split fails after two focused tries.
- Vitest identity disappears without explicit manifest.
- any gate command fails twice after focused diagnosis.
- live Node job exceeds 15 minutes.
- staged diff contains C#, archived OpenSpec, unrelated plan, or user work.

No improvising big brain framework. Small fix. Green gate. Next gate. grug nod
head.
