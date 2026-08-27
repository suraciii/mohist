# packages/web — Contribution Rules

Scope: `packages/web/` (React 19 + Vite + TanStack Query). Read this file before changing this directory.

## The one rule

**Dependencies only flow downward**: `app → pages → widgets → features → entities → shared`. Higher layers may import lower layers; the reverse is forbidden. Enforced by `npm run check:fsd -w packages/web`; CI blocks violations.

## Rules

1. **Cross-slice access goes through public exits**. The slice-root `index.ts` is its only public API; importing slice internals is forbidden. Add new exports to the slice's `index.ts`, not deeper paths in callers.
2. **Cross-slice references in entities use the `@x` notation** (e.g. `entities/issue/@x/workflow`); do not import another entity's internals directly.
3. **Slices on the same layer do not import from each other.** If two slices need the same code, move that code down to a lower layer.
4. **Choose the layer before placing new code**. When unsure, choose lower — upgrading is easy, downgrading is not.
5. **Run the FSD gate before handoff**: `npm run check:fsd -w packages/web` (enforced by CI).

## Layers

- `app`: application bootstrap, routing, global providers, global styles. It consumes route pages and the application shell through page or widget `index.ts`, never their internal `ui` or `model` files.
- `pages`: route-level screens and interaction or state valid only within one route (e.g. Settings search belongs to `pages/settings`, not a reusable feature).
- `widgets`: large self-contained UI blocks used by pages.
- `features`: reusable user actions that provide product value.
- `entities`: business entities and their stateful model/API adapters.
- `shared`: business-agnostic API client, UI primitives, browser capability, and utilities. It owns no business logic: Theme context and keyboard-shortcut registry live here (`app` mounts ThemeProvider), static filter values shared by several domain APIs live in `shared/config`, and missing-resource presentation lives in `shared/ui`.
- `shared/ui/components`: shadcn/ui primitives; keep generated UI kit code in `shared`, not in parallel top-level `components` folders.

## Entity Query Clients

For entity server reads, use the dual-write convention: export an `xxxQueryOptions(...)` factory that owns the query key and fetch behavior, including `enabled` and `refetchInterval` settings, alongside a thin `useXxx()` hook that calls `useQuery` with that factory and the project context. Query keys are single-sourced in the factory, so consumers use the hook or pass the factory to `useQuery`, prefetch, or invalidation instead of rebuilding key arrays. Mutations use the same pairing: a client function, an `xxxMutationOptions(...)` factory owning invalidation, and a thin `useMutation` hook.

The unified session clients in `src/entities/coder-session/api/client.ts` are the reference implementation and keep this factory-plus-thin-hook pairing intact. Older query modules that predate this convention are follow-up cleanup; do not retrofit them as part of unrelated changes.
