# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: completeness
  Evidence: The spec covered overwrite confirmation ("SHALL NOT silently overwrite
  existing values") and the design called out the EOF / non-interactive stdin abort
  in decision D8, but `specs/notify-setup/spec.md` had no explicit scenario for the
  non-interactive stdin case. Added a "Non-interactive stdin aborts without writing"
  scenario to the "Existing values are overwritten only after confirmation"
  requirement so the spec, design D8, and the task acceptance criteria all name the
  same edge case.
  Verification: Re-read
  `openspec/changes/issue-353/specs/notify-setup/spec.md` — the new scenario sits
  under the overwrite-confirmation requirement, states "SHALL abort without writing"
  and "SHALL NOT hang waiting for input", and does not alter any other scenario or
  requirement.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: `design.md` lists "Exact Hermes webhook receiver path (`/webhooks/mohist`)"
  as an open question and provides a `--webhook-url` override to cover divergence.
  The derived default is therefore an assumption pending confirmation against the
  Hermes subscription docs during implementation.
  SuggestedAction: When implementing T-001, confirm the receiver path against
  `docs/hermes-notifications.md` / Hermes subscription docs; adjust the derived
  default or rely on the documented override if it differs.
  Status: follow-up

## Review Summary

### Alignment

Every issue acceptance criterion traces to a spec scenario and a task acceptance
criterion:

- Probe-then-write (AC1) -> "Health endpoint reachable".
- Probe-down abort, no write, non-zero (AC2) -> "Health endpoint unreachable aborts
  without writing config".
- Shared secret identity (AC3) -> "Secret is identical on both sides".
- Overwrite confirmation, no silent overwrite (AC4) -> "Existing values are
  overwritten only after confirmation" (+ newly added non-interactive scenario).
- No `hermes` fork (AC5) -> "No hermes process is launched".
- `--platform` reflected in `--deliver` (AC6) -> "Delivery platform specified".
- Inline `--prompt '{message}'`, no `--prompt-file` (AC7) -> "Delivery platform
  specified" asserts both clauses.

All issue Non-Goals (no Hermes config edit, no `hermes` invocation, no engine
re-implementation, no multi-platform fan-out, no import/export) are reflected in the
proposal Non-Goals, design Non-Goals, and spec requirement "Do not fork processes or
modify Hermes configuration".

### Completeness

- All seven acceptance criteria are covered by `specs/notify-setup/spec.md`.
- The single spec file's four requirements all map to the bundled task T-001 and its
  enumerated acceptance criteria (help, probe-down, fresh write, overwrite
  accept/decline, non-interactive stdin, shared-secret identity, platform deliver,
  no-platform placeholder, no fork, single-file write, JSONC strip unit, build, spec
  tests, repo test gate).
- Edge cases: reload guidance covered; non-interactive stdin now explicit (item-1).

### Consistency

- Proposal declares one new capability `notify-setup`; spec lives at
  `specs/notify-setup/spec.md`; no Modified Capabilities — matches #350 being done.
- Design decisions D1-D8 each map to a spec requirement (D1->command placement,
  D2/D3->direct JSONC write, D4->probe, D5->shared secret + defaults, D6->CLI-local
  strip, D7->receiver URL, D8->overwrite confirmation incl. the new scenario).
- Naming is uniform: `mo notify setup`, `notify` group, `MohistCliCommands.Notify.cs`,
  `CliNotifySetupCommandSpecs.cs`, `Mohist:Notifications:Hermes`,
  `~/.mohist/config.jsonc`.
- Default enabled types (`approval_requested`, `workflow_failed`, `issue_completed`)
  are identical across issue context, spec, design D5, and the existing
  `HermesNotificationOptions` defaults / `NotificationKinds` constants.

### Feasibility

Verified against the working tree:

- `IFileSystem` (`packages/cli/Mohist.Cli/IFileSystem.cs`) exposes
  `ReadAllText` / `WriteAllText` used by the JSONC round-trip; `FakeFileSystem`
  (`packages/cli/tests/Mohist.Cli.Tests/Support/FakeFileSystem.cs`) is the test
  double referenced in the task.
- `ICommandExecutor` / `FakeCommandExecutor` exist and are already DI-registered in
  `MohistCliCommands.RunAsync`, so the "no hermes fork" assertion has a ready
  recording double.
- `MohistCliApi` exposes `Output`, `Error`, `Http`, `StandardInput`, `FileSystem`
  (used by `OtelCommands`, `Issue.Comment`, etc.), so the command has all I/O
  surfaces the task names.
- `MohistCliCommands.Build` (`packages/cli/Mohist.Cli/MohistCliCommands.cs:10`) is the
  `root.Subcommands.Add(...)` registration point the task cites; the
  `OtelCommands.Build` / `ConfigProvidersCommands.BuildConfig` siblings confirm the
  group pattern.
- `OtelCommands.RunStatusAsync` (`MohistCliCommands.Otel.cs:165`) already implements
  the friendly `HttpRequestException` / `TaskCanceledException` handling the task
  says to mirror.
- `MohistConfigurationExtensions.StripJsoncComments` lives in the server assembly
  (`packages/server/src/Mohist.Server/Infrastructure/Config/MohistConfigurationExtensions.cs:34`),
  confirming design D6's rationale for a CLI-local copy rather than a server-package
  dependency.
- `HermesNotificationOptions` (`packages/server/src/Mohist.Server/Notifications/HermesNotificationOptions.cs`)
  is unchanged from #350 and already binds `Mohist:Notifications:Hermes`, so no
  schema change is needed — consistent with the proposal's "Modified Capabilities:
  None".
- `docs/hermes-notifications.md` exists, so the probe-down message can point at it.

Task granularity: a single self-contained feature slice bundling D1-D8 with tests
inline (no separate "add tests" / "register DI" / "create file" task). This matches
the task-splitting guidance (no cross-platform abstraction, no independent
component, no prerequisite refactor) and is the opposite of too fine-grained.

### Dependencies

Single task T-001 with `dependsOn: []` and `priority: 1`. No cycles possible. The
explicit prerequisite #350 is in stage `done`, so the config section this command
populates already exists and is bound server-side.

<promise>PASS</promise>
