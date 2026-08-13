# Self Review Report

## Verdict: PASS

## Must-Fix Findings

None. The plan is ready to build against the issue's current contract.

## Observations

### O-001: The live issue has no body-level acceptance criteria

Classification: observation.

The required `mo issue view 596 --project proj_f6c141d63b6243bfbb481737b2243b87`
read shows the title `mo --help` silently omits four command areas and multiple
leaf commands, but its `body` is empty. The four areas named by the plan are
verified omissions: `MohistCliCommands.Build` registers `audit`, `workspace`,
`slack`, and `github` (`packages/cli/Mohist.Cli/MohistCliCommands.cs:16,31,34-35`),
while `CommandPresentations.AttachTo` currently attaches none of them. The
plan's progressive help, local behavior, structural validation, and reference
alignment requirements are therefore useful elaboration and prevention around
the titled bug, not omitted issue criteria.

### O-002: Existing `webhook` capability placement is inconsistent

Classification: observation.

The current presentation catalog puts `webhook` under Automation
(`packages/cli/Mohist.Cli/CommandPresentations.cs:48`), while the CLI reference
puts it under Operations (`docs/cli-reference.md:137`). The design does not
state which placement wins. This does not make the plan incomplete: T-003
explicitly requires a comparison that finds no differently classified root
area, so implementation cannot satisfy the task while leaving the mismatch.
The implementer should use the CLI reference's product classification unless
that product contract is deliberately changed.

### O-003: T-001's exhaustive claim is structurally proven one task later

Classification: observation.

T-001 completes every presentation and T-002, which depends on T-001, adds the
real-tree validator that proves none were missed. T-001 is therefore not fully
self-verifying in isolation, but the overall ordered plan is verifiable: T-002
requires an empty diagnostic set from the real tree, and T-003 depends on both
tasks before running the full gate.

## Dimension Review

### Issue Contract: checked, no issue

The live issue was read before the artifacts. Its only substantive contract is
the title-level bug: four missing command areas and multiple missing leaf
commands. Proposal lines 3-11 directly address both symptoms and add a
regression gate without changing command syntax or execution behavior.

### Coverage: checked, no issue

- Root omissions are covered by the root-help requirement, T-001's exact-once
  root criterion, and explicit placement of `workspace`, `audit`, `github`, and
  `slack`.
- Descendant omissions are covered at every depth: group help lists visible
  direct children, leaf help uses the full invocation, and T-002 traverses every
  visible node rather than a hand-maintained subset.
- All seven plan requirements have an implementation task: root, group, leaf,
  local/side-effect-free usage, and behavior preservation are in T-001;
  structural coverage is in T-002; reference alignment is in T-003.
- Hidden commands/options, JSON field discovery, parser usage errors,
  documentation alignment, and unchanged non-help behavior all have explicit
  acceptance criteria rather than relying only on design prose.

### Correctness: checked, no issue

The failure mechanism and proposed correction match the code. Root rendering
currently filters commands without presentations
(`CommandHelpRenderer.cs:10-13`), while group and leaf rendering fall back to
registration descriptions (`CommandHelpRenderer.cs:53-76,96-97`). Requiring
catalog metadata and enumerating the live `Command` tree removes those silent
paths. The existing renderer already derives parent-based invocation paths,
arguments, visible options, and descriptor-backed JSON fields, so preserving
those mechanisms satisfies progressive root/group/leaf discovery without a
second syntax model. `CommandHelpHook.LocalHelpAction` already bypasses command
actions, and `RenderNearestUsage` already owns local parse errors; focused fake
dependency tests are the correct regression proof.

### Codebase Consistency: checked, no issue

The design uses the established authorities and test seams:

- `MohistCliCommands.Build` constructs the real `System.CommandLine` tree and
  attaches presentations to those same instances.
- `CommandPresentationCatalog` is already the metadata store, and the CLI test
  assembly already has `InternalsVisibleTo` access.
- Resource descriptors are already attached to `--json` options, including
  `OtelCommands.TracesDescriptor`; the plan does not duplicate those fields.
- The proposed tests use existing fake HTTP, filesystem, executor, terminal,
  and project-state seams. No real network, process, service, or wall clock is
  required.
- Updating `docs/cli-reference.md` in the same delivery follows the repository
  rule that product documentation and executable help converge together.

### Task Breakdown: checked, no issue

The three tasks are coherent delivery slices with valid spec anchors. T-001
delivers user-visible help and focused behavior tests; T-002 adds deterministic
real-tree and synthetic structural enforcement after the catalog exists; T-003
aligns documentation and runs the full repository gate. Dependencies are valid
and acyclic (`T-001` -> `T-002` -> `T-003`), each task has observable acceptance
criteria, fast verification is required during CLI work, and final delivery is
gated by `npm run verify`.

<promise>PASS</promise>
