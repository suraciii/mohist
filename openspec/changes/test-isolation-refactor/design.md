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
   - `database.ts`: Remove singleton
   - `state-manager.ts`: Add constructor parameter
   - `db/index.ts`: Update exports

2. **Phase 2: Update production entry point**
   - `server/index.ts`: Explicit instantiation

3. **Phase 3: Update test utilities (if any)**
   - Check for shared test setup helpers

4. **Phase 4: Update all test files**
   - Batch update 24 test files
   - Verify each passes individually

5. **Phase 5: Verify parallel execution**
   - Remove sequential test flags
   - Run full suite multiple times

**Rollback:** Git revert. No database migrations or external state changes.

## Open Questions

1. Should we keep `DatabaseManager.create()` static factory for convenience?
   - **Resolution:** No, direct `new DatabaseManager()` is clearer

2. How to handle existing `getStateManager()` calls in test files?
   - **Resolution:** Replace all with explicit `new StateManager(db)`

3. Any shared test setup utilities to update?
   - **Resolution:** Check during implementation if common setup extracted
