# Run Diagnostics

Explaining a failed WorkflowRun must be one request. The Server assembles
the facts; a CLI renders them. This spec defines the two read-only
diagnostics surfaces: `mo run why` and `mo doctor`.

## Design Drivers

- Failure triage today joins Run state JSON, task logs, dispatch payloads,
  and Runner registries by hand. Issue #655's triage needed all four; no
  surface answered the question. Diagnosis cost must not scale with fact
  dispersion.
- A diagnosis is a read model over existing facts. It adds no stored
  resource, no write path, and no lifecycle of its own.
- Operator-facing paths are logical. Process-scoped Runner internals, such as
  directory-handle paths, never appear in diagnosis output.
- The assembly contract is language-independent, so the Go CLI migration
  consumes it unchanged.

## Model

A **Diagnosis** is assembled for one WorkflowRun:

```text literal
Diagnosis
  Failure                  # run failure: reason, stage, task id, error
  Tasks[]                  # the failed task first, then its stage tasks
    TaskId, Attempt, Uses
    RenderedWith           # with-input after template rendering
    Workspace              # logical path, named | fallback, branch
    ExitCode, Error
    Recovery               # handler applied, budget remaining
  Dispatch                 # the persisted dispatch snapshot (payload freeze)
  Events                   # bounded recent run-event window
```

A **Doctor check** is one deployment fact:

```text literal
DoctorCheck
  Name                     # revision-alignment | migrations |
                           # verification-command | model-catalog
  Status                   # ok | fail
  Detail, NextAction
```

## Semantics

### Assembly

- `GET /api/runs/{ref}/diagnosis` assembles the Diagnosis from run state,
  task attempts, the dispatch snapshot, and run events. It is read-only and
  serves live and terminal runs alike.
- Every task dispatch persists a dispatch snapshot on first issue
  (first-writer-wins). A snapshot lives as long as its Run and is deleted
  with it. A diagnosis reports `dispatch: missing` when no snapshot exists;
  that outcome is a defect signal, not normal state.
- Workspace identity in a diagnosis is the logical Workspace path and
  binding kind (`named` or `fallback`). Directory-handle paths and other
  process-scoped internals are mapped to the Workspace identity before
  rendering.

### Rendering

- `mo run why <run>` renders the Diagnosis. The default view is the failure
  chain; `--json` selects fields. Exit status is 0 whenever a diagnosis is
  rendered; an unresolvable Run reference is the only error.
- `mo doctor` evaluates the check list against the connected Server and
  local services. `revision-alignment` compares CLI, Server, Runner, and
  Slack against one revision. `verification-command` reports Projects whose
  built-in Profile runs would fail closed for a missing command.
  `model-catalog` reports runtimes whose discovered catalog is empty or
  incomplete. A failing check prints its next action; exit status is 1 when
  any check fails.

## Status

- The dispatch snapshot store records no rows on the live deployment.
  Diagnosing and closing that persistence gap is part of the first
  implementation slice.
- Diagnosis assembly, the doctor check list, and both CLI commands are
  unimplemented.
