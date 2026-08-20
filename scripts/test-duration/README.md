# Test Duration Gate

The test-duration executor gives `npm run verify` one bounded, inspectable local
acceptance run. It also produces application and Repository evidence for CI and
aggregates that evidence for Gate. Every mode uses one canonical plan and proves
report freshness, test totals, duration limits, source identity, and process
cleanup for its declared scope.

## Run Identity And Evidence

Each execution scope owns a unique directory outside the repository. An
explicit artifact root is only a parent; every scope still creates a unique
child. Gate reads completed scope evidence and never creates, removes, or
refreshes a producer report.

The executor records the exact `HEAD`, plan identity, and selected scope. A
local `verify` run requires a clean index and worktree. A CI producer records the
checked-out revision. Each producer checks source identity before and after its
build and after Spec execution. Every report, log, and temporary directory
belongs to the same scope run. Before a lane starts, the executor creates its
report parent and removes the declared report target. A passing lane exits zero
and writes a fresh, non-empty report at that path.

The scope directory is retained on success, ordinary failure, and deadline
failure. It contains:

- `run.json` and `build-stamp.json` provenance;
- `plan.json` with the plan identity, selected owner, tracks, dependencies, and
  Resource claims;
- raw lane logs and reports; and
- `summary.json` with every lane result, parsed totals, cleanup state, deadline
  state, and the first failure.

Failure to write the plan or final summary fails the scope. Gate fails when a
required scope directory or final summary is missing.

## Execution Graph

The canonical plan declares one scope for each application and one Repository
scope. The current applications are Server, Web, CLI, Runner, and Slack. A
local `verify` run executes all six scopes and aggregates their evidence. CI
executes the same scopes as independent jobs and runs Gate after every producer
job completes.

An application scope builds its application once from fresh outputs. Its L0,
L1, application Architecture, and owned static-check lanes use that build. The
Repository scope runs plan validation, cross-application and repository
Architecture tracks, documentation, formatting, and repository-wide static
checks. A scope may run independent lanes concurrently when the plan proves
their output and Resource isolation. Local `verify` prepares all application
builds first so shared Server/CLI project-reference outputs cannot be written
while another scope is executing; the prepared application test scopes then
fan out together. CI jobs have separate runners and do not need this local
build barrier.

Gate validates producer source and plan identity, scope completeness, unique
track ownership, reports, budgets, cleanup, and the canonical first failure. It
does not build or run a Spec.

## Deadline And Cleanup

One absolute 300-second deadline covers the complete local `verify` run,
including every scope, process-tree cleanup, report parse, and summary write. It
is never reset for a later scope or phase. Each CI producer has a plan-defined
scope deadline that covers its build, Specs, cleanup, and evidence write. An
outer CI timeout must include setup overhead and exceed the internal scope
deadline. It does not relax that deadline.

On the first scope or lane failure, external interrupt, or deadline, the local
scheduler stops admitting work, terminates its owned process trees, and waits
only within the same absolute deadline. A CI producer applies the same rule
inside its scope. POSIX uses the owned process group. Windows uses the owned
process tree. A failed spawn or failed tree termination cannot be reported as
clean convergence. Evidence from completed scopes and lanes is never deleted.

The final report distinguishes the triggering failure from cancelled and
not-started lanes. A missing, stale, empty, failed, skipped, or not-run report is
a failure, not a green omission.

## Scheduling And Resources

The configuration declares capacity for each execution host. A lane starts only
when its dependencies and every claimed Resource are available. Local
application scopes may overlap only when their build outputs and Resources are
independent. CI application jobs use separate hosts and may run concurrently.

Each lane owns its temporary and runtime IPC directories, database, telemetry
database, logical endpoint scope, and report path. Fixtures use Orleans
in-memory transport and never probe or bind host ports. Node TypeScript checks
run through `node --import tsx` without a shared IPC server.

Duration-measurement tracks claim an exclusive measurement Resource on their
host and run in deterministic order. Throughput lanes begin after the
measurement barrier. CI selects only a complete application or Repository
scope. It cannot select a project, track, class, or test case.

## Host-Exclusive Duration Evidence

Resource claims coordinate only one local invocation or CI producer. They do
not reserve CPU, I/O, ports, or scheduling from another worktree or direct test
apphost. Valid duration evidence therefore requires an external host lease with
no competing Mohist build, Server Spec host, or comparable CPU and I/O test
process.

The executor does not scan for, wait for, retry around, or terminate foreign
processes. Evidence collected without host exclusivity remains useful failure
data but cannot justify a performance baseline or threshold change.

## Duration Policy

Every configured track must report a nonzero total. Failed, skipped, and not-run
cases fail. Every track must enforce its configured p95 and single-Spec limits.
An allowlist or `baseline-pending` state is invalid in complete acceptance
evidence.

Retry, sleep, skip, allowlist expansion, threshold change, timeout increase, and
global serialization are not gate recovery mechanisms.

## Migration Status

The executor validates the checked-in application plan and exposes the closed
`test:fast`, `test:app`, and portfolio commands. Each application builds once
and passes the same run root to the track guard. The Repository executor runs
the declared repository checks, Gate validates the six scope evidence bundles
without rerunning tests, and local `verify` runs those scopes under one
absolute deadline before applying Gate validation. Obsolete public aliases are
removed; new Specs are added by extending the canonical plan.

## Platform Rules

On Windows, the executor resolves npm through the current Node executable. It
does not pass a `.cmd` file directly to `CreateProcess` or enable a shell.
Missing npm identity fails before child admission. On every platform, compiled
test reporters produce the canonical reports.
