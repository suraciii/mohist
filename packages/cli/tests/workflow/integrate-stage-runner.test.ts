import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { Stage, IssueStatus } from '../../src/types';
import type { StageContext } from '../../src/workflow/stage-context';
import { IntegrateStageRunner } from '../../src/workflow/integrate-stage-runner';
import { EventBus } from '../../src/services/event-bus';

function createMockContext(
  tmpDir: string,
  issueNumber = 42,
  overrides?: Partial<StageContext>
): StageContext {
  const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
  fs.mkdirSync(changeDir, { recursive: true });

  const emitSpy = vi.fn();
  const eventBus = new EventBus();
  vi.spyOn(eventBus, 'emit').mockImplementation(emitSpy);

  return {
    issue: {
      id: `issue-${issueNumber}`,
      number: issueNumber,
      title: 'Test Issue',
      body: '',
      stage: Stage.Integrate,
      status: IssueStatus.Active,
      projectId: 'test-project',
      labels: [],
      priority: 'p2' as const,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
    acpOptions: { worktreePath: tmpDir } as any,
    artifactManager: {
      getChangeDir: vi.fn().mockReturnValue(changeDir),
      createChangeDir: vi.fn(),
      readArtifact: vi.fn().mockReturnValue(null),
      writeArtifact: vi.fn().mockReturnValue(true),
      exists: vi.fn().mockReturnValue(true),
      readTasks: vi.fn().mockReturnValue(null),
      updateTaskPasses: vi.fn().mockReturnValue(true),
      archiveChange: vi.fn().mockResolvedValue(undefined),
    },
    worktreeManager: {} as any,
    projectRepo: {} as any,
    eventBus: eventBus as any,
    checkpointManager: { save: vi.fn(), load: vi.fn(), deleteAll: vi.fn() } as any,
    issueRepo: {
      updateStage: vi.fn(),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
    } as any,
    stageExecutionRepo: {
      create: vi.fn().mockReturnValue({ id: 'exec-1', issueId: `issue-${issueNumber}`, stage: Stage.Integrate, status: 'running', taskResults: [], checkResults: [], createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() }),
      appendTaskResult: vi.fn(),
      updateStatus: vi.fn(),
      updateCheckResults: vi.fn(),
      updateTaskResults: vi.fn(),
    } as any,
    ...overrides,
  } as StageContext;
}

function createMainSpec(tmpDir: string, capability: string, requirements: string[]) {
  const specDir = path.join(tmpDir, 'openspec', 'specs', capability);
  fs.mkdirSync(specDir, { recursive: true });
  let content = '# OpenSpec Capability: ' + capability + '\n\n';
  for (const req of requirements) {
    content += req + '\n\n';
  }
  fs.writeFileSync(path.join(specDir, 'spec.md'), content, 'utf-8');
}

function createChangeSpec(changeDir: string, capability: string, content: string) {
  const specsDir = path.join(changeDir, 'specs');
  fs.mkdirSync(specsDir, { recursive: true });
  const capabilityDir = path.join(specsDir, capability);
  fs.mkdirSync(capabilityDir, { recursive: true });
  fs.writeFileSync(path.join(capabilityDir, 'spec.md'), content, 'utf-8');
}

describe('IntegrateStageRunner', () => {
  let tmpDir: string;
  let runner: IntegrateStageRunner;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-integrate-test-'));
    runner = new IntegrateStageRunner({ worktreePath: tmpDir });
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  describe('spec sync and archive', () => {
    it('applies delta specs to openspec/specs before attempting archive', async () => {
      const issueNumber = 42;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'test-cap', [
        '### Requirement: ExistingReq\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.'
      ]);

      createChangeSpec(changeDir, 'test-cap', `## ADDED Requirements

### Requirement: NewReq

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const archiveChangeSpy = vi.fn().mockImplementation(async () => {
        const archiveDir = path.join(tmpDir, 'openspec', 'changes', 'archive');
        fs.mkdirSync(archiveDir, { recursive: true });
        const datePrefix = new Date().toISOString().slice(0, 10);
        const newName = `${datePrefix}-${issueNumber}-test-change`;
        fs.renameSync(changeDir, path.join(archiveDir, newName));
      });

      const baseCtx = createMockContext(tmpDir, issueNumber);
      const ctx = createMockContext(tmpDir, issueNumber, {
        artifactManager: {
          ...baseCtx.artifactManager,
          archiveChange: archiveChangeSpy,
        },
      });
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(result.output).toMatchObject({ integrate: true });

      const mainSpecPath = path.join(tmpDir, 'openspec', 'specs', 'test-cap', 'spec.md');
      expect(fs.existsSync(mainSpecPath)).toBe(true);
      const mainSpecContent = fs.readFileSync(mainSpecPath, 'utf-8');
      expect(mainSpecContent).toContain('NewReq');
      expect(mainSpecContent).toContain('ExistingReq');

      const archiveDir = path.join(tmpDir, 'openspec', 'changes', 'archive');
      expect(fs.existsSync(archiveDir)).toBe(true);
    });

    it('writes a structured integrate:spec-sync result containing capabilities, counts, target files, and conflicts or success summary', async () => {
      const issueNumber = 43;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'my-cap', [
        '### Requirement: MyReq\n\nMy content.\n\n#### Scenario: My scenario\n\nMy scenario content.'
      ]);

      createChangeSpec(changeDir, 'my-cap', `## ADDED Requirements

### Requirement: AnotherReq

Another requirement content.

#### Scenario: Another scenario
Another scenario content.`);

      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      const output = result.output as { steps?: Array<{ step: string; status: string; output: unknown }> };
      expect(output.steps).toBeDefined();
      const specSyncStep = output.steps?.find(s => s.step === 'integrate:spec-sync');
      expect(specSyncStep).toBeDefined();
      const specSyncOutput = specSyncStep!.output as { capabilities?: string[]; counts?: { added: number; modified: number; removed: number; renamed: number }; targetFiles?: string[]; valid?: boolean };
      expect(specSyncOutput.capabilities).toContain('my-cap');
      expect(specSyncOutput.counts?.added).toBe(1);
      expect(specSyncOutput.targetFiles).toContain('openspec/specs/my-cap/spec.md');
      expect(specSyncOutput.valid).toBe(true);
    });

    it('archives to openspec/changes/archive/YYYY-MM-DD-<change>/ only after spec sync succeeds', async () => {
      const issueNumber = 44;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'arch-cap', [
        '### Requirement: ArchReq\n\nArch content.\n\n#### Scenario: Arch scenario\n\nArch scenario content.'
      ]);

      createChangeSpec(changeDir, 'arch-cap', `## ADDED Requirements

### Requirement: ArchAdded

Arch added content.

#### Scenario: Arch added scenario
Arch added scenario content.`);

      const archiveChangeSpy = vi.fn().mockImplementation(async () => {
        const archiveDir = path.join(tmpDir, 'openspec', 'changes', 'archive');
        fs.mkdirSync(archiveDir, { recursive: true });
        const datePrefix = new Date().toISOString().slice(0, 10);
        const newName = `${datePrefix}-${issueNumber}-test-change`;
        fs.renameSync(changeDir, path.join(archiveDir, newName));
      });

      const baseCtx = createMockContext(tmpDir, issueNumber);
      const ctx = createMockContext(tmpDir, issueNumber, {
        artifactManager: {
          ...baseCtx.artifactManager,
          archiveChange: archiveChangeSpy,
        },
      });
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);

      const archiveDir = path.join(tmpDir, 'openspec', 'changes', 'archive');
      expect(fs.existsSync(archiveDir)).toBe(true);
      const entries = fs.readdirSync(archiveDir);
      const archivedEntry = entries.find(e => e.includes(`${issueNumber}-test-change`));
      expect(archivedEntry).toBeDefined();
      expect(archivedEntry?.startsWith(new Date().getFullYear().toString())).toBe(true);
    });

    it('writes a structured integrate:archive-change result containing archive path and success or failure summary', async () => {
      const issueNumber = 45;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'arc-cap', [
        '### Requirement: ArcReq\n\nArc content.\n\n#### Scenario: Arc scenario\n\nArc scenario content.'
      ]);

      createChangeSpec(changeDir, 'arc-cap', `## ADDED Requirements

### Requirement: ArcAdded

Arc added content.

#### Scenario: Arc added scenario
Arc added scenario content.`);

      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      const output = result.output as { steps?: Array<{ step: string; status: string; output: unknown }> };
      expect(output.steps).toBeDefined();
      const archiveStep = output.steps?.find(s => s.step === 'integrate:archive-change');
      expect(archiveStep).toBeDefined();
      const archiveOutput = archiveStep!.output as { archivePath?: string; success?: boolean };
      expect(archiveOutput.archivePath).toContain('openspec/changes/archive');
      expect(archiveOutput.success).toBe(true);
    });

    it('spec sync failure blocks Integrate and does not archive, merge, run final health, or mark Done', async () => {
      const issueNumber = 46;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'fail-cap', [
        '### Requirement: ExistingReq\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.'
      ]);

      createChangeSpec(changeDir, 'fail-cap', `## ADDED Requirements

### Requirement: ExistingReq

Duplicate requirement content.

#### Scenario: Duplicate scenario
Duplicate scenario content.`);

      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('Spec sync failed');

      const archiveDir = path.join(tmpDir, 'openspec', 'changes', 'archive');
      expect(fs.existsSync(archiveDir)).toBe(false);

      expect(ctx.artifactManager.archiveChange).not.toHaveBeenCalled();
    });

    it('archive failure blocks Integrate and does not merge, run final health, or mark Done', async () => {
      const issueNumber = 47;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'arcfail-cap', [
        '### Requirement: ArcFailReq\n\nArcFail content.\n\n#### Scenario: ArcFail scenario\n\nArcFail scenario content.'
      ]);

      createChangeSpec(changeDir, 'arcfail-cap', `## ADDED Requirements

### Requirement: ArcFailAdded

ArcFail added content.

#### Scenario: ArcFail added scenario
ArcFail added scenario content.`);

      const archiveError = new Error('Archive failed');
      const baseCtx = createMockContext(tmpDir, issueNumber);
      const ctx = createMockContext(tmpDir, issueNumber, {
        artifactManager: {
          ...baseCtx.artifactManager,
          archiveChange: vi.fn().mockRejectedValue(archiveError),
        },
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('Archive failed');

      expect(result.nextStage).toBeUndefined();
    });
  });
});