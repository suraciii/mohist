## Context

The [proposal](proposal.md) establishes the motivation and the [action-contract-pages specification](specs/action-contract-pages/spec.md) is the normative contract. Today only `mohist/opencode` and `mohist/pi` have product contract pages under `docs/actions/`; the Git (`git.md`) and GitHub PR (`github-pr.md`) group pages list only required inputs and omit outputs, error codes, and usage examples; and eight supported Actions have no page at all (`mohist/github-pr-checks`, `core/process`, `core/script`, `core/artifact-exists`, `core/marker`, `mohist/openspec-tasks`, `mohist/openspec-artifacts`, `mohist/archive-change`). The Action manifest collection in `packages/runner/src/actions/built-ins.ts:38` is the authoritative source for inputs (name, accepted kinds, required/default), output fields, and business error codes; the `mohist/pi` page is the format precedent.

Constraints inherited from the issue and `docs/README.md` writing rules:

- **Product docs only.** `docs/` uses product + domain language and forbids technical language (no API endpoints, field names as wire types, component classes, or source paths except a single per-page footer).
- **Authoritative source.** Content must mirror the manifest collection without introducing or omitting declared inputs, outputs, or error codes.
- **No runtime change.** Manifests, execution functions, dispatch validation, catalog publication, profiles, and the `mohist/pi` runtime-gap note are out of scope.
- **Chinese prose; preserved symbols.** Body prose is Chinese; configuration field names, error codes, Action names, and YAML keys stay in their original form.

Stakeholders: workflow authors writing `uses`/`with` task and check definitions; the epic #51 "Action 插件模型" tracking the manifest contract surface.

## Goals / Non-Goals

**Goals:**

- Publish one contract page section per supported built-in Action, each with the three facets (inputs with required/kinds/default, outputs, business error codes) plus a directly usable example.
- Fill the eight currently-undocumented Actions and expand the existing Git and GitHub PR group pages beyond input-only.
- Make `docs/actions/README.md` enumerate every supported built-in Action with a link to its contract page; remove the "OpenSpec 和 `core/*` 的独立产品契约页仍待补齐" gap footnote.
- Keep each page consistent with the manifest in `built-ins.ts` so that reading docs alone is sufficient to write a correct task.

**Non-Goals:**

- No auto-generation of docs from the catalog, no new build step, and no test harness that parses `docs/actions/*.md`.
- No changes to manifests, execution functions, runner, server, CLI, Web, dependencies, or stored data.
- No design-layer content (`design/workflow/actions.md` Status, runtime internals); the only permitted `design/` edit is updating the existing "文档缺口" status footnote to reflect that the docs gap is closed.
- No pages for tombstoned Actions (e.g. `mohist/acp-agent`).
- No edits to `mohist/pi`'s existing 实装差距 note beyond keeping it intact.

## Decisions

### D1 — Group pages by family; one section per Action

Contract pages stay grouped by family rather than one file per Action, mirroring the existing `git.md` and `github-pr.md` layout. Final layout under `docs/actions/`:

- `opencode.md` — `mohist/opencode` (already complete).
- `pi.md` — `mohist/pi` (already complete; gap note preserved).
- `git.md` — `mohist/workspace-prepare`, `mohist/rebase`, `mohist/rebase-status`, `mohist/merge-ready`, `mohist/push` (expand each to three facets + example).
- `github-pr.md` — `mohist/create-github-pr`, `mohist/mark-github-pr-ready`, `mohist/merge-github-pr`, `mohist/github-pr-status`, **plus** `mohist/github-pr-checks` (new section).
- `core.md` (new) — `core/process`, `core/script`, `core/artifact-exists`, `core/marker`.
- `openspec.md` (new) — `mohist/openspec-tasks`, `mohist/openspec-artifacts`, `mohist/archive-change`.

Each Action gets a level-2 section with the same four blocks in the same order: a one-sentence purpose, an inputs table (`字段 | 必填 | 默认 | 含义`), an outputs table (`字段 | 含义`), an error code table (`错误码 | 含义`), and a copy-pasteable YAML example.

**Alternatives considered:**

- *One file per Action (19 files).* Rejected: simple Git/PR/core/OpenSpec Actions have nothing to say beyond the three facets and would produce many near-empty files; the existing group pattern already works for tightly related families. `opencode.md`/`pi.md` stay separate because they already carry substantial Session semantics beyond the manifest.
- *Move `mohist/opencode` and `mohist/pi` into a single "agent" page.* Rejected: both already work as standalone pages and carry distinct Session, runtime, and responsibility-boundary content; merging forces either trimming that content or duplicating it.

### D2 — Manifest is the authority; pages mirror it by hand

Documentation is hand-written Chinese prose that mirrors the manifest declarations in `built-ins.ts`. No code generation, no catalog snapshot embedded in docs, and no test that parses Markdown. The accepted kinds are translated to product-facing terms (`string → 文本`, `number → 数值`, `boolean → 布尔`, `array → 数组`, `object → 对象`) while the underlying symbol stays recognizable from the YAML keys. Aliases are documented once per Action (e.g. `core/marker.contains` as the legacy alias of `expect`, `mohist/create-github-pr.message` as an alias of `title`).

Platform-reserved codes (`invalid-input`, `unexpected-error`, `timeout`) are not listed as Action business errors; each page's error code table is preceded by one sentence noting that the platform also produces those codes for dispatch validation and deadline failures, so recovery handlers can match them.

**Alternatives considered:**

