## Context

Issue #350 shipped the outbound Hermes notification engine: a server-side
CloudEvent subscriber that renders a body and POSTs JSON to a `WebhookUrl`
under `Mohist:Notifications:Hermes` in `~/.mohist/config.jsonc`, optionally
HMAC-signed with a shared `Secret`. The engine is done; what is missing is a
guided way to make the **two sides agree** on that shared secret and on the
receiver address.

This issue adds a `mo notify setup` CLI affordance that mechanically aligns the
Mohist outbound config with a Hermes subscription by generating one secret,
writing the Mohist side, and emitting the matching Hermes `subscribe` command
for the user to copy-paste. See `proposal.md` for motivation and
`specs/notify-setup/spec.md` for requirements.

Relevant current state:

- `HermesNotificationOptions` (`packages/server/src/Mohist.Server/Notifications/HermesNotificationOptions.cs:9`)
  already defines the public contract: `WebhookUrl`, `Secret`, `EnabledTypes`
  (defaults `approval_requested`, `workflow_failed`, `issue_completed`). No
  schema change is needed.
- `mo config set` routes through the flat `ConfigService` key schema via
  `/api/config/{key}` (`packages/cli/Mohist.Cli/MohistCliCommands.ConfigProviders.cs:39`).
  That path **cannot** express the nested `Mohist:Notifications:Hermes`
  object — it is a flat string-keyed variable store. Writing the nested
  section therefore has to go direct to the JSONC file.
- `MohistConfigurationExtensions.StripJsoncComments`
  (`packages/server/src/Mohist.Server/Infrastructure/Config/MohistConfigurationExtensions.cs:34`)
  is the existing comment-strip helper used to load the file; it is read-only
  and lives in the server assembly, so the CLI cannot reference it without a
  cross-package dependency it does not have today.
- The architecture boundary (`design/architecture.md`) is explicit: the CLI
  owns user entry/command-line interaction; neither the server nor the CLI
  forks `hermes` or edits Hermes config. #350 already established that
  server-side delivery is a network callback, not a process launch. This
  command follows the same line: one HTTP GET for liveness, then write its
  own config file.

## Goals / Non-Goals

**Goals:**

- Probe the Hermes webhook platform once and abort cleanly (non-zero, no
  config write, no stack trace) when it is not reachable.
- Generate one random secret and guarantee it is byte-identical between the
  Mohist config write and the printed Hermes `subscribe` command.
- Write `Mohist:Notifications:Hermes` (receiver URL, secret, default
  `EnabledTypes`) directly to `~/.mohist/config.jsonc`, prompting before any
  overwrite.
- Emit a copy-pasteable `hermes webhook subscribe mohist` command with the
  same `--secret`, inline `--prompt '{message}'`, and a user-selected
  `--deliver` platform (placeholder guidance when none given).
- Never fork `hermes`, never touch Hermes-side files.

**Non-Goals:**

