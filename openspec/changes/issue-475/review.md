# Review Findings

## 1. The shared output contract is not migrated across the command tree

**Severity: blocking**

Many resource-returning leaves still register `--output` and call the legacy envelope/table writers. Examples include every repository leaf in `packages/cli/Mohist.Cli/MohistCliCommands.Repository.cs:27-232`, feedback list/show in `MohistCliCommands.Issue.Feedback.cs:86-185`, workflow/project-workflow reads and mutations, agent/session commands, routing commands, and project list/show. `MohistCliCommands.cs:69-74` still provides the legacy `OutputOption`, and the grepable call sites include `PrintWithOutputAsync`, `PrintPostWithOutputAsync`, `PrintPatchWithOutputAsync`, and `PrintDeleteWithOutputAsync` throughout those files.

These commands therefore still accept `--output`, lack a `ResourceDescriptor`, cannot perform local bare-`--json` field discovery, and retain command-specific JSON/envelope behavior. This violates the issue's requirement that the shared contract apply to all existing commands and the resource-output requirements that every resource leaf declare fields and reject legacy selectors. The passing tests cover the migrated Issue/Epic representatives but do not provide the structural completeness guard described by the plan.

## 2. Cancelling an event stream reports success instead of exit 130

**Severity: blocking**

Both cancellation handlers in `NdjsonStream.cs:24-27` and `NdjsonStream.cs:70-73` return `0` when the cancellation token is signalled; `ReadSelectedAsync` does the same at `NdjsonStream.cs:156-159` and `190-193`. `EventCommands.RunTailAsync` passes these methods either the root token or its local Ctrl-C token (`MohistCliCommands.Event.cs:57-94`), so a user interrupt is translated into a successful command result. The root mapping to 130 in `MohistCliCommands.cs:226-234` is bypassed because the stream swallows the cancellation.

The acceptance criteria and execution spec require every user cancellation to stop the command with exit code `130`; this also risks automation treating an interrupted live stream as completed successfully. There is no regression test covering `events tail` cancellation and its exit code.

## 3. Legacy failure handling still returns non-contract exit code 4

**Severity: blocking**

`MohistCliApi.FailureExitCode` explicitly maps HTTP 404 to `4` at `MohistCliApi.cs:1268-1272`. The method is still used by the numerous legacy command paths at `MohistCliApi.cs:185,301,357,426,467,590,641,1003,1057,1171,1194,1219` and by `MohistCliCommands.Agent.cs:851`. A valid operation that receives a 404 therefore returns `4`, although the issue fixes operation failures to `1` and reserves `2` for local usage errors.

Those same legacy paths also print failures directly in `PrintResponseAsync`/`PrintRawResponseAsync` (`MohistCliApi.cs:1167-1197`) without the shared `CliResultWriter`, so they omit the normalized attempt state and can lose the required stable fallback code/details formatting. The new `CliResponseReader` does not correct commands that still use `SendAsync` and these old renderers.

The CLI test suite passes (`1,399` tests), but it does not exercise the unmigrated 404 and cancellation paths; the change is not ready to merge until those paths are migrated or covered by a structural contract test.

<promise>FAIL</promise>
