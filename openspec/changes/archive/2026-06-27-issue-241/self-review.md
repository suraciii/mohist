# Self Review Report

## Result: PASS

The plan for issue-241 was reviewed against the issue body, the proposal, design, the single
spec under `specs/cli-interface/`, and `tasks.json`. Every technical claim in the design was
cross-checked against the current source tree (`packages/server`, `packages/cli`), and every
issue acceptance criterion was traced to a spec requirement and a task acceptance entry.

## Repaired Items

_None._ No safe repairs were required; the artifacts are internally consistent and accurate.

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: feasibility
  Evidence: `MohistCliApi.ParseTableShape` (`MohistCliApi.cs:566-573`) silently falls back to
  `TableShape.ProjectList` when an unknown shape name is passed, instead of erroring. If the
  implementer forgets to add the three new enum entries (`SessionMetadata`,
  `SessionTranscriptSummary`, `SessionRecovery`) or misspells the dispatch names, table output
  would degrade to the project-list renderer rather than fail loudly. This is pre-existing
  framework behavior, not a plan defect.
  SuggestedAction: The task already mandates adding the three enum entries and matching
  dispatch cases, and requires integration tests asserting the rendered output per verb — which
  will catch a missing entry. No plan change needed; implementer should ensure the test for each
  verb asserts a session-specific field (e.g. `New session:`) rather than only the exit code.
  Status: follow-up

## Verification Summary

### Alignment

All nine issue acceptance criteria are covered:
- `session show` metadata (table/json) → spec "CLI issue session show returns session metadata".
- `session transcript` summary (table) / full (json) → spec "CLI issue session transcript
  returns summary or full transcript".
- `session compact` prints `New session: <id>` + 409 passthrough → specs "CLI issue session
  compact reports new session identifier" + "CLI session mutating verbs surface session_active
  conflicts".
- `session reset` same shape → spec "CLI issue session reset reports new session identifier".
- Existing `mo issue sessions <num>` unchanged → spec "Existing list command is preserved" +
  design Decision 1.
- `--project/--project-id` + `-o table|json` on all verbs → spec "All session subcommands accept
  project reference and output options".
- `mo issue session --help` lists the four verbs → spec "Help lists the four session subcommands".
- CLI integration tests (success + conflict) → task T-001 acceptance + description.
- `name` source documented → spec + task.

### Completeness

- All requirements covered by specs (6 requirements, 16 scenarios).
- All specs covered by the single task T-001.
- Edge cases considered: 404 nonexistent session (per verb), 409 `session_active` (compact/reset),
  table-vs-json divergence, and preservation of the existing list command.

### Consistency

- Proposal "Modified Capabilities: cli-interface" maps 1:1 to the spec requirements.
- Task `spec` link (`specs/cli-interface/spec.md#CLI-provides-mo-issue-session-command-group`)
  points to an existing requirement heading.
- Design Decisions 1-5 align with the spec requirements and the proposal impact list.
- Naming is uniform: `session` (singular) group; `show`/`transcript`/`compact`/`reset` verbs;
  `SessionMetadata` / `SessionTranscriptSummary` / `SessionRecovery` table shapes; `New session:`
  output prefix; `agentSessionId` field source — all consistent across proposal, design, spec,
  and task.

### Feasibility (verified against source)

- Server endpoints exist: `IssueRoutes.Sessions.cs:24` (GET metadata), `:36` (GET transcript),
  `:48` (POST compact), `:73` (POST reset). 409 emitted as
  `ApiResults.Conflict("Cannot compact while session is active", "session_active", ...)` at
  `:69`/`:94`.
- Recovery shape verified: `AgentSessionRecoveryResult` (`IAgentSessionGrain.cs:84-93`) carries
  `AgentSessionId` = the new follow-on runtime id, sourced from
  `session.Status.AgentRuntimeSessionId` in `BuildRecoveryResult` (`AgentSessionGrain.cs:275`).
  This confirms the design's load-bearing note that the `New session:` line must read
  `agentSessionId`, not `id`.
- CLI helpers exist: `PrintWithOutputAsync` (`MohistCliApi.cs:462`),
  `PrintPostWithOutputAsync` (`:476`), `PrintEnvelopeAsync` (`:522` → routes non-success to
  `PrintResponseAsync` which writes `error (code)` to stderr and returns 4 for 404 / 1 otherwise,
  including 409), `ApiResponseException(message, code)` (`:797`), `TableShape` enum (`:539`),
  `TableRenderer` partial with dispatch switch (`TableRenderer.cs:35`), `ProjectIssuesPath`
  (`MohistCliCommands.Issue.cs:47`), `MohistCliCommands.Escape`, `ResolveProjectIdAsync`
  (`MohistCliApi.cs:655`).
- Command-group pattern verified: `BuildWorkflow` (`MohistCliCommands.Issue.cs:853-918`) and
  `BuildSessions` (`:813-851`) are the exact templates cited by the task; registration at
  `issue.Subcommands.Add(...)` (`:14-40`).
- Test infrastructure verified: `RecordingHttpHandler` + `FakeFileSystem` + `SetupEnv` in
  `CliEpicCommandSpecs.cs:13-30` is the cited harness pattern.
- Decision 2's "no new POST-error plumbing" claim confirmed: the existing pipeline already
  satisfies the load-bearing 409 `session_active` passthrough in both `-o json` and `-o table`
  modes.
- Task granularity is appropriate: a single cohesive feature slice (one command group, four
  peer verbs, shared path/renderer/test setup). It is not over-split — there is no standalone
  "define interface", "register DI", "create file", or separate "add tests" task; tests are
  bundled with the implementation.

### Dependency Completeness

- Single task T-001 with `dependsOn: []` (first task). No cycles. No `dependsOn` validation
  needed beyond the first task.

<promise>PASS</promise>
