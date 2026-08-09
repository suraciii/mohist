# Authentication and Access

Mohist is a self-hosted software production system for one person. Its
authentication model therefore assumes **one administrator, you**, plus several
machine callers. Every request must belong to an authenticated identity. Who is
calling determines what they can do and provides a trusted actor for Activity
and Approval records.

## Access Overview

| Caller | Proof of identity | Access |
|---|---|---|
| Local `mo` on the Server host | Reads the administrator credential file automatically | Operator |
| Remote `mo` | Device authorization with `mo auth login` | Operator |
| Web UI | Exchanges an administrator-level token for a browser session | Operator |
| Scripts, CI, and external Agents | Personal access token in `MOHIST_TOKEN` | Operator or read-only, selected at issuance |
| Runner | Registers during installation and receives a machine credential | Runner surface only |
| Local service processes, such as a Slack adapter | Service credential file | Operator |
| Inbound integrations and external callbacks | Endpoint-specific token | Only that integration endpoint, scoped to a Project |
| GitHub | Native GitHub HMAC signature | GitHub ingress endpoints only |

Every request must present an identity that Mohist validates consistently.
Invalid authentication returns 401. Insufficient permission returns 403. The
unauthenticated surface is a closed list: health checks, login pages and APIs,
and GitHub ingress endpoints.

## Identity Model

There are three caller types:

| Caller | Meaning | Origin |
|---|---|---|
| Administrator | The system's only user, with every capability | Created automatically during Server installation |
| Machine | Runner, local service such as a Slack adapter, script, external Agent, or inbound integration | Holds its own token |
| Agent | Attribution identity for a Mohist Agent | Created automatically with the Agent definition |

Rules:

- There is one administrator. There is no second user, role, or permission
  group.
- Each token can be issued, revoked, and expired independently. Compromise of
  one token does not affect the others.
- An issued token is shown in full only once. The Mohist database stores only
  its fingerprint, so a database disclosure does not reveal the token.
- Agent identity provides attribution. Mohist records an Agent's actions under
  that Agent instead of the administrator.

## Collaborators Do Not Get Mohist Logins

Mohist does not issue credentials to a second person. Colleagues participate
through identities on external platforms. They review a Pull Request under
their GitHub account and are checked against the approver list; see
[GitHub](github.md). They invoke an Agent as a Slack workspace member and are
checked by Connection access policy; see [Slack](slack.md). The platform
authenticates the person. Mohist validates only the platform-backed identity
and configured list.

## Local Use Requires No Login

Use on the Server host remains login-free. On first start, Server generates an
administrator credential file under `~/.mohist/` with mode 600. Local `mo`
reads it automatically and receives administrator identity. The ability to read
this file is the local trust boundary, equivalent to the ability to sign in to
the host through SSH.

## Remote CLI Device Authorization

On another machine, configure the Server address with `MOHIST_SERVER_URL` and
run:

```bash
mo auth login
```

The command prints a verification code and confirmation link, and opens a
browser when available. After the administrator confirms in the Web UI, the CLI
completes login and stores a local session with mode 600. It renews an expiring
session automatically. Run `mo auth login` again when renewal fails.

```bash
mo auth status    # Show the current identity and Server
mo auth logout    # Sign out and remove the local session
```

## Scripts and External Agents

Callers without a browser, including CI, scripts, and external Agents, use a
personal access token:

```bash
mo auth token create --name ci-bot --scope readonly --ttl 720h
```

The token is shown in full only once and must expire. The default lifetime is
90 days and the maximum is one year, so a leaked token is not valid forever.
The caller supplies it through an environment variable:

```bash
export MOHIST_TOKEN=moh_pat_...
mo issue list
```

Manage tokens with:

```bash
mo auth token list
mo auth token revoke ci-bot
```

## Web UI Token Login

The Web UI requires login. Paste an administrator-level token, either the
contents of the local credential file or a full-scope personal access token, to
receive a browser session. Device-authorization confirmation also occurs in the
Web UI and requires an existing login.

## Runner Registration

During `mo install runner`, the installer obtains a one-time registration token
from Server. On first start, Runner exchanges it for a machine credential and
uses that credential for every report and connection. Runner credentials cover
only the Runner surface and bind to that Runner identity, so another caller
cannot impersonate it. Revoking a Runner credential invalidates it immediately.
Run the installation registration again to recover.

## Inbound Integration Tokens

Each inbound integration endpoint that receives an external callback has its
own Project-scoped token. Compromise of one endpoint does not affect others.
GitHub ingress uses native GitHub signatures instead; see [GitHub](github.md).

## Token Scopes

| Scope | Access |
|---|---|
| Operator | All reads, writes, and administration |
| Read-only | View state and resources without modification |
| Runner | Claim work and report heartbeats, results, and logs |
| Integration | Invoke inbound integration endpoints for one Project |

Administrator credentials and personal access tokens default to operator scope. A
personal access token can be narrowed to read-only when issued. Machine
credential scopes are fixed and cannot be expanded.

## Agent Identity and External Delivery

A Mohist Agent has its own identity. Mohist attributes its Activity to that
Agent. When it delivers to an external system by creating a GitHub Issue, Pull
Request, or comment, it also appears under an independent bot identity instead
of impersonating the administrator. See [GitHub](github.md) for GitHub identity
configuration.

## Non-goals

- Multiple users, roles, permission groups, or Project-level authorization.
- A public developer platform or third-party application registration.
- Single sign-on or enterprise identity federation.

## Status

The single-administrator model, local bootstrap, authenticated Web UI, remote
CLI device authorization and renewal, personal access tokens, Runner and
integration credentials, Scope enforcement, attribution, and audit records are
implemented. API and SignalR access require authentication outside the closed
exemption list described above. Multiple users, external identity federation,
and public application registration remain Non-goals, not unfinished parts of
this model.
