// Must run before tests/setup.ts, which imports @testing-library/react and so
// loads react-dom. react-dom evaluates its act-environment state at load, so
// IS_REACT_ACT_ENVIRONMENT has to be true before that import resolves;
// setting it inside setup.ts (after the import) left a window where React
// warned "the current testing environment is not configured to support act(...)"
// via console.error, which setup.ts treats as fatal — flaking CI. Listing this
// file first in vitest test.setupFiles guarantees the flag is set first.
Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
