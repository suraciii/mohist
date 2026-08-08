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

- Title uses Conventional Commits; after squash it becomes the commit on main.
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

## License

Contributions are licensed under the MIT License.
