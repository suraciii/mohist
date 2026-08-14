## Context

`ManagedRuntimeTransaction` already stages a release and writes its candidate
runtime target set before validation. CLI targets have an immutable runtime
identity, but the stable `mo` entrypoint is outside that target set. Therefore
the runtime pointer can advance without changing the executable that users run.

## Decision

The managed transaction owns a small `ManagedCliLauncher` collaborator.

For a CLI-containing scope:

1. Resolve the default target to the managed stable launcher path. An explicit
   `--cli-path` remains the requested stable entrypoint.
2. Publish the candidate and persist the candidate target set as today.
3. If the stable entrypoint does not already delegate to the exact candidate
   runtime identity, copy its existing contents to the transaction directory,
   write an executable shell launcher to a temporary file, and atomically move
   that file over the stable entrypoint.
4. Invoke the stable entrypoint with `--version`. Success requires the
   candidate source revision in its output; merely starting an executable is
   insufficient.
5. Commit active, verified, and transaction pointers, then discard the backup.

The launcher state is held only in the in-process managed update session. The
backup is durable at `transactions/<id>/cli-launcher.previous`, so rollback can
restore the exact prior direct executable or launcher before returning failure.
The real filesystem implementation preserves the copied file mode so a direct
ELF remains executable after restoration.

## Failure Semantics

- Launcher write or permission failure restores the existing launcher and the
  preceding active target set.
- Candidate identity verification failure invokes the same rollback path.
- Pointer commit failure invokes the same rollback path before reporting
  failure.
- If launcher restoration itself cannot be proven, the managed transaction
  keeps its existing fail-closed recovery record and does not emit success.

## Alternatives Considered

- Keep `PATH` resolution as the default activation target: rejected because it
  can select an arbitrary stale executable rather than the stable managed
  entrypoint.
- Validate only the runtime-identity file behind the launcher: rejected because
  it cannot prove that the path users execute reaches that file.
- Make launcher activation a separate post-commit operation: rejected because
  it recreates the pointer-new/entrypoint-old mixed state on a crash or error.
