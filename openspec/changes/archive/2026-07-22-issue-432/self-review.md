# Self-Review — issue-432 (second pass)

Re-reviewed `proposal.md`, `design.md`, `tasks.json`, and `specs/` after the fix
pass. The five problems from the first review are resolved and verified against
the codebase, and all 10 issue acceptance criteria still map to a spec + task.
Two minor, non-blocking wording nits remain; neither affects buildability.

## Prior problems — resolved

- **P1 (JSON persistence/load path):** Fixed and verified. The factual error is
  gone — the design now states custom Profiles persist as JSON
  (`SerializeProfile` = `JsonSerializer.Serialize`, `DeserializeProfile` =
  `FromProfileJson`, confirmed in `ProjectWorkflowProfileManager.cs:552,559` and
  `IssueWorkflowProfileManager.cs:179,228`). D8 scopes load as deserialize→
  `Validate(Definition)` and explicitly replaces the `DeserializeProfile`
  try/catch→`null` swallow; D11 adds a one-shot stored-Definition migration;
  D2/T-003 cover the `CheckDefinitionSurrogate` `Name`→`Id` migration (confirmed
  `WorkflowDefinitionSurrogates.cs:125,129` uses `Name`). The migration plan
  step 8/9 no longer claims "stored as YAML text." T-003 carries matching
  acceptance criteria (incl. a no-silent-null regression test), and the spec
  adds the "load validates a deserialized model through the same rules" scenario.
- **P2 (transitional guard source):** D4 and T-003 now label the inline-agent
  `with`-guard errors `source: action`, making CLI/save Definition-error parity
  unambiguous.
- **P3 (arch test):** T-001 acceptance criteria require an arch test asserting
  the library references no Orleans/ASP.NET.
- **P4 (identifiers):** Spec reworded "stage name"→"stage identifier (`stage`)"
  and added scenarios for duplicate/empty stage identifiers and duplicate check
  id; T-001 acceptance criteria updated to match.
- **P5 (snippet exclusion):** Spec/proposal/design/tasks reworded away from the
  inaccurate `<...>` language to "complete example block vs partial snippets"
  (the doc genuinely contains no `<...>`).

## Verification re-run

- All 10 issue acceptance criteria map to a spec requirement and a task.
- `tasks.json` is valid JSON; dependency graph is a DAG with strictly-lower-
  priority deps; every task has test-inclusive acceptance criteria; all
  `passes=false`.
- Every spec requirement has at least one `#### Scenario` (validation 14/40,
  validate-command 4/5, golden-cases 4/5).
- Codebase facts the fixes lean on re-confirmed: persistence is JSON;
  `CheckDefinitionSurrogate` is keyed on `Name`.

## Minor, non-blocking observations (no fix required to build)

1. **D8 heading is now stale.** The section title reads "…runtime load uses the
   same parser," but the body states load uses `Validate(Definition)` and
   "cannot call `Parse(yaml)` because storage is JSON." The body is correct and
   unambiguous; only the title contradicts it. Cosmetic.
2. **"Same rule set" is slightly loose for `Validate(Definition)`.** D1/D8 and
   the load spec scenario say load runs "the same rule set as `Parse`."
   Precisely, `Validate(Definition)` runs the *semantic* rules (identifier
   uniqueness/required, template roots/positions/ordering) on a typed model; the
   YAML-structural rules (unknown-field rejection, type-coercion rejection) are
   inherent to the YAML→model conversion in `Parse` and are meaningless on an
   already-typed model. The single-owner intent is clear; an implementer should
   not try to re-derive unknown-field checks inside `Validate(Definition)`.

The plan is internally coherent, correctly scoped (including the previously
missing persistence/load/migration path), and ready to build.

<promise>PASS</promise>
