# Test Isolation Refactor

## Why

Tests fail due to global singleton pattern in `DatabaseManager` and `StateManager`. Multiple test files running in parallel compete for the same database connection, causing race conditions and unique constraint violations. This makes the test suite unreliable and blocks CI/CD.

## What Changes

**BREAKING** - Remove global singleton pattern and refactor to dependency injection:

1. **Remove global database singleton** (`database.ts`)
   - Remove `instance` global variable
   - Remove `getDatabase()`, `resetDatabase()`, `closeDatabase()` functions
   - Make `DatabaseManager` instantiation explicit

2. **Refactor StateManager to dependency injection** (`state-manager.ts`)
   - Change constructor to require `DatabaseManager` instance parameter
   - Remove `stateManagerInstance` global variable
   - Remove `getStateManager()`, `resetStateManager()` functions
   - Production code must explicitly create and pass dependencies

3. **Update production code entry point** (`server/index.ts`)
   - Explicitly instantiate `DatabaseManager`
   - Pass to `StateManager` constructor
   - Propagate instances through service initialization

4. **Update all test files** (24 test files)
   - Replace `resetDatabase()` with `new DatabaseManager({ inMemory: true })`
   - Replace `getStateManager()` with `new StateManager(db)`
   - Ensure proper cleanup in `afterEach` blocks

## Capabilities

### New Capabilities
- `test-isolation`: Complete test isolation through dependency injection, ensuring each test suite has its own database instance

### Modified Capabilities
- None - This is purely an infrastructure refactoring with no external behavior changes

## Impact

**Files affected:**
- `packages/cli/src/db/database.ts` - Remove singleton pattern
- `packages/cli/src/db/index.ts` - Update exports
- `packages/cli/src/server/state-manager.ts` - Add DI, remove singleton
- `packages/cli/src/server/index.ts` - Update initialization
- `packages/cli/tests/*.test.ts` - Update all 24 test files

**Behavior impact:**
- Zero external API changes
- Zero user-facing behavior changes
- Internal architecture only

**Testing impact:**
- All tests must be updated before merge
- Test execution becomes fully parallel-safe
- No more flaky tests due to database conflicts
