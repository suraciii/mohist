# Authentication and Access

Mohist is self-hosted for one person, but it is not used by one process. The Web
UI, remote CLI, Runner, integrations, scripts, and external Agents all need to
act without sharing the administrator's long-lived secret. Mohist therefore
keeps one human administrator and gives each machine caller a separate,
revocable identity.

This model preserves zero-login use on the Server host while making remote
effects attributable. It also limits a leaked credential to the access selected
when that credential was issued.

## Choose the Right Access Method

| Caller | Access method | Why |
|---|---|---|
| Local `mo` on the Server host | Administrator credential file, discovered automatically | Access to the host is already the local trust boundary. |
| Remote `mo` | Device authorization with `mo auth login` | The administrator approves the machine without copying the root credential to it. |
| Web UI | Administrator-level token exchanged for a browser session | The browser receives a revocable session instead of retaining the root token. |
| Script or CI | Personal access token in `MOHIST_TOKEN` | Each automation can have its own lifetime and read or write capability. |
| Direct external Agent caller | Personal access token with an explicit Project grant | A headless caller can reach only the private Projects selected for that token. |
| Runner | Machine credential issued during registration | A Runner can claim and report work but cannot act as the administrator. |
| Local service or inbound integration | Dedicated service or integration credential | A compromise remains inside that component's boundary. |
| GitHub | Native GitHub signature | Mohist can trust the platform event without sharing a general Mohist token. |

Invalid credentials are rejected as unauthenticated. Valid credentials without
the required capability are rejected as forbidden. Mohist does not reveal
whether a private resource exists until the caller has access to its Project.

## One Administrator, Several Machine Identities

Mohist has one administrator. It does not create a second Mohist login, role, or
permission group for a collaborator. Instead, each machine receives a separate
credential, and each Mohist Agent receives a stable identity for attribution.

This separation has three consequences:

- A token can expire or be revoked without rotating every other credential.
- Activity and Approval records identify the actual administrator, machine, or
  Agent that caused an effect.
- Agent attribution does not make the Agent a login identity. External callers
  authenticate with their own personal access token.

Mohist shows an issued token in full only once and stores only its fingerprint.
A database disclosure therefore does not reveal the token value.

## Collaborators Use Platform Identity

Mohist does not issue credentials to another person. A colleague reviews a Pull
Request through their GitHub account and is checked against the approver list;
see [GitHub](github.md). A Slack workspace member invokes an Agent through a
Connection access policy; see [Slack](slack.md).

The platform proves who the person is. Mohist validates the platform-backed
identity and the configured access policy. That boundary is separate from the
personal access token required by a direct external Agent caller.

## Local CLI Requires No Login

On first start, Server creates an administrator credential under `~/.mohist/`
with mode 600. Local `mo` reads it automatically. Anyone who can read that file
already has access equivalent to signing in to the host, so another login prompt
would not add a useful security boundary.

## Remote CLI Uses Device Authorization

On another machine, configure `MOHIST_SERVER_URL` and run:

```bash
mo auth login
```

The command prints a verification code and confirmation link and opens a browser
when available. After the administrator confirms in the Web UI, CLI stores a
local session with mode 600. It renews an expiring session automatically. Run
`mo auth login` again when renewal fails.

```bash
mo auth status
mo auth logout
```

Device authorization keeps the administrator credential off the remote machine
and makes every new login an explicit decision in an already trusted browser.

## Scripts and CI Use Personal Access Tokens

Create a separate token for each headless caller:

```bash
mo auth token create --name ci-bot --scope readonly --ttl 720h
```

The token is shown in full only once. Its default lifetime is 90 days and its
maximum lifetime is one year, so a leaked token cannot remain valid forever.
Supply it through the environment:

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

## Direct Agent Calls Need a Project Grant

The [External Agent API](agent-api.md) is available for headless callers that
launch or continue Agent work directly. A general PAT Scope is not enough for
this private-Project boundary. The PAT must also carry a persisted Project grant.

Create a PAT with an explicit grant for one or more Projects:

~~~text literal
mo auth token create --name release-agent --scope operator --ttl 720h --project proj_123
mo auth token create --name observer --scope readonly --ttl 720h --project proj_123
~~~

An operator PAT may use the explicit `operator_all` grant instead:

~~~text literal
mo auth token create --name owner-agent --scope operator --ttl 720h --all-projects
~~~

Repeat `--project` to grant more than one Project. Use `--all-projects` only with
operator Scope. These choices are mutually exclusive, and Mohist never infers
all-Project access from operator Scope alone. A PAT without either grant remains
usable on existing control-plane routes but cannot use the direct API.

Call `/api/v1` with the PAT as a Bearer token. A Web session cookie, Runner or
service credential, and trusted Agent Connection identity cannot substitute for
the PAT. Cookie-only and otherwise unusable direct requests return `401` with a
`WWW-Authenticate: Bearer` challenge and do not reveal whether a token was
missing, expired, or revoked.

For a usable PAT, Mohist resolves the persisted `ExternalAgentCaller`, checks
its route Scope, and checks the selected Project grant before it looks up a
Project, Agent, Job, Session, Input, or Turn, or reads an idempotency mapping.
An out-of-grant Project returns `403 forbidden` even when that Project does not
exist. Only after the grant passes can a missing or foreign resource return its
resource-specific `404`. Failed authentication and authorization do not create
or read request mappings, rejection records, execution records, outbox items,
or public events.

## Web UI Uses a Revocable Session

Paste an administrator-level token into the Web UI to receive a browser session.
The browser does not retain the root token as its everyday credential. Device
authorization confirmation also occurs in the Web UI and requires an existing
login.

## Runner Registration Separates Execution Access

During `mo install runner`, the installer obtains a one-time registration token.
Runner exchanges it for a credential bound to that Runner identity and uses the
credential for all later connections and reports. This prevents another machine
from presenting the same Runner name to gain its access.

Revocation invalidates the Runner credential immediately. Repeat the
installation registration flow to recover; do not reuse the revoked secret.

## Integrations Keep Independent Boundaries

Each inbound integration receives a Project-constrained credential so a leak in
one callback does not authorize another. GitHub ingress uses native GitHub
signatures instead. Local adapters use service credentials and still apply the
external platform's own member and workspace policies.

## Capability Summary

| Capability | Access |
|---|---|
| Operator | Reads, writes, and administration. |
| Read-only | Product observation without modification. |
| Runner | Work claim, heartbeat, result, artifact, and log reporting. |
| Integration | One inbound integration within its Project boundary. |

Machine credentials have fixed capabilities and cannot expand themselves.
Sensitive infrastructure remains operator-only even when an operation only
reads data.

## Non-goals

- Multiple Mohist users, roles, permission groups, or reusable Project ACLs.
- A public developer platform or third-party application registration.
- Single sign-on or enterprise identity federation.

The explicit Project grant on an external Agent PAT is only a credential safety
boundary. It does not introduce any of these broader identity models.

The single-administrator model, local bootstrap, authenticated Web UI, remote
CLI device authorization and renewal, personal access tokens, persisted direct
API Project grants, Runner and integration credentials, capability enforcement,
attribution, and audit records are implemented. See [External Agent API](agent-api.md)
for the direct route, idempotency, public observation, and event-resume contract.
