# Self-hosting

Mohist is a self-hosted product. This guide covers long-running deployment,
startup at boot, remote access, backup, and upgrade.

## Deployment Modes

Server supports two long-running deployment modes. In both modes, **Runner runs
on the host** because it operates Git Repositories and invokes shell tools such
as OpenCode, Git, and `gh`. Runner is not part of the Server container. When
Slack Agent Connections are used, the optional `mohist-slack` managed service
also runs on the host. It makes outbound connections to Slack and Server and
needs no public inbound port.

Server enables resource-bounded observation by default. Traces are retained for
up to 72 hours within a 1 GiB observation storage budget. The OTLP receiver
listens only on `localhost:4318`, and default deployment does not publish port
4318. Run `mo otel status` to inspect `healthy`, `degraded`, or `off` state. To
disable observation completely, set `Mohist:Otel:Enabled=false` and restart
Server.

| Mode | Use when | Behavior |
|---|---|---|
| **systemd** | A Linux host such as a NUC, NAS, VPS, or laptop | Native processes. `mo install` creates units, enables startup, and can install Runner too. |
| **Docker** | Docker is available and isolation or easy migration is preferred | Server runs in a container with all state in a mounted volume. Runner connects from the host. |

Choose one mode. Remote access, backup, and upgrade sections cover both.

