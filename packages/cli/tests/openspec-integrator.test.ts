import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { OpenSpecIntegrator } from '../src/openspec/open-spec-integrator';

describe('OpenSpecIntegrator', () => {
  let tmpDir: string;
  let projectPath: string;
  let changeDir: string;
  let integrator: OpenSpecIntegrator;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
    projectPath = tmpDir;
    changeDir = path.join(tmpDir, 'change');
    fs.mkdirSync(changeDir, { recursive: true });
    integrator = new OpenSpecIntegrator();
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  function createMainSpec(capability: string, requirements: string[]) {
    const specDir = path.join(projectPath, 'openspec', 'specs', capability);
    fs.mkdirSync(specDir, { recursive: true });
    let content = '# OpenSpec Capability: ' + capability + '\n\n';
    for (const req of requirements) {
      content += req + '\n\n';
    }
    fs.writeFileSync(path.join(specDir, 'spec.md'), content, 'utf-8');
  }

  function createChangeSpec(capability: string, content: string) {
    const specsDir = path.join(changeDir, 'specs');
    fs.mkdirSync(specsDir, { recursive: true });
    const capabilityDir = path.join(specsDir, capability);
    fs.mkdirSync(capabilityDir, { recursive: true });
    fs.writeFileSync(path.join(capabilityDir, 'spec.md'), content, 'utf-8');
  }

  function createLegacyFlatChangeSpec(capability: string, content: string) {
    const specsDir = path.join(changeDir, 'specs');
    fs.mkdirSync(specsDir, { recursive: true });
    fs.writeFileSync(path.join(specsDir, `${capability}.md`), content, 'utf-8');
  }

  describe('preview', () => {
    it('reads change specs and main specs without modifying any files', async () => {
      createMainSpec('test-cap', [
        '### Requirement: ExistingReq\n\nSome content.\n\n#### Scenario: Test scenario\n\nSome scenario content.'
      ]);
      createChangeSpec('test-cap', `## ADDED Requirements

### Requirement: NewReq

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.valid).toBe(true);
      expect(summary.added).toBe(1);
      expect(fs.existsSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'))).toBe(true);
      const mainSpecContent = fs.readFileSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'), 'utf-8');
      expect(mainSpecContent).not.toContain('NewReq');
    });

    it('returns empty summary when change has no specs directory', async () => {
      const emptyChangeDir = path.join(tmpDir, 'empty-change');
      fs.mkdirSync(emptyChangeDir, { recursive: true });

      const summary = await integrator.preview(emptyChangeDir, projectPath);

      expect(summary.capabilities).toEqual([]);
      expect(summary.valid).toBe(true);
    });

    it('also supports legacy flat specs/<capability>.md files', async () => {
      createLegacyFlatChangeSpec('flat-cap', `## ADDED Requirements

### Requirement: FlatReq

Flat content.

#### Scenario: Flat scenario`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.valid).toBe(true);
      expect(summary.capabilities).toContain('flat-cap');
      expect(summary.targetFiles).toContain('openspec/specs/flat-cap/spec.md');
    });
  });

  describe('apply', () => {
    it('writes updated openspec/specs/<capability>/spec.md only after all validation passes', async () => {
      createMainSpec('test-cap', [
        '### Requirement: ExistingReq\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.'
      ]);
      createChangeSpec('test-cap', `## ADDED Requirements

### Requirement: NewReq

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const summary = await integrator.apply(changeDir, projectPath);

      expect(summary.valid).toBe(true);
      expect(fs.existsSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'))).toBe(true);
      const mainSpecContent = fs.readFileSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'), 'utf-8');
      expect(mainSpecContent).toContain('NewReq');
      expect(mainSpecContent).toContain('ExistingReq');
    });

    it('does not write when validation fails', async () => {
      createMainSpec('test-cap', [
        '### Requirement: ExistingReq\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.'
      ]);
      createChangeSpec('test-cap', `## ADDED Requirements

### Requirement: ExistingReq

Duplicate requirement content.

#### Scenario: Duplicate scenario
Duplicate scenario content.`);

      const summary = await integrator.apply(changeDir, projectPath);

      expect(summary.valid).toBe(false);
      expect(summary.conflicts.length).toBeGreaterThan(0);
      const mainSpecContent = fs.readFileSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'), 'utf-8');
      expect(mainSpecContent).not.toContain('Duplicate requirement content');
    });

    it('rejects malformed delta sections with structured conflict output', async () => {
      createChangeSpec('test-cap', `## CHANGED Requirements

### Requirement: BrokenReq

Broken content.

#### Scenario: Broken scenario`);

      const summary = await integrator.apply(changeDir, projectPath);

      expect(summary.valid).toBe(false);
      expect(summary.conflicts).toEqual(
        expect.arrayContaining([
          expect.objectContaining({
            capability: 'test-cap',
            type: 'malformed_delta',
          }),
        ]),
      );
      expect(summary.errors.some(error => error.includes('Unsupported delta section header'))).toBe(true);
      expect(fs.existsSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'))).toBe(false);
    });

    it('does not write any candidate files when post-sync validation fails', async () => {
      createMainSpec('test-cap', [
        '### Requirement: ExistingReq\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.'
      ]);
      createChangeSpec('test-cap', `## ADDED Requirements

### Requirement: AnotherReq

Another content.

#### Scenario: Added scenario
Added scenario content.`);
      createChangeSpec('second-cap', `## ADDED Requirements

### Requirement: BrokenReq

Broken content without a scenario.`);

      const summary = await integrator.apply(changeDir, projectPath);

      expect(summary.valid).toBe(false);
      expect(summary.conflicts.some(c => c.type === 'missing_scenarios' && c.capability === 'second-cap')).toBe(true);

      const existingSpec = fs.readFileSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'), 'utf-8');
      expect(existingSpec).not.toContain('AnotherReq');
      expect(fs.existsSync(path.join(projectPath, 'openspec', 'specs', 'second-cap', 'spec.md'))).toBe(false);
    });
  });

  describe('RENAMED requirements', () => {
    it('are processed before removed, modified, and added', async () => {
      createMainSpec('test-cap', [
        '### Requirement: OriginalReq\n\nOriginal content.\n\n#### Scenario: Original scenario\n\nOriginal scenario content.'
      ]);
      createChangeSpec('test-cap', `## ADDED Requirements

### Requirement: BrandNewReq

Brand new content.

#### Scenario: Brand new scenario
Brand new scenario content.

## REMOVED Requirements

### Requirement: OriginalReq

## RENAMED Requirements

FROM: OriginalReq
TO: RenamedReq

## MODIFIED Requirements

### Requirement: RenamedReq

Modified content should work because rename was processed first.

#### Scenario: Modified scenario
Modified scenario content.`);

      const summary = await integrator.apply(changeDir, projectPath);

      expect(summary.valid).toBe(true);
      const mainSpecContent = fs.readFileSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'), 'utf-8');
      expect(mainSpecContent).toContain('RenamedReq');
      expect(mainSpecContent).not.toContain('OriginalReq');
      expect(mainSpecContent).toContain('BrandNewReq');
    });

    it('fail when source requirement does not exist in main spec', async () => {
      createChangeSpec('test-cap', `## RENAMED Requirements

FROM: NonExistentReq
TO: NewNameReq`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.valid).toBe(false);
      expect(summary.conflicts.some(c => c.type === 'missing_source')).toBe(true);
    });

    it('fail when target requirement already exists', async () => {
      createMainSpec('test-cap', [
        '### Requirement: TargetReq\n\nTarget content.\n\n#### Scenario: Target scenario\n\nTarget scenario content.'
      ]);
      createChangeSpec('test-cap', `## RENAMED Requirements

FROM: SourceReq
TO: TargetReq`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.valid).toBe(false);
      expect(summary.conflicts.some(c => c.type === 'duplicate_target')).toBe(true);
    });
  });

  describe('MODIFIED requirements', () => {
    it('fail when source requirement does not exist in main spec', async () => {
      createChangeSpec('test-cap', `## MODIFIED Requirements

### Requirement: NonExistentReq

Modified content.

#### Scenario: Modified scenario
Modified scenario content.`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.valid).toBe(false);
      expect(summary.conflicts.some(c => c.type === 'missing_source')).toBe(true);
    });
  });

  describe('intelligent MODIFIED-to-ADDED correction', () => {
    it('apply converts MODIFIED with missing source to ADDED when target does not exist', async () => {
      createMainSpec('test-cap', [
        '### Requirement: ExistingReq\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.'
      ]);
      createChangeSpec('test-cap', `## MODIFIED Requirements

### Requirement: BrandNewReq

New requirement content that was incorrectly marked as MODIFIED.

#### Scenario: New requirement scenario
New requirement scenario content.`);

      const summary = await integrator.apply(changeDir, projectPath);

      expect(summary.valid).toBe(true);
      expect(summary.corrections.length).toBe(1);
      expect(summary.corrections[0]).toEqual({
        capability: 'test-cap',
        requirement: 'BrandNewReq',
        from: 'modified',
        to: 'added',
        reason: 'missing-source-treated-as-new-requirement'
      });
      const mainSpecContent = fs.readFileSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'), 'utf-8');
      expect(mainSpecContent).toContain('BrandNewReq');
      expect(mainSpecContent).toContain('ExistingReq');
    });

    it('apply does not correct MODIFIED when source exists (valid modification)', async () => {
      createMainSpec('test-cap', [
        '### Requirement: ExistingReq\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.'
      ]);
      createChangeSpec('test-cap', `## MODIFIED Requirements

### Requirement: ExistingReq

Modified content.

#### Scenario: Modified scenario
Modified scenario content.`);

      const summary = await integrator.apply(changeDir, projectPath);

      expect(summary.valid).toBe(true);
      expect(summary.corrections.length).toBe(0);
      const mainSpecContent = fs.readFileSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'), 'utf-8');
      expect(mainSpecContent).toContain('ExistingReq');
      expect(mainSpecContent).toContain('Modified content');
    });

    it('preview does not apply correction even if target does not exist', async () => {
      createMainSpec('test-cap', [
        '### Requirement: ExistingReq\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.'
      ]);
      createChangeSpec('test-cap', `## MODIFIED Requirements

### Requirement: BrandNewReq

New requirement content that was incorrectly marked as MODIFIED.

#### Scenario: New requirement scenario
New requirement scenario content.`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.valid).toBe(false);
      expect(summary.conflicts.some(c => c.type === 'missing_source')).toBe(true);
      expect(summary.corrections.length).toBe(0);
      const mainSpecContent = fs.readFileSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'), 'utf-8');
      expect(mainSpecContent).not.toContain('BrandNewReq');
    });

    it('correction respects rename ambiguity', async () => {
      createMainSpec('test-cap', [
        '### Requirement: SourceReq\n\nSource content.\n\n#### Scenario: Source scenario\n\nSource scenario content.'
      ]);
      createChangeSpec('test-cap', `## RENAMED Requirements

FROM: SourceReq
TO: TargetReq

## MODIFIED Requirements

### Requirement: TargetReq

Modified content for target.

#### Scenario: Modified scenario
Modified scenario content.`);

      const summary = await integrator.apply(changeDir, projectPath);

      expect(summary.valid).toBe(true);
      expect(summary.corrections.length).toBe(0);
      expect(summary.conflicts.length).toBe(0);
    });
  });

  describe('REMOVED requirements', () => {
    it('fail when source requirement does not exist in main spec', async () => {
      createChangeSpec('test-cap', `## REMOVED Requirements

### Requirement: NonExistentReq`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.valid).toBe(false);
      expect(summary.conflicts.some(c => c.type === 'missing_source')).toBe(true);
    });
  });

  describe('ADDED requirements', () => {
    it('fail when target requirement already exists', async () => {
      createMainSpec('test-cap', [
        '### Requirement: ExistingReq\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.'
      ]);
      createChangeSpec('test-cap', `## ADDED Requirements

### Requirement: ExistingReq

Additional content.

#### Scenario: Additional scenario
Additional scenario content.`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.valid).toBe(false);
      expect(summary.conflicts.some(c => c.type === 'duplicate_target')).toBe(true);
    });
  });

  describe('duplicate headers', () => {
    it('fail validation when duplicate headers exist within same change', async () => {
      createChangeSpec('test-cap', `## ADDED Requirements

### Requirement: DuplicateReq

First instance.

#### Scenario: First scenario

## MODIFIED Requirements

### Requirement: DuplicateReq

Second instance.

#### Scenario: Second scenario`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.valid).toBe(false);
      expect(summary.conflicts.some(c => c.type === 'duplicate_header')).toBe(true);
    });
  });

  describe('missing scenarios', () => {
    it('fail validation when requirement block has no scenarios', async () => {
      createChangeSpec('test-cap', `## ADDED Requirements

### Requirement: NoScenarioReq

Content without scenario.`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.valid).toBe(false);
      expect(summary.conflicts.some(c => c.type === 'missing_scenarios')).toBe(true);
    });

    it('pass validation when requirement block has at least one scenario', async () => {
      createChangeSpec('test-cap', `## ADDED Requirements

### Requirement: WithScenarioReq

Content with scenario.

#### Scenario: Has scenario`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.valid).toBe(true);
    });
  });

  describe('missing target spec', () => {
    it('allows ADDED requirements to create new capability spec', async () => {
      createChangeSpec('new-cap', `## ADDED Requirements

### Requirement: NewCapabilityReq

New capability content.

#### Scenario: New capability scenario
New capability scenario content.`);

      const summary = await integrator.apply(changeDir, projectPath);

      expect(summary.valid).toBe(true);
      expect(fs.existsSync(path.join(projectPath, 'openspec', 'specs', 'new-cap', 'spec.md'))).toBe(true);
    });

    it('fails MODIFIED requirements against non-existent target spec', async () => {
      createChangeSpec('non-existent-cap', `## MODIFIED Requirements

### Requirement: SomeReq

Modified content.

#### Scenario: Modified scenario`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.valid).toBe(false);
      expect(summary.conflicts.some(c => c.type === 'missing_source')).toBe(true);
    });

    it('fails REMOVED requirements against non-existent target spec', async () => {
      createChangeSpec('non-existent-cap', `## REMOVED Requirements

### Requirement: SomeReq`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.valid).toBe(false);
      expect(summary.conflicts.some(c => c.type === 'missing_source')).toBe(true);
    });

    it('fails RENAMED requirements against non-existent target spec', async () => {
      createChangeSpec('non-existent-cap', `## RENAMED Requirements

FROM: SomeReq
TO: OtherReq`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.valid).toBe(false);
      expect(summary.conflicts.some(c => c.type === 'missing_source')).toBe(true);
    });
  });

  describe('no-write dry-run behavior', () => {
    it('preview does not modify any files', async () => {
      createMainSpec('test-cap', [
        '### Requirement: ExistingReq\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.'
      ]);
      createChangeSpec('test-cap', `## ADDED Requirements

### Requirement: NewReq

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const beforeContent = fs.existsSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'))
        ? fs.readFileSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'), 'utf-8')
        : '';

      await integrator.preview(changeDir, projectPath);

      const afterContent = fs.readFileSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'), 'utf-8');
      expect(afterContent).toBe(beforeContent);
      expect(afterContent).not.toContain('NewReq');
    });

    it('apply modifies files only when valid', async () => {
      createMainSpec('test-cap', [
        '### Requirement: ExistingReq\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.'
      ]);
      createChangeSpec('test-cap', `## ADDED Requirements

### Requirement: NewReq

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const summary = await integrator.apply(changeDir, projectPath);

      expect(summary.valid).toBe(true);
      const mainSpecContent = fs.readFileSync(path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'), 'utf-8');
      expect(mainSpecContent).toContain('NewReq');
    });
  });

  describe('structured sync summary', () => {
    it('returns correct counts and target files for mixed deltas', async () => {
      createMainSpec('cap-a', [
        '### Requirement: ExistingA\n\nContent A.\n\n#### Scenario: A scenario\n\nA content.'
      ]);
      createMainSpec('cap-b', [
        '### Requirement: ExistingB\n\nContent B.\n\n#### Scenario: B scenario\n\nB content.'
      ]);
      createChangeSpec('cap-a', `## ADDED Requirements

### Requirement: AddedInA

Added content.

#### Scenario: Added scenario

## REMOVED Requirements

### Requirement: ExistingA`);

      createChangeSpec('cap-b', `## MODIFIED Requirements

### Requirement: ExistingB

Modified content.

#### Scenario: Modified scenario`);

      const summary = await integrator.preview(changeDir, projectPath);

      expect(summary.capabilities).toContain('cap-a');
      expect(summary.capabilities).toContain('cap-b');
      expect(summary.added).toBe(1);
      expect(summary.removed).toBe(1);
      expect(summary.modified).toBe(1);
      expect(summary.targetFiles).toContain('openspec/specs/cap-a/spec.md');
      expect(summary.targetFiles).toContain('openspec/specs/cap-b/spec.md');
    });
  });
});
