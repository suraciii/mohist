# Authentication and Access

Mohist is self-hosted for one person, but several machines and services must
act on that person's behalf. Each machine caller receives a separate,
revocable identity instead of sharing the administrator's long-lived secret.
This keeps local use simple and makes remote effects attributable.

## Product Commitments

- Mohist has one human Administrator. It does not create a second user, role,
  or collaborator login.
- Every machine caller has a separate Credential and Principal.
- Credentials can expire or be revoked independently.
- Activity and Approval records identify the Administrator, machine, service, or
  Agent that caused an effect.
- A token is shown in full only once. Mohist stores its fingerprint, not its
  value.
- Invalid credentials are unauthenticated. Valid credentials without the
  required capability are forbidden.
- Mohist does not reveal whether a private resource exists until the caller
  can access its Project.

## Choose the Right Access Method

- **Local `mo` on the Server host:** reads the administrator credential file
  discovered automatically. Access to the host is the local trust boundary.
- **Remote `mo`:** uses device authorization with `mo auth login`. The
  Administrator approves the machine without copying the root credential.
- **Web UI:** exchanges an administrator-level token for a browser session. The
  browser keeps a revocable session instead of the root token.
- **Script or CI:** uses a personal access token (PAT) in `MOHIST_TOKEN`. Each
  automation can have its own lifetime and read or write capability.
- **Direct External Agent caller:** uses a PAT with an explicit Project grant.
  The caller can reach only the private Projects selected for that token.
- **Runner:** uses a machine Credential issued during registration. A Runner
  can claim and report work but cannot act as the Administrator.
- **Local service or inbound integration:** uses a dedicated service or
  integration Credential. A compromise stays inside that component's boundary.
- **GitHub:** uses a native GitHub signature. Mohist can trust the platform
  event without sharing a general Mohist token.

## Identity and Attribution

Mohist has one Administrator. Each machine receives a separate Credential as
its own Principal. Each Mohist Agent receives a stable Agent Identity for
attribution.

This separation means:

- one token can expire or be revoked without rotating other credentials;
- Activity and Approval records identify the actual Administrator, machine, or
  Agent that caused an effect;
- an Agent Identity attributes effects but is not the caller Principal;
- external callers authenticate with their own PAT.

## Collaborators Use Platform Identity

Mohist does not issue credentials to another person. A colleague reviews a Pull
Request through their GitHub account and is checked against the approver list.
A Slack workspace member invokes an Agent through a Connection access policy.
See [GitHub](github.md) and [Slack](slack.md).

The platform proves who the person is. Mohist validates that platform identity
against the configured access policy. This is separate from the PAT required by
a direct External Agent caller.

## Local CLI Requires No Login

On first start, Server creates an administrator Credential under `~/.mohist/`
with mode 600. Local `mo` reads it automatically. Anyone who can read that
file already has access equivalent to signing in to the host, so another login
prompt adds no useful boundary.

## Remote CLI Uses Device Authorization

On another machine, configure `MOHIST_SERVER_URL` and run:

```bash
mo auth login
```

The command prints a verification code and confirmation link and opens a
browser when available. After the Administrator confirms in the Web UI, CLI
stores a local session with mode 600. It renews an expiring session
automatically. Run `mo auth login` again when renewal fails.

```bash
mo auth status
mo auth logout
```

Device authorization keeps the administrator Credential off the remote machine
and makes every new login an explicit decision in an already trusted browser.

## Scripts and CI Use Personal Access Tokens

Create a separate token for each headless caller:

```bash
mo auth token create --name ci-bot --scope readonly --ttl 720
```

The token is shown in full only once. Its default lifetime is 90 days and its
maximum lifetime is one year. Supply it through the environment:

```bash
export MOHIST_TOKEN=moh_pat_...
mo issue list
```

Manage tokens independently:

```bash
mo auth token list
mo auth token revoke ci-bot
```

Use read-only Scope when automation only observes state. Use operator Scope only
when it must create or change work.

## ExternalAgentCaller

The [External Agent API](agent-api.md) supports headless callers that launch or
continue Agent work directly. A general PAT Scope is not enough for a private
Project. The PAT must also carry a persisted Project grant.

Create a PAT with an explicit grant for one or more Projects:

~~~text literal
mo auth token create --name release-agent --scope operator --ttl 720 --project proj_123
mo auth token create --name observer --scope readonly --ttl 720 --project proj_123
~~~

An operator PAT may use the explicit `operator_all` grant instead:

~~~text literal
mo auth token create --name owner-agent --scope operator --ttl 720 --all-projects
~~~

Repeat `--project` to grant more than one Project. Use `--all-projects` only
with operator Scope. These choices are mutually exclusive. Mohist never infers
all-Project access from operator Scope alone. A PAT without either grant remains
usable on existing control-plane routes but cannot use the direct API.

Call `/api/v1` with the PAT as a Bearer token. A Web session cookie, Runner or
service Credential, and trusted Agent Connection identity cannot substitute for
the PAT.

For a usable PAT, Mohist checks route Scope and the selected Project grant
before it looks up a Project, Agent, Job, Session, Input, or Turn, or reads an
idempotency mapping.

## Web UI Uses a Revocable Session

Paste an administrator-level token into the Web UI to receive a browser session.
The browser does not retain the root token as its everyday Credential. Device
authorization confirmation also occurs in the Web UI and requires an existing
login.

## Runner Registration Separates Execution Access

During `mo install runner`, the installer obtains a one-time registration token.
Runner exchanges it for a Credential bound to that Runner identity and uses the
Credential for later connections and reports. This prevents another machine
from presenting the same Runner name to gain access.

Revocation invalidates the Runner Credential immediately. Repeat the
installation registration flow to recover. Do not reuse the revoked secret.

## Integrations Keep Independent Boundaries

Each inbound integration receives a Project-constrained Credential. A leak in
one callback therefore does not authorize another. GitHub ingress uses native
GitHub signatures instead. Local adapters use service Credentials and still
apply the external platform's member and Workspace policies.

## Capability Summary

- **Operator:** reads, writes, and administers.
- **Read-only:** observes product state without modification.
- **Runner:** claims work and reports heartbeat, result, artifact, and log facts.
- **Integration:** operates one inbound integration within its Project boundary.

Machine Credentials have fixed capabilities and cannot expand themselves.
Sensitive infrastructure remains operator-only even when an operation only reads
data.

## Non-goals

- Multiple Mohist users, roles, permission groups, or reusable Project ACLs.
- A public developer platform or third-party application registration.
- Single sign-on or enterprise identity federation.

The explicit Project grant on an External Agent PAT is only a Credential safety
boundary. It does not introduce any broader identity model.

## Implementation Gaps

The single-Administrator model, local bootstrap, authenticated Web UI, remote
CLI device authorization and renewal, PATs, persisted direct API Project grants,
Runner and integration Credentials, capability enforcement, attribution, and
audit records are implemented. The direct route, idempotency, public
observation, and event-resume contracts are documented in [External Agent
API](agent-api.md).
