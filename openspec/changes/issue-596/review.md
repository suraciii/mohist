# Review

## Verdict: FAIL

### Must-Fix Findings

- **MF-001: The CLI reference omits the visible `webhook` root area.**
  `packages/cli/Mohist.Cli/MohistCliCommands.cs:28` registers `webhook` directly
  under `mo`, and `CommandPresentations.cs:48-50` gives it explicit visible
  Automation coverage. The executable root-help test also expects
  `webhook` in the Automation capability (`packages/cli/tests/Mohist.Cli.Tests/CliProgressiveHelpSpecs.cs:23`).
  However, `docs/cli-reference.md:136` lists Automation without `webhook`,
  while `docs/cli-reference.md:137` lists Operations without it as well.
  Therefore the reference command map is not the complete visible registered
  root surface and does not match the capability index, violating the
  `CLI reference matches the executable help surface` requirement and T-003
  acceptance criteria 1, 5. Add `webhook` under Automation and retain its
  existing Operations action table.

## Dimension Review

- **Issue acceptance criteria: checked, issue found.** The issue was read before
  reviewing the diff. Its target is the missing `mo --help` command areas and
  leaf commands; the executable change addresses those omissions, but the
  required reference alignment remains incomplete as described in MF-001.
- **Coverage: checked, issue found.** Root, group, leaf, structural coverage,
  local usage, hidden-node, and documentation requirements are represented in
  the plan and implementation. The documentation root map misses one visible
  area, so coverage is incomplete overall.
- **Correctness: checked, no additional issue.** Root rendering now requires
  explicit metadata, group and leaf rendering uses direct live-tree children,
  exact invocation paths are derived from parent relationships, and descriptor
  JSON fields remain available. The focused tests cover workspace, nested
  session scheduling, deep workspace leaves, OTel fields, hidden options,
  side-effect-free help, nearest nested usage, and representative execution.
- **Consistency with the surrounding codebase: checked, issue found.** The
  executable catalog and help test consistently classify `webhook` under
  Automation, but the CLI reference does not include it in either root row.
  No other root classification mismatch was found.
- **Tests and verification: checked, no issue.** The CLI test project passed all
  1,820 tests, including the progressive-help and coverage-validator suites.
  `npm run verify` passed docs checking, build, all .NET suites, architecture
  tests, Web (4,697), Runner (1,613), and Slack (70) tests. `git diff --check`
  passed. The verification-repair server fixture changes also passed the full
  server suites; they are unrelated observations rather than acceptance
  failures.

## Observations

- The current branch includes verification-repair edits to server test
  fixtures and architecture source digests in addition to the CLI change. They
  do not alter product behavior and the full gate passes, but they are outside
  the issue's CLI help scope.
- This is the first review of the delivered implementation. The existing
  `self-review.md` reviewed the implementation plan, not this implementation.

<promise>FAIL</promise>
