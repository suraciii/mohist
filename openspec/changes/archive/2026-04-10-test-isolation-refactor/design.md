## Context

Current mohist architecture uses global singleton pattern for database and state management:

```typescript
// database.ts - Global singleton
let instance: DatabaseManager | null = null;
export function getDatabase() { ... }
export function resetDatabase() { ... }

// state-manager.ts - Global singleton  
let stateManagerInstance: StateManager | null = null;
export function getStateManager() { ... }
```

This causes test isolation failures because:
1. Vitest runs test files in parallel by default
2. All test suites share the same global `instance` 
3. When Test A creates a project, Test B's `resetDatabase()` closes the connection
4. Test B then fails with "UNIQUE constraint failed" or connection errors

The current workaround forces tests to run sequentially, slowing down CI/CD significantly.

## Goals / Non-Goals

**Goals:**
- Complete test isolation: each test suite has independent database instance
- Maintain production code simplicity: minimal changes to initialization logic
- Zero external API changes: no user-facing behavior modifications
- Enable parallel test execution: remove flakiness from test suite

**Non-Goals:**
- No new features or capabilities
- No performance optimizations (beyond test speed from parallelism)
- No changes to database schema or migrations
- No changes to service layer logic

## Decisions

### Decision 1: Pure Dependency Injection Pattern

**Choice:** Remove all global singletons, require explicit instantiation

**Rationale:**
- Global state is the root cause of test isolation issues
- Explicit dependencies make code more testable and predictable
- No hidden shared state between tests

**Alternative considered:** Keep singletons but add thread-local storage
- **Rejected:** Adds complexity, still has global state, doesn't solve the fundamental problem

### Decision 2: Factory Pattern for Production Entry Point

**Choice:** `server/index.ts` becomes the composition root

```typescript
// Production initialization
const db = new DatabaseManager();
const stateManager = new StateManager(db);
```

**Rationale:**
- Clear dependency chain visible in one place
- Easy to understand initialization order
- Can swap implementations for testing

### Decision 3: In-Memory Databases for Tests

**Choice:** Tests use `new DatabaseManager({ inMemory: true })`

**Rationale:**
- Each test gets completely isolated database
- No filesystem I/O = faster tests
- Automatic cleanup when connection closes

### Decision 4: Constructor Injection Over Setter Injection

**Choice:** Pass dependencies via constructor, not setters

**Rationale:**
- Dependencies are required, not optional
- Immutable after construction
- Easier to reason about object state

### Decision 5: StateManager Owns Database Initialization

**Choice:** StateManager constructor calls `initializeDatabase(db)` internally

**Rationale:**
- StateManager is the composition root for all repositories
- Calling `initializeDatabase` is idempotent (checks schema version before running)
- Tests using StateManager don't need a separate init step
- Tests using repos directly must call `initializeDatabase(db)` themselves (same as today)

**Alternative considered:** Caller always calls `initializeDatabase` explicitly
- **Rejected:** Adds boilerplate to every StateManager usage site; StateManager already encapsulates all repo creation, schema init is part of that responsibility

**After refactoring:**
```typescript
// Production (server/index.ts)
const db = new DatabaseManager();
const stateManager = new StateManager(db);  // initializes schema internally

// Tests using StateManager
const db = new DatabaseManager({ inMemory: true });
const sm = new StateManager(db);  // schema ready, no extra step needed

// Tests using repos directly
const db = new DatabaseManager({ inMemory: true });
initializeDatabase(db);  // explicit init required
const repo = new ProjectRepo(db);
```

## Risks / Trade-offs

**[Risk] Large refactoring surface area**
→ **Mitigation:** Change is mechanical and type-safe. TypeScript compiler will catch missing updates. Review each file systematically.

**[Risk] Production startup failures**
→ **Mitigation:** Manual testing of `npm run server` after changes. Add integration test for startup sequence.

**[Risk] Merge conflicts with in-flight changes**
→ **Mitigation:** This change only affects initialization code (`database.ts`, `state-manager.ts`, `server/index.ts`). Two active changes focus on workflow engine and observability - minimal overlap.

**[Trade-off] Slightly more verbose initialization**
→ **Acceptance:** Production code needs one extra line to create DatabaseManager. Tests need explicit instance creation. This is acceptable cost for test reliability.

**[Trade-off] Breaking internal API changes**
→ **Acceptance:** Marked as BREAKING in proposal. No external users affected. All internal consumers updated in single PR.

## Migration Plan

1. **Phase 1: Update core files**
   - `database.ts`: Remove singleton (instance, getDatabase, resetDatabase, closeDatabase)
   - `state-manager.ts`: Accept `DatabaseManager` in constructor, remove singleton functions
   - `db/index.ts`: Remove singleton function exports

2. **Phase 2: Update production entry point**
   - `server/index.ts`: Explicit `new DatabaseManager()` + `new StateManager(db)`

3. **Phase 3: Update affected test files (6 files)**
   - `database.test.ts` — Replace 6x `resetDatabase`/`closeDatabase`
   - `services.test.ts` — Replace 3x `resetDatabase`/`closeDatabase`
   - `api-routes.test.ts` — Replace `resetDatabase`/`closeDatabase` + `new StateManager()` → `new StateManager(db)`
   - `e2e.test.ts` — Replace `resetDatabase`/`closeDatabase` + `new StateManager()` → `new StateManager(db)`, remove redundant `initializeDatabase` call
   - `agent-workflow-e2e.test.ts` — Replace `resetDatabase`/`closeDatabase`
   - `advance-stage.test.ts` — Replace `resetDatabase`/`closeDatabase`

4. **Phase 4: Verify parallel execution**
   - Run full test suite multiple times to verify no flakiness

**Rollback:** Git revert. No database migrations or external state changes.

## Open Questions

1. ~~Should we keep `DatabaseManager.create()` static factory for convenience?~~
   - **Resolution:** No, direct `new DatabaseManager()` is clearer

2. ~~How to handle existing `getStateManager()` calls in test files?~~
   - **Resolution:** Replace all with explicit `new StateManager(db)`

3. ~~Any shared test setup utilities to update?~~
   - **Resolution:** No shared test setup helpers found. Each test creates its own instances inline.

4. ~~Who calls `initializeDatabase` after refactoring?~~
   - **Resolution:** StateManager constructor handles it internally. Tests using repos directly must call it explicitly (same as today).
