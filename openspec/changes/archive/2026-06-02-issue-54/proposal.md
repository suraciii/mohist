## Why

Installed Mohist CLIs currently expose `mo skills` while resolving built-in skill guidance from files that are not installed next to the bare `mo` binary. This makes discovery stubs unreliable after local install or `mo update`, even though version-matched skill guidance is shipped in the publish output.

## What Changes

- Store Mohist-packaged built-in skill assets in a CLI-managed cache at `~/.mohist/cli/skill-data`.
- Resolve built-in skill assets from `MOHIST_SKILLS_DIR` first, then the managed cache when version-compatible, then `AppContext.BaseDirectory/skill-data` as a publish/development fallback.
- Synchronize published `skill-data` into the managed cache during `mo update` and `scripts/install-mo.sh`.
- Add a small asset manifest that records the CLI build identity and bundled built-in skill names.
- Make `mo skills get` and `mo skills path` report clear diagnostics when managed assets are missing, stale, or incompatible with the running CLI.
- Preserve `MOHIST_SKILLS_DIR` for development and test overrides.
- Keep runtime/internal `.mohist/skills` separate from packaged CLI skill asset management.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `mohist-skill-guidance`: Built-in skill guidance resolution must support managed packaged assets, version compatibility checks, diagnostics, and environment override precedence.
- `cli-interface`: CLI install/update behavior must keep packaged skill assets synchronized with the installed binary so `mo skills get` works from a simple binary installation.

## Impact

- Affects the local `mo skills get` and `mo skills path` command behavior and diagnostics.
- Affects CLI update/install paths, including `SourceCodeUpdater.UpdateCliAsync` and `scripts/install-mo.sh`.
- Adds or updates packaged asset manifest generation and validation for built-in skill assets.
- Writes only Mohist-managed packaged CLI assets under `~/.mohist/cli/skill-data`; it does not mutate runtime/internal `.mohist/skills` state or require a running Mohist server.
