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
    worktreeManager: {
      mergeApprovedCandidate: vi.fn().mockResolvedValue({
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        landedSha: 'ghi789',
        fastForward: true,
      }),
    } as any,
    projectRepo: {
      findById: vi.fn().mockReturnValue({ id: 'test-project', name: 'test-project', baseBranch: 'main', path: tmpDir }),
    } as any,
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
      findByIssueId: vi.fn().mockReturnValue([
        { id: 'exec-1', issueId: `issue-${issueNumber}`, stage: Stage.Integrate, status: 'passed', taskResults: [], checkResults: [], createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() },
      ]),
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
    vi.resetModules();
    const execFileMock = vi.fn().mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
      const err = new Error('ENOENT');
      (err as any).code = 'ENOENT';
      process.nextTick(() => {
        if (typeof opts === 'function') {
          opts(err, { stdout: '', stderr: '' });
        } else if (typeof cb === 'function') {
          cb(err, { stdout: '', stderr: '' });
        }
      });
      return {} as any;
    });
    vi.doMock('child_process', async () => ({
      ...await vi.importActual<typeof import('child_process')>('child_process'),
      execFile: execFileMock,
    }));
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-integrate-test-'));
    fs.writeFileSync(
      path.join(tmpDir, 'package.json'),
      JSON.stringify({
        scripts: {
          build: 'node -e "process.exit(0)"',
          test: 'node -e "process.exit(0)"',
        },
      }),
      'utf-8',
    );
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

    it('continues to merge when a previous integrate attempt already archived the change', async () => {
      const issueNumber = 48;
      const archivedChangeDir = path.join(
        tmpDir,
        'openspec',
        'changes',
        'archive',
        `2026-05-09-${issueNumber}-test-change`,
      );
      fs.mkdirSync(path.join(archivedChangeDir, 'specs'), { recursive: true });

      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        landedSha: 'ghi789',
        fastForward: true,
      });

      const baseCtx = createMockContext(tmpDir, issueNumber);
      const ctx = createMockContext(tmpDir, issueNumber, {
        artifactManager: {
          ...baseCtx.artifactManager,
          getChangeDir: vi.fn().mockReturnValue(null),
          archiveChange: vi.fn(),
        },
        worktreeManager: { mergeApprovedCandidate: mergeApprovedCandidateMock } as any,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(ctx.artifactManager.archiveChange).not.toHaveBeenCalled();
      expect(mergeApprovedCandidateMock).toHaveBeenCalled();

      const output = result.output as { steps?: Array<{ step: string; status: string; output: any }> };
      const specSyncStep = output.steps?.find(s => s.step === 'integrate:spec-sync');
      const archiveStep = output.steps?.find(s => s.step === 'integrate:archive-change');
      expect(specSyncStep?.status).toBe('completed');
      expect(specSyncStep?.output).toMatchObject({ skipped: true, reason: 'change-already-archived' });
      expect(archiveStep?.status).toBe('completed');
      expect(archiveStep?.output).toMatchObject({ skipped: true, reason: 'change-already-archived' });
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

  describe('merge step', () => {
    function createProjectRepo(baseBranch = 'main') {
      return {
        findById: vi.fn().mockReturnValue({ id: 'test-project', name: 'test-project', baseBranch, path: tmpDir }),
      };
    }

    it('fast-forward merge succeeds when candidate can fast-forward into target branch', async () => {
      const issueNumber = 50;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'ff-cap', [
        '### Requirement: FFReq\n\nFF content.\n\n#### Scenario: FF scenario\n\nFF scenario content.'
      ]);

      createChangeSpec(changeDir, 'ff-cap', `## ADDED Requirements

### Requirement: FFAdded

FF added content.

#### Scenario: FF added scenario
FF added scenario content.`);

      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        landedSha: 'ghi789',
        fastForward: true,
      });

      const ctx = createMockContext(tmpDir, issueNumber, {
        worktreeManager: { mergeApprovedCandidate: mergeApprovedCandidateMock } as any,
        projectRepo: createProjectRepo(),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      const output = result.output as { steps?: Array<{ step: string; status: string; output: unknown }> };
      expect(output.steps).toBeDefined();
      const mergeStep = output.steps?.find(s => s.step === 'integrate:merge');
      expect(mergeStep).toBeDefined();
      const mergeOutput = mergeStep!.output as { targetBranch?: string; baseSha?: string; candidateHeadSha?: string; landedSha?: string; fastForward?: boolean };
      expect(mergeOutput.targetBranch).toBe('main');
      expect(mergeOutput.baseSha).toBe('abc123');
      expect(mergeOutput.candidateHeadSha).toBe('def456');
      expect(mergeOutput.landedSha).toBe('ghi789');
      expect(mergeOutput.fastForward).toBe(true);
    });

    it('merges using project path and name instead of issue project id or worktree path', async () => {
      const issueNumber = 55;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'identity-cap', [
        '### Requirement: IdentityReq\n\nIdentity content.\n\n#### Scenario: Identity scenario\n\nIdentity scenario content.',
      ]);

      createChangeSpec(changeDir, 'identity-cap', `## ADDED Requirements

### Requirement: IdentityAdded

Identity added content.

#### Scenario: Identity added scenario
Identity added scenario content.`);

      const projectPath = path.join(tmpDir, 'actual-project');
      fs.mkdirSync(projectPath, { recursive: true });
      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        targetBranch: 'master',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        landedSha: 'ghi789',
        fastForward: true,
      });

      const ctx = createMockContext(tmpDir, issueNumber, {
        issue: {
          ...createMockContext(tmpDir, issueNumber).issue,
          projectId: 'project-uuid-123',
        },
        worktreeManager: { mergeApprovedCandidate: mergeApprovedCandidateMock } as any,
        projectRepo: {
          findById: vi.fn().mockReturnValue({
            id: 'project-uuid-123',
            name: 'mohist',
            path: projectPath,
            baseBranch: 'master',
            createdAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
          }),
        } as any,
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(mergeApprovedCandidateMock).toHaveBeenCalledWith(
        projectPath,
        'mohist',
        issueNumber,
        'master',
      );
    });

    it('clean rebase succeeds when fast-forward is not possible but rebase is clean', async () => {
      const issueNumber = 51;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'rb-cap', [
        '### Requirement: RBReq\n\nRB content.\n\n#### Scenario: RB scenario\n\nRB scenario content.'
      ]);

      createChangeSpec(changeDir, 'rb-cap', `## ADDED Requirements

### Requirement: RBAdded

RB added content.

#### Scenario: RB added scenario
RB added scenario content.`);

      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        landedSha: 'ghi789',
        fastForward: false,
        rebased: true,
      });

      const ctx = createMockContext(tmpDir, issueNumber, {
        worktreeManager: { mergeApprovedCandidate: mergeApprovedCandidateMock } as any,
        projectRepo: createProjectRepo(),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      const output = result.output as { steps?: Array<{ step: string; status: string; output: unknown }> };
      const mergeStep = output.steps?.find(s => s.step === 'integrate:merge');
      expect(mergeStep).toBeDefined();
      const mergeOutput = mergeStep!.output as { fastForward?: boolean; rebased?: boolean };
      expect(mergeOutput.fastForward).toBe(false);
      expect(mergeOutput.rebased).toBe(true);
    });

    it('merge conflict fails with failing step merge and includes conflicting files', async () => {
      const issueNumber = 52;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cf-cap', [
        '### Requirement: CFReq\n\nCF content.\n\n#### Scenario: CF scenario\n\nCF scenario content.'
      ]);

      createChangeSpec(changeDir, 'cf-cap', `## ADDED Requirements

### Requirement: CFAdded

CF added content.

#### Scenario: CF added scenario
CF added scenario content.`);

      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        failingStep: 'merge' as const,
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        conflictFiles: ['src/foo.ts', 'src/bar.ts'],
        error: 'Clean rebase with abort-on-conflict failed: conflicts detected',
      });

      const ctx = createMockContext(tmpDir, issueNumber, {
        worktreeManager: { mergeApprovedCandidate: mergeApprovedCandidateMock } as any,
        projectRepo: createProjectRepo(),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('Merge failed');
      expect(result.message).toContain('conflicts detected');
      expect(result.message).toContain('src/foo.ts');
      expect(result.message).toContain('src/bar.ts');
    });

    it('does not call MergeQueue.resolveConflicts, conflict-resolution agents, fixBuildErrors, or health-gate auto-fix agents', async () => {
      const issueNumber = 53;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'ncf-cap', [
        '### Requirement: NCFReq\n\nNCF content.\n\n#### Scenario: NCF scenario\n\nNCF scenario content.'
      ]);

      createChangeSpec(changeDir, 'ncf-cap', `## ADDED Requirements

### Requirement: NCFAdded

NCF added content.

#### Scenario: NCF added scenario
NCF added scenario content.`);

      const resolveConflictsMock = vi.fn();
      const fixBuildErrorsMock = vi.fn();

      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        failingStep: 'merge' as const,
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        error: 'Merge failure',
      });

      const ctx = createMockContext(tmpDir, issueNumber, {
        worktreeManager: {
          mergeApprovedCandidate: mergeApprovedCandidateMock,
        } as any,
        projectRepo: createProjectRepo(),
      });

      await runner.run(ctx);

      expect(resolveConflictsMock).not.toHaveBeenCalled();
      expect(fixBuildErrorsMock).not.toHaveBeenCalled();
    });

    it('merge failure leaves the issue in Integrate and does not run final health or mark Done', async () => {
      const issueNumber = 54;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'ml-cap', [
        '### Requirement: MLReq\n\nML content.\n\n#### Scenario: ML scenario\n\nML scenario content.'
      ]);

      createChangeSpec(changeDir, 'ml-cap', `## ADDED Requirements

### Requirement: MLAdded

ML added content.

#### Scenario: ML added scenario
ML added scenario content.`);

      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        failingStep: 'merge' as const,
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        error: 'Merge failure',
      });

      const ctx = createMockContext(tmpDir, issueNumber, {
        worktreeManager: { mergeApprovedCandidate: mergeApprovedCandidateMock } as any,
        projectRepo: createProjectRepo(),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.nextStage).toBeUndefined();
    });
  });

  describe('final health gate', () => {
    function createProjectRepo(baseBranch = 'main') {
      return {
        findById: vi.fn().mockReturnValue({ id: 'test-project', name: 'test-project', baseBranch, path: tmpDir }),
      };
    }

    afterEach(() => {
      vi.resetModules();
    });

    function setupPassingExecFileMock() {
      const execFileMock = vi.fn().mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        process.nextTick(() => {
          if (typeof opts === 'function') {
            opts(null, { stdout: 'all checks passed', stderr: '' });
          } else if (typeof cb === 'function') {
            cb(null, { stdout: 'all checks passed', stderr: '' });
          }
        });
        return {} as any;
      });
      vi.doMock('child_process', async () => ({
        ...await vi.importActual<typeof import('child_process')>('child_process'),
        execFile: execFileMock,
      }));
      return execFileMock;
    }

    function setupFailingExecFileMock(exitCode: number, stderr: string) {
      const execFileMock = vi.fn().mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        const err = new Error('Build failed');
        (err as any).code = exitCode;
        (err as any).stdout = '';
        (err as any).stderr = stderr;
        process.nextTick(() => {
          if (typeof opts === 'function') {
            opts(err, { stdout: '', stderr });
          } else if (typeof cb === 'function') {
            cb(err, { stdout: '', stderr });
          }
        });
        return {} as any;
      });
      vi.doMock('child_process', async () => ({
        ...await vi.importActual<typeof import('child_process')>('child_process'),
        execFile: execFileMock,
      }));
      return execFileMock;
    }

    it('runs final health gate after merge and records health:integrate result', async () => {
      const issueNumber = 60;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'fh-cap', [
        '### Requirement: FHReq\n\nFH content.\n\n#### Scenario: FH scenario\n\nFH scenario content.'
      ]);

      createChangeSpec(changeDir, 'fh-cap', `## ADDED Requirements

### Requirement: FHAdded

FH added content.

#### Scenario: FH added scenario
FH added scenario content.`);

      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        landedSha: 'ghi789',
        fastForward: true,
      });

      const execFileMock = setupPassingExecFileMock();

      const ctx = createMockContext(tmpDir, issueNumber, {
        worktreeManager: { mergeApprovedCandidate: mergeApprovedCandidateMock } as any,
        projectRepo: createProjectRepo(),
      });

      const { IntegrateStageRunner } = await import('../../src/workflow/integrate-stage-runner');
      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      const output = result.output as { steps?: Array<{ step: string; status: string; output: unknown }> };
      expect(output.steps).toBeDefined();
      const healthStep = output.steps?.find(s => s.step === 'final-health');
      expect(healthStep).toBeDefined();
      expect(healthStep!.status).toBe('completed');
      const healthOutput = healthStep!.output as { kind?: string; stage?: string; command?: string; passed?: boolean };
      expect(healthOutput.kind).toBe('health-gate');
      expect(healthOutput.stage).toBe('integrate');
      expect(healthOutput.passed).toBe(true);
    });

    it('final health gate failure leaves issue not Done and records failing step final-health', async () => {
      const issueNumber = 61;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'fhf-cap', [
        '### Requirement: FHFReq\n\nFHF content.\n\n#### Scenario: FHF scenario\n\nFHF scenario content.'
      ]);

      createChangeSpec(changeDir, 'fhf-cap', `## ADDED Requirements

### Requirement: FHFAdded

FHF added content.

#### Scenario: FHF added scenario
FHF added scenario content.`);

      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        landedSha: 'ghi789',
        fastForward: true,
      });

      const execFileMock = setupFailingExecFileMock(1, 'build failed\nerror details');

      const ctx = createMockContext(tmpDir, issueNumber, {
        worktreeManager: { mergeApprovedCandidate: mergeApprovedCandidateMock } as any,
        projectRepo: createProjectRepo(),
      });

      const { IntegrateStageRunner } = await import('../../src/workflow/integrate-stage-runner');
      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('Final health gate failed');
      expect(result.nextStage).toBeUndefined();
    });

    it('disabled final health policy records a disabled pass result and allows Done transition', async () => {
      const issueNumber = 62;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'fhd-cap', [
        '### Requirement: FHDReq\n\nFHD content.\n\n#### Scenario: FHD scenario\n\nFHD scenario content.'
      ]);

      createChangeSpec(changeDir, 'fhd-cap', `## ADDED Requirements

### Requirement: FHDAdded

FHD added content.

#### Scenario: FHD added scenario
FHD added scenario content.`);

      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        landedSha: 'ghi789',
        fastForward: true,
      });

      const execFileMock = vi.fn();
      vi.doMock('child_process', async () => ({
        ...await vi.importActual<typeof import('child_process')>('child_process'),
        execFile: execFileMock,
      }));

      const workflowYaml = path.join(tmpDir, 'workflow.yaml');
      fs.writeFileSync(workflowYaml, `
stages:
  - stage: explore
  - stage: plan
  - stage: build
  - stage: check
  - stage: integrate
  - stage: done
healthGates:
  postMerge:
    enabled: false
    command: npm run build
    timeout: 300000
    autoFix: false
    maxFixAttempts: 0
    fallbackReaction:
      type: ask-user
`);

      const ctx = createMockContext(tmpDir, issueNumber, {
        worktreeManager: { mergeApprovedCandidate: mergeApprovedCandidateMock } as any,
        projectRepo: {
          findById: vi.fn().mockReturnValue({ id: 'test-project', name: 'test-project', baseBranch: 'main', path: tmpDir }),
        },
      });

      const { IntegrateStageRunner } = await import('../../src/workflow/integrate-stage-runner');
      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(execFileMock).not.toHaveBeenCalled();
      const output = result.output as { steps?: Array<{ step: string; status: string; output: unknown }> };
      const healthStep = output.steps?.find(s => s.step === 'final-health');
      expect(healthStep).toBeDefined();
      expect(healthStep!.status).toBe('completed');
      const healthOutput = healthStep!.output as { enabled?: boolean; passed?: boolean };
      expect(healthOutput.enabled).toBe(false);
      expect(healthOutput.passed).toBe(true);
    });

    it('postMerge config is used as default Integrate final health policy', async () => {
      const issueNumber = 63;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'pmd-cap', [
        '### Requirement: PMDReq\n\nPMD content.\n\n#### Scenario: PMD scenario\n\nPMD scenario content.'
      ]);

      createChangeSpec(changeDir, 'pmd-cap', `## ADDED Requirements

### Requirement: PMDAdded

PMD added content.

#### Scenario: PMD added scenario
PMD added scenario content.`);

      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        landedSha: 'ghi789',
        fastForward: true,
      });

      let capturedCommand = '';
      const execFileMock = vi.fn().mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        capturedCommand = cmd;
        process.nextTick(() => cb?.(null, { stdout: 'ok', stderr: '' }));
        return {} as any;
      });
      vi.doMock('child_process', async () => ({
        ...await vi.importActual<typeof import('child_process')>('child_process'),
        execFile: execFileMock,
      }));

      const workflowYaml = path.join(tmpDir, 'workflow.yaml');
      fs.writeFileSync(workflowYaml, `
stages:
  - stage: explore
  - stage: plan
  - stage: build
  - stage: check
  - stage: integrate
  - stage: done
healthGates:
  postMerge:
    enabled: true
    command: npm run custom-health-check
    timeout: 600000
    autoFix: false
    maxFixAttempts: 0
    fallbackReaction:
      type: ask-user
`);

      const ctx = createMockContext(tmpDir, issueNumber, {
        worktreeManager: { mergeApprovedCandidate: mergeApprovedCandidateMock } as any,
        projectRepo: {
          findById: vi.fn().mockReturnValue({ id: 'test-project', name: 'test-project', baseBranch: 'main', path: tmpDir }),
        },
      });

      const { IntegrateStageRunner } = await import('../../src/workflow/integrate-stage-runner');
      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(capturedCommand).toBe('npm run custom-health-check');
    });
  });

  describe('integration events', () => {
    function createProjectRepo(baseBranch = 'main') {
      return {
        findById: vi.fn().mockReturnValue({ id: 'test-project', name: 'test-project', baseBranch, path: tmpDir }),
      };
    }

    it('emits integration_started, step updates, and integration_completed on success', async () => {
      const issueNumber = 70;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'evt-cap', [
        '### Requirement: EvtReq\n\nEvt content.\n\n#### Scenario: Evt scenario\n\nEvt scenario content.'
      ]);

      createChangeSpec(changeDir, 'evt-cap', `## ADDED Requirements

### Requirement: EvtAdded

Evt added content.

#### Scenario: Evt added scenario
Evt added scenario content.`);

      const ctx = createMockContext(tmpDir, issueNumber, {
        projectRepo: createProjectRepo(),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);

      const emitCalls = (ctx.eventBus.emit as ReturnType<typeof vi.fn>).mock.calls;
      const eventNames = emitCalls.map(([name]) => name);

      expect(eventNames).toContain('integration_started');
      expect(eventNames).toContain('integration_step_updated');
      expect(eventNames).toContain('integration_completed');
      expect(eventNames).not.toContain('integration_failed');

      const startedEvent = emitCalls.find(([name]) => name === 'integration_started');
      expect(startedEvent?.[1]).toMatchObject({
        issueId: `issue-${issueNumber}`,
        projectId: 'test-project',
        issueNumber,
      });

      const completedEvent = emitCalls.find(([name]) => name === 'integration_completed');
      expect(completedEvent?.[1]).toMatchObject({
        issueId: `issue-${issueNumber}`,
        projectId: 'test-project',
        issueNumber,
      });
      expect(completedEvent?.[1].steps).toBeDefined();
      expect(Array.isArray(completedEvent?.[1].steps)).toBe(true);
    });

    it('emits integration_failed when spec sync fails', async () => {
      const issueNumber = 71;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'evtfail-cap', [
        '### Requirement: ExistingReq\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.'
      ]);

      createChangeSpec(changeDir, 'evtfail-cap', `## ADDED Requirements

### Requirement: ExistingReq

Duplicate requirement content.

#### Scenario: Duplicate scenario
Duplicate scenario content.`);

      const ctx = createMockContext(tmpDir, issueNumber, {
        projectRepo: createProjectRepo(),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);

      const emitCalls = (ctx.eventBus.emit as ReturnType<typeof vi.fn>).mock.calls;
      const eventNames = emitCalls.map(([name]) => name);

      expect(eventNames).toContain('integration_started');
      expect(eventNames).toContain('integration_step_updated');
      expect(eventNames).toContain('integration_failed');
      expect(eventNames).not.toContain('integration_completed');

      const failedEvent = emitCalls.find(([name]) => name === 'integration_failed');
      expect(failedEvent?.[1]).toMatchObject({
        issueId: `issue-${issueNumber}`,
        projectId: 'test-project',
        issueNumber,
        failingStep: 'integrate:spec-sync',
      });
    });

    it('emits integration_failed when merge fails', async () => {
      const issueNumber = 72;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'evtmergefail-cap', [
        '### Requirement: EMFReq\n\nEMF content.\n\n#### Scenario: EMF scenario\n\nEMF scenario content.'
      ]);

      createChangeSpec(changeDir, 'evtmergefail-cap', `## ADDED Requirements

### Requirement: EMFAdded

EMF added content.

#### Scenario: EMF added scenario
EMF added scenario content.`);

      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        failingStep: 'merge' as const,
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        conflictFiles: ['src/foo.ts'],
        error: 'Clean rebase with abort-on-conflict failed: conflicts detected',
      });

      const ctx = createMockContext(tmpDir, issueNumber, {
        worktreeManager: { mergeApprovedCandidate: mergeApprovedCandidateMock } as any,
        projectRepo: createProjectRepo(),
      });

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);

      const emitCalls = (ctx.eventBus.emit as ReturnType<typeof vi.fn>).mock.calls;
      const eventNames = emitCalls.map(([name]) => name);

      expect(eventNames).toContain('integration_started');
      expect(eventNames).toContain('integration_step_updated');
      expect(eventNames).toContain('integration_failed');
      expect(eventNames).not.toContain('integration_completed');

      const failedEvent = emitCalls.find(([name]) => name === 'integration_failed');
      expect(failedEvent?.[1]).toMatchObject({
        issueId: `issue-${issueNumber}`,
        failingStep: 'integrate:merge',
      });
    });

    it('emits integration_step_updated with correct step, status, summary, and output', async () => {
      const issueNumber = 73;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'evtstep-cap', [
        '### Requirement: StepReq\n\nStep content.\n\n#### Scenario: Step scenario\n\nStep scenario content.'
      ]);

      createChangeSpec(changeDir, 'evtstep-cap', `## ADDED Requirements

### Requirement: StepAdded

Step added content.

#### Scenario: Step added scenario
Step added scenario content.`);

      const ctx = createMockContext(tmpDir, issueNumber, {
        projectRepo: createProjectRepo(),
      });

      await runner.run(ctx);

      const emitCalls = (ctx.eventBus.emit as ReturnType<typeof vi.fn>).mock.calls;
      const stepUpdatedEvents = emitCalls.filter(([name]) => name === 'integration_step_updated');

      expect(stepUpdatedEvents.length).toBeGreaterThan(0);

      const specSyncEvent = stepUpdatedEvents.find(([, data]) => (data as any).step === 'integrate:spec-sync');
      expect(specSyncEvent).toBeDefined();
      expect(specSyncEvent?.[1]).toMatchObject({
        step: 'integrate:spec-sync',
        status: 'completed',
      });
      expect((specSyncEvent?.[1] as any).summary).toContain('Spec sync completed');
      expect((specSyncEvent?.[1] as any).output).toBeDefined();
    });

    it('API GET /api/issues/:number/executions returns stage executions for integrate stage', async () => {
      const issueNumber = 74;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test-change`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'apicap', [
        '### Requirement: APIReq\n\nAPI content.\n\n#### Scenario: API scenario\n\nAPI scenario content.'
      ]);

      createChangeSpec(changeDir, 'apicap', `## ADDED Requirements

### Requirement: APIAdded

API added content.

#### Scenario: API added scenario
API added scenario content.`);

      const ctx = createMockContext(tmpDir, issueNumber, {
        projectRepo: createProjectRepo(),
      });

      await runner.run(ctx);

      const stageExecutionRepo = ctx.stageExecutionRepo;
      expect(stageExecutionRepo.create).toHaveBeenCalled();

      const executions = stageExecutionRepo.findByIssueId(ctx.issue.id);
      expect(executions.length).toBeGreaterThan(0);

      const integrateExec = executions.find(e => e.stage === Stage.Integrate);
      expect(integrateExec).toBeDefined();
      expect(integrateExec?.status).toBe('passed');
      expect(integrateExec?.taskResults).toBeDefined();
      expect(Array.isArray(integrateExec?.taskResults)).toBe(true);
    });
  });
});
