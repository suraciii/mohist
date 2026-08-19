# Spec Docs Cleanup — Audit and Plan

Branch: `docs/spec-cleanup` (worktree `mohist-spec-docs-cleanup`). The audit
started from `master` 5b4ee33a4; the completed candidate is rebased onto
`master` cacf3b327.
Docs-only change. No source or test edits. No build/test during execution; `npm run verify` at the end.

## 1. Scope confirmed by audit

- `docs/` 30 files + `docs/actions/` 8 files (product spec).
- `design/` 26 top-level files + `design/decisions/` 2 + `design/runtimes/` 3 + `design/workflow/` 9 (design spec).
- Root: `AGENTS.md`, `CLAUDE.md`, `CONTEXT.md`, `README.md`, `CONTRIBUTING.md`.
- Templates: `.github/PULL_REQUEST_TEMPLATE.md`, `.github/ISSUE_TEMPLATE/{bug-report,feature-request,refactor}.md`.
- Nested agent files: `packages/web/AGENTS.md` (keep as-is), `test/agentic/{AGENTS.md,README.md}`.
- `openspec/` contains only `changes/` (in-flight delivery artifacts and archive). There is no active workflow-instruction document under `openspec/`; nothing to clean there. No `ROADMAP.md` exists.

Audit method: 8 read-only audit passes (7 area audits + 1 delta audit after pulling master 980354fcb → 5b4ee33a4). Every cited command was verified against `package.json` scripts and `packages/cli` sources; every cross-doc link and anchor was resolved.

## 2. Rule resolutions (contradictions settled before editing)

