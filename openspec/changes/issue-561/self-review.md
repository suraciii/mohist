# Self-Review: issue-561

## Artifacts Reviewed

- `proposal.md` — issue motivation, acceptance criteria, capabilities, and impact
- `design.md` — source context, managed releases, identity contract, transaction state machine, risks, migration, and open questions
- `tasks.json` — five implementation slices, acceptance criteria, and dependency graph
- `specs/update-source-identity/spec.md`
- `specs/managed-runtime-artifacts/spec.md`
- `specs/update-runtime-consistency/spec.md`
- `specs/update-runtime-recovery/spec.md`

The current issue was read from the canonical Mohist CLI and cross-checked against the existing CLI update pipeline, Server web-update pipeline, service installers, runtime identity code, and focused update tests.

## Findings

### 1. P1 — The Server web-update endpoint remains an out-of-band mutator

`SystemRoutes.cs:14-29` exposes a separate `/api/system/update` mutation path. `SystemUpdateService.cs:449-482` still builds `dotnet build Mohist.sln` directly in `state.SourcePath` and restarts the configured Server unit; `SystemUpdateService.cs:169-201` later treats Server hash/readiness as sufficient to restart the Runner and complete the job. It does not publish a managed candidate, activate a `RuntimeTarget`, verify CLI/Server/Runner identities, or use the rollback transaction.

This conflicts with the new service contract in `design.md:78-88`: once units point at versioned managed artifacts, the web job's source-tree build either leaves the active artifact unchanged or becomes another source-bound deployment path. The design calls the web job a report-only projection in `design.md:27,152`, but neither `tasks.json:T-005` nor the migration plan disables this mutation or routes it through the same coordinator. A user can therefore still obtain a successful web update with the exact build/restart-versus-running-version ambiguity that issue 561 is intended to remove.

**Required revision:** Decide explicitly whether `/api/system/update` is disabled as a mutator or delegates to the same local transaction coordinator. Specify its status/outcome behavior and add a focused test proving it cannot report success without the same candidate activation and CLI/Server/Runner verification contract.

### 2. P1 — Durable transaction state has no crash-reconciliation or atomic multi-service activation contract

`design.md:47-53` introduces a durable transaction file, and `design.md:106-121` defines nonterminal activation states, but no component is assigned to read a nonterminal record after the CLI process crashes or the continuation never starts. The recovery spec covers an in-process verification failure and CLI continuation failure (`specs/update-runtime-recovery/spec.md:10-14,45-52`), not a process death between service-target writes, CLI-slot replacement, and verification. `tasks.json:T-004` likewise tests cancellation and continuation but has no crash/restart reconciliation criterion.

The risk is observable with the current service boundary: `SystemdServiceInstaller.cs:143-180` writes and installs one unit at a time. A crash after the Server unit or CLI slot changes but before the Runner target and transaction state are committed can leave a mixed target set. The next update has no specified recovery owner, and a lock alone would only reject work while preserving the half-update. That violates the issue acceptance criterion that failure cannot leave a non-runnable or source-ambiguous half-update.

**Required revision:** Define write-ahead ordering and atomicity for the complete service-target set, then define a reconciler/resume rule for every nonterminal transaction when `mo update` or the managed runtime starts. Add deterministic kill-point tests after each destructive boundary, including partial Server/Runner unit activation and CLI-slot replacement, proving restoration or no-verified-runtime cleanup.

### 3. P1 — CLI identity is not an executable contract

`design.md:90-100` says the validator will parse an embedded CLI informational version/source revision, but it does not define the output grammar, the embedded fields, or how `dotnet publish` writes the selected source revision and release ID. The current validator only checks that `mo --version` exits successfully and returns non-empty text (`packages/cli/Mohist.Cli/Update/RuntimeConsistencyValidator.cs:53-79`). The CLI project has static version properties and no source-identity property (`packages/cli/Mohist.Cli/Mohist.Cli.csproj:11-14`), while its existing local version reader strips the `+...` suffix (`packages/cli/Mohist.Cli/MohistCliApi.cs:784-805`). Existing update tests use arbitrary non-empty strings such as `1.0.0+oldhash` (`packages/cli/tests/Mohist.Cli.Tests/Update/UpdateVerifyRuntimeSpecs.cs:34-55`) without asserting that the hash is parsed or compared.

