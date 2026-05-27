# Web Architecture

This package follows an incremental Feature-Sliced Design layout.

## Layers

- `app`: application bootstrap, routing, global providers, global styles.
- `pages`: route-level screens and page-local model code.
- `widgets`: large self-contained UI blocks used by pages.
- `features`: reusable user actions that provide product value.
- `entities`: business entities and their stateful model/API adapters.
- `shared`: business-agnostic API client, UI primitives, and utilities.

## Import Direction

Code can import only from lower layers:

`app -> pages -> widgets -> features -> entities -> shared`

Slices on the same layer should not import from each other. If two slices need the same code, move that code down to a lower layer.

## Migration Notes

The current structure is intentionally incremental:

- `entities/project/api/queries.ts` still contains broad project-scoped query hooks. Split it by entity or feature only when a concrete change benefits from the smaller boundary.
- Route pages live under `pages/*/ui` even when they still contain local orchestration logic.
- Larger page sections live under `widgets/*/ui`; shared UI primitives live under `shared/ui`.
