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

A **Doctor check** is one deployment or configuration fact:

```text literal
DoctorCheck
  Name                     # canonical check name, see the inventory
  Status                   # ok | warn | fail
  Detail                   # evidence: what was observed
  NextAction               # correction for a non-ok check; null for ok
```

A check status means:

```text literal
ok    the fact is healthy
warn  the fact deserves attention, but the platform can still serve and
      execute work; warn never changes the process exit status
fail  the platform cannot satisfy the contract this check represents; fail
      sets the process exit status to 1
```

The canonical checks, in the order the Server returns them and the CLI
renders them:

| Name | Owner | Reads Project configuration |
| --- | --- | --- |
| `revision-alignment` | deployment | no |
| `migrations` | database | no |
| `model-catalog` | Runner runtime | no |
| `verification-command` | required Project configuration | yes |
| `project-verification-optional` | optional Project configuration | yes |

The platform checks (`revision-alignment`, `migrations`) and the Runner check
(`model-catalog`) never read Project configuration. Project verification is
reported independently from Server health and Runner readiness.

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
- `mo doctor` renders the five canonical checks in order. Each check prints
  its name, status, and detail; a `warn` or `fail` check also prints its next
  action. Exit status is 1 when any check is `fail`, and 0 otherwise, so a
  `warn`-only result exits 0.

### Project verification classification

A Project's verification command is required only when that Project can
execute a workflow. Otherwise the Project is optional. The classification is
a pure function of persisted facts:

```text literal
required(project) = hasActiveExecution(project) AND NOT explicitlyOptional(project)

hasActiveExecution(project) =
    EXISTS WorkflowRunRow
      WHERE MetadataProjectId = project.Id
        AND Status NOT IN ('completed', 'stopped')
```

- `Completed` and `Stopped` are the only terminal statuses. `Failed` is
deliberately not terminal because Retry/Rerun revive it, and a revived run
still needs the frozen verification command.
- The query filters the stored lowercase `WorkflowRunRow.Status` at the
  database layer and never deserializes run state.
- A Project with a verification command produces no finding in either Project
  check.
- A required Project without a command makes `verification-command` `fail`.
- An optional Project without a command makes `project-verification-optional`
  `warn`.
- Missing Projects are sorted by name with `StringComparer.Ordinal` before the
  detail is built, so the message is deterministic.

### Strict mode

Strict mode is a request-scoped flag, not a stored setting. `mo doctor
--strict` sends `strict=true`, and the Server then treats every Project
without a verification command as required:

```text literal
required(project, strict) =
    strict OR (hasActiveExecution(project) AND NOT explicitlyOptional(project))
```

- Default: only active, non-optional Projects fail.
- Strict: every Project without a command fails, restoring the
  all-invalid-configurations-fail behavior on demand.
- Strict mode never changes platform or Runner checks.

### Doctor API and CLI

- `GET /api/doctor/checks` (operator scope) accepts an optional `strict`
  query flag and returns the flat, always-present array of five checks in
  canonical order.
- Each check object carries `name`, `status`, `detail`, and `nextAction` in
  that key order. `status` is one of `ok`, `warn`, `fail`. `nextAction` is
  present for every non-`ok` check and `null` for `ok`.
- `mo doctor` rejects any status other than `ok`, `warn`, or `fail` as
  `invalid_response`.
- `--json` projects the same fields and discovers fields without a network
  request.

### Configuration

`Mohist:Doctor:OptionalProjects` is a string array of Project names or ids
that are explicitly optional (for example, fixtures). A matching Project is
treated as optional even when it has active execution. Names match
case-insensitively and ids match ordinally. An absent or empty value means no
explicit optional Projects. The configuration is read once per doctor
evaluation and never written.

## Status

Diagnosis assembly, the doctor check list, and both CLI commands are
implemented on the Server and the Go CLI. The dispatch snapshot store
persists one snapshot per WorkflowRun and WorkId.
