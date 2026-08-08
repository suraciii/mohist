# Agent Instructions

## Project Overview

Active development — do not design for version compatibility.

- server: ASP.NET Core + Orleans, .NET 11 (`packages/server/`)
- runner: TypeScript, Node (`packages/runner/`)
- web: React 19 + Vite + TanStack Query (`packages/web/`)
- cli: .NET, command `mo` (`packages/cli/`)
- `docs/` user docs · `design/` developer design · `openspec/` workflow artifacts

## Source of Truth

Read before changing code:

- **Product direction**: [`docs/vision.md`](docs/vision.md)
- **Product language**: [`CONTEXT.md`](CONTEXT.md) — unified vocabulary and avoided usage
- **Boundaries and placement**: [`design/architecture.md`](design/architecture.md) — execution facts vs state arbitration
- **Domain decomposition**: [`design/domain-analysis.md`](design/domain-analysis.md)
- **Conventions**: [`design/conventions.md`](design/conventions.md)
- **Testing**: [`design/testing.md`](design/testing.md) — tracks, hard rules, commands, fake entries
- **Documentation writing**: [`docs/README.md`](docs/README.md) and [`design/README.md`](design/README.md)

## Engineering Principles

- Study established products before designing a solution. Reuse proven patterns and conventions when they fit the current requirements.
- Choose the simplest design that fully meets the current requirements.
- Grow the system in working layers. Do not trade a working product for unfinished complexity.
- Keep modules small and keep different concerns separate.
- Check existing dependencies before adding code or a package. Prefer a maintained library when it reduces complexity or improves reliability.
- Make architecture decisions for the long term. Do not create a stopgap that is meant to be replaced later.
- Remove obsolete paths. Add compatibility code only when the product contract requires it.
- Keep models small. Add only the properties that the current contract needs.

## Architecture Constraints

- Keep models minimal. Execution facts and state arbitration stay separate ([`design/architecture.md`](design/architecture.md)).
- No real external dependencies: network, processes, git, databases, system services — all through fakes (DI / factory hooks / mocks).
- Time must be injectable (C# `TimeProvider`, TS `vi.useFakeTimers`); no wall clock, no `while(now<deadline)` assertions.
- Explicit state machines for concurrent behavior; wait with queues / events / boundary signals, not polling.

## Specs

- Write the spec before implementing.
- `docs/`: product spec — rules in [`docs/_agents.md`](docs/_agents.md).
- `design/`: design spec — rules in [`design/agents.md`](design/agents.md).
- Body is the spec; the gap is the footnote.
- No comments by default; comments never cite docs or issues.

## Verification

Before handoff, run the build and the tests: `npm run build` and `npm test` (tracks and commands in [`design/testing.md`](design/testing.md)).

Testing principles: tests must verify quickly — long-running, non-scalable, and flaky tests are defects, to be fixed. Report failed tests, missing tools, and environment limits. Do not hide them.
