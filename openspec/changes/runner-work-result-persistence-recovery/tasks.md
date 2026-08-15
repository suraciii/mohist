# Tasks: Runner Work-Result Persistence Recovery

- [x] Retain exact returned results in the journal after a recoverable local
      completion-write failure.
- [x] Gate admission and owner reporting until retained completions become
      durable at a successful control-plane poll boundary.
- [x] Preserve the historical `started` recovery fence across a process exit.
- [x] Add focused journal and RunnerHost regressions for deferred persistence,
      no early report, recovery reporting, and no re-execution.
- [x] Run format, Runner type checks, test-boundary checks, and focused Vitest.
