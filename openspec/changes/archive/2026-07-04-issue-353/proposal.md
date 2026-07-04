## Why

Configuring Hermes notifications today (per #350) means hand-editing two systems:
the Mohist outbound config and the Hermes subscription. The shared signing secret
lives in both, and any mismatch silently breaks delivery — the user only finds out
when notifications never arrive and has to cross-check two tools' output to find
which side is wrong. There is no guided path that generates the secret once and
hands back the exact commands for both sides.

## What Changes

- Add a `mo notify setup` CLI command that mechanically aligns Mohist outbound
  notification config with a Hermes subscription — turning the error-prone pairing
  into copy-paste.
- Probe the Hermes webhook health endpoint (default `http://127.0.0.1:8644/health`,
  overridable). When unreachable, print a clear "Hermes webhook platform not
  started" message with setup steps (pointing at the #350 docs) and exit non-zero
  **without writing any Mohist config** — never modify the other tool's config.
- Generate one random secret shared by Mohist outbound signing and Hermes
  subscription verification.
- Write the outbound address, secret, and default enabled notification types into
  the existing `Mohist:Notifications:Hermes` config; prompt for confirmation before
  overwriting values that already exist (no silent overwrite).
- Print a complete, copy-pasteable `hermes webhook subscribe mohist` command
  carrying the **same** `--secret`, an inline `--prompt '{message}'` passthrough
  (Mohist renders the body; Hermes only forwards it), and the user-selected
  `--deliver` platform (e.g. `--platform telegram`); omit `--deliver` guidance with
  a placeholder when no platform is specified.
- Command does **not** fork `hermes` and does **not** edit Hermes config — it only
  reads the port once (health probe) and writes Mohist's own config file.

## Capabilities

### New Capabilities

- `notify-setup`: a `mo notify setup` guided CLI command that probes Hermes webhook
  readiness, generates a shared secret, writes Mohist outbound notification config,
  and emits the matching Hermes `subscribe` command for the user to run.

### Modified Capabilities

- None. The outbound Hermes notification engine and its `Mohist:Notifications:Hermes`
  config schema (from #350) are unchanged; this command only populates that config.

## Impact

- **CLI**: new `notify` command group with a `setup` subcommand. It writes
  `~/.mohist/config.jsonc` directly (the `Mohist:Notifications:Hermes` section lives
  outside the flat `ConfigService` key schema), reusing the JSONC round-trip /
  comment-strip helpers, then prints `mo update server` to reload.
- **Config**: writes the existing `Mohist:Notifications:Hermes` section — no schema
  change, no new keys.
- **Dependencies**: reuses `System.CommandLine`, the existing JSONC helpers, and a
  one-shot `HttpClient` GET for the health probe.
- **Tests**: CLI specs for probe-down abort (non-zero exit, no config written),
  secret generation, overwrite confirmation, the printed `subscribe` command shape
  (shared secret, inline `--prompt`, `--deliver` platform), and the no-platform
  placeholder case.
