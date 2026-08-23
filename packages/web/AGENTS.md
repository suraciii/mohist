# packages/web — Contribution Rules

Scope: `packages/web/` (React 19 + Vite + TanStack Query). Read this file before changing this directory.

## The one rule

**Dependencies only flow downward**: `shared → entities → features → widgets → pages → app`. Higher layers may import lower layers; the reverse is forbidden. Enforced by `npm run check:fsd -w packages/web`; CI blocks violations.

## Rules

1. **Cross-slice access goes through public exits**. The slice-root `index.ts` is its only public API; importing slice internals is forbidden. Add new exports to the slice's `index.ts`, not deeper paths in callers.
2. **Cross-slice references in entities use the `@x` notation** (e.g. `entities/issue/@x/workflow`); do not import another entity's internals directly.
3. **Choose the layer before placing new code**: utilities/base UI in `shared`, domain models in `entities`, user operations in `features`, composite blocks in `widgets`, routed pages in `pages`. When unsure, choose lower — upgrading is easy, downgrading is not.
4. **Run the FSD gate before handoff**: `npm run check:fsd -w packages/web` (enforced by CI).

## Entity Query Clients

For entity server reads, use the dual-write convention: export an `xxxQueryOptions(...)` factory that owns the query key and fetch behavior, including `enabled` and `refetchInterval` settings, alongside a thin `useXxx()` hook that calls `useQuery` with that factory and the project context. Query keys are single-sourced in the factory, so consumers use the hook or pass the factory to `useQuery`, prefetch, or invalidation instead of rebuilding key arrays. Mutations use the same pairing: a client function, an `xxxMutationOptions(...)` factory owning invalidation, and a thin `useMutation` hook.

The unified session clients in `src/entities/coder-session/api/client.ts` are the reference implementation and keep this factory-plus-thin-hook pairing intact. Older query modules that predate this convention are follow-up cleanup; do not retrofit them as part of unrelated changes.
