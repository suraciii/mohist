## Context

Mohist's coder-agent skills and user docs lag behind the real CLI/API command
surface, so capabilities that already exist are effectively invisible. This is a
**text-only** change: it touches markdown docs, skill `SKILL.md` text, and
`design/conventions.md`. No runtime code, CLI command implementation, API, or
persisted data changes; no migration; nothing breaking.

Current state (verified against the working tree and live `mo --help`):

- **Epic skill** (`packages/cli/Mohist.Cli/skill-data/mohist-create-epic/SKILL.md`)
  still says the epic "does **not** participate in workflow execution" and only
  documents the `done`/`close` tail of the lifecycle — never the autopilot
  `start`/`pause`/`resume` that actually drives self-directed progression.
- **Dispatcher skill** (`.../skill-data/mohist/SKILL.md`) hands the agent a
  partial cheat-sheet (`show|list|start|approve|close`) that omits most of the
  real issue lifecycle (`reject`/`retry`/`rerun`/`stop`/`force-stop`/`resume`/
  `rebase`) and all epic autopilot commands.
- **CLI reference** (`docs/cli-reference.md`) markets itself as "和 Web UI 功能
  等价" and "完整命令参考" while silently dropping four real top-level command
  groups: `mo agent`, `mo label`, `mo workflow`, `mo otel`.
- **Conventions** (`design/conventions.md`) has no standing rule for what gets a
  CLI/skill entry versus what stays Web-only.
- **`docs/epics.md`** already contains a complete, current autopilot section
  (Start/Pause/Resume, idempotency, running-but-idle, auto-advancement). Per the
  proposal, the epic user-doc autopilot scope is **already satisfied** and needs
  only verification, not authoring.

The runtime is ready; the guidance surfaces are not. Stakeholders: coder agents
(consume `mo skills get`), human users (consume `docs/`), and the standing
boundary between Web (display) and CLI/skill (functional entry).

## Goals / Non-Goals

**Goals:**

- Epic skill documents the full autopilot lifecycle and recommends autopilot over
  manual per-issue starts; stale "non-executing" framing removed.
- Dispatcher skill surfaces the complete issue + epic lifecycle command set,
  replacing the partial cheat-sheet.
- Resolve and record whether to introduce a dedicated operations skill.
- CLI reference documents the `agent`/`label`/`workflow`/`otel` groups and stops
  claiming Web-UI equivalence or absolute completeness.
- Record the display-surface vs functional-entry boundary as a standing
  convention.
- Skill source edits propagate to the managed cache so `mo skills get` matches
  source.

**Non-Goals:**

- No new CLI command, API, or runtime behavior (covered by a separate CLI issue).
- No Web UI changes.
- No metrics/inbox/agent-ops display surfaces in the CLI (those stay Web-only per
  the boundary rule).
- No re-authoring of `docs/epics.md` autopilot content (already current).

## Decisions

### Decision 1 — Keep operations in the dispatcher; do NOT create `mohist-operate`

The operations-skill question (introduce a dedicated `mohist-operate` scenario
skill vs. keep issue/epic operational flows in the dispatcher) is resolved as:
**keep them in the dispatcher; make the dispatcher's command surface complete
and authoritative.**

Rationale: the existing three scenario skills (`mohist-explore`,
`mohist-create-issue`, `mohist-create-epic`) each encode a *methodology* —
multi-step flows with templates, decision points, and user-confirmation gates.
The operational lifecycle (`start`/`approve`/`reject`/`retry`/`rerun`/`stop`/
`resume`/`rebase`, epic `start`/`pause`/`resume`) is a *command reference*: the
choices among them (e.g. `retry` vs `rerun` vs `rebase`) are fully answerable by
`mo <cmd> --help`, not a multi-step methodology warranting a skill. Adding a
fifth skill would expand the very contract-bearing surface this issue exists to
keep aligned — increasing future drift risk for no methodology gain.

Alternatives considered:

- **Create `mohist-operate`.** Rejected: would mostly duplicate `mo --help`,
  adds a maintenance surface, and this issue is literally about reducing
  skill/reality drift. The dispatcher already owns "route to the right surface";
  the complete cheat-sheet belongs there as the single source.
- **Split — keep create skills, add operate skill to mirror them.** Rejected for
  the same reason; symmetry of naming is not a sufficient reason to split a
  command reference into its own skill.

### Decision 2 — CLI reference documents the four missing groups in existing style

Add concise cheat-sheet sections for `mo agent`, `mo label`, `mo workflow`, and
`mo otel`, matching the existing Issue/Epic section style (compact command list
+ one-line purposes), with the standard "all subcommands support `-o` and
`--project`/`--project-id`" note where applicable. Do not author new deep-doc
pages; point to `mo <cmd> --help` for full flag detail. This satisfies the spec
requirement that each documented group describe its subcommands consistent with
actual CLI behavior.

Subcommand surface to document (verified from `mo --help`):

- `mo agent`: `create`, `list`/`ls`, `show`, `update`, `delete`, `session`
  (`list`/`show`/`transcript`/`launch`/`followup`/`cancel`).
