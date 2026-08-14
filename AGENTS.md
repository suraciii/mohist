# Agent Instructions

## Project Overview

Active development.

- server: ASP.NET Core + Orleans, .NET 11 (`packages/server/`)
- runner: TypeScript, Node (`packages/runner/`)
- web: React 19 + Vite + TanStack Query (`packages/web/`)
- cli: .NET, command `mo` (`packages/cli/`)
- `docs/` user docs · `design/` developer design · `openspec/` workflow artifacts

## Engineering Principles

- Follow KISS and YAGNI. Choose the simplest implementation that fully meets the current requirements. Avoid speculative abstractions, configuration, and indirection.
- Do not preserve backward compatibility. Remove obsolete paths instead of adding compatibility layers, fallbacks, or migrations.
- Study established products before designing a solution. Reuse proven patterns and conventions when they fit the current requirements.
- Grow the system in working layers. Start with the smallest version that works end to end, then add each capability on top of a product that already works. Never trade a working product for unfinished complexity.
- Keep components modular and concerns clearly separated.
- Lean on the dependencies already in the project before writing your own implementation or adding packages. Check their documentation and types before assuming they lack a capability.
- Prefer established, well-maintained libraries when they reduce overall complexity or improve reliability. Do not reimplement common functionality without a clear reason.
- Make architectural decisions for the long term. Do not accept a stopgap that only works for now and is meant to be replaced later.
- Keep models small. Add only the properties that the current contract needs.

## Architecture Constraints

- Keep models minimal. Execution facts and state arbitration stay separate.
- No real external dependencies: network, processes, git, databases, system services — all through fakes (DI / factory hooks / mocks).
- Time must be injectable (C# `TimeProvider`, TS `vi.useFakeTimers`); no wall clock, no `while(now<deadline)` assertions.
- Explicit state machines for concurrent behavior; wait with queues / events / boundary signals, not polling.

## Specs

- Write the spec before implementing: `docs/` = product spec, `design/` = design spec.
- Body is the spec; the gap is the footnote.
- This file holds only rules that apply across the whole repo. Narrow-scope technical details belong in code comments, not here.
- Comments explain "why", never "what"; they never cite docs or issues.

## Verification

Before handoff, run the full local gate: `npm run verify` (build + all tests).
During development, run `npm run test:fast` for the quick tier.

Testing principles: tests must verify quickly — long-running, non-scalable, and flaky tests are defects, to be fixed. Report failed tests, missing tools, and environment limits. Do not hide them.
