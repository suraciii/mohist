# Capability: Test Isolation

## Requirements

### REQ-1: Database Manager Instantiation
**The system MUST support explicit DatabaseManager instantiation.**

- DatabaseManager SHALL be instantiable via `new DatabaseManager(config)`
- Configuration options SHALL include:
  - `inMemory`: boolean - Use in-memory SQLite database
  - `dbPath`: string - Path to database file (optional, defaults to ~/.mohist/mohist.db)
- The global singleton pattern SHALL be removed
- No global functions (`getDatabase`, `resetDatabase`, `closeDatabase`) SHALL exist

**Acceptance Criteria:**
- [ ] `new DatabaseManager({ inMemory: true })` creates isolated in-memory database
- [ ] Multiple instances can coexist without interference
- [ ] TypeScript compilation passes without global database functions

### REQ-2: StateManager Dependency Injection
**The system MUST inject database dependency into StateManager.**

- StateManager constructor SHALL require a `DatabaseManager` instance parameter
- StateManager SHALL create all repositories using the provided database instance
- No global StateManager instance SHALL exist
- No global functions (`getStateManager`, `resetStateManager`) SHALL exist

**Acceptance Criteria:**
- [ ] `new StateManager(db)` creates manager with repositories bound to db
- [ ] StateManager uses injected database for all operations
- [ ] TypeScript compilation passes without global state manager functions

### REQ-3: Production Initialization
**The production server MUST explicitly wire dependencies.**

- Server startup SHALL:
  1. Create `DatabaseManager` instance
  2. Pass to `StateManager` constructor
  3. Use `stateManager.getXxxRepo()` for all service initialization
- No implicit global database or state manager SHALL be accessed

**Acceptance Criteria:**
- [ ] Server starts successfully with explicit dependency wiring
- [ ] All repositories use the same database instance within a server instance
- [ ] No global variable access in production code path

### REQ-4: Test Isolation
**Each test suite MUST have complete database isolation.**

- Tests SHALL create their own `DatabaseManager` instances
- Tests SHALL create their own `StateManager` instances
- Tests SHALL NOT share database connections with other tests
- Tests SHALL clean up resources in `afterEach` blocks

**Acceptance Criteria:**
- [ ] Tests run successfully with `vitest --run` (parallel execution)
- [ ] No "UNIQUE constraint failed" errors from parallel test execution
- [ ] No connection errors from closed databases
- [ ] All 24 test files updated to use DI pattern

### REQ-5: Backward Compatibility
**External APIs SHALL remain unchanged.**

- HTTP API endpoints SHALL behave identically
- CLI commands SHALL behave identically
- Database schema SHALL remain unchanged
- Configuration files SHALL remain unchanged

**Acceptance Criteria:**
- [ ] All existing API tests pass without modification
- [ ] All existing CLI tests pass without modification
- [ ] Integration tests pass without modification

## Test Strategy

### Unit Tests
- Each service/repo test creates isolated database
- Verify constructor injection works correctly
- Verify no global state pollution between tests

### Integration Tests
- Server startup test with DI
- Full request lifecycle test

### Regression Tests
- Run full test suite 10 times to verify no flakiness
- Verify parallel execution speedup