- No outbound engine work (done in #350).
- No multi-platform fan-out; one `--deliver` platform per run.
- No Hermes config editing or `hermes` CLI invocation — only a printed
  command.
- No config import/export/template versioning.

## Decisions

### D1. New `notify` command group in the CLI, not a server endpoint

The command is user-entry/interaction, which `design/architecture.md` places
in the CLI. It does not advance workflow or issue state, so there is no
reason to expose a server API. Implementation: a new
`MohistCliCommands.Notify.cs` with a `Build(MohistCliApi)` that creates a
`notify` parent command and a `setup` subcommand, registered alongside the
other groups in `MohistCliCommands.Build`
(`packages/cli/Mohist.Cli/MohistCliCommands.cs:10`).

This mirrors the existing group pattern (`OtelCommands`, `ConfigProvidersCommands`).

### D2. Write the JSONC file directly; do not go through `mo config set`

`mo config set` talks to `/api/config/{key}`, which is the flat
`ConfigService` variable schema and cannot represent the nested
`Mohist:Notifications:Hermes` object. The command therefore reads
`~/.mohist/config.jsonc`, parses to a JSON DOM, sets
`Mohist.Notifications.Hermes.{WebhookUrl,Secret,EnabledTypes}`, and writes
the file back through `IFileSystem` (so tests use `FakeFileSystem`).

Alternatives considered:

- **Add nested-object support to the config API.** Rejected: it would grow
  the server contract for a single CLI affordance and mix the flat variable
  store with structured sections.
- **Shell out to an editor / emit a patch.** Rejected: not copy-pasteable,
  not scriptable, not testable.

### D3. JSONC round-trip via `JsonNode`; accept comment loss on the written section

The file is parsed with `JsonNode.Parse` (after stripping comments with a
CLI-local copy of the strip logic — see D6), mutated, and serialized back.
The .NET JSON DOM does not preserve comments, so a write regenerates the
file without comments.

Trade-off: existing user comments are lost on overwrite. This is acceptable
because the file is machine-owned configuration and the command only runs on
explicit user action with an overwrite confirmation. The alternative —
surgical text editing of just the `Hermes` subtree — is significantly more
fragile (tracking brace/quote positions, handling absent sections) for no
behavioral gain, since the server re-reads the stripped file regardless. If
the file does not exist, the command seeds it with the full
`Mohist.Notifications.Hermes` structure.

### D4. Health probe modeled on the OTel status probe

A single `HttpClient.GetAsync("<base>/health")` with a short explicit
timeout. The default base is `http://127.0.0.1:8644` (overridable via an
option). On `HttpRequestException` (connection refused / DNS), on
`TaskCanceledException` (timeout), or on any non-success status, the command
prints a clear "Hermes webhook platform is not started" message with setup
steps pointing at `docs/hermes-notifications.md`, writes **nothing**, and
returns non-zero — exactly the friendly-no-stack-trace discipline already
used in `OtelCommands.RunStatusAsync`
(`packages/cli/Mohist.Cli/MohistCliCommands.Otel.cs:165`). Because the probe
target is an absolute external URL (not the Mohist server), the command
constructs a throwaway `HttpRequestMessage` rather than reusing
`api.Http` (whose `BaseAddress` points at the Mohist server).

### D5. One secret, generated once, handed to both sides

`RandomNumberGenerator.GetBytes(32)` rendered as base64url (URL-safe, no
padding, safe to embed in a shell command). The same string literal is both
written to `Mohist:Notifications:Hermes:Secret` and interpolated into the
printed `--secret` argument, so byte-identity is structural, not
coincidental. `issue_started` stays off by default to match #350 defaults.

### D6. CLI-local comment-strip helper (no server package dependency)

The existing `StripJsoncComments` lives in the server assembly. Pulling the
CLI into a server package dependency just to share one pure function would
invert the layering (the CLI must not depend on the server runtime). The
command therefore carries a small private strip routine identical in
behavior. The two copies are pure functions over a stable JSONC subset; the
duplication is intentional and bounded.

### D7. Receiver URL derived from the probe base, overridable

The webhook receiver URL is derived from the probed Hermes base address by
appending `/webhooks/mohist` (matching the `docs/hermes-notifications.md`
example). A `--webhook-url` option lets the user override the derived value
for non-standard Hermes deployments, so the derivation is a convenience, not
an assumption baked into the data path.

### D8. Overwrite confirmation is interactive with a safe default

Before overwriting any existing `Mohist:Notifications:Hermes` value, the
command prompts `y/N` on `StandardInput`. The default is **no** (abort, write
nothing). If stdin is at EOF or not a TTY-equivalent (non-interactive), the
command also aborts rather than hanging — it never silently overwrites.

## Risks / Trade-offs

- `[JSONC comment loss on overwrite]` -> Acceptable for machine-owned config;
  the write is explicit and confirmed. Documented in `--help`.
- `[Webhook URL derivation assumes Hermes path convention]` -> `--webhook-url`
  override escapes the assumption; the derived value is only a default.
- `[Non-interactive prompt hang]` -> Detect EOF / unreadable stdin and abort
  with a message; never block silently.
- `[Concurrent edit race with mo config set]` -> Last-writer-wins on the
  nested section. Mohist is a single-user local tool; the risk is low and
  matches existing config-edit semantics.
- `[Health probe false-negative on slow Hermes]` -> Short explicit timeout;
  a slow-but-healthy Hermes is reported as "not started", which is the safer
  failure mode (no config written, user re-runs).

## Migration Plan

No data migration: the config schema is unchanged from #350. Deployment is
just shipping the new CLI binary; nothing restarts automatically.

Rollback is removing the command — it writes only to
`~/.mohist/config.jsonc` on explicit user action and touches no other state.
A user who already ran `mo notify setup` keeps a valid #350 config; the
printed Hermes command is independent and unaffected.

After a successful write the command prints `mo update server` so the
managed server reloads the new config (per `AGENTS.md`, never `dotnet run`).

## Open Questions

- Exact Hermes webhook receiver path (`/webhooks/mohist`) — to be confirmed
  against the Hermes subscription docs during implementation; the
  `--webhook-url` override covers divergence.
- Whether a `--yes` non-interactive confirmation flag is wanted for scripted
  use. Not required by the spec; deferred unless a need surfaces.
