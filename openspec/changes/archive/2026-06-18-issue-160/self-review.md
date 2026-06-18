# Self Review Report

## Result: PASS

The plan for issue-160 (`mo epic` command group) is internally consistent and fully traceable. All 13 issue acceptance criteria map to proposal "What Changes" entries, spec scenarios, and task acceptance criteria. No blocking defects were found. Two implementation-level follow-ups are noted below; both are already captured as Open Questions in `design.md` and do not block the plan.

## Repaired Items

None. No safe, in-scope repairs were required. The artifacts are coherent as written.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: feasibility
  Evidence: The spec/task pass `--priority` verbatim to the API, while `mo issue` normalizes priority case-insensitively (e.g. `P2`→`p2`). The issue's acceptance-criterion example uses lowercase (`--priority p1`), so no artifact is violated, but a user typing `--priority P1` will store the value as-is. This is explicitly listed as `design.md` Open Question #1.
  SuggestedAction: During T-001 implementation, check whether the existing issue-CLI priority normalization helper is trivially reusable; if so, apply it to `mo epic create/update` for UX parity. If not trivially reusable, ship verbatim pass-through and file a follow-up issue. No plan artifact needs changing now.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: `design.md` D5 introduces a single `EpicMembership` table shape for both `link` and `unlink`, noting it "distinguishes link vs unlink via the shape name passed by the caller" — but the task's shape mapping lists both commands against the same `EpicMembership` enum value, so the exact disambiguation mechanism (two enum values vs. one value plus a verb hint) is left to the implementer. The spec only requires "a clear success confirmation identifying both the Epic and the linked issue", so the contract is satisfiable either way.
  SuggestedAction: At implementation time, pick one approach (two shapes `EpicLink`/`EpicUnlink`, or one `EpicMembership` shape that inspects the request method / accepts a verb label) and document it in the render code. No spec change required since the observable contract ("clear confirmation") is already met.
  Status: follow-up

### Review evidence verified

- **Alignment**: All 8 subcommands (`list`/`create`/`show`/`update`/`link`/`unlink`/`done`/`close`), `--project`/`--project-id` override, `-o table|json`, `mo epic --help` + per-subcommand `--help`, conflict surfacing (`DUPLICATE_EPIC_MEMBERSHIP`, `EPIC_NOT_READY_TO_MARK_DONE`), `mo epic show 8` namespace isolation, and the four mandated integration-test cases all trace issue AC → proposal → spec scenario → task criterion.
- **Completeness**: Spec `cli-interface/spec.md` carries 18 `#### Scenario:` blocks under the modified "Epic CLI Commands" requirement covering every command, both dual-track `<id|num>` resolution, all three conflict codes, output/project-override options, help, the no-start-command invariant, and integration test coverage.
- **Consistency**: Modified capability is `cli-interface` across proposal/spec/design/tasks. Delta spec uses `## MODIFIED Requirements` with the requirement header `### Requirement: Epic CLI Commands` matching the existing baseline (`openspec/specs/cli-interface/spec.md:676`) exactly. Command naming (`link`/`unlink` superseding the old `add-issue`/`remove-issue` sketch) is uniform across all artifacts. Task `spec` reference `specs/cli-interface/spec.md#Epic CLI Commands` resolves to a real file and header.
- **Feasibility**: Single task T-001 is a complete feature slice — not over-split (no "define interface"/"register DI"/standalone "add tests" tasks; tests are bundled per guidance). The required `MohistCliApi` write-output helpers and `EpicList`/`EpicShow`/`EpicMembership` table shapes are created within the same task that consumes them, so no task depends on unconsumed infrastructure. Endpoints in the task criteria match `EpicRoutes.cs` (`GET/POST /api/projects/{p}/epics`, `GET/PATCH .../{id}`, `POST/DELETE .../{id}/issues[/{issueId}]`, `POST .../{id}/done`, `POST .../{id}/close`) and the server-side `int.TryParse` dual-track resolver (`EpicRoutes.cs:35-37`) confirms the CLI verbatim-pass-through approach in design D2.
- **Dependency completeness**: `tasks.json` contains one task with `dependsOn: []`; the graph is trivially acyclic and there are no `dependsOn` entries to validate against earlier priorities.

<promise>PASS</promise>
