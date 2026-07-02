## Context

`packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs` is the largest CLI file in the repo: **scc Complexity 223 / 2268 lines**, ~2.5× the next CLI sibling. `mo issue …` has the widest subcommand vocabulary in the CLI, and the entire surface lives in one `internal static class IssueCommands` (MohistCliCommands.Issue.cs:8).

#254 split the CLI into per-command classes and explicitly deferred `IssueCommands` (its Complexity was 86 then). It has since grown into a clearer outlier than #254's original three targets, making it the next step of epic #22 (代码复杂度热点治理). #254 already established two reusable precedents this change extends:

- **Partial-per-concern layout** — `SourceCodeUpdater` is `internal sealed partial class` across `MohistCliCommands.Update.cs` + `Update.{Stages,Operations,Finalize,Outcome}.cs`; `TableRenderer` is split across `TableRenderer.{cs,Issues,Epics,Labels,Runners,ProjectWorkflow,Entities,IssueTemplates}.cs`.
- **Cross-cutting helper consolidation** — sibling `EpicCommands.ValidateOutput(MohistCliApi api, string? output)` (MohistCliCommands.Epic.cs:38) already collapses the output-mode validation idiom into one `private static (string Mode, int Exit)` helper.

`IssueCommands` repeats that idiom inline **24×** (`MohistCliApi.ValidateOutputMode` → `is Invalid` → write error → `return 1` → cast `Valid`) and repeats the project-id resolution idiom inline **31×** (`ctx.GetValue(projectOpt)` + `ctx.GetValue(projectIdOpt)` + `await api.ResolveProjectIdAsync(...)` + `if (null) return 1`). These two idioms — not the file split alone — are where most complexity can be removed.

