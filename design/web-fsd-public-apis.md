# Web entity public APIs

The Issue and Agent entity roots have accumulated query clients, mutations,
derived models, UI helpers, transport types, and wildcard type exports. The
root files are the only public exits for their slices, so every export becomes
a dependency that can constrain future ownership changes.

## Design Drivers

- Preserve every symbol currently consumed through the entity root.
- Remove root exports with no consumer instead of adding compatibility aliases.
- Replace wildcard model-type exports with explicit stable domain types.
- Keep implementation files, deep internal imports, and `@x` cross-import APIs
  unchanged.
- Make the public contract readable without opening every model file.

## Semantics

The public API contains stable domain concepts, supported query/mutation
operations, and UI components intentionally reused outside the slice. Query
keys, raw client helpers, event dispatch internals, and derived helpers remain
private unless a current consumer requires them.

The migration is mechanical: first inventory root consumers, then delete only
unconsumed exports, then replace `export * from './model/types'` with explicit
type exports. Typecheck, the FSD gate, and the full Web test suite are the
acceptance checks.

No public API is reconstructed through a second barrel. Consumers continue to
import the slice root, and code inside a slice continues to use relative
imports.

## Non-Goals

- This change does not rename domain types or query factories.
- This change does not move API clients between segments.
- This change does not redesign the Issue or Agent data-access convention.
- This change does not change runtime behavior.

## Status

The Issue and Agent roots now expose the audited consumer inventory through
explicit exports. The compiler and full Web test suite are the final checks for
missed type consumers.
