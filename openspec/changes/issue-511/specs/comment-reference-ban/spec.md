### Requirement: ArchTest forbids issue/spec/design references in server C# comments

An architecture test MUST fail when server C# source comments contain any of: an issue reference (`issue-\d+`), a task identifier (`T-\d{3}`), a design-doc path (`design/…/*.md`), or an `openspec/` reference. The rule follows the AGENTS.md convention that comments must not cite issue, spec, or document numbers, because such references rot (one already points at a non-existent `design/workflow/scheduling.md`). The test scope is server C# source only; runner / web / cli comment cleanup is explicitly out of scope for this change.

#### Scenario: A new issue-number reference fails the build

- **WHEN** a contributor adds a C# comment containing `issue-512` to server source
- **THEN** the architecture test MUST fail

#### Scenario: A new task identifier fails the build

- **WHEN** a contributor adds a C# comment containing `T-006` to server source
- **THEN** the architecture test MUST fail

#### Scenario: A new design-doc path fails the build

- **WHEN** a contributor adds a C# comment containing `design/workflow/foo.md` to server source
- **THEN** the architecture test MUST fail

#### Scenario: A new openspec reference fails the build

- **WHEN** a contributor adds a C# comment containing `openspec/` to server source
- **THEN** the architecture test MUST fail

### Requirement: Baseline ratchet freezes existing violations as shrink-only

Until the existing 38 violations across 26 files are cleared, the architecture test MUST use a baseline ratchet modeled on `spec-file-size-baseline.json`: the current offending occurrences are frozen into a named baseline that MAY only shrink. The test MUST fail if the count of offending occurrences grows beyond the frozen baseline, and MUST fail if a baseline-listed occurrence is edited into a different violation rather than removed.

#### Scenario: Baseline violations are permitted while uncleared

- **WHEN** the codebase contains exactly the frozen set of offending occurrences
- **THEN** the architecture test MUST pass

#### Scenario: Growth beyond the baseline fails

- **WHEN** a new offending occurrence is added beyond the frozen baseline
- **THEN** the architecture test MUST fail and identify the new occurrence

#### Scenario: Shrinkage is allowed and updates the baseline

- **WHEN** an offending occurrence is removed from a file
- **THEN** the baseline entry for that occurrence MUST be removed so the ratchet tightens
- **AND** the architecture test MUST pass

### Requirement: Existing violations cleared to zero, then hard ban

The 38 existing offending occurrences SHALL be cleared from the 26 files. Pure provenance citations (e.g. `(issue-490 T-001, design D2)`) MUST be deleted while preserving any explanatory prose around them; comments that explain "why" MUST be rewritten to state the reason inline without the citation. After the baseline reaches zero, the architecture test MUST become a hard ban with no exemptions.

#### Scenario: Dangling scheduling.md reference resolved inline

- **WHEN** the migration comment that cited the non-existent `design/workflow/scheduling.md` is inspected
- **THEN** the citation MUST be removed
- **AND** the schema decision it pointed at MUST be stated inline in the comment, or the citation-only comment MUST be deleted

#### Scenario: UnifiedSessionRoutes T-005 remark becomes a verified fact

- **WHEN** the `UnifiedSessionRoutes` remark citing `T-005` is inspected
- **THEN** the task identifier MUST be removed
- **AND** if the CLI still consumes the `agent-sessions/{sessionId}` route (verified against CLI source), the remark MUST be restated as a factual statement of current usage; if the CLI no longer uses it, the old-route remark MUST be removed

#### Scenario: Baseline emptied yields a no-exemption ban

- **WHEN** the baseline ratchet list is emptied
- **THEN** the architecture test MUST reject any offending occurrence with no baseline exemption path
