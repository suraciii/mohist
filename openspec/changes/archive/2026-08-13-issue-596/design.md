## Context

The proposal identifies CLI help as Mohist's executable navigation map for both
people and Agents. The registered command tree has grown, but its separately
curated presentation catalog has not: root help omits `workspace`, `audit`,
`github`, and `slack`, and several existing groups and leaves have no explicit
presentation metadata.

`MohistCliCommands.Build` is the syntax authority. It creates the complete
`System.CommandLine` tree, installs the local help action, and then calls
`CommandPresentations.AttachTo` to attach `CommandPresentation` records to the
same `Command` instances. `CommandHelpRenderer` already derives invocation
paths, arguments, visible options, and most JSON field lists from that live
tree. However, root rendering currently filters out commands without metadata,
while group and leaf rendering can fall back to each command's registration
description. Both behaviors allow missing curated help to pass silently.

The implementation must preserve these constraints from the CLI help spec:

- Help and parser-usage output are computed locally and do not resolve a
  Project, contact the Server, prompt, launch a process, or invoke a command
  action.
- Root, group, and leaf help remain progressively scoped.
- Registered command names, arguments, options, execution behavior, output,
  requests, and exit semantics do not change.
- Hidden commands and options remain absent from the public discovery surface.

The affected stakeholders are CLI users and Agents that discover commands
through help, CLI maintainers adding command nodes, and documentation
maintainers keeping `docs/cli-reference.md` aligned with the executable surface.

## Goals / Non-Goals

**Goals:**

- Give every visible command node an explicit, one-sentence product
  presentation, and give every visible root child an explicit capability
  classification.
- Render all visible root commands exactly once, including `workspace` under
  Work and `audit`, `github`, and `slack` under Operations.
- Preserve direct-child group discovery and exact-path leaf help at arbitrary
  command depth.
- Add an automated structural gate that traverses the registered tree and
  reports every missing presentation by complete `mo ...` invocation path.
- Keep JSON field help tied to the leaf's existing resource descriptor.
- Update the CLI reference's root map and implementation-gap text to match the
  delivered help surface.

**Non-Goals:**

- Adding, removing, renaming, or aliasing commands. In particular, the current
  registered `github update` leaf is not changed to `github edit` here.
- Changing command handlers, Server APIs, request payloads, result rendering,
  authentication, or Project resolution.
- Expanding root help into a full command tree or expanding group help beyond
  direct children.
- Generating the complete CLI reference from the runtime command tree.
- Requiring presentations for hidden command nodes or hidden options while
  they remain hidden.

## Decisions

### 1. Treat the registered command tree as the discovery authority

All coverage checks and rendering will walk the `Command` instances produced by
`MohistCliCommands.Build`. Presentation metadata remains attached after the
tree is fully registered, so metadata annotates syntax rather than recreating
it. Traversal includes the root and every non-hidden descendant and constructs
paths from parent relationships, beginning with `mo`.

This keeps command existence, hierarchy, arguments, options, and visibility in
one authority. `CommandPresentations` remains the curated authority only for
product summaries, capability grouping, boundaries, notes, examples, and the
few JSON field overrides that cannot be inferred from an option descriptor.

**Alternatives considered:**

- Use a second path manifest as the expected command tree. Rejected because it
  would duplicate registration and could drift in the same way as the current
  help catalog.
- Use `Command.Description` as automatic coverage. Rejected because syntax
  descriptions include implementation-oriented text and cannot express root
  capability classification; the spec also requires explicit presentation
  coverage rather than fallback text.

### 2. Complete the explicit presentation catalog and validate it structurally

`CommandPresentations.AttachTo` will be extended for all currently uncovered
areas and descendants. This includes dedicated coverage for `workspace`,
`audit`, `github`, and `slack`, plus validation-reported omissions in existing
areas such as `agent spawn`, `agent subscription`, `session tree`, `session
detach`, `session schedule`, and `otel traces`. Coverage is not limited to this
known list: the completed tree traversal is the acceptance criterion.

Add a small presentation-coverage validator in the CLI assembly. Given a root
command, it returns stable diagnostics for every visible node that lacks an
explicit non-empty `CommandPresentation`. A missing presentation on a direct
root child is reported as missing root classification/presentation; a missing
descendant is reported as missing presentation. Every diagnostic includes the
full path, for example `mo session schedule cancel`.

The validator reads only `CommandPresentationCatalog`; it must never accept
`Command.Description` as satisfying coverage. The CLI test project can access
the validator through the existing `InternalsVisibleTo` relationship. A test
builds the real root with fakes and requires an empty diagnostic set. Separate
synthetic-tree tests prove that missing root and nested metadata produce the
required full-path diagnostics.

**Alternatives considered:**

- Validate only the four missing root areas. Rejected because it would fix the
  present symptom but allow the next registered leaf to become undiscoverable.
- Fail every normal CLI invocation during startup when metadata is incomplete.
  Rejected because completeness is a build/test acceptance concern and should
  not add a new runtime failure mode to non-help commands. Renderers will still
  require explicit metadata, so a bad build cannot silently omit a command.

### 3. Make rendering complete, required, and progressively scoped

Root rendering will enumerate every visible direct child, require its explicit
presentation, group it by `CommandCapability`, sort entries deterministically,
and render it once. It will no longer filter out children with missing
metadata. It will continue to omit descendants, arguments, and options.

Group rendering will enumerate only visible direct children and require each
child's explicit summary. Nested groups use the existing parent-derived path
for usage and further-help guidance. Leaf rendering will continue to derive the
complete path, arguments, and visible options from the selected command and
will not enumerate siblings or descendants.