- *Auto-generate the tables from the manifest catalog at build time.* Rejected: `docs/` is hand-curated Chinese product prose with usage examples, responsibility-boundary notes, and cross-references to `workflow-definition.md`/`workflow-profiles.md` that cannot be derived from a JSON catalog. Auto-generation would also couple docs builds to the runner package and the docs/ tooling has no such build step today.
- *Embed JSON catalog snippets in each page.* Rejected: violates the `docs/` rule against technical language (wire/field shapes) and would drift from the prose.

### D3 — Examples are self-contained and bind Variables explicitly

Every Action section ends with one YAML snippet that a reader can paste into a Workflow definition stage. Each snippet:

- Uses only inputs declared by the manifest.
- Binds every required input to a literal or to an identified template expression (`${{ repository.* }}`, `${{ workspace.* }}`, `${{ issue.* }}`, `${{ vars.* }}`).
- Names any Variable it references and points to `workflow-definition.md` for the Variables model.
- For delivery Actions, mirrors the explicit-binding pattern already proven in `mohist/github-pr` (post-#445): repository/branch/remote/PR identity come from `with`, never from implicit Variable fallback.

The snippet is the smallest correct usage; recovery and elaborate stage wiring stay in `workflow-profiles.md`. When an Action is commonly used as a stage check rather than a stage task (e.g. `core/script` for `git diff --check`, `mohist/github-pr-status` for `merged` verification), the example shows the `checks:` block form.

**Alternatives considered:**

- *One snippet per common usage.* Rejected: multiple snippets per Action invite drift and bloat; one canonical snippet plus a one-line pointer to the bundled profile covers the common cases.
- *Use `${{ vars.* }}` without naming the Variable.* Rejected: violates the spec scenario "every input referenced by a snippet SHALL be either a literal value or a `${{ }}` expression whose binding source is identified by the page".

### D4 — Overview enumerates every supported Action; gap footnote is removed

`docs/actions/README.md` "当前 Action" section becomes a flat enumeration of every supported built-in Action grouped by family, each entry linking to its section anchor or page. The "实装差距" section drops the bullet "Git Actions 和 GitHub PR Actions 的输入契约见对应产品契约页。OpenSpec 和 `core/*` 的独立产品契约页仍待补齐." and keeps only the `mohist/pi` unimplemented-runtime bullet plus the dispatch-validation note. The design-layer status note in `design/workflow/actions.md` "Status" item 5 ("文档缺口…") is updated to reflect that the gap is closed (one-line edit, no new design content).

**Alternatives considered:**

- *Leave a residual "partial coverage" footnote.* Rejected: the spec scenario "The remaining-Actions gap footnote is removed" forbids it, and the acceptance criterion explicitly requires removal.
- *Restructure `README.md` into a capabilities matrix.* Rejected: scope creep; the existing simple enumeration is sufficient and matches the rest of `docs/`.

### D5 — Verification is a manual manifest cross-check

There is no automated docs test. Verification is a per-Action manual diff between the manifest entry in `built-ins.ts` and the rendered table, plus a paste-test of each example into a representative profile to confirm the YAML parses and required inputs are bound. The reviewer checklist mirrors the spec scenarios: every declared input/output/error appears; no undeclared symbol appears; platform codes are not listed as Action business errors; the overview links resolve; the gap footnote is gone; `mohist/pi`'s gap note is intact.

**Alternatives considered:**

- *Add a docs lint test that parses Markdown tables and compares them to the catalog.* Rejected: would require new tooling, a Markdown parser, and a place to mount the test; the cost is disproportionate for a low-risk docs change and there is no existing precedent for docs linting in this repo. Recorded as an open question if drift becomes a recurring problem.

## Risks / Trade-offs

- **[Docs drift from manifests after future manifest edits]** -> Manual cross-check at review time; the design-layer status note now points editors to `built-ins.ts` as the authority. If drift recurs, revisit D5 and add a catalog-vs-docs lint in a follow-up issue.
- **[Overview links break when section anchors move]** -> Use stable anchor names derived from the Action `name`; verify each link resolves as part of D5.
- **[Translator prose diverges on accepted-kind terminology]** -> Pin the kind→Chinese term mapping in D2 and apply it consistently across every page.
- **[Example snippets become invalid after future input changes]** -> Each snippet's required inputs are the contract surface; any future manifest change that affects them is itself a contract change and requires a docs update in the same issue.
- **[Reader mistakes platform codes for Action codes]** -> Each error code table is preceded by an explicit one-sentence note attributing `invalid-input`/`unexpected-error`/`timeout` to the platform.

## Migration Plan

Documentation-only change; deploy = merge to `master`; rollback = revert the merge commit. No data migration, no schema, no server state, no runner coordination.

Implementation order (each step lands before the next):

1. Create `docs/actions/core.md` and `docs/actions/openspec.md` with the full three-facet sections and examples for the eight new Actions.
2. Expand `docs/actions/git.md` and `docs/actions/github-pr.md` to add outputs, error codes, and examples for every existing Action; add the missing `mohist/github-pr-checks` section to `github-pr.md`.
3. Update `docs/actions/README.md` enumeration and remove the remaining-Actions gap footnote per D4.
4. Update the single status line in `design/workflow/actions.md` Status item 5.
5. Manual manifest cross-check (D5) for all 19 supported Actions; paste-test representative examples.

Rollback strategy: revert the merge commit. Docs return to their pre-issue state.

## Open Questions

- **Docs lint.** Should a future issue add an automated catalog-vs-docs consistency check (parsing `docs/actions/*.md` tables against `builtInActions`)? This issue keeps verification manual; automation is a candidate follow-up if drift becomes recurrent.
- **Per-Action pages for future Actions.** If a future built-in Action carries substantial product semantics beyond its manifest (like `mohist/opencode`), should the default remain "group page section" or shift to "dedicated page"? This issue keeps the group default; the call can be made per-Action when such a case arises.