- R1 Tables. The goal bans tables; `docs/README.md` and `docs/_agents.md` currently prescribe "tables over lists". The goal wins. All tables in `docs/` and `design/` convert to short prose or concrete examples, and the two convention docs stop prescribing tables in the same change.
- R2 Contract vs implementation detail. Keep in `design/`: durable protocol contracts — wire-payload examples, grammars, minimal model literals (sanctioned by `design/agents.md`), boundary symbol names. Remove from `design/`: pseudocode that restates method bodies, per-field dumps, storage mechanics (columns, constraint names, file names), HTTP route dumps, CLI dumps, internal error enums, "Verification"/test-acceptance sections. Keep in `docs/` API/CLI reference docs: public routes, request/response examples (serialization examples are preserved), public error codes, the command language.
- R3 Implementation-status statements are preserved (goal phase 3), but stripped of issue/PR numbers, changelog narration ("changed to", "is retired", "moved to"), review wording ("tracked for removal", "follow-up work", "planned in change X"), and per-package delivery checklists. Gap statements stay as plain product facts.
- R4 Writing-contract duplication. `docs/_agents.md` becomes the single owner of the docs writing contract; `docs/README.md` keeps the index and links to it. `design/agents.md` becomes the single owner of the design writing rules; `design/README.md` keeps the index and links to it.
- R5 Launch-idempotency rule appears in three design docs. `design/event-routing.md` owns it once; `design/issue-watch.md` and `design/event-response.md` link.
- R6 The Action input DTO is triplicated with a real contradiction (`prompt: string` in `design/workflow/actions.md` vs `prompt: PromptSpec` in the runtime docs). Code truth: `packages/runner/src/core/prompt.ts:14` defines `PromptSpec = string | StructuredPrompt`. `design/workflow/actions.md` owns the contract with the correct shape; `design/runtimes/opencode.md` and `design/runtimes/pi.md` delete their copies and link.
- R7 Workflow state diagram and Issue health enumeration are duplicated between `docs/concepts.md` and `docs/the-workflow.md`. `docs/the-workflow.md` owns both; `docs/concepts.md` links.
- R8 The Slack CLI block lives in both `docs/slack.md` and `docs/cli-reference.md`. `docs/cli-reference.md` owns the command language; `docs/slack.md` keeps scenario usage and links.
- R9 `docs/mobile-pwa.md` is a decision record for an unimplemented proposal, not a product contract. Move to `design/decisions/mobile-pwa.md`, strip issue refs (#106, #352), fix the dead `mo notify setup` command to `mo notification setup`.
- R10 `design/runner-runtime-readiness.md` is framed as a change proposal (pinned revision, deferral plan, OpenSpec references). Merge its durable witness contract and fencing rule into `design/runner.md`; delete the file.
- R11 `design/testing.md`: gate internals (run directories, DAG, lane scheduling) move to a new `scripts/test-duration/README.md` (tool documentation next to the tooling; not a new spec layer); the C# focused-test how-to moves to `CONTRIBUTING.md`; the testing tracks/hard rules/guards stay, trimmed.
- R12 `CLAUDE.md` is byte-identical to `AGENTS.md`. Reduce it to a one-line pointer to `AGENTS.md`.
- R13 `AGENTS.md` keeps exactly the two gate commands (`npm run verify`, `npm run test:fast`) as part of the global verification rule; they are the canonical pre-handoff contract, not command detail. Everything else stays rule-level.
- R14 "Implementation source:" footers are code-provenance pointers sanctioned by the docs writing contract; they are not delivery tracking. Keep the pattern.
- R15 Model/reasoning-config leaks: replace concrete vendor model names in examples (`claude-sonnet-4`, `gpt-5`, `sonnet`, `variant: high/medium/xhigh`) with neutral placeholders (`model-a`/`model-b` pattern already used in `design/workflow/variables.md`). Example structure stays intact.
- R16 Dead/stale references to fix (doc-only): `--ttl 720h` → `--ttl 720` (`docs/agent-api.md`, `docs/auth.md`); `mo workflow approve` → `mo run approve` and `mo issue comment` → `mo issue comment create` (`design/event-routing.md`); `--author` → `--display-name` (`design/event-response.md`); drop dead symbol `CatalogOnlyTypes` (`design/event-protocol.md`); drop stale `ReconcileRunningAsync` status entry (`design/agent-execution.md`); `npm run test:browser` → `npm run test:browser -w packages/web` (`design/testing.md`); `mo run validate` → `mo workflow validate --file` (`design/workflow/actions.md`); `main` → `master` (`CONTRIBUTING.md`, `.github/PULL_REQUEST_TEMPLATE.md`); fix the section-name cross-ref in `design/prompt-management.md`; drop the dead "Server configuration documentation" reference (`docs/self-host.md`); re-verify and fix the page map / settings sections / URL prefixes in `docs/web-ui.md` against `packages/web/src/app/AppContent.tsx` and `pages/settings/lib/sections.tsx`.
- R17 Terminology alignment to `CONTEXT.md`: "external Agent" → "External Agent" (`docs/issues.md`, `docs/sub-issues.md`, `docs/skills.md`); systematic "Session" → "AgentSession" where the Mohist resource is meant (`docs/workspaces.md`); readiness values lowercase `ready` / `needs-setup` / `unknown` everywhere (`design/agent-subscriptions.md`, `design/slack.md`, `design/subagents.md`, `design/web-ui.md`, `CONTEXT.md` itself); fix the Mohist App sentence in `docs/concepts.md` per the glossary; define or replace "TaskRun" (`docs/concepts.md`); "review gate"/"delivery gate" → "approval point" wording (`design/workflow/builtin-workflows.md`); "Runner login" → credential wording (`docs/github.md`).
- R18 `CONTEXT.md` stays a pure glossary: remove the config literal (`uses: mohist/agent`), the storage clause ("only its hash stored"), and the two design-scope terms (Runtime Binding, Agent Availability) after confirming no `docs/` file relies on them (audit grep: zero occurrences in `docs/`). Enum spellings stay — they are the canonical term forms.
- R19 Getting Started (`docs/getting-started.md`): current master version is nearly end-to-end. Remaining: convert the prerequisites table to prose, drop the implementation-source footer, and make the External Agent path self-contained by inlining the one skill-install step (or stating explicitly that the `mo`-only path needs nothing else).
- R20 "Buzz" is undefined in `docs/slack.md` — remove the references there (product docs must not lean on an undefined external product). In `design/slack.md` the industry-study analogy is legitimate; add a one-line gloss at first use.
- R21 `docs/agent-sessions.md` HTTP endpoint blocks (default-execution-config, agent-tasks launch) are public API contract; consolidate into `docs/agent-api.md` if not already covered there, otherwise delete as duplication. `design/agent-api.md` keeps rationale only.
- R22 `test/agentic/README.md` is mostly Chinese with stale command history; `test/agentic/AGENTS.md` duplicates its structure section. Merge into one English `test/agentic/README.md` (structure + environment facts, no delivery history); `test/agentic/AGENTS.md` becomes a one-line pointer.
- R23 `design/db-migrations.md` keeps the authoring contract and squash procedure; the point-in-time "Current accepted deltas" snapshot moves to a new `design/decisions/squashed-baseline.md` record.
- R24 Diagrams: keep ASCII diagrams that carry architecture/workflow/system-boundary content; convert diagrams that merely restate adjacent numbered steps or prose into prose. Convert bare ``` fences to `text literal`/`text diagram` per the design convention.

## 3. Per-file dispositions

Legend: keep = no structural change (minor polish only); trim = remove flagged content in place; move/merge/delete as stated. Line numbers cite the audit baseline; editors must re-locate by content, not by line number.

### Root and templates

- `AGENTS.md` — keep. No change beyond what R13 implies (already compliant).
- `CLAUDE.md` — reduce to pointer (R12).
- `CONTEXT.md` — trim per R18; fix readiness-value casing.
- `README.md` — trim: delete the Implementation Status table and tracking prose; convert the repo-tree diagram to a short list.
- `CONTRIBUTING.md` — keep; fix `main` → `master`; receive the focused-test how-to from `design/testing.md` (R11).
- `.github/PULL_REQUEST_TEMPLATE.md` — keep; fix `main` → `master`.
- `.github/ISSUE_TEMPLATE/*.md` — keep, no change.
- `packages/web/AGENTS.md` — keep, no change.
- `test/agentic/README.md` + `test/agentic/AGENTS.md` — merge per R22.

### docs/

- `docs/README.md` — trim: move the Writing Contract to `docs/_agents.md` (R4); keep the index.
- `docs/_agents.md` — merge: absorb the writing contract, define rules once, drop the table-promotion rule (R1).
- `docs/getting-started.md` — keep + trim per R19.
- `docs/vision.md` — keep; inline the stage-chain arrow fragment if it duplicates `the-workflow.md` (verify, else leave).
- `docs/concepts.md` — trim: drop the Issue-property and health tables (R7), the Skill listing table, the priority enum; fix the Mohist App sentence and "TaskRun" (R17); link the lifecycle diagram to `the-workflow.md`.
- `docs/the-workflow.md` — trim: move Profile field-level customization to `docs/workflow-definition.md`; keep the lifecycle diagram and health contract (single owner, R7); drop the artifact storage path pattern, the `runner-lost` code, the Plan artifact table.
- `docs/issues.md` — trim: move the ~35% command/option enumeration into scenario prose with links to `docs/cli-reference.md`; move the recovery matrix to `docs/troubleshooting.md`; genericize the model name (R15); fix "external Agent" casing; drop the dispatch narration and precondition acceptance contracts, keep the lifecycle rules as product semantics.
- `docs/epics.md` — trim: convert the three tables (properties, lifecycle entry conditions, operation mapping) to prose with one concrete example; keep lifecycle rules and current limitations as product facts.
- `docs/sub-issues.md` — trim: convert the derived-state table to prose; keep the implementation-status section but strip per-package checklist form (R3); fix "external Agent" casing.
- `docs/repositories.md` — keep.
- `docs/workspaces.md` — trim: move the event-name/payload enumeration to `docs/event-routing.md`; drop the Runner-assignment/migration internals narration; fix "Session" → "AgentSession" (R17); keep the Implementation Gaps section as plain facts.
- `docs/agent-api.md` — trim/merge: keep the product commitments (intro, auth model, idempotency, privacy) and the public contract examples; consolidate the endpoint blocks from `docs/agent-sessions.md` here (R21); move fingerprint/projection/cursor/precedence mechanics to `design/agent-api.md` where not already covered; fix `--ttl 720h`; convert tables; define PAT at first use.
- `docs/agent-sessions.md` — trim: move the two HTTP endpoint blocks to `docs/agent-api.md` (R21); compress the two step-by-step procedure flows; convert 7 tables; strip design vocabulary from the gaps section ("ownership lease, effect fence, candidate reconciliation"); align the observable-state wording with the public status vocabulary; keep Current Scope/Implementation Gaps as plain facts (R3).
- `docs/agent-supervision.md` — keep; rename the "Implementation Status" list to plain gap statements (R3).
- `docs/subagents.md` — trim: move the stop/detach/spawn race arbitration to `design/subagents.md`; drop the redundant diagram-or-table pair (keep one); keep the boundary content.
- `docs/skills.md` — keep; fix "External Agent" casing throughout; drop the roadmap sentence ("remains on the roadmap").
- `docs/runner.md` — trim: move the timeout/journaling/SIGKILL internals to `design/runner.md`; compress the boot-sequence narration; convert the table; keep operator-facing env-var facts.
- `docs/workflow-definition.md` — keep: delete the CI-mechanics sentence; move the validator-ownership paragraph to `design/workflow/definition.md`; convert the template-namespace table; everything else is the DSL contract.
- `docs/workflow-profiles.md` — trim: genericize the model/reasoning example (R15); reword the delivery-tracking line; keep the rest.
- `docs/event-routing.md` — keep: convert the two tables to prose/examples; absorb the workspace event enumeration from `docs/workspaces.md` if it is product-visible contract (else drop).
- `docs/github.md` — trim: delete the Status section and branch-name migration caveat; convert three tables; fix "Runner login" wording (R17).
- `docs/slack.md` — trim: drop the duplicated CLI block (R8); collapse the 26-row behavior matrix into boundary prose; remove the Status/Current-gaps delivery tracking; remove "Buzz" (R20); convert remaining tables.
- `docs/hermes-notifications.md` — keep: trim the gaps section wording; convert the table.
- `docs/auth.md` — trim: remove the `ExternalAgentCaller` internal type name, the wire-level 401/403/404 narration, and the storage-behavior enumeration; fix `--ttl 720h`; align wording with Principal/Credential; convert tables; keep the model.
- `docs/web-ui.md` — trim: re-verify and fix the page map / settings list / URL prefixes (R16); delete both gap sections' tracking wording and the mobile review note; convert tables.
- `docs/mobile-pwa.md` — move to `design/decisions/mobile-pwa.md` (R9).
- `docs/observability.md` — keep: drop the Status paragraph; keep deployment defaults (they are operator contract).
- `docs/self-host.md` — trim: remove code-identifier leaks (`services.close()`, `generation-drain-timeout` string, EF Core migration mention), the storage-file inventory, the dead doc reference, the bootstrap-migration note; convert tables; keep operator configuration contract.
- `docs/troubleshooting.md` — trim: convert the four tables to prose examples; keep the symptom→recovery structure.
- `docs/cli-reference.md` — trim: delete the "Completed" delivery ledger and migration framing; condense the remaining gaps to plain statements; convert six tables; trim DTO dumps to single examples (R2).
- `docs/actions/README.md` — trim: drop the author-instruction line and status section; convert tables.
- `docs/actions/agent.md` — keep: convert two tables.
- `docs/actions/core.md` — keep: convert tables to example-first prose; add one boundary paragraph (why these primitives exist).
- `docs/actions/git.md` — keep: same table treatment.
- `docs/actions/github-pr.md` — keep: same table treatment.
- `docs/actions/opencode.md` — trim: remove the gaps section; genericize the model name; convert tables.
- `docs/actions/openspec.md` — trim: move the `archiveHint` dispatch-snapshot internals to `design/workflow/task-dispatch.md`; convert tables.
- `docs/actions/pi.md` — trim: remove the gaps section and token-field list; genericize the model name; convert the table.

### design/

- `design/README.md` — keep: strip the two issue numbers; keep WIP status markers (R3); link writing rules to `design/agents.md` (R4).
- `design/agents.md` — merge: absorb the full writing rules from `design/README.md` (single owner, R4).
- `design/architecture.md` — trim: compress the coordinator section to its contract (drop class/interface roll-call and storage detail); delete the redundant five-line diagram; convert tables.
- `design/conventions.md` — trim heavily: keep the identity/facts-claims-settlement/role-suffix/entity-map core; remove the ~600-line schema/fence/pseudocode body (DTO field lists, fence-token schemas, storage constraints, algorithm pseudocode) — the code expresses it.
- `design/agent-api.md` — trim: keep boundary decisions, projection consistency, precedence, privacy; move the route/JSON/error catalog to `docs/agent-api.md` where it is public contract; convert tables.
- `design/agent-execution.md` — trim: delete the stale `ReconcileRunningAsync` status entry; delete the duplicated CAS step list (owned by `design/conventions.md`); convert tables; keep the diagrams.
- `design/agent-mentions.md` — keep.
- `design/agent-runtime-reasoning-capability.md` — trim: drop the "Implementation boundary" delivery paragraph.
- `design/agent-subscriptions.md` — trim: keep the boundary statement and read-state semantics; remove the DTO/route/status dump (code expresses it); fix readiness-value casing.
- `design/agent-supervision.md` — keep: convert the one table.
- `design/auth.md` — trim: convert the two step-list diagrams to prose; drop the retired-header narration; convert tables; keep the drivers and boundary content.
- `design/cli.md` — trim: delete the test acceptance contract section; convert tables; drop the one-line diagram; keep the trade-off rationale.
- `design/domain-analysis.md` — trim: drop the "issue 417" reference; keep either the diagram or the normative table, not both (table is declared normative — drop the diagram); convert remaining tables.
- `design/db-migrations.md` — keep: move the "Current accepted deltas" snapshot to `design/decisions/squashed-baseline.md` (R23).
- `design/event-protocol.md` — trim: drop the dead `CatalogOnlyTypes` reference and the #412 footnote; convert the two tables; keep the grammar.
- `design/event-response.md` — trim: fix `--author` → `--display-name`; drop the duplicated launch-idempotency rule (R5).
- `design/event-routing.md` — trim: fix the dead `mo workflow approve`; cut the migration mapping and CLI dump; drop the #532 reference; own the launch-idempotency rule (R5).
- `design/eventbus.md` — trim: drop the storage column and metric-name detail; move the test-observation contract to `design/testing.md` if durable, else delete; define DLQ at first use.
- `design/github-integration.md` — trim: cut the translator pseudocode and secret-address dumps; drop the follow-up/implementation-plan wording; convert the table.
- `design/hermes-webhook.md` — trim: replace the four tables with the JSON example plus prose; keep the wire contract.
- `design/issue-breakdown.md` — trim: strip the issue numbers from the decision history (keep the reasoning); convert the table.
- `design/issue-list-read.md` — trim: keep the three drivers and the cost/invalidation boundaries; cut the field lists and per-event invalidation enumeration; drop the test acceptance checks.
- `design/issue-templates.md` — trim: convert the six tables (the two loading-phase tables say the same thing twice — one prose pass); drop the storage-migration line.
- `design/issue-watch.md` — trim: drop the pseudocode blocks and the #532 footnote; link the idempotency rule (R5).
- `design/observability.md` — trim: remove config-key/rotation/file-name specifics and the verification contract; keep budgets as constraints and the line contract; fix the bare fence (R24).
- `design/outbound-webhook.md` — trim: cut the field table, secret-address scheme, CLI dump, and migration name; keep the v1 boundary and payload example.
- `design/prompt-management.md` — keep: drop one of the two duplicated resolution flows (keep the diagram or the pseudocode — prefer the diagram, it is architecture); fix the section-name cross-ref.
- `design/repositories.md` — keep: convert the failure table; drop the four-step diagram if a sentence covers it.
- `design/runner-runtime-readiness.md` — merge into `design/runner.md`; delete the file (R10).
- `design/runner.md` — trim: absorb the witness contract (R10); absorb the runner internals from `docs/runner.md`; collapse the failure/supervision/persisted-file tables into invariants; keep the decision record and dispatch protocol; convert remaining tables; drop the "tracked for removal" wording.
- `design/scheduled-input.md` — trim: cut the verification contract and route/error-code detail; keep the durable-intent boundary and lifecycle rules.
- `design/session-timeline.md` — trim: drop the delivery-tracking sentence and the mapping table; keep derivation principles and examples.
- `design/slack.md` — trim: convert the 26-row decision table to short prose entries; compress the provisioning/lease procedures into invariants; fix readiness-value casing; add the Buzz gloss (R20); drop the future-phase roadmap wording.
- `design/subagents.md` — trim: absorb the race arbitration from `docs/subagents.md`; remove the verification contract and the redundant procedure/diagram pairs; fix readiness code-spellings; convert tables.
- `design/task-log.md` — keep: convert the two tables; drop the tracking sentence.
- `design/testing.md` — move per R11: gate internals → `scripts/test-duration/README.md` (new); focused-test how-to → `CONTRIBUTING.md`; keep tracks/hard rules/guards trimmed of tables and roadmap items; fix the `test:browser` command.
- `design/web-ui.md` — keep: convert the ownership table; fix readiness value spelling; drop the one-line diagram.
- `design/workspace.md` — keep: convert the reclamation table.
- `design/decisions/epic-status-revival.md` — keep: trim the issue number from the title.
- `design/decisions/issue-owns-epic-membership.md` — keep: trim the issue number from the title; convert the failure table.
- `design/runtimes/README.md` — keep: trim the route/DTO dump and the "outside this change" wording.
- `design/runtimes/opencode.md` — keep: trim the command table, SDK signatures, and migration-era handling; delete the duplicated Action DTO and link to `design/workflow/actions.md` (R6); drop the issue-409 archive-path reference.
- `design/runtimes/pi.md` — keep: same treatment as opencode.md (R6); drop the issue-409 path.
- `design/workflow/actions.md` — keep: fix `mo run validate` → `mo workflow validate --file`; own the Action input contract with the correct `PromptSpec` shape (R6); convert the capabilities table; trim the status churn.
- `design/workflow/builtin-workflows.md` — keep: reword "review gate"/"delivery gate" to approval-point wording (R17).
- `design/workflow/definition.md` — keep: convert the two tables (the rules table becomes a list); absorb the validator-ownership paragraph from `docs/workflow-definition.md`.
- `design/workflow/issue-coordination.md` — keep: convert the authority table; cut the "Other Interactions" diagram/catalog to a short list.
- `design/workflow/profile.md` — keep: convert the two tables; move the route dump toward `docs/` or delete as code-expressed; resolve the `status: implemented` vs "Planned" contradiction (drop the stale one); compress the fence/replay narration to invariants.
- `design/workflow/recovery.md` — keep: drop "issue #465"; convert the table; compress the executor pseudocode into rules; drop the unjustified two-branch diagram.
- `design/workflow/run-state.md` — keep: trim the SQLite backup procedure and field-name mentions.
- `design/workflow/task-dispatch.md` — keep: convert the 15-row context table to prose/list; compress the parent-context mechanics; absorb the `archiveHint` internals from `docs/actions/openspec.md`.
- `design/workflow/variables.md` — keep: convert the four tables; neutral placeholders for vendor model names (R15); move the `WorkflowRunProfile` misnomer section to `design/decisions/`; drop the "open question is resolved" wording.

## 4. Execution batches (disjoint file sets, run in parallel)

- B1 root+templates: `AGENTS.md` (no-op check), `CLAUDE.md`, `CONTEXT.md`, `README.md`, `.github/PULL_REQUEST_TEMPLATE.md`, `test/agentic/*`.
- B2 docs core: `docs/README.md`, `docs/_agents.md`, `docs/getting-started.md`, `docs/vision.md`, `docs/concepts.md`, `docs/the-workflow.md`, `docs/issues.md`, `docs/epics.md`, `docs/sub-issues.md`, `docs/repositories.md`, `docs/workspaces.md`.
- B3 docs agent/API: `docs/agent-api.md`, `docs/agent-sessions.md`, `docs/agent-supervision.md`, `docs/subagents.md`, `docs/skills.md`, `docs/runner.md`, `docs/workflow-definition.md`, `docs/workflow-profiles.md`, `docs/event-routing.md`.
- B4 docs integrations/ops/actions: `docs/github.md`, `docs/slack.md`, `docs/hermes-notifications.md`, `docs/auth.md`, `docs/web-ui.md`, `docs/observability.md`, `docs/self-host.md`, `docs/troubleshooting.md`, `docs/cli-reference.md`, `docs/actions/*`, plus the `docs/mobile-pwa.md` → `design/decisions/mobile-pwa.md` move.
- B5 design A: `design/README.md`, `design/agents.md`, `design/architecture.md`, `design/conventions.md`, `design/agent-api.md`, `design/agent-execution.md`, `design/agent-mentions.md`, `design/agent-runtime-reasoning-capability.md`, `design/agent-subscriptions.md`, `design/agent-supervision.md`, `design/auth.md`, `design/cli.md`, `design/domain-analysis.md`, `design/db-migrations.md` (+ creates `design/decisions/squashed-baseline.md`).
- B6 design B: `design/event-protocol.md`, `design/event-response.md`, `design/event-routing.md`, `design/eventbus.md`, `design/github-integration.md`, `design/hermes-webhook.md`, `design/issue-breakdown.md`, `design/issue-list-read.md`, `design/issue-templates.md`, `design/issue-watch.md`, `design/observability.md`, `design/outbound-webhook.md`, `design/prompt-management.md`, `design/repositories.md`, `design/runner-runtime-readiness.md`, `design/runner.md`, `design/scheduled-input.md`, `design/session-timeline.md`, `design/slack.md`, `design/subagents.md`, `design/task-log.md`, `design/testing.md`, `design/web-ui.md`, `design/workspace.md`, plus `CONTRIBUTING.md` and new `scripts/test-duration/README.md` (R11).
- B7 design subdirs: `design/decisions/epic-status-revival.md`, `design/decisions/issue-owns-epic-membership.md`, `design/runtimes/*`, `design/workflow/*`.

Cross-batch handoffs are pre-assigned so no two batches edit the same file: R7 inside B2; R8 inside B4; R21 inside B3; R5/R10 inside B6; R6 inside B7; R11 B6 owns both targets; docs→design moves (`docs/runner.md` internals → `design/runner.md`; `docs/subagents.md` race → `design/subagents.md`; `docs/actions/openspec.md` archiveHint → `design/workflow/task-dispatch.md`) — the source-side editor (B3/B4) only deletes and links; the target-side editor (B6/B7) writes the absorbed content from the audit description; both sides use the same one-paragraph summary stated in this plan.

Absorbed-content summaries (canonical text both sides rely on):
- Runner internals (docs/runner.md → design/runner.md): "When a Runtime Session quarantines or a Runner shuts down, the Runner drains in-flight work before it releases ownership. Two env-var budgets bound the drain: `QUARANTINE_DRAIN_TIMEOUT_MS` (default 60s) and `RUNTIME_SHUTDOWN_TIMEOUT_MS` (default 30s). Results produced during drain are journaled so a restart can settle them exactly once."
- Subagent race arbitration (docs/subagents.md → design/subagents.md): "Stop, detach, and spawn can race. The Session owner arbitrates: a stop that lands before a spawn completes cancels the spawn; a detach that lands first removes the subtree from stop scope. The full concurrency protocol lives in design/subagents.md."
- archiveHint (docs/actions/openspec.md → design/workflow/task-dispatch.md): "`mohist/archive-change` receives its `archiveHint` input from the dispatch snapshot at run start, not from live Issue state, so a mid-run change of the Issue's archive intent does not alter a running archive."

## 5. Verification (after execution)

1. `npm run docs:check` (bounded docs check) in the worktree.
2. `git diff --check` (whitespace/conflict markers).
3. Changed-path review: `git status` / `git diff --stat` must show only docs, templates, plans, and the two new docs (`scripts/test-duration/README.md`, `design/decisions/squashed-baseline.md`, `design/decisions/mobile-pwa.md`); no source or test changes.
4. Open-decision review: list any architecture conflicts or ownership disputes left unresolved.
5. Residual sweep: grep the changed docs for issue/PR references (`#[0-9]+`, `/issues/`, `/pull/`), vendor model names, and tables (`^|`); confirm zero hits outside legitimate product-domain Issue examples.
6. `npm run verify` (full gate) as the final step.
