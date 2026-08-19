# Contributing to mohist

Thanks for your interest in mohist!

## Issues

- Search existing issues first; link the closest one when you open a new one.
- Choose the template by external behavior: Feature request / Bug report /
  Refactor. The template descriptions carry the signal.
- Add the `mohist` label to route the issue into the Mohist pipeline; `p0`–`p4`
  labels map to priority.

## Development flow

1. Create a branch from master.
2. Implement the change with tests for new behavior.
3. Run the build, typecheck, and tests the change requires. Command details
   live in `AGENTS.md` and `design/testing.md`.
4. Commit with [Conventional Commits](https://www.conventionalcommits.org/):
   `feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`.
5. Push and open a pull request into master, following the PR template.

## Pull requests

- Title uses Conventional Commits; after squash it becomes the commit on master.
- Body states what and why, not how — the template has the required sections.
- Include tests for new behavior; update docs when the change is user-facing.
- State breaking changes and migration steps explicitly.
- Keep the branch based on latest master: branch protection is strict, so an
  outdated PR must be rebased before merge.

## Documentation

Read the writing rules before editing documents:
[`docs/README.md`](docs/README.md) for product docs,
[`design/README.md`](design/README.md) for design docs. Before requesting a
review, re-read the code behind any fact you state, and check that every
example runs and every link resolves.

## Focused C# Tests

C# test projects use Microsoft Testing Platform with xUnit v3. VSTest
`--filter` does not select focused tests in these projects. Run the compiled
apphost directly with `-class` or `-method`.

Build the selected project once, list the target, and then run it:

```bash
dotnet build packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore
packages/cli/tests/Mohist.Cli.Tests/bin/Debug/net11.0/Mohist.Cli.Tests \
  -list classes -noColor -noLogo \
  -class Mohist.Cli.Tests.Skills.SkillsContentTests
packages/cli/tests/Mohist.Cli.Tests/bin/Debug/net11.0/Mohist.Cli.Tests \
  -noColor -noLogo \
  -class Mohist.Cli.Tests.Skills.SkillsContentTests
```

Confirm the apphost supports the selector with `--help`. In a new worktree, run
`npm ci` first. If the selected project has no `obj/project.assets.json`, run an
explicit `dotnet restore` before the `--no-restore` build. Focused runs are
development evidence; `npm run verify` remains the final local gate.

## License

Contributions are licensed under the MIT License.
