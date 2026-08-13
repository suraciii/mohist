## Why

Mohist relies on layered CLI help as the executable navigation map for both people and Agents, but `mo --help` currently omits the registered `workspace`, `audit`, `github`, and `slack` areas and incomplete presentation coverage also strands multiple descendant commands from scoped discovery. Because new CLI areas have continued to ship without being added to the separate help catalog, discoverability must become complete and enforced against the current command tree now.

## What Changes

- Make root help list every visible registered command area in the appropriate Work, Automation, Operations, or Tools section, including `workspace`, `audit`, `github`, and `slack`.
- Make group and leaf help provide a navigable, one-sentence presentation for every visible registered subarea and action, including currently uncovered nested and recently added commands.
- Keep help local, side-effect free, and progressively scoped: root help remains an area index, group help lists its direct children, and leaf help shows the exact invocation without expanding the whole tree.
- Prevent a visible command from being added without help coverage; incomplete help metadata must be reported by automated verification instead of silently hiding or degrading that command's discovery path.
- Align the CLI reference's command map and implementation-gap status with the complete executable help surface.

## Capabilities

- `cli-help`: Complete root-to-leaf discovery for the visible registered command tree, including capability grouping, scoped command presentations, and structural coverage that rejects undiscoverable commands.

## Impact

- **CLI help surface:** `packages/cli/Mohist.Cli/CommandPresentations.cs`, the help catalog/rendering path, and the registered command tree used as the discovery authority.
- **CLI tests:** structural command-tree coverage plus root, group, leaf, and local usage-error regression tests under `packages/cli/tests/Mohist.Cli.Tests/`.
- **Documentation:** `docs/cli-reference.md` command map and implementation-gap section.
- **Runtime behavior:** command names, arguments, execution semantics, Server APIs, and dependencies are unchanged; the change affects local help and usage discovery only.
