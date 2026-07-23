# Self-Review — issue-478 (unified Project/Issue/Run Variables read/write CLI)

Re-review after the fix task. Reviewed artifacts: `proposal.md`, `design.md`, `tasks.json`,
`specs/variable-commands/spec.md`, `specs/variable-resources/spec.md`, against the issue body and
the current codebase.

## Summary

The blocker from the prior review is resolved. The `workflow-profile/variables` → `variables`
route rename now accounts for every caller, including the production Web UI clients that the
first review found missing:

- `proposal.md` Impact now has a dedicated **Web** bullet (`entities/settings/api/client.ts`,
  `entities/issue/api/client.ts` + MSW/tests) and a corrected Runner file reference
  (`server/connection.ts`).
- `design.md` D5 lists the Web production clients by file and line, the alias alternative is
  explicitly rejected in favor of migrating clients, the route-rename risk names Runner/Web/test
  callers, and Migration step 4 covers Web clients + path-contract specs.
- `tasks.json` T-001 description/output/notes and three new acceptance criteria cover the Web
  clients, MSW handlers, Web specs, and the server path-contract regression specs; the build line
  adds `npm run typecheck`/`test:run` for `packages/web`.

## Verification performed

- **Spec format**: both specs use exactly `### Requirement:` / `#### Scenario:` (4 hashtags); no
  3-hashtag scenarios; every requirement has ≥1 scenario; normative SHALL/MUST language; no
  `ADDED/MODIFIED/REMOVED` headers. variable-commands = 11 requirements / 21 scenarios;
  variable-resources = 8 requirements / 17 scenarios.
- **Acceptance-criteria coverage**: all nine issue acceptance criteria map onto a spec
  requirement and a task (shared verbs/key-path/`--stage`/value typing; string-vs-`--value-json`
  mutual exclusion; `unset` inheritance without persisted `null`; scope-local reads; Run
  `--effective` merge + stage; Run target resolution; attempt-snapshot invariant for accepted vs.
  not-yet-dispatched; write-boundary rejection of non-object root / invalid JSON / invalid key path).
- **Task graph**: valid JSON; 3-task DAG (T-001 → T-002 → T-003); every `dependsOn` references a
  strictly lower-priority task; each task bundles its own tests (no standalone test tasks); splits
  are by feature module (server resource API / CLI command group / legacy switchover), not by
  technical step.
- **Minor item resolved**: the previously unspecified `list --stage` behavior is now a scenario
  ("list --stage returns the scope's own raw stage slice, no merge").

## Minor observations (not blockers)

- `design.md` Open Questions still lists the `list --stage` question as "design assumes yes", which
  is now superseded by the spec scenario that mandates it. The two are consistent (not
  contradictory); the OQ could simply be marked resolved. Does not affect buildability.
- The `issue variable` positional issue-number argument is implicit (it follows the existing
  `<number>` convention used by all `mo issue` commands and appears in the spec examples), rather
  than stated as an explicit requirement. Consistent with convention; not a gap.

## Verdict

The plan accounts for all consumers of the renamed API (Runner, Web production clients, MSW/test
handlers, and server path-contract specs), the specs are well-formed and cover every issue
acceptance criterion, and the task graph is a sound DAG. The plan is ready to build.

<promise>PASS</promise>
