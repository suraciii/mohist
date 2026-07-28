## Why

Mohist's server production contracts currently expose test-only grain controls and model services that are always registered as optional, allowing tests to exercise paths that cannot occur in production. AgentSession tests also depend on a test-only flush command because the asynchronous persistence boundary does not provide a completion signal.

## What Changes

- Remove test-only activation and grain-key controls from production grain interfaces and implementations; server specs will use supported cluster lifecycle controls and known grain identities instead.
- Make dependencies that the production composition root always supplies required, remove their unreachable fallback behavior, and retain nullable types only for genuinely optional caches or side-channel sinks.
- Register the no-op event push implementation explicitly in the composition root rather than selecting it inside a nullable production dependency.
- Expose a deterministic completion signal at the AgentSession asynchronous persistence boundary so tests can await the persistence operation they caused.
- Remove `FlushForTestAsync` from the AgentSession grain contract and migrate its callers without polling, wall-clock waits, or a change to production persistence timing or ordering.
- Preserve all external API, CLI, workflow, event-dispatch, profile-resolution, and persistence behavior.

## Capabilities
- `server-production-contracts`: Production grain and service contracts exclude test-only lifecycle controls and unreachable optional-dependency fallbacks while retaining explicitly optional infrastructure behavior.
- `agent-session-persistence-observation`: AgentSession persistence provides a deterministic, per-operation completion observation for tests without exposing a test-only flush command or changing persistence behavior.

## Impact

- **Server**: Grain interfaces and implementations for Issue, Workflow, Runner, AgentSession, and profile-reference coordination; dependency-injected event, profile, background-task, and AgentJob services under `packages/server/src/Mohist.Server/`.
- **Tests**: Server unit, spec, and architecture tests will replace test-only grain calls and nullable constructor shortcuts with cluster lifecycle controls, explicit collaborators, and the persistence completion signal.
- **Dependencies and public surfaces**: No external API, CLI, Runner protocol, persistence schema, or package dependency changes are expected; affected contracts are internal server interfaces and constructor dependencies.
