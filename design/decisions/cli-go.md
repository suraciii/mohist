# CLI in Go

Status: accepted

## Problem

The C# CLI requires a .NET runtime on every host and updates through a
source build of the whole repository. Runtime-root discovery is a recurring
defect class, and each managed update spends a full build. The repository
validated the Go toolchain, install, and update paths with the Slack adapter
port; the CLI shares that operational shape (single binary, service-agnostic
install, self-update) while remaining the control-plane client and the
deployment bootstrap (`mo update`).

## Decision

The CLI becomes a static Go binary in `packages/go/mohist-cli`. The
behavioral contract in [`../cli.md`](../cli.md) and
[`../../docs/cli-reference.md`](../../docs/cli-reference.md) does not change:
command tree, flags, field-selecting JSON output, exit codes, credential-file
format, and Skill assets are preserved. Migration is phased by command group;
the C# implementation is deleted at cutover.

The Go CLI is a thin client. It duplicates no domain model: workflow Profile
validation delegates to the Server, and local checks stay limited to what the
CLI itself owns (argument shape, file encoding, credential files).

## Alternatives considered

**Keep the C# CLI.** No port risk, but every host keeps the runtime
dependency, managed updates keep the source-build cost, and the shared
`Mohist.Workflow.Definition` assembly keeps the CLI coupled to Server build
cycles.

**Generate a typed client instead of a port.** Fixes contract drift without a
language change but leaves the runtime dependency and the update cost
untouched.

## Consequences

- Field-selecting JSON output and exit codes are the hard parity contract.
  Golden contract tests recorded from the C# implementation gate each group
  before it migrates. Table rendering approximates; it is not a contract.
- Managed update publishes Go artifacts per revision; the binary self-replaces
  atomically. Exact-revision deployment alignment is unchanged.
- The first Go slice carries the new diagnostics commands
  ([`../diagnostics.md`](../diagnostics.md)) so the new skeleton is exercised
  by production use early.
- Skills, docs, and agent instructions that call `mo` require no change.