| Scenario | Recommended mode | Guide |
|---|---|---|
| Local development or trial | Development scripts: `dev:server`, `dev:runner`, and `dev:web` | [Getting Started](getting-started.md) |
| Daily laptop use | systemd as a local daemon | [Local daemon](#local-daemon) |
| Always-on home server or NAS | systemd or Docker | [Always-on server](#always-on-server-or-nas) or [Docker](#docker-mode) |
| Remote VPS | Either mode with a reverse proxy or VPN | [Remote Access](#remote-access) |

## systemd Mode

The `mo` CLI installs Server, optional Runner, and optional Slack adapter as
systemd user services.

### Local Daemon

This mode supports daily laptop use and starts at boot.

#### Linux

First complete the build and CLI installation in
[Getting Started](getting-started.md), including `npm ci`, `npm run build`, and
`npm run install:cli`.

```bash
# Install, enable, restart, and enable user lingering.
mo install server
mo install runner

# Install only when Slack Agent Connections are used.
mo install slack
```

The core units are `mohist.service` for Server and `mohist-runner.service` for
Runner. Slack adds `mohist-slack.service`. `mo install` enables and restarts the
units and runs `loginctl enable-linger`, so they run before login and after
logout.

The Slack service reads the local operator credential from the protected
`~/.mohist/operator-token` file and does not copy its contents into the unit. If
the installation command explicitly supplies `MOHIST_OPERATOR_TOKEN`, that
value is written as explicit installation configuration. Otherwise,
`MOHIST_OPERATOR_TOKEN_PATH` points to the protected systemd credential file.
Set `MOHIST_OPERATOR_TOKEN_PATH` during installation to select another
protected source file.

Common operations:

```bash
systemctl --user status mohist mohist-runner
systemctl --user restart mohist             # Prefer: mo update server
journalctl --user -u mohist -f              # Follow Server logs

mo service status slack
mo service logs slack -f
```

Prefer `mo update server` and `mo update runner` for managed-service restarts.
Do not start a second instance with `dotnet run`; changing Runner identity can
break sticky Workflow assignment.

#### macOS

`mo install` does not support macOS. The CLI currently supports Linux systemd
and Windows Scheduled Tasks. During development, use `npm run dev:server` and
`npm run dev:runner`, or create a launchd property list.

#### Windows

```bash
mo install server
mo install runner

# Install only when Slack Agent Connections are used.
mo install slack
```

The CLI creates platform-specific Scheduled Tasks that start at login.

### Always-on Server or NAS

Use an always-on NUC, NAS, mini PC, or older laptop for unattended operation.

1. Install .NET 11 SDK, Node.js 22.19.0 or later, and OpenCode according to its
   official documentation.
2. Build Mohist:

   ```bash
   git clone <mohist-repository-url> /opt/mohist
   cd /opt/mohist
   npm ci
   npm run build
   npm run install:cli
   ```

3. Create a dedicated user, which is recommended:

   ```bash
   sudo useradd -m -s /bin/bash mohist
   ```

4. Install user services as that user:

   ```bash
   sudo -u mohist mo install server --repo-root /opt/mohist
   sudo -u mohist mo install runner --repo-root /opt/mohist

   # Install only when Slack Agent Connections are used.
   sudo -u mohist mo install slack --repo-root /opt/mohist
   ```

Mohist installs systemd **user** services, not system services. A dedicated user
with lingering, enabled automatically by `mo install`, provides startup at boot
and always-on operation.

Connect from a laptop with SSH port forwarding:

```bash
ssh -L 3456:localhost:3456 mohist@your-server
# Then open http://localhost:3456 locally.
```

#### Remote Repository Access

Runner needs either an SSH key with push access or an HTTPS token:

```bash
sudo -u mohist ssh-keygen -t ed25519
# Add the public key as a deploy key in GitHub or GitLab.
```

Operational constraints:

- The Runner user must be able to push to the base branch.
- Each Issue workspace consumes disk space. Reclaim stale Git worktree metadata
  with `git worktree prune`.
- Server and Runner listen on localhost by default. Use a reverse proxy or VPN
  for remote access.

## Docker Mode

Use a container when the host should not install .NET SDK or when isolation and
migration are important. The Web application is built into the image. All
Server state is stored in a mounted volume. Runner remains on the host and
connects to Server over HTTP.

### Build

The root `Dockerfile` builds the Web application with Node, publishes Server
with .NET, and runs it on the ASP.NET runtime image.

```bash
git clone <mohist-repository-url>
cd mohist
docker build -t mohist-server .
```

The image uses the .NET 11 preview runtime from
`mcr.microsoft.com/dotnet/nightly/aspnet:11.0-preview`, matching `global.json`.

### Start

Run one container with a named volume:

```bash
docker run -d \
  -p 3456:3456 \
  -v mohist-data:/data \
  --name mohist \
  --restart unless-stopped \
  mohist-server
```

Or use the repository `docker-compose.yml`:

```bash
docker compose up -d
docker compose logs -f
```

Verify that `curl http://localhost:3456/api/health` returns a response beginning
with `{ "status": "ok"`.

### Persistent Data

The image sets `HOME=/data`. Server resolves all state under `$HOME/.mohist/`,
including `mohist.db`, `otel.db`, `attachments/`, `artifacts/`, and
`system-update.json`. A volume mounted at `/data` therefore contains all Server
state.

```bash
docker volume inspect mohist-data
```

Stop Server before a file-level volume backup so SQLite and related files form
one consistent snapshot:

```bash
docker compose stop
docker run --rm -v mohist-data:/d -v "$PWD":/backup alpine \
  tar czf /backup/mohist-data-$(date +%Y%m%d).tgz -C /d .
docker compose start
```

For restore, stop Server and extract into the intended empty restore volume
before starting Server against that volume.

```bash
docker compose stop
docker run --rm -v mohist-data-restored:/d -v "$PWD":/backup alpine \
  tar xzf /backup/mohist-data-YYYYMMDD.tgz -C /d
# Point compose at mohist-data-restored, then start it.
docker compose up -d
```

For host-visible files, replace the compose named volume with `./data:/data` and
make the directory owned by container user 1001:

```bash
sudo chown -R 1001:1001 ./data
```

### Connect Runner

Install Runner on the host as described under
[Always-on Server or NAS](#always-on-server-or-nas), then point it at the
container:

```bash
SERVER_URL=http://localhost:3456 RUNNER_ID=my-runner npm start
```

Set `RUNNER_ID` explicitly. Container hostnames and networks can change. A
default identity based on hostname can drift and break sticky Workflow
assignment.

### Pi Provider Retry Policy

Runner validates two optional settings before it claims work. The default
policy treats quota, balance, billing, and usage-limit messages as terminal
failures and permits five consecutive provider retries. Additional patterns are
JSON regular-expression strings appended to the defaults. The threshold must be
a positive integer.

```bash
MOHIST_PROVIDER_ERROR_PATTERNS='["account suspended","provider-specific limit"]'
MOHIST_PROVIDER_RETRY_THRESHOLD=5
```

Invalid JSON, regular expressions, or threshold stop Runner startup with a
diagnostic. Pi continues to manage credentials; Mohist configuration does not
copy them.

### Bounded Runtime Shutdown

Set these environment variables in the Runner service when a deployment needs
to tune runtime teardown:

```bash
QUARANTINE_DRAIN_TIMEOUT_MS=60000
RUNTIME_SHUTDOWN_TIMEOUT_MS=30000
```

`QUARANTINE_DRAIN_TIMEOUT_MS` bounds a quarantined OpenCode generation. Active
turns still running when it expires receive a definite
`generation-drain-timeout` failure, while completed journal results continue to
report. `RUNTIME_SHUTDOWN_TIMEOUT_MS` is the shared deadline for OpenCode
process/dispatcher teardown and Pi `services.close()`. After the deadline the
Runner abandons the wait and proceeds with best-effort forced teardown. Keep
the Runner service's own process limits configured separately; for example,
use systemd `MemoryMax` on the Runner unit to protect the host from aggregate
runtime memory growth.

### Host-level resource protection

The Runner does not apply per-work memory, RSS, wall-clock, or turn budgets.
Action and workflow timeouts remain explicit contract fields, and cancellation
still terminates the command process group. `mo install runner` does not guess
host capacity; configure aggregate protection on the service or container
instead. On Linux, the recommended unit override is:

```ini
[Service]
MemoryMax=4G
```

Choose `MemoryMax` above the Runner baseline and the shared OpenCode/Pi runtime
headroom. This unit-level aggregate limit applies to the service as a whole and
does not change individual workflow outcomes.

## Remote Access

These options apply to systemd and Docker. The reverse-proxy upstream is
`localhost:3456` in both cases because Docker publishes that port.

### Reverse Proxy and HTTPS

Use Caddy or nginx to access Mohist through a domain. A Caddy example:

```caddyfile
mohist.yourdomain.com {
    reverse_proxy localhost:3456
}
```

Caddy obtains a certificate from Let's Encrypt when the domain and public IP
or DDNS resolve correctly.

Without a domain, create a locally trusted certificate:

```bash
mkcert -install
mkcert mohist.local your-server-ip

# Configure Server TLS with the generated certificate.
# See `mo server --help` and Server configuration documentation.
```

### Tailscale or WireGuard VPN

Put the Server and client devices on one private network:

```bash
# Server host
curl -fsSL https://tailscale.com/install.sh | sh
sudo tailscale up

# Install and sign in to Tailscale on each client, then open:
http://your-server-tailscale-name:3456
```

This option needs no public domain, certificate, or port forwarding.

### Cloudflare Tunnel

For a host behind NAT without a public IP:

```bash
cloudflared tunnel login
cloudflared tunnel create mohist
cloudflared tunnel route dns mohist mohist.yourdomain.com

# Configure the tunnel upstream as localhost:3456.
cloudflared tunnel run mohist
```

Cloudflare terminates external TLS through its edge network.

### Web Links from Slack

Slack Agent Connections do not depend on remote Web access. Replies must be
self-contained in Slack. Configure **External Web URL** only after a remote
access option above is working for Slack members. Mohist can then include
**Open in Mohist** as a fallback path for complete execution evidence and
manual takeover.

Without this setting, replies show stable Job and Session identifiers only.
`localhost`, `127.0.0.1`, and addresses available only from the Server host are
invalid External Web URLs and must not appear in Slack messages.

## Backup

### Data That Must Be Backed Up

- **Mohist data root:** The database, observation data, attachments, artifacts,
  credentials, ingress progress, conversation ownership, and pending delivery.
  The default systemd location is `~/.mohist/`. Docker uses
  `/data/.mohist/` inside the mounted volume.
- **Project Repositories:** These include Issue artifacts under
  `openspec/changes/`. Commits pushed to a remote Repository are already
  replicated there.

Do not back up only `mohist.db`. A Slack Connection creates no second backup
boundary because `mohist-slack` is stateless. Back up and restore the complete
Server data root or container volume as one unit.

### Rebuildable Data

- Worktrees under `<repo>/.mohist/worktrees/`.
- Temporary logs under `~/.mohist/logs/`.

### Backup Procedure

For systemd, stop Server briefly and archive the complete data root. This avoids
copying SQLite and related files at different points in time.

```bash
systemctl --user stop mohist
tar czf /backup/mohist-data-$(date +%Y%m%d).tgz -C ~/.mohist .
systemctl --user start mohist
```

For Docker, use the stopped-volume procedure under
[Persistent Data](#persistent-data).

For off-host retention, use a backup tool such as restic or borg. Stop Server
or use a storage-level atomic snapshot before reading its data root.

```bash
restic -r /backup/mohist backup ~/.mohist <repo>/openspec
```

## Upgrade

Back up the complete Server data root before an upgrade. Server applies EF Core
schema migrations and required repository data upgrades at startup. A backup is
the rollback boundary if migration or the new version fails.

For systemd:

```bash
cd /opt/mohist
git pull
npm ci
npm run build

# Required once when upgrading an installation whose `mo` binary predates
# managed CLI launcher activation. This is the reachable bootstrap path.
bash scripts/install-mo.sh

mo update

# Or update one installed service:
# mo update server
# mo update runner
# mo update slack
```

`mo update` rebuilds and restarts installed Server, Runner, and Slack services
and synchronizes the `mo` CLI.

The bootstrap command above is only needed for the first deployment of the
managed CLI launcher behavior. It publishes the current source checkout
directly to the stable user path; a pre-change `mo` binary cannot perform this
migration through a legacy `mo update cli` path.

For Docker:

```bash
# Pull a registry image:
docker compose pull

# Or rebuild locally:
docker compose build

docker compose up -d
```

Runner and optional `mohist-slack` still run on the host in Docker mode. A
Server container upgrade does not update them. Run `mo update runner` and
`mo update slack` separately.

## Monitoring

Both modes expose `/api/health`.

For systemd, a user cron can restart Server after a failed health check:

```bash
*/5 * * * * curl -sf http://localhost:3456/api/health || systemctl --user restart mohist
```

The Docker image includes a `HEALTHCHECK` against `/api/health`; `docker ps`
shows its state. Compose `restart: unless-stopped` restarts a stopped container.

For broader monitoring, export logs to Loki or ELK and monitor health through
Prometheus or Uptime Kuma.

## Security

- **Do not expose plain HTTP to the public internet.** Built-in authentication
  protects Mohist, but bearer and browser-session credentials still require a
  confidential transport. Use a VPN or a TLS-terminating reverse proxy.
- **Runner has shell access.** Run it as a dedicated non-root user because an
  Agent can ask it to execute arbitrary commands.
- **Limit SSH key scope.** Give Runner read and write access only to required
  Project Repositories instead of using the administrator's primary key.
- **Trust Repository content.** Agents read the code. Do not store secrets or
  tokens in a Repository.

## Current Limitations

- Authentication is designed for one Administrator. There are no additional
  users, roles, or enterprise identity providers.
- Mohist does not terminate public TLS. Remote access requires a VPN or a
  TLS-terminating reverse proxy as described above.

The default deployment listens on localhost and relies on host access as its
outer trust boundary. Built-in credentials protect the application boundary.

---

Implementation source: `mo install` under `packages/cli/` and `scripts/` for
systemd; root `Dockerfile` and `docker-compose.yml` for Docker.
