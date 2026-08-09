# Documentation Language and Diagram Migration

## Goal

Make the active Mohist documentation one coherent English corpus with portable
ASCII diagrams and deterministic documentation checks.

The active documentation surface is:

- `README.md`, `CONTEXT.md`, and `CONTRIBUTING.md`;
- `docs/**/*.md` for product behavior and user guidance;
- `design/**/*.md` for architecture and implementation design.

Historical OpenSpec artifacts, release notes, generated content, third-party
notices, and existing implementation plans are outside this migration. They
remain historical records and must not be rewritten to satisfy a current-doc
style rule.

## Authorities

- `CONTEXT.md` is the only glossary for cross-context product terms. It defines
  terms, boundaries, and discouraged aliases. It contains no implementation
  decisions.
- `docs/vision.md` owns product positioning and the interaction model.
- Each product rule has one authoritative document under `docs/`. Other product
  documents link to it instead of restating it.
- `design/architecture.md` and `design/domain-analysis.md` own system boundaries
  and context relationships. Topic design documents own implementation
  semantics within those boundaries.
- `docs/_agents.md`, `docs/README.md`, and `design/README.md` must state the same
  language and diagram policy.

## Writing Contract

1. Active prose is English. Use short sentences, active voice, American
   spelling, and one stable term for each concept. Use ASD-STE100 writing rules
   as a target, not as a compliance claim.
2. Preserve commands, identifiers, field names, API paths, serialized values,
   and quoted external text when their exact spelling is part of the contract.
3. Preserve product semantics. Translation must not change lifecycle,
   ownership, ordering, failure behavior, status, or implementation-gap claims.
4. Use `must`, `may`, and `must not` for requirements, options, and
   prohibitions. Do not use a new umbrella term to hide an existing domain
   distinction.
5. Use `Approval`, `approval point`, and `approve` / `reject`. An Agent is a
   proxy that can occupy a workflow role that a person can also occupy. Do not
   introduce an Agent-only decision channel.
6. Lead conceptual sections with the problem, constraint, or trade-off that
   explains why the behavior or boundary exists. Describe the macro product or
   system model before exact mechanics.
7. Do not rewrite classes, methods, call chains, storage operations, or source
   layout as prose. Keep exact commands, fields, and algorithms only in task,
   reference, or contract sections where the detail resolves real ambiguity.

## Diagram Contract

- Use fenced `text` blocks and ASCII characters for diagrams.
- Add a diagram only when it clarifies a system boundary, ownership relation,
  dependency direction, sequence, hierarchy, or state transition.
- Give every arrow a meaning. Keep the normative rule in prose next to the
  diagram.
- Prefer a table for exact mappings and numbered steps for a linear procedure.
- Do not add PlantUML or Mermaid source. Convert the three existing PlantUML
  blocks to small ASCII diagrams.
- Keep diagrams narrow enough to read in a normal Markdown viewport. Split a
  large diagram by concern instead of shrinking labels or drawing decoration.

## Slices and Parallel Write Sets

| Slice | Write set | Depends on | Result |
|---|---|---|---|
| S0 | this plan | none | Scope, authorities, and gates are fixed before migration |
| S1 | `README.md`, `CONTEXT.md`, `docs/README.md`, `docs/_agents.md`, `docs/vision.md`, `docs/concepts.md`, `docs/getting-started.md` | S0 | The reader entry path and glossary are English and consistent |
| S2 | remaining `docs/**/*.md` | S0, S1 terminology | Product specs and user guidance are English without semantic changes |
| S3 | `design/README.md`, `design/architecture.md`, `design/domain-analysis.md`, then remaining `design/**/*.md` | S0, S1 terminology | Design corpus is English; architecture and context maps use ASCII diagrams |
| S4 | `scripts/check-docs.ts`, tests, baseline or configuration, `package.json`, `package-lock.json` | S0 | Language, links, anchors, and diagram format are checked deterministically |
| S5 | cross-file links and final integration fixes | S1-S4 | One internally consistent corpus passes all gates |

Parallel workers must own disjoint files. Shared indexes, the glossary, package
scripts, and final anchor repairs each have one owner. Workers do not commit,
push, or create PRs; the integration owner reviews and commits complete slices.

## Migration Order

1. Commit this plan on a branch created from the current `origin/master`.
2. Migrate the entry path and canonical terminology first.
3. Migrate product and design topic clusters in parallel. Translate headings
   and all inbound links to those headings in the same integration batch.
4. Convert PlantUML and Unicode line-art diagrams to fenced ASCII text.
5. Enable the documentation gate only after the corpus satisfies it. Do not
   hide remaining non-English prose behind a permanent allowlist.
6. Review the migrated corpus for design intent. Remove code narration and
   ensure high-level documents explain their problem, constraints, boundaries,
   and material trade-offs.
7. Run focused documentation checks, then the complete local gate on the final
   commit.

## Acceptance

- Active documentation contains no Han-script prose.
- The language rules in `docs/_agents.md`, `docs/README.md`, and
  `design/README.md` agree.
- `CONTEXT.md` remains a glossary and contains no migration or implementation
  policy.
- No PlantUML, Mermaid, Unicode box-drawing, or Unicode arrow glyph remains in
  active documentation diagrams.
- `README.md` includes one concise product/system overview diagram.
- `design/domain-analysis.md` includes a high-level context relationship map.
- Existing workflow, concept, session-tree, and architecture diagrams retain
  their meaning after conversion.
- Concept and design documents explain why their major boundaries exist and do
  not narrate implementation code step by step.
- Every relative Markdown link resolves, and every local heading fragment
  resolves after English heading migration.
- The getting-started path installs or invokes `mo` before its first use.
- `npm run docs:check`, `git diff --check`, and `npm run verify` pass on the
  final branch. Failures must be reported as source, test, or environment
  failures; they must not be hidden with skips or relaxed checks.

## Delivery

Use a dedicated documentation branch and one pull request. Keep commits
reviewable by concern:

1. plan;
2. entry path and policy;
3. product documentation migration;
4. design documentation migration and diagrams;
5. deterministic checks and integration fixes.

The pull request must state the exact migration scope, checks run, remaining
out-of-scope historical content, and the fact that this is a prose/diagram
change with no runtime behavior change.