- `mo label`: `list`/`ls`, `add`, `update`, `remove`/`rm`.
- `mo workflow`: `list`/`ls` (distinct from `mo project workflow template/config`
  which is already documented under 项目管理).
- `mo otel`: `query <sql>` (no server required), `status` (server required).

### Decision 3 — Drop absolute completeness/equivalence claims permanently

Replace "`mo` 是 Mohist 的命令行入口。和 Web UI 功能等价 …本文是完整命令参考"
with an accurate, humble scope statement (entry point for scripting/automation;
documents the command groups; see `mo --help` for the full tree). 

Alternative considered: add the four missing groups, then **re-assert**
completeness. Rejected: the absolute claim is exactly what drifted into a lie
once, and a standing absolute will re-drift the next time a command group lands.
A humble scope statement is robust against recurrence and is what the spec
demands ("SHALL NOT assert it is a complete reference unless every real command
group is documented" — we choose not to make the assertion at all).

### Decision 4 — Boundary convention: display vs functional entry (three tiers)

Add a new section to `design/conventions.md` (not `architecture.md` — per the
proposal) recording the standing rule. The rule is refined into three tiers,
which also justifies Decision 1:

1. **Display / read-only surfaces** (dashboards, metrics, inbox, agent ops
   views) → **Web-only**, no CLI/skill entry.
2. **State-changing functional entry points** (create/start/approve/reject/
   retry/stop/resume/rebase/done/close, etc.) → get a **`mo` CLI command**.
3. **A coder-agent skill entry** is added **only when there is methodology to
   encode** (multi-step flow, templates, decisions, confirmation gates) — not
   for every CLI command.

This is the standing test for future "should this get a CLI/skill entry?"
questions. It connects directly to Decision 1: operations satisfy tier 2 (CLI)
but not tier 3 (no methodology), so they stay in the dispatcher rather than
becoming a dedicated skill.

### Decision 5 — Epic skill rewrite: autopilot-first lifecycle

Rewrite the epic skill's lifecycle framing from "`done`/`close` only, epic is a
non-executing organizer" to an autopilot-first model: recommend `mo epic start`
as the default way to drive an epic, document `start`/`pause`/`resume` with
idempotency and running-but-idle, then keep `done`/`close` as the terminal
tail. Remove the sentence claiming the epic does not participate in workflow
execution. The create mechanics (description, link, prerequisites) stay; only
the lifecycle section and the stale framing change. Source truth:
`docs/epics.md` lifecycle section, which is already current.

### Decision 6 — Skill propagation via `mo skills sync`; verify with `mo skills get`

After editing `SKILL.md` source under `skill-data/`, run `mo skills sync` (which
syncs working-tree skill-data into the managed cache so `mo skills get` reflects
local edits), then verify each edited skill with `mo skills get <name>` so
output matches source byte-for-byte (modulo sync formatting normalization).
Acceptance gate for this change, not a runtime behavior change.

## Risks / Trade-offs

- [Skill/reality drift recurs after this change] → The new "skills are a
  contract-bearing surface" requirement plus the humble CLI scope statement
  (Decision 3) remove the absolute claims that hid drift. No automated guard is
  added here (out of scope: no runtime code), so periodic re-audit is still
  manual. Recorded as a known limitation.
- [Dispatcher becomes long with the full command surface] → Acceptable: it
  remains a reference table, not prose; readability survives a complete
  cheat-sheet better than a partial one that misleads.
- [Operations may later grow real methodology and warrant a skill] → The
  boundary rule (Decision 4, tier 3) is the trigger: if operational flows
  accumulate multi-step decisions/templates, re-elevate to a dedicated skill
  then. Not needed today.
- [`docs/epics.md` drifts again while the skill is edited] → The epic skill
  rewrite sources its lifecycle content from `docs/epics.md`; the implementer
  verifies the two stay consistent rather than copying blindly.
- [`mo workflow list` confused with `mo project workflow template/config`] →
  Decision 2 documents them as distinct groups in distinct sections, mirroring
  the CLI's actual structure.

## Migration Plan

Text-only change; no data migration, no rollout ordering, zero downtime.

1. Edit `mohist-create-epic/SKILL.md` (Decision 5).
2. Edit `mohist/SKILL.md` (Decision 1 — complete command surface, remove partial
   cheat-sheet, drop "Promote to a scenario skill" caveat per the recorded
   decision).
3. Edit `docs/cli-reference.md` (Decisions 2 + 3).
4. Edit `design/conventions.md` (Decision 4).
5. Run `mo skills sync`; verify with `mo skills get mohist` and
   `mo skills get mohist-create-epic` that cache matches source.
6. Verify acceptance criteria against the edited files and the live CLI.

Rollback: revert the markdown/skill edits; re-run `mo skills sync`. No runtime
state to restore.

## Open Questions

- None blocking. The operations-skill decision (Decision 1) is resolved and
  recorded, satisfying the spec. Whether to later add an automated CI check
  that CLI command groups appear in `docs/cli-reference.md` (to make the
  "skills/docs are a contract-bearing surface" requirement machine-checked) is
  deferred — it would be runtime/tooling work outside this change's text-only
  scope.
