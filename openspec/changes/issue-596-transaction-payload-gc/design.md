# Design: Managed Runtime Transaction Payload GC

## Current Gap

`ManagedRuntimeTransaction.CommitAsync` persists a verified pointer and leaves
the transaction state. The first Issue 596 implementation may remove the
current transaction's `snapshot`, `build`, and `candidate` directories after
launcher finalization, but historical transaction directories are never
examined. A runtime can therefore retain every old source archive and build
tree even though the immutable release is the only executable artifact.

## Decision 1: Lock the Whole Managed Update

`IFileSystem` exposes a best-effort `TryAcquireExclusiveLock` operation. The
real filesystem opens `<runtime-root>/.update.lock` with `FileShare.None`.
`ManagedRuntimeTransaction` acquires it after `UpdateSourceResolver` has
established the runtime root and holds the handle in `ManagedUpdateSession`
until `CommitAsync` or `RollbackAsync` completes. A failed Prepare releases it
before returning.

The lock serializes all new managed updates, including the interval between
candidate staging and final Commit/Rollback. Existing fake filesystems use the
default no-op handle so their behavior remains deterministic. If the real lock
cannot be acquired, Prepare fails closed for that update; the collector itself
never becomes a reason to fail an otherwise successful update.

## Decision 2: Payload-Only, State-Gated Collection

The collector receives the runtime root and the current transaction id. It
reads `active.json` and `verified.json` before scanning. If either pointer is
present but malformed, the whole collection pass is skipped. Their transaction
ids are protected even if their status is unexpected.

For each immediate child of `runtime/transactions`:

1. Skip the current id, protected pointer ids, symlink roots, missing state, and
   unreadable state.
2. Skip `candidate-staged`, `candidate-activated`, `recovery-failed`, and all
   unknown statuses.
3. For `verified` and `rolled-back`, remove only the exact child directories
   `snapshot`, `build`, and `candidate`, after rejecting symlink payload roots.
   The source snapshot is intentionally owner-read-only after extraction, so
   this operation uses an injected filesystem cleanup primitive that restores
   owner write permission only inside that exact payload root before deletion.
4. Keep the transaction directory and `state.json`, even when all payload
   directories are gone.

The scan never enters `releases`; release retention is a separate policy and
is deliberately out of this slice. A transaction without a durable state file
is treated as potentially active because `UpdateSourceResolver` creates its
payload directories before writing state.

The cleanup primitive does not run `chmod` against the runtime root or a release
root. On Unix it adds owner write permission to directories and files below the
exact payload root, without following symbolic links; on Windows it clears the
read-only attribute for the same bounded tree. If permission repair or deletion
cannot be proven, that payload is retained.

## Decision 3: Failure and Concurrency Semantics

The update lock prevents two current managed updates from changing pointers or
transaction state while collection runs. The state gate also makes a collector
safe around an older updater: a transaction that has not published a valid
terminal state is retained. Collection errors are logged and ignored. Deleting
one payload root failing does not authorize deleting another root or the state
record.

This does not claim to coordinate binaries that predate the lock if they are
actively mutating the same runtime. The conservative state checks prevent the
new collector from deleting their in-progress transaction, and the next
managed update takes ownership under the lock.

## Focused Verification

The CLI specs cover:

- old verified payload removal while pointers, state, and release stay;
- candidate and recovery payload retention;
- missing or malformed state retention;
- current/protected transaction retention; and
- lock release on Prepare failure and Commit/Rollback completion.

The tests use only the injected filesystem and activator. They do not inspect or
delete the installed live runtime.