For `--json` help, the resource descriptor already attached to the leaf option
remains the preferred field source. `CommandPresentation.JsonFields` and
`JsonFieldGroups` remain for explicit overrides such as leaves with multiple
result shapes. Presentation completion will not duplicate field lists that are
already represented by a descriptor.

**Alternatives considered:**

- Use the default `System.CommandLine` help renderer. Rejected because it does
  not provide Mohist capability groups, resource boundaries, scoped further
  help, or the existing JSON field contract.
- Recursively render descendants in root or group help. Rejected because it
  would make root help noisy and violate the progressive-discovery requirement.

### 4. Keep help and usage on the existing local parse path

`CommandHelpHook.LocalHelpAction` remains the only action used for `--help`.
It selects root, group, or leaf rendering from the parsed command and does not
invoke the operational action. Parse failures continue through
`RenderNearestUsage`, which finds the nearest recognized command and renders a
complete invocation path with exit code 2.

Regression tests will request representative root, nested-group, and deep-leaf
help with no active Project and a rejecting HTTP handler. They will assert no
HTTP requests or external command executions. An unknown action below a nested
group will assert scoped stderr usage, exit code 2, and no operational effects.

**Alternatives considered:**

- Build a separate parser just for help. Rejected because it would create a
  second hierarchy and could disagree with the parser used for execution.
- Discover help by running command handlers in a dry-run mode. Rejected because
  handlers can resolve Projects, prompt, or access external dependencies.

### 5. Use structural and focused rendering tests instead of snapshots

The primary completeness test traverses the entire real tree, so new visible
commands fail verification automatically. Focused output tests cover the
contract that structure alone cannot prove:

- Root classifications, exact-once entries, and absence of descendants and
  leaf flags.
- `workspace --help` direct children without expanding `workspace repo`.
- `session schedule --help` direct actions and complete nested path.
- `workspace repo add --help` exact usage, arguments, and visible options.
- `otel traces --help` exact usage and descriptor-backed JSON fields.
- Representative recently added children under `agent`, `session`, and `otel`.
- Nearest nested usage errors and local, side-effect-free behavior.

Whole-output snapshots are avoided because option wording and wrapping are not
the invariant. Existing command execution tests remain the regression guard for
the non-help behavior that this change must preserve.

**Alternatives considered:**

- Snapshot every help page. Rejected because snapshots would be large and
  brittle while still requiring a separate check that every registered node
  has a page.
- Assert only selected command names in root help. Rejected because that is the
  current failure mode: a growing hand-maintained expected subset cannot prove
  completeness.

### 6. Align the CLI reference without changing unrelated command contracts

Update the root command map in `docs/cli-reference.md` to include `audit` under
Operations, and add its current `list` action to the operations table. The
existing entries for `workspace`, `github`, and `slack` remain under their
specified capabilities. Remove the implementation-gap statement that these
areas are absent from root help.

The separate documented gap between the canonical `github edit` vocabulary
and the currently registered `github update` command remains. This design adds
presentation coverage for the command that actually exists but does not resolve
that unrelated rename because the change explicitly preserves command syntax.

**Alternatives considered:**

- Generate the reference from runtime metadata. Rejected for this change
  because the reference also contains product semantics and future canonical
  gaps that are not executable metadata.
- Rename `github update` while aligning the reference. Rejected because it
  would violate the no-command-surface-change requirement.

## Risks / Trade-offs

- [A presentation can be present but semantically stale] -> Keep summaries
  product-oriented, add focused assertions for the newly covered commands, and
  review the CLI reference and presentation changes together. Structural
  validation guarantees completeness, not prose correctness.
- [The centralized catalog remains an additional maintenance obligation] ->
  The live tree, not the catalog, defines command existence, and the structural
  test makes any registration/catalog drift an immediate verification failure.
- [A path typo in `CommandPresentations` can silently attach nothing] -> The
  real-tree validator reports the actual uncovered full path; renderer lookup
  no longer falls back to registration descriptions.
- [Hidden nodes may later become public without metadata] -> Traversal skips
  only nodes whose current `Hidden` flag is true. Making one visible causes the
  coverage test to fail until a presentation is added.
- [Large integration areas such as Slack make manual coverage verbose] -> Keep
  metadata to one-sentence behavior summaries and reuse small local helpers only
  where commands genuinely share semantics; do not introduce a parallel
  command-definition DSL.
- [Documentation can drift after this issue] -> Root completeness is enforced
  in executable tests, and this migration updates the reference in the same
  change. Automatic manual generation remains out of scope.

## Migration Plan

1. Add explicit presentations and root classifications for every visible node
   in the current built command tree.
2. Add the pure tree-coverage validator and tests for the real tree plus
   synthetic missing-root and missing-descendant cases.
3. Change root, group, and leaf rendering to require presentation metadata and
   retain the current progressive scoping and local help action.
4. Add focused root, group, leaf, JSON-field, nested-usage, and side-effect
   regression tests.
5. Update `docs/cli-reference.md` to add `audit` to the Operations map and
   remove the resolved root-help gap.
6. Run `npm run test:fast` during implementation and the full `npm run verify`
   gate before delivery.

No data, API, Server, or configuration migration is required. The change ships
with the CLI package. Rollback is a normal CLI code/documentation revert; no
persistent state is written by help, and command execution contracts are
unchanged.

## Open Questions

None. The specification fixes capability placement, visible-tree coverage,
progressive scope, local behavior, and the prohibition on command-surface
changes. The existing `github update` versus canonical `github edit` gap is a
separate change rather than an unresolved decision for this design.