As written, a stale CLI can pass the proposed check, and two independently published CLIs can expose the same version without a verifiable source/release identity. This directly blocks acceptance criteria 2 and 3 and makes the required CLI part of `RuntimeIdentity` non-testable.

**Required revision:** Define one canonical CLI identity schema or exact machine-readable command, specify how the publish step embeds `sourceRevision` and `releaseId` into the executable, require missing/ambiguous fields to fail, and add tests for matching, stale, same-version/different-source, and missing-identity CLIs.

### 4. P1 — Component-specific update semantics are left ambiguous

The proposal says full and component-specific updates use the same target identity (`proposal.md:7-10`), but the consistency spec only says a component-specific update verifies the component it updates and components it activates or relies on (`specs/update-runtime-consistency/spec.md:10-22`). The design and tasks do not define whether `mo update server` must build and atomically activate a paired Runner release, may leave the existing Runner untouched, or must fail unless the whole CLI/Server/Runner set becomes consistent. The current implementation explicitly warns that Server-only updates do not refresh the Runner (`packages/cli/Mohist.Cli/Update/UpdateOperations.cs:377-390`), so this is a real behavior choice rather than a naming detail.

Different implementations can all satisfy the current task wording while producing different user-visible guarantees: one can report a Server-only success with a stale Runner, while another rebuilds all components and changes the scope and downtime of the command. That leaves the issue's “Server, Runner, and CLI use one version fact” acceptance criterion untestable for component commands.

**Required revision:** Add an explicit operation matrix covering `update`, `update cli`, `update server`, and `update runner`: artifacts built, service targets changed, identities required for success, rollback scope, and whether the result is globally consistent or component-scoped. Add matching positive and negative scenarios and task acceptance tests for each command.

### 5. P2 — A fixed source identity does not make the source input immutable

The design resolves `git rev-parse HEAD` once and passes the path/revision through the pipeline (`design.md:31-45`), while explicitly declining worktree watching (`design.md:20-27`). A clean worktree can still be changed or advanced by another process after preflight and before the Server and Runner builds. Both builds then read mutable files from the same path, while the release manifest can continue to label the artifacts with the earlier revision. The plan has no source lock, immutable checkout, build snapshot, or fail-closed revalidation when source content changes.

This permits two artifacts to carry one target identity while containing content that was not present at that identity, weakening the “sole version authority” guarantee. The issue's clean-source acceptance does not eliminate mutation during a multi-stage update.

**Required revision:** Choose and document an immutable input strategy: materialize the selected revision into a staging checkout, hold a source mutation lock, or revalidate a content/revision fingerprint at the build boundary and abort before activation on change. Add a deterministic source-mutation-during-build test.

## Issue Coverage Check

| Issue acceptance criterion | Planned coverage | Review status |
|---|---|---|
| Clean explicit root produces a matching Runner | Source identity, managed release, strict Runner readback | Blocked by the undefined identity schema and source mutability gaps (findings 3 and 5) |
| Server, Runner, and CLI share one version fact | Full-update consistency and release manifest | Blocked for CLI identity and component-specific scope (findings 3 and 4) |
| Success only after confirmed consistency | Strict validator and recovery state machine | Blocked by the out-of-band web mutator and missing crash reconciliation (findings 1 and 2) |
| Failure leaves no half-update | Candidate store and rollback | Blocked for process crash and partial service activation (finding 2) |
| Default and explicit roots show target/actual results | Source context and UpdateReport | Covered in principle by `specs/update-source-identity/spec.md:41-53` and `tasks.json:T-005`, subject to the execution gaps above |

## Verification Limits

- No product tests or full gates were run; this was a read-only plan review as requested.
- The findings are plan defects or missing contracts, not claims that the unimplemented change already fails in production.
- The task dependency graph itself is acyclic and its dependencies point to earlier priorities.

## Verdict

The plan is not ready to build. Findings 1-4 are P1 blockers because they leave an alternate mutating path, crash-state behavior, CLI identity, and component command guarantees undefined. Finding 5 is P2 but affects the core source-authority invariant and should be resolved before implementation.

<promise>FAIL</promise>
