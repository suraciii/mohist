# Runner Execution Environment

Mohist tasks run in the environment of the managed Runner service, not in the
interactive terminal that installed a tool. This guide defines how an operator
captures the intended non-secret tool environment, previews and applies a
change, and verifies the environment that a Runner process actually loaded.

## Product Commitments

- A managed Runner captures a controlled environment snapshot when it is
  installed. The snapshot is host-local and does not contain credentials.
- The active snapshot is separate from a pending candidate. A candidate is not
  active until a new Runner process reports its version.
- A refresh stops new admission, waits for all owned work and result
  acknowledgements, and then restarts the Runner. It never interrupts work.
- Existing work keeps its original process environment. A refresh does not
  change a running process or retry a failed Workflow.
- The Server and Web UI show only sanitized metadata and observations. They do
  not read or display the raw environment.
- A failed or unconfirmed activation keeps the previous snapshot available for
  local recovery.

## Environment Snapshot

An **environment snapshot** is the small host-local set of values that the
Runner service loads at process start. Version is the SHA-256 digest of the
canonical `NAME=value` lines, in variable-name order.

The first version captures these variables:

- `PATH`
- `DOTNET_ROOT`
- `DOTNET_ROOT_X64`
- `GOROOT`
- `GOPATH`
- `JAVA_HOME`
- `ANDROID_HOME`
- `ANDROID_SDK_ROOT`
- `M2_HOME`
- `GRADLE_HOME`
- `NVM_BIN`
- `NVM_PATH`

`PATH` keeps the terminal order after duplicate removal. A path entry is
discarded when it is empty, relative, or under `/tmp/`, `/var/tmp/`, the
current user's `/run/user/<uid>/`, or the configured `TMPDIR`. Other variables
must contain one absolute path and no newline or NUL. The capture operation
does not run shell startup files and does not copy any other variable.

The active file is `~/.config/mohist/runner-environment.env`. Version one does
not follow `XDG_CONFIG_HOME`; the fixed path matches the managed systemd unit
and prevents a written-but-unloaded snapshot. The file is mode `0600`. The
existing `runner.env` file remains the owner of Runner application settings
such as `ENABLED_AGENT_RUNTIMES`; the two files must not be merged.

Older managed installations may not have this file or its systemd directive.
Run the explicit local migration before the first refresh:

```text literal
mo runner environment initialize
```

`initialize` measures the allowlisted environment of the currently running
Runner process through its systemd `MainPID`, writes the active snapshot, and
idempotently adds the fixed `EnvironmentFile` directive. For legacy units it
also removes inline `Environment=` assignments for the fixed allowlist, while
preserving unrelated service settings; this is required for a candidate to
remove a previously inherited variable. Ambiguous continued environment
directives fail closed. It does not use the invoking terminal, contact Server,
restart the Runner, pause admission, or change current work. It verifies the
process digest after `daemon-reload` and restores the unit and snapshot if any
step fails. A stopped service, missing process environment, or process identity
change fails closed. Existing active snapshots are verified rather than
replaced. Initialization is migration evidence only; candidate apply still
requires the normal fence and new-process activation witness.

The local transaction files are also fixed under `~/.config/mohist/`:

- `runner-environment.candidate.env` is the inactive candidate;
- `runner-environment.candidate.json` records only its version, variable names,
  and capture time;
- `runner-environment.previous.env` is the recoverable pre-apply snapshot; and
- `runner-environment-application.json` records the update id and the identity
  witness needed to resume or cancel a fence after the CLI exits.

These files are mode `0600`. Their contents never leave the host.

## Capture and Preview

Run these commands as the operating-system user that owns the Runner service:

```text literal
mo runner environment capture [--runner-id <runner-id>]
mo runner environment initialize
mo runner environment status [--runner-id <runner-id>] [--json]
```

`capture` reads the current process environment and writes the candidate and its
local metadata atomically. It does not restart the service or contact Server.
`apply` publishes the candidate version as the target of a Server environment
application; no raw values or candidate file contents are sent. `status` shows
the active version, candidate version, application state, and the latest
sanitized identity/settlement facts.

The preview identifies added, removed, and changed variable names. It never
prints values. A candidate with an invalid path or unsafe value remains
inactive and reports the validation error.

## Apply and Cancel

Apply an exact candidate version:

```text literal
mo runner environment apply --version <candidate-version> [--runner-id <runner-id>]
mo runner environment cancel --update-id <update-id> [--runner-id <runner-id>]
```

Only one environment application may exist for a Runner. The application has
these states:

```text literal
candidate -> waiting -> applying -> active
                    \-> cancelled
applying -> failed
applying -> unconfirmed
```

`waiting` begins a Runner update fence. Server rejects new claims for that
fence and returns the current owner rows plus the latest in-flight and awaiting
acknowledgement counts. The application is settled only when both the owner
ledgers are empty and the current process generation reports zero in-flight and
zero awaiting-acknowledgement work. A stale or disconnected observation is not
settled.

