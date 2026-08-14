## Context

`ManagedRuntimeTransaction` already stages a release and writes its candidate
runtime target set before validation. CLI targets have an immutable runtime
identity, but the stable `mo` entrypoint is outside that target set. Therefore
the runtime pointer can advance without changing the executable that users run.

## Decision

The managed transaction owns a small `ManagedCliLauncher` collaborator.

For a CLI-containing scope:

1. Resolve the default target to the managed stable launcher path. An explicit
   `--cli-path` must name an existing absolute entrypoint and remains the
   requested stable entrypoint. A missing or relative explicit path is rejected
   before source staging, so the update cannot report success for a path that
   was never a real invocation target.
2. Publish the candidate and persist the candidate target set as today.
3. If the stable entrypoint does not already delegate to the exact candidate
   runtime identity, copy its existing contents to the transaction directory,
   write an executable shell launcher to a temporary file, and atomically move
   that file over the stable entrypoint.
4. Invoke the exact activated entrypoint with `--version`. Success requires
   the candidate source revision in its output; merely starting an executable
   is insufficient. This means an explicit `--cli-path` is both activated and
   identity-verified at the path the caller named.
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

## First Deployment

An installation running a pre-change CLI cannot self-apply this transaction:
that binary routes `mo update cli` through the old behavior. The supported
bootstrap is executable from the source checkout:

```bash
bash scripts/install-mo.sh
mo update cli
```

The script publishes the current CLI and places it at the stable user path
before the managed flow is used. `npm run install:cli` remains the documented
initial tool installation path; it is not described as a legacy update escape
hatch.

## Alternatives Considered

- Keep `PATH` resolution as the default activation target: rejected because it
  can select an arbitrary stale executable rather than the stable managed
  entrypoint.
- Validate only the runtime-identity file behind the launcher: rejected because
  it cannot prove that the path users execute reaches that file.
- Make launcher activation a separate post-commit operation: rejected because
  it recreates the pointer-new/entrypoint-old mixed state on a crash or error.
