## Why

`mo update --repo-root <path>` can currently report a successful build and restart while the managed Runner is still executing a stale artifact or code from another source directory. This makes an explicit-source deployment untrustworthy: the selected repository must be the sole version authority, and success must mean that the running Server and Runner have both confirmed that version.

## What Changes

- Resolve one update source identity from the explicit or default repository root and carry it through CLI self-update continuation, Server build, Runner build, service activation, and runtime readback.
- Build and install Server and Runner artifacts from that source into stable, versioned managed runtime locations. Service units switch to absolute active artifacts instead of depending on an arbitrary worktree or relative build output. **BREAKING** for installations or custom units that rely on source-bound working directories and relative entrypoints.
- Make the full update and component-specific updates use the same target identity. The full update coordinates the CLI, Server, and installed Runner so they cannot silently build one version and run another.
- Verify the running identities against the target source/artifact before declaring success. Build or restart success alone is insufficient; a mismatch or unavailable required identity fails the update and emits no success result.
- Treat activation and verification as a recoverable update transaction. Preserve the last verified runtime and service target on failure, stop or remove an unverified candidate when no verified version exists, and report the expected version, observed version, and an actionable recovery result.
- Keep default-root and explicit-root updates distinguishable in human output and dry-run previews. This change does not alter Agent model selection, provider fallback, or inference configuration.

## Capabilities

- `update-source-identity`: The selected `--repo-root` and its resolved source identity are authoritative for the entire update, including CLI continuation, Server, Runner, and target/actual reporting.
- `managed-runtime-artifacts`: Server and Runner builds are installed as stable versioned artifacts, and managed service targets activate those artifacts without reading an implicit source worktree.
- `update-runtime-consistency`: Running CLI, Server, and Runner identities are checked against the target before success; mismatches fail the update rather than being downgraded to a warning.
- `update-runtime-recovery`: Candidate activation, verification failure, rollback to the last verified version, no-verified-version cleanup, and actionable recovery output form one bounded failure contract.

## Impact

- **CLI (`packages/cli`):** update orchestration and context, source/build operations, runtime consistency checks, Runner refresh results, service installation options, managed runtime paths, and their fake-boundary tests.
- **Server (`packages/server`):** runtime identity production and readback used to confirm the installed Server artifact; existing update/status surfaces may gain the artifact facts needed for exact verification, without changing workflow or Agent domain state.
- **Runner (`packages/runner`):** build manifest and startup/connection identity so the running process can be matched to the selected source artifact; Runner execution and model behavior remain unchanged.
- **Managed services and local filesystem:** systemd unit generation, versioned runtime storage, activation links, and rollback state change from source-bound execution to verified installed artifacts. No external dependency is required.
- **Documentation and tests:** self-host/update guidance and deterministic tests covering default and explicit roots, identity mismatches, successful activation, and recovery paths.