After settlement, the local manager saves the old snapshot, atomically installs
the candidate, and restarts the service. The application becomes `active` only
when the new process generation reports the target environment version. A
restart request, a systemd success response, or a Server heartbeat alone is not
activation evidence.

`cancel` is valid only in `waiting`. It discards the candidate application and
removes the fence created by that application. It does not clear another drain
or any unrelated admission condition. If the local command exits while waiting,
the Server fence remains; a later `status` or `cancel` operation must resolve it.
The system must not release a fence only because its initiating CLI process
disappeared.

The CLI retries the settlement command with a bounded wait. Each retry first
refreshes the current `(processGeneration, connectionGeneration)` identity;
stale or disconnected observations stop the transaction rather than being
treated as empty work. A successful systemd restart is followed by the same
identity refresh until a new process generation reports the target version.
The local application record is removed only after Server confirms activation
or a confirmed rollback.

If restart or confirmation fails, the manager restores the old snapshot and
tries one confirmation of the old version. A confirmed rollback records
`failed` and removes this application's fence. If rollback cannot be confirmed,
the state is `unconfirmed`; the fence remains until local recovery confirms an
environment and an operator resolves the failure.

Managed Mohist upgrades must preserve the active and previous snapshot files.
The updater must not regenerate them from the updater process's `PATH`.

## Tool Checks

Tool checks run locally, as the service user, with the candidate or active
snapshot, the Runner root as the working directory, and no shell. The command
surface is:

```text literal
mo runner environment check <executable> [--snapshot active|candidate] [--report] [-- <argument> ...]
```

The executable and arguments are passed as an argument vector. The command has
a bounded duration and no interactive input. The result reports the executable
name, resolved path, exit status, duration, and check time. Output is local by
default; `--report` explicitly sends only the sanitized observation to Server.
The default snapshot is `active`; `candidate` is an opt-in check of the local
inactive candidate. A check is an observation, not permanent capability
evidence, Runtime readiness, or a project test result.

The check is bounded to 10 seconds, accepts at most 16 arguments with a total
argument-vector size of 4096 bytes, and launches without a shell, stdin, or
captured output. It uses the selected snapshot, the Runner root as its working
directory, and the service user's identity. The local result has one of
`passed`, `failed`, `not-found`, `timed-out`, or `error` outcomes. A non-zero
tool exit is reported as `failed`; it does not make the CLI lose the diagnostic
result.

## Web and Server Visibility

`GET /api/runners` and `GET /api/runners/{runnerId}` expose an optional
`environment` summary. The first projection slice contains only the active
version and load time, plus durable application metadata: update id, phase,
target and previous versions, identity witnesses, request/completion times, and
a bounded failure code. The summary is available for an offline Runner from
durable state and does not activate the Runner grain. Active work and the
existing drain projection remain the source for tasks that block an update.

`POST /api/runner/{runnerId}/environment/observation` accepts an explicit,
sanitized observation report and `GET` returns the latest report. A report may
contain candidate metadata (`source`, operating-system `user`, capture time,
version, variable names, and name-only diffs), the process-generation
activation witness, and up to eight recent tool checks. Each tool check has
only the executable name, resolved executable path, selected snapshot kind and
version, bounded outcome, exit status, duration, and check time. Arguments,
environment values, credentials, and command output are rejected or discarded
at the boundary. Reports carrying a process generation are accepted only for
the currently registered generation; candidate-only reports may be retained
while the Runner is offline. The latest report is durable and survives a
Server restart without activating the Runner merely to render status.

The Web detail page may show this sanitized report alongside the active and
application summary and blockers, but capture, apply, and check execution
remain local CLI operations. The page must not suggest that a remote browser
can read a terminal environment or execute a command.

The Runner includes the active environment version and load time in registration
and heartbeat identity. The pair `(processGeneration, environmentVersion)` is
the activation witness. A new process with an old version is not the target
activation.

## Boundaries

This feature does not download or select SDK versions, install tools, execute
shell startup scripts, copy the full terminal environment, provide a toolchain
configuration language, or retry Workflow work. Project verification owns
project-specific version requirements. Managed release installation remains the
responsibility of Issue #971; this feature only preserves and updates the
Runner's host environment.

## Implementation Status

The install path now captures the fixed host allowlist into the managed
systemd environment file. Runner registration and heartbeat report the loaded
environment version and load time, and the identity read model exposes the
current process generation. Server owns one durable environment application per
Runner, keeps its fence across process replacement, and requires a
current-generation target-version witness before releasing that fence. The
local CLI now owns candidate capture, bounded apply/cancel, atomic
active/previous rotation, restart confirmation, rollback, and bounded local
tool checks. Server stores the explicit sanitized observation report and the
global projection/Web detail page render it without exposing raw values or
command output. The remaining acceptance work for Issue #1009 is the
end-to-end Go task scenario and live-runtime proof.