**Constraints that must hold bit-for-bit (the refactor's contract):** command names/aliases (e.g. `list`/`ls`), argument/option names and shapes, HTTP methods and path shapes, `mo issue update`'s PATCH field-omission semantics, `--stage-models`/`--stage-model-variants` `@file` expansion, output formats, and exit codes are all unchanged — inherited from `cli-interface/spec.md` and #254's "逐字节保持" bar.

**Stakeholders:** CLI only. No server / runner / web / dependency / install / runtime change. The existing issue spec suite is the refactor guardian: `CliIssueWorkflowConfigSpecs`, `CliIssueSessionSpecs`, `CliIssueLabelSpecs`, `CliIssueTemplateCommandSpecs`, `CliIssueUpdatePatchBodySpecs`, `CliIssuePrereqSpecs`, `CliIssueCommentAndFeedbackSpecs`, `CliIssueRerunFromStageSpecs`, `CliIssueRejectAndStopSpecs`, `CliIssueExecutionConfigFlagsSpecs`, `CliIssueCommandSpecs`.

## Goals / Non-Goals

**Goals:**
- Convert `IssueCommands` to `internal static partial class` and split it into per-cluster partial files, with the core partial holding only `Build()` + shared helpers.
- Collapse the 24× output-mode validation idiom into `IssueCommands.ValidateOutput(api, output)`, signature/return-shape aligned with `EpicCommands.ValidateOutput`.
- Collapse the 31× project-id resolution idiom into a shared helper.
- Drop each partial file out of the cli package's scc Complexity top tier.
- Normalize the two `internal`-but-uncalled helpers (`ParseLabelsFromIssue`, `PrintCreateGuidance`) back to `private` as they migrate to their owning cluster.

**Non-Goals:**
- No change to any observable CLI behavior (commands, aliases, flags, output, exit codes, HTTP paths/bodies).
- No backfill of the pre-existing test gaps under `BuildAction`/`BuildGetSub` verb factories (independent test debt).
- No new public/API surface; `IssueCommands.Build()` keeps its current shape.
- No performance work.

## Decisions

### D1. Partial layout and cluster ownership

`IssueCommands` becomes `internal static partial class`. New file set under `packages/cli/Mohist.Cli/`, mirroring the `TableRenderer.*.cs` flat-name convention (all in the `Mohist.Cli` namespace, no subfolder):

| File | Members (current location) | ~Lines |
|------|---------------------------|--------|
| `MohistCliCommands.Issue.cs` (core) | `Build()` (:10), `NumberArg()` (:47), `ProjectIssuesPath()` (:49), `IssueTemplatesPath()` (:2261), `IsOptionProvided()` (:355), **`ValidateOutput()`** (new), **`ResolveProjectId()`** (new) | ~120 |
| `MohistCliCommands.Issue.Crud.cs` | `BuildList` (:56), `BuildCreate` (:123), `ApplyFrontmatter` (:269), `BuildShow` (:316), `BuildUpdate` (:362), `LoadCurrentLabelsAsync` (:525), `ParseLabelsFromIssue` (:541, →`private`), `PrintCreateGuidance` (:721, →`private`) | ~500 |
| `MohistCliCommands.Issue.Lifecycle.cs` | `BuildAction` factory (:560, serves start/approve/close/reopen/retry/rerun/force-stop/resume/unarchive), `BuildReject` (:588), `BuildRerunFromStage` (:640), `BuildStop` (:678), `BuildRebase` (:757), `BuildArchive` (:788), `BuildGetSub` factory (:858, serves logs/events/diff/commits) | ~285 |
| `MohistCliCommands.Issue.Session.cs` | `BuildSessions` (:885), `BuildSession` (:925), `SessionNameArg` (:940), `BuildSessionShow` (:945), `BuildSessionTranscript` (:990), `BuildSessionCompact` (:1035), `BuildSessionReset` (:1081), `BuildSessionFollowup` (:1127) | ~310 |
| `MohistCliCommands.Issue.Workflow.cs` | `BuildWorkflow` (:1196), `BuildWorkflowConfig` (:1263), `BuildWorkflowConfigGet` (:1273), `PrintWorkflowProfileAsync` (:1315), `BuildWorkflowConfigPreview` (:1339), `BuildWorkflowConfigClear` (:1620) | ~370 |
| `MohistCliCommands.Issue.WorkflowConfigSet.cs` | `BuildWorkflowConfigSet` (:1392, **228L — the file's largest method**) | ~230 |
| `MohistCliCommands.Issue.Feedback.cs` | `BuildFeedback` (:1792), `BuildFeedbackCreate` (:1801), `BuildFeedbackList` (:1865), `BuildFeedbackShow` (:1910), `ExtractLatestId` (:1988) | ~215 |
| `MohistCliCommands.Issue.Prereq.cs` | `BuildPrereq` (:2008), `BuildPrereqAdd` (:2016), `BuildPrereqRemove` (:2063) | ~100 |
| `MohistCliCommands.Issue.Comment.cs` | `BuildComment` (:2111), `BuildCommentAdd` (:2118) | ~60 |
| `MohistCliCommands.Issue.Template.cs` | `BuildTemplate` (:2172), `BuildTemplateList` (:2180), `BuildTemplateGet` (:2217) | ~90 |

**Cluster ownership is by call-graph, not by current physical position.** Two helpers physically sit inside the lifecycle region but belong elsewhere: `PrintCreateGuidance` (:721) is called only from `BuildCreate` → moves to Crud; `ExtractLatestId` (:1988) is called only from `BuildFeedbackShow` → moves to Feedback.

**`IssueTemplatesPath` stays in the core partial** even though only the Template cluster calls it, because the spec (`cli-module-structure/spec.md`) enumerates it as a required core shared helper alongside `NumberArg`/`ProjectIssuesPath`/`IsOptionProvided`. Keeping the four cross-cutting helpers in one place is the invariant the split is meant to establish.

**Private access across partials:** C# merges all partials into one class, so `private static` helpers declared in the core partial (`ValidateOutput`, `ResolveProjectId`, `NumberArg`, `ProjectIssuesPath`, `IssueTemplatesPath`, `IsOptionProvided`) are callable from every cluster partial with no visibility change. This is what makes a `static partial class` split viable without widening encapsulation.

**Alternatives considered:**
- *One file per individual subcommand (~30 files).* Rejected — too granular; verb factories like `BuildAction` (8 verbs) and `BuildGetSub` (4 verbs) are one logical unit and read better together; clusters match the spec scenarios.
- *Subfolder `Issues/`.* Rejected — `TableRenderer.*.cs` and `Update.*.cs` are flat in `Mohist.Cli/`; follow the in-repo precedent.

### D2. Split `BuildWorkflowConfigSet` into its own partial

`BuildWorkflowConfigSet` (:1392–1619, 228L) is the single largest method in the file and alone would keep a `Workflow.cs` partial near the top of the cli complexity ranking. It is carved into `MohistCliCommands.Issue.WorkflowConfigSet.cs`. It is self-contained (own option set, own PATCH body assembly, own `@file` expansion for `--stage-models`/`--stage-model-variants`), so it splits cleanly with no shared state beyond the core helpers.

**Alternatives considered:**
- *Keep it inside `Issue.Workflow.cs`.* Rejected — the resulting ~600L file would merely relocate the god-file problem, undermining the "脱离 cli 包前列" acceptance criterion.
- *Split it further by section (option definition vs body assembly vs @file expansion).* Rejected — those sections share local state and read top-to-bottom as one unit; splitting would scatter closures and hurt readability.

### D3. Output-mode validation — collapse into `ValidateOutput`

Add to the core partial, byte-for-byte the body already proven in `EpicCommands.ValidateOutput` (MohistCliCommands.Epic.cs:38):

```csharp
private static (string Mode, int Exit) ValidateOutput(MohistCliApi api, string? output)
{
    var validation = MohistCliApi.ValidateOutputMode(output);
    if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
    {
        api.Error.WriteLine(invalid.Message);
        return ("json", 1);
    }
    return (((MohistCliApi.OutputModeResult.Valid)validation).Mode, 0);
}
```

Each of the 24 call sites becomes:
```csharp
var (mode, exit) = ValidateOutput(api, output);
if (exit != 0) return exit;
```

**Behavior preservation:** the current inline block writes the error via `api.Error.WriteLine(invalid.Message)` then `return 1`; the helper does exactly that and returns `("json", 1)`. The `("json", …)` placeholder mirrors `EpicCommands` and is never observed because the caller returns on `exit != 0`. The local function return type (`Task<int>`) is unchanged.

**Alternatives considered:**
- *Put the helper on `MohistCliApi` itself.* Rejected — it would widen the API surface and diverge from the `EpicCommands.ValidateOutput` sibling; the idiom is a command-builder concern, not an API concern.
- *Return `string?` (null on invalid).* Rejected — loses the explicit exit code and diverges from the established `(Mode, Exit)` tuple shape the spec mandates.

### D4. Project-id resolution — collapse into `ResolveProjectId`

Add to the core partial a helper mirroring the `ValidateOutput` tuple contract:

```csharp
private static async Task<(string ProjectId, int Exit)> ResolveProjectId(
    MohistCliApi api, string? project, string? projectId)
{
    var resolved = await api.ResolveProjectIdAsync(project, projectId);
    if (resolved is null)
        return ("", 1);
    return (resolved, 0);
}
```

Each of the 31 call sites becomes:
```csharp
var (resolvedProjectId, exit) = await ResolveProjectId(api, project, projectId);
if (exit != 0) return exit;
```

**Behavior preservation:** `api.ResolveProjectIdAsync` (MohistCliApi.cs:941) already writes its own diagnostics (`--project`/`--project-id` mismatch, `NoActiveProjectMessage`, missing state file) and returns `null` on failure. The current inline idiom is `if (resolvedProjectId is null) return 1;` — the helper returns exit `1` with no extra output, so error text, ordering, and exit code are identical. No new I/O or control-flow is introduced; the helper is a pure delegation wrapper.

**Note on partial consolidation:** the `ctx.GetValue(projectOpt)` / `ctx.GetValue(projectIdOpt)` reads remain at each call site (they need the locally-declared options), so this collapses the resolve+null-check half of the idiom — the half that repeats verbatim and carries the error/exit contract. Forcing the option reads into the helper would require threading two `Option` values plus `ParseResult` for no complexity win.

**Alternatives considered:**
- *Return `Task<string?>` and keep the null check inline.* Rejected — leaves the `if (… is null) return 1;` line duplicated 31×; the tuple form makes the exit contract symmetric with `ValidateOutput`.
- *Hoist into `MohistCliCommands` as a generic cross-command helper.* Rejected — out of scope (would touch `Epic`/`Project`/etc.); keep this change localized to `IssueCommands` as the proposal specifies.

### D5. Visibility normalization on the two uncalled `internal` helpers

`ParseLabelsFromIssue` (:541) and `PrintCreateGuidance` (:721) are `internal` today but a repo-wide grep shows **zero callers outside `MohistCliCommands.Issue.cs`**. They are both called from exactly one cluster (Update and Create respectively, both CRUD), so they move to `Issue.Crud.cs` and become `private`. Because they are now `private` members of the same partial class, the intra-class call sites compile unchanged.

**Risk note:** this is the only visibility change in the refactor; the grep evidence (no external references) is what makes it safe and is the acceptance check.

## Risks / Trade-offs

- **[Output-mode consolidation drifts from the inline form]** → Mitigation: the helper body is copied verbatim from `EpicCommands.ValidateOutput` which is already production-proven; the suite (`CliIssueLabelSpecs`, `CliIssueSessionSpecs`, `CliIssueWorkflowConfigSpecs`, …) exercises the `--output` paths on every cluster.
- **[Project-id helper changes error ordering or exit code]** → Mitigation: `ResolveProjectIdAsync` owns all error output and already returns `null`; the helper adds no I/O and returns exit `1` exactly as the inline `if (null) return 1;` did. Ordering is unchanged because the helper is a straight delegation.
- **[A cluster partial loses access to a shared helper after the move]** → Mitigation: private members are visible across partials of the same class, so a missing helper is a compile error (C# `TreatWarningsAsErrors` + compile gate), not a silent runtime fault.
- **[`PrintCreateGuidance`/`ParseLabelsFromIssue` going `private` breaks an unseen caller]** → Mitigation: repo-wide grep confirms no callers outside this file; the visibility change is the only such change and is self-checking at compile time.
- **[`BuildWorkflowConfigSet` carve-out adds review churn]** → Mitigation: it is a verbatim move into a new partial, in its own commit, guarded by `CliIssueWorkflowConfigSpecs` (the highest-detail issue spec, covering `@file` expansion and PATCH semantics).
- **[Move-only churn obscures a real regression in review]** → Mitigation: commit ordering (Migration Plan below) keeps each step independently green so review is incremental rather than one diff.

## Migration Plan

CLI-only internal refactor — no API, wire, persistence, or deploy change; no data migration. Rollout is by commit ordering within the PR, each step compiled + spec-green before the next:

1. **Core + helpers first** — make `IssueCommands` `static partial`; add `ValidateOutput` and `ResolveProjectId` to the (still-monolithic) file; rewrite the 24 + 31 inline idioms to call them. Compile + full issue spec suite green. (Largest behavior-adjacent step, isolated for review.)
2. **Visibility normalization** — move `ParseLabelsFromIssue`/`PrintCreateGuidance` to their owning cluster regions conceptually and flip to `private` (still in one file). Compile green.
3. **Carve partials cluster-by-cluster**, one commit each, each a verbatim move: `Crud` → `Lifecycle` → `Session` → `Workflow` (+ `WorkflowConfigSet`) → `Feedback` → `Prereq` → `Comment` → `Template`. Run the relevant spec file(s) per cluster after each move; core partial trimmed to `Build()` + shared helpers last.
4. **Verify acceptance** — `npm test -w packages/cli` (or repo `npm test` which includes the CLI project, C# `TreatWarningsAsErrors` as lint), `scc packages/cli/Mohist.Cli/ --sort complexity` to confirm no `Issue.*` file sits in the top five.

**Rollback:** revert the PR. There is no state to migrate and `IssueCommands.Build()`'s public shape is unchanged, so `Program.cs`/callers are unaffected in either direction.

## Open Questions

- Whether to add lightweight unit tests for the new `ValidateOutput`/`ResolveProjectId` helpers, or rely on the existing spec suite as the pass-through guardian. The proposal's Non-Goals ("不新增对外行为测试") leans toward relying on the existing suite; a direct unit test is optional nice-to-have, not required by acceptance.
- Final call on whether `BuildWorkflowConfigSet` warrants its own file (D2) vs. living inside `Issue.Workflow.cs`. Recommendation: split — leaving it in `Workflow.cs` recreates a ~600L outlier and works against the complexity-ranking acceptance criterion.
