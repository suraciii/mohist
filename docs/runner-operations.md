# Runner Operations

## Starting Runner

```bash
mo install runner --repo-root "$PWD"  # First registration and start
# Later
mo service start runner
```

Managed `mo install` services are supported on Linux user-systemd only. During development on other platforms, use `npm run dev:server` and `npm run dev:runner`.

The first installation requests a one-time enrollment from the running Server.
Runner exchanges it for a machine credential and stores that credential under
its root. Later starts reuse the credential.

Runner connects to `http://localhost:3456` by default, registers its capacity and
capabilities, and waits for Server assignments. Start Runner after Server;
Runner cannot connect while Server is unavailable.

## Debugging Runner

### Runner Logs

```bash
mo service logs runner          # Operational logs from service-manager
# Or inspect stdout from the Runner process directly
```

### Execution Logs for One Issue

```bash
mo issue logs <number>
mo issue events <number>             # Event stream
mo session list --issue <number>     # AgentSessions for the Issue
```

### Common Runner Problems

- **Presence is offline or stale:** Read `mo runner status` and follow its
  Server-provided start or re-enrollment action. Do not infer process state from
  a disconnected control channel alone.
- **Control is disconnected:** The Runner process may still be present while
  the current control lease is unavailable. Restore the connection using the
  reported action; do not collapse this into an offline claim.
- **Admission is blocked, draining, or capacity is full:** Read the reason
  codes, drain identity, configured slots, and active owners. Wait for the
  Server-provided action; do not cancel work from a status read.
- **An Issue waits after starting:** It may be waiting for eligible global
  Runner capacity or Runtime readiness. The status projection identifies which
  fact blocks fresh work.
- **A task produces no output:** OpenCode may be stuck. Run
  `mo run pause --issue <number>` and inspect logs.
- **Workspace identity error:** Preserve required commits, remove the Workspace,
  and retry after a manual marker, branch, or origin change.
- **Git push failed:** Configure an SSH key or token with permission for the
  remote Repository.

`mo service start runner` preserves enrolled managed-service configuration. Use
`npm run dev:runner` only from a source checkout for development.

## Self-hosting

For a long-running Runner managed as a service instead of foreground
`dev:runner`, see [Self-hosting](self-host.md).
