## Why

Mohist's coder-agent skills and user docs lag behind the real CLI/API command surface, so capabilities that already exist are effectively invisible: an agent reading the epic skill is told epics "do not participate in workflow execution" and only learns the `done`/`close` lifecycle, never the autopilot `start`/`pause`/`resume` that actually drives self-directed progression — so it falls back to starting issues one by one. The dispatcher skill hands the agent a CLI cheat-sheet (`show|list|start|approve|close`) that omits most of the real issue lifecycle plus all epic autopilot commands. The CLI reference markets itself as "与 Web UI 功能等价" and "完整命令参考" while silently dropping the `agent`, `label`, `workflow`, and `otel` command groups. The result is that humans and agents work around, or make wrong workflow choices because of, missing or false documentation. The runtime is ready; the guidance surfaces are not. This change aligns the text-only guidance surfaces to the product as it actually behaves today.

## What Changes

- **Epic skill** (`packages/cli/Mohist.Cli/skill-data/mohist-create-epic/SKILL.md`): add the autopilot lifecycle (`mo epic start`/`pause`/`resume`, idempotency, running-but-idle), recommend autopilot over manual per-issue starts, and remove the stale "does not participate in workflow execution" framing. Lifecycle guidance moves from `done`/`close` only to the full five-state self-driving model.
- **Dispatcher skill** (`packages/cli/Mohist.Cli/skill-data/mohist/SKILL.md`): replace the partial CLI cheat-sheet with the complete issue/epic lifecycle command surface (incl. `reject`/`retry`/`rerun`/`stop`/`force-stop`/`resume`/`rebase` and `mo epic start`/`pause`/`resume`), so the agent drives issues/epics directly instead of guessing.
- **Operations skill decision**: decide whether to introduce a dedicated `mohist-operate` scenario skill (issue/epic start, approve, reject, retry, stop, resume lifecycle) versus keeping these flows inside the dispatcher; record the decision and, if created, ship its minimum viable content. Folded into the `coder-agent-skills` capability below.
- **CLI reference** (`docs/cli-reference.md`): document the missing command groups (`mo agent`, `mo label`, `mo workflow`, `mo otel`) and remove the false "与 Web UI 功能等价" / "完整命令参考" claims.
- **Boundary convention** (`design/conventions.md`): record the "展示面只留 Web；功能入口才进 CLI 与 skill" rule as the standing test for what gets a CLI/skill entry versus what stays Web-only.
- **Skill sync**: after editing skill source, run the skill sync so `mo skills get <name>` output matches source (acceptance: source and managed cache agree).
- **No code**: this change touches only markdown/skill text and `design/conventions.md`. No CLI command, API, or runtime behavior is added or changed. **BREAKING**: none.

Note: the epic autopilot *user* documentation in `docs/epics.md` (Start/Pause/Resume, autonomous advancement) is already current and already governed by the existing `epic-docs` spec, so it requires verification rather than authoring.

## Capabilities

### New Capabilities
- `coder-agent-skills`: The distributed coder-agent skill guidance (the `mo skills` content shipped from `packages/cli/Mohist.Cli/skill-data/`) is a contract-bearing surface that SHALL stay aligned with the real CLI/API command surface — the epic skill SHALL cover the autopilot lifecycle and SHALL NOT claim the epic is a non-executing organizer; the dispatcher skill SHALL surface the complete issue/epic lifecycle command set; and the operations-skill decision (introduce a dedicated operate skill or keep it in the dispatcher) SHALL be resolved and recorded. Establishes that skill content accuracy is itself a requirement, distinct from `update-runtime-consistency` which only verifies `SKILL.md` files exist.
- `cli-reference`: `docs/cli-reference.md` is the canonical CLI command reference and SHALL document every real command group (including `agent`, `label`, `workflow`, and `otel`) and SHALL NOT claim parity with or equivalence to the Web UI.

### Modified Capabilities
- _(none — the epic user-doc autopilot behavior is already specified by `epic-docs` and `epic-lifecycle`, and is already satisfied; no requirement-level change is needed there.)_

## Impact

- **Agent skills** (`packages/cli/Mohist.Cli/skill-data/`): `mohist/SKILL.md` (dispatcher command surface) and `mohist-create-epic/SKILL.md` (autopilot lifecycle, de-staled framing) rewritten; possibly a new `mohist-operate/` skill plus `manifest.json` entry if the decision favors a dedicated operations skill. Changes propagate to the managed skill cache via skill sync, verified by `mo skills get`.
- **User docs** (`docs/cli-reference.md`): add `agent`/`label`/`workflow`/`otel` sections; remove the Web-UI-equivalence and "完整参考" claims.
- **Conventions** (`design/conventions.md`): new "展示面 vs 功能入口" CLI/skill boundary rule.
- **No runtime impact**: no `packages/server`, `packages/web`, `packages/runner`, or CLI command code changes; no API or persisted-data changes; no migration.
- **Verification**: doc/skill text review; `mo skills get` source-vs-cache consistency check; confirm the four missing command groups appear in `docs/cli-reference.md` and that no equivalence claim remains.
