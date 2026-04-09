## ADDED Requirements

### Requirement: Change artifacts directory structure

The system SHALL create and manage a standardized directory structure for change artifacts.

#### Scenario: Create change directory
- **WHEN** a new change is initiated for issue #42 titled "user auth"
- **THEN** the system SHALL:
  1. Generate slug from issue title: "user-auth"
  2. Create directory at `.mohist/changes/42-user-auth/`
  3. Create subdirectories: `specs/`
  4. Initialize with template files if applicable

#### Scenario: Artifacts directory layout
- **GIVEN** a change directory exists
- **THEN** the system SHALL organize artifacts as follows:
  ```
  .mohist/changes/42-user-auth/
  ├── proposal.md           # Change proposal document
  ├── design.md             # Technical design document
  ├── specs/                # Capability specifications
  │   ├── auth-flow.md
  │   └── session-mgmt.md
  └── prd.json              # Product requirements with tasks
  ```

#### Scenario: Handle duplicate slugs
- **GIVEN** issue #42 and issue #43 both titled "user auth"
- **WHEN** creating change directories
- **THEN** the system SHALL:
  - Issue #42: `.mohist/changes/42-user-auth/`
  - Issue #43: `.mohist/changes/43-user-auth/` (different issue number ensures uniqueness)

### Requirement: Git integration for artifacts

All change artifacts SHALL be tracked in Git alongside the codebase.

#### Scenario: Artifacts committed to Git
- **WHEN** a change artifact is created or updated
- **THEN** the system SHALL:
  1. Stage the file using `git add`
  2. Commit with descriptive message including issue number
  3. Include co-author information if applicable

#### Scenario: Git commit message format
- **GIVEN** a change is being committed
- **THEN** the commit message SHALL follow:
  ```
  [mohist-change] #{number}: {action}

  - {description of changes}

  Issue: #{number}
  Phase: {explore|plan|build|review}
  Iteration: {n} (if applicable)
  ```

#### Scenario: Git attributes for artifacts
- **GIVEN** artifacts are stored in `.mohist/changes/`
- **THEN** the system SHALL recommend adding to `.gitattributes`:
  ```
  .mohist/changes/** linguist-generated=true
  ```
- **AND** provide a command to auto-generate this configuration

### Requirement: Artifact versioning

The system SHALL support versioning of change artifacts as they evolve through iterations.

#### Scenario: Track artifact evolution
- **WHEN** a design document is updated during the Plan phase inner loop
- **THEN** the system SHALL:
  1. Commit each version separately
  2. Tag iterations if configured: `change-{number}-plan-iter-{n}`
  3. Maintain history available via `git log`

#### Scenario: View artifact history
- **GIVEN** a user wants to see how a design evolved
- **WHEN** they request history
- **THEN** the system SHALL provide:
  - List of all commits affecting the artifact
  - Diff between any two versions
  - Summary of changes per iteration

### Requirement: Artifact access API

The system SHALL provide an API for reading and writing change artifacts.

#### Interface
```typescript
class ChangeArtifactsManager {
  constructor(projectPath: string);

  // Directory operations
  createChangeDir(issueNumber: number, title: string): string;
  findChangeDir(issueNumber: number): string | null;
  listChanges(): Array<{ number: number; slug: string; path: string }>;

  // Read operations
  readProposal(issueNumber: number): string | null;
  readDesign(issueNumber: number): string | null;
  readSpecs(issueNumber: number): Array<{ name: string; content: string }>;
  readPrd(issueNumber: number): PrdJson | null;

  // Write operations
  writeProposal(issueNumber: number, content: string): void;
  writeDesign(issueNumber: number, content: string): void;
  writeSpec(issueNumber: number, capability: string, content: string): void;
  writePrd(issueNumber: number, prd: PrdJson): void;

  // Version control
  commitChanges(issueNumber: number, message: string): void;
  getHistory(issueNumber: number): CommitHistory[];
}
```

#### Scenario: Read artifact
- **WHEN** a component needs to read a design document
- **THEN** the system SHALL provide the artifact content from the change directory
- **AND** return null if the artifact doesn't exist

#### Scenario: Write artifact
- **WHEN** an Agent generates a new artifact
- **THEN** the system SHALL:
  1. Write the artifact to the appropriate location
  2. Stage the file in Git
  3. Optionally commit if auto-commit is enabled

#### Scenario: Concurrent access
- **GIVEN** multiple agents try to write to the same artifact simultaneously
- **WHEN** write conflicts occur
- **THEN** the system SHALL:
  1. Use file locking or atomic writes
  2. Queue writes if necessary
  3. Log all write operations

### Requirement: Archive completed changes

The system SHALL support archiving completed changes to reduce clutter.

#### Scenario: Archive change
- **GIVEN** a change is in the "Done" stage
- **WHEN** the user initiates archive (or auto-archive after N days)
- **THEN** the system SHALL:
  1. Move the change directory from `.mohist/changes/` to `.mohist/changes/archive/`
  2. Preserve Git history
  3. Update any references

#### Scenario: Restore archived change
- **GIVEN** a change is archived
- **WHEN** the user requests restoration
- **THEN** the system SHALL:
  1. Move the directory back to `.mohist/changes/`
  2. Verify all files are intact

#### Scenario: List archived changes
- **WHEN** a user lists all changes
- **THEN** the system SHALL provide:
  - Active changes (in `.mohist/changes/`)
  - Archived changes (in `.mohist/changes/archive/`)

## UPDATED Specifications

### Artifact Templates

The system SHOULD provide templates for new artifacts:

**proposal.md template:**
```markdown
# Proposal: {title}

## Problem
{description of the problem}

## Solution
{high-level solution}

## Impact
{expected impact}

## Timeline
{estimated timeline}
```

**design.md template:**
```markdown
# Design: {title}

## Overview
{overview}

## Architecture
{architecture diagram or description}

## Decisions
{key design decisions}

## Risks
{identified risks}

## Open Questions
{open questions}
```

**spec.md template:**
```markdown
## ADDED Requirements

### Requirement: {capability name}

#### Scenario: {scenario name}
- **GIVEN** {context}
- **WHEN** {action}
- **THEN** {expected outcome}
```

### File Operations Safety

#### Scenario: Handle missing change directory
- **GIVEN** an operation targets a non-existent change
- **WHEN** the directory is not found
- **THEN** the system SHALL throw a clear error:
  ```
  ChangeNotFoundError: Change directory for issue #{number} not found.
  Expected: .mohist/changes/{number}-{slug}/
  ```

#### Scenario: Handle permission errors
- **GIVEN** a file cannot be written due to permissions
- **WHEN** write fails
- **THEN** the system SHALL:
  1. Catch the error
  2. Provide helpful message including file path
  3. Suggest checking permissions

### Configuration

The ChangeArtifactsManager SHALL support configuration:

```typescript
interface ArtifactsConfig {
  autoCommit: boolean;        // Default: true
  commitMessageTemplate: string;
  archiveAfterDays: number;   // Default: 30 (0 = never)
  preserveHistory: boolean;   // Default: true
}
```
