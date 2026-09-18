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

## Capture and Preview

Run these commands as the operating-system user that owns the Runner service:

```text literal
mo runner environment capture [--runner-id <runner-id>]
mo runner environment status [--runner-id <runner-id>] [--json]
```

`capture` reads the current process environment, writes a candidate atomically,
and sends only its version, source, user, capture time, and changed variable
names to Server. It does not restart the service. If the metadata write fails,
the candidate remains local and inactive; `apply` is refused until the metadata
is published. `status` shows the active version, candidate version, application
state, and the latest sanitized tool observations.

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
mo runner environment check <executable> [-- <argument> ...]
```

The executable and arguments are passed as an argument vector. The command has
a bounded duration and no interactive input. The result reports the executable
name, resolved path, exit status, duration, and check time. Output is local by
default; only this sanitized observation may be sent to Server. A check is an
observation, not permanent capability evidence, Runtime readiness, or a project
test result.

## Web and Server Visibility

`GET /api/runners` and `GET /api/runners/{runnerId}` expose an `environment`
summary with the active version, candidate version, application state, source,
capture and load times, update id, failure code, and recent tool observations.
Raw values, full paths from the snapshot, credentials, and command output are
never returned. The Web detail page may show the summary, blockers, and a
cancel action. It must direct capture and apply to the local CLI; it must not
suggest that a remote browser can read a terminal environment.

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
environment version and load time, and poll observations retain current
process-generation settlement counts. Refresh, application, tool checks, and
the `environment` status projection remain target work for Issue #1009.
