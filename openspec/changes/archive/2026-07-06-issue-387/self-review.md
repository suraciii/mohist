# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `proposal.md` line 26 (server-info capability) stated `mo server info` supports `-o table|json|yaml`, but both the `server-info` spec ("accepted values (`table`, `json`)") and the actual code (`OutputOption` default formats = `"table, json"` in `MohistCliCommands.cs:61`, used at `MohistCliCommands.System.cs:25`) only support `table|json`. The `yaml` format does not exist for this command. Task T-005 and its acceptance criteria correctly say `table|json`, so only the proposal was wrong.
  Verification: Changed `table|json|yaml` → `table|json` in `proposal.md`. The proposal now matches the spec, the task, and the code. The doc gate (`CliReferenceDocsSpecs.cs`) and the `server-info` spec were untouched and remain internally consistent.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: `proposal.md` and `design.md` reference source files by shortened names (`Project.cs:79`, `Notify.cs:63`, `System.cs:19`, `Server.cs:9-26`) while the actual files are partial-class files named `MohistCliCommands.Project.cs`, `MohistCliCommands.Notify.cs`, etc. The line numbers are accurate (verified: `MohistCliCommands.cs:219/226/233`, `System.cs:19`, `Notify.cs:63` all match), so the references are unambiguous, just shorthand.
  SuggestedAction: Optional — during implementation, treat the shortened names as the corresponding `MohistCliCommands.<Resource>.cs` partial files. Not repaired here to keep self-review scope narrow per repair policy.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: Tasks T-001, T-002, T-003 all edit the same root `Build` method in `MohistCliCommands.cs` (deleting lines 14, 15, 24 respectively) yet declare no `dependsOn` among them. This is acceptable because AFK tasks run sequentially by priority and there is no logical/data dependency (each migration is self-contained: relocate one factory + its own tests). No compile or spec risk exists in the final integrated state.
  SuggestedAction: If the workflow ever parallelizes AFK tasks, consider linearizing T-001→T-002→T-003 via `dependsOn` to avoid same-file edit conflicts. Under the current priority-ordered sequential model, no change is needed.
  Status: follow-up

## Notes

Verified against the live codebase:
- Root `Build` registrations (`MohistCliCommands.cs:14-15,24,33`) and private factories (`:219/226/233`) match the design exactly.
- `CliReferenceDocsSpecs.cs:78-107` is a hard doc-gate asserting `mo status`/`mo logs`/`mo system info`/`mo use <project>` exist in `docs/cli-reference.md` — T-006 correctly updates this.
- `docs/cli-reference.md:306-310` gap table contains exactly the five rows to remove.
- `ServerCommands.Build` (`Server.cs:15-23`) has 9 subcommands; adding `info` yields 10 as the design states.
- `SystemCommands.Build` (`System.cs:14`) currently registers only `info`; D3's reframe (logs in, info out) keeps the group non-empty.
- All six specs have matching tasks (T-001..T-006); all `dependsOn` entries point to existing IDs with strictly lower priority; no cycles.

<promise>PASS</promise>
