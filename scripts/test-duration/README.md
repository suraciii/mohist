# Test Duration Gate

The test-duration tool gives `npm run verify` one bounded, inspectable local
acceptance run. It proves report freshness, test totals, duration limits, and
process cleanup against one exact source revision.

## Run Identity And Evidence

Each invocation owns a unique directory outside the repository. An explicit
artifact root is only a parent; every run still creates a unique child. A
report-only check reads an existing run and never creates, removes, or refreshes
evidence.

The gate records the exact `HEAD` and requires a clean index and worktree. It
checks source identity before and after the build and after duration execution.
Every report, log, and temporary directory belongs to the same run. Before a
lane starts, the gate creates its report parent and
removes the declared report target. A passing lane exits zero and writes a
fresh, non-empty report at that path.

The run directory is retained on success, ordinary failure, and deadline
failure. It contains:

- `run.json` and `build-stamp.json` provenance;
- `plan.json` with selected tracks, dependencies, and resource claims;
- raw lane logs and reports; and
- `summary.json` with every lane result, parsed totals, cleanup state, deadline
  state, and the first failure.

Failure to write the plan or final summary fails the gate.

## Execution Graph

The documentation check runs first. The fresh build and read-only structural
checks then run as independent siblings. Failure or cancellation of one aborts
the other. Only after both succeed and source identity is revalidated does the
gate write the matching build stamp.

Duration-measurement lanes run in configured order. Bounded throughput lanes
follow the measurement barrier. Shared report evaluation finishes the run.

## Deadline And Cleanup

One absolute 300-second deadline starts before the build and covers every phase,
process-tree cleanup, report parse, and summary write. It is never reset for a
later phase. Scheduling reserves enough time for termination grace and final
reporting and does not admit a lane after the execution cutoff.

On the first lane failure, external interrupt, or deadline, the scheduler stops
admitting work, terminates process trees, and waits only within the same absolute
deadline. POSIX uses the owned process group. Windows uses the owned process
tree. A failed spawn or failed tree termination cannot be reported as clean
convergence. Evidence from completed lanes is never deleted.

The final report distinguishes the triggering failure from cancelled and
not-started lanes. A missing, stale, empty, failed, skipped, or not-run report is
a failure, not a green omission.

## Scheduling And Resources

The configuration declares host, .NET, and Node capacities. A lane starts only
when its dependencies and every claimed resource are available.

Each lane owns its temporary and runtime IPC directories, database, telemetry
database, logical endpoint scope, and report path. Fixtures use Orleans
in-memory transport and never probe or bind host ports. Node TypeScript checks
run through `node --import tsx` without a shared IPC server.

The duration-measurement tracks claim an exclusive measurement resource and run
in deterministic order. Throughput lanes begin after the measurement barrier.
A focused `--track` run does not add hidden prerequisite tracks.

## Host-Exclusive Duration Evidence

Resource claims coordinate only one gate invocation. They do not reserve CPU,
I/O, ports, or scheduling from another worktree or direct test apphost. Valid
duration evidence therefore requires an external host lease with no competing
Mohist build, Server Spec host, or comparable CPU and I/O test process.

The gate does not scan for, wait for, retry around, or terminate foreign
processes. Evidence collected without host exclusivity remains useful failure
data but cannot justify a performance baseline or threshold change.

## Duration Policy

Every configured track must report a nonzero total. Failed, skipped, and not-run
cases fail. Enforced tracks retain their configured p95 and single-test limits.
`baseline-pending` requires an explicit non-empty reason and remains governed by
the deadline.

Retry, sleep, skip, allowlist expansion, threshold change, timeout increase, and
global serialization are not gate recovery mechanisms.

## Platform Rules

On Windows, the gate resolves npm through the current Node executable and does
not pass a `.cmd` file directly to `CreateProcess` or enable a shell. Missing
npm identity fails before child admission. On every platform, compiled test
reporters produce the canonical reports.
