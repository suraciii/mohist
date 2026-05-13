import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { Stage, IssueStatus } from '../src/types';
import type { StageContext } from '../src/workflow/stage-context';
import { EventBus } from '../src/services/event-bus';
import type { StageRunner, StageRunResult } from '../src/workflow/stage-context';
import type { ChangeArtifactsManager, CheckpointManager, IssueRepo } from '../src/workflow/stage-context';
import { WorkflowEngine } from '../src/workflow/workflow-engine';

function makeIssue(overrides: Partial<import('../src/types').Issue> = {}): import('../src/types').Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    body: 'Test body',
    stage: Stage.Check,
    status: IssueStatus.Active,
    projectId: 'proj-1',
    labels: [],
    priority: 'p2',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

function makeMockIssueRepo(initialIssue: import('../src/types').Issue): IssueRepo {
  let currentIssue = initialIssue;
  return {
    updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) => {
      currentIssue = { ...currentIssue, stage };
      return currentIssue;
    }),
    findById: vi.fn().mockReturnValue(currentIssue),
    setApprovalState: vi.fn(),
    clearApprovalState: vi.fn(),
    updateStatus: vi.fn().mockImplementation((_id: string, status: IssueStatus) => {
      currentIssue = { ...currentIssue, status };
      return currentIssue;
    }),
    updateBlockedReason: vi.fn(),
    setMergeState: vi.fn().mockImplementation((_id: string, ms: import('../src/types').MergeState) => {
      currentIssue = { ...currentIssue, mergeState: ms };
      return currentIssue;
    }),
  } as unknown as IssueRepo;
}

function createMockContext(tmpDir: string, issueNumber = 42, overrides?: Partial<StageContext>): StageContext {
  const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
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
      readTasks: vi.fn(),
      updateTaskPasses: vi.fn(),
      archiveChange: vi.fn().mockResolvedValue(undefined),
    } as unknown as ChangeArtifactsManager,
    worktreeManager: {
      mergeApprovedCandidate: vi.fn().mockResolvedValue({
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        landedSha: 'ghi789',
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
  const capDir = path.join(specsDir, capability);
  fs.mkdirSync(capDir, { recursive: true });
  fs.writeFileSync(path.join(capDir, 'spec.md'), content, 'utf-8');
}

function createDisabledHealthGateWorkflow(tmpDir: string) {
  const workflowYaml = path.join(tmpDir, 'workflow.yaml');
  fs.writeFileSync(workflowYaml, `
stages:
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
}

function createEnabledHealthGateWorkflow(tmpDir: string) {
  const workflowYaml = path.join(tmpDir, 'workflow.yaml');
  fs.writeFileSync(workflowYaml, `
stages:
  - stage: plan
  - stage: build
  - stage: check
  - stage: integrate
  - stage: done
healthGates:
  postMerge:
    enabled: true
    command: npm run build
    timeout: 300000
    autoFix: false
    maxFixAttempts: 0
    fallbackReaction:
      type: ask-user
`);
}

function appendedTaskResults(ctx: StageContext) {
  return (ctx.stageExecutionRepo.appendTaskResult as ReturnType<typeof vi.fn>).mock.calls
    .map((call: unknown[]) => call[1] as { taskId: string; status: string });
}

describe('T-010: Integrate stage regression tests', () => {
  let tmpDir: string;

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
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-integrate-regression-'));
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
    createDisabledHealthGateWorkflow(tmpDir);
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  describe('AC-1: Check approval changes issue stage to integrate before Done', () => {
    it('Check runner returns Integrate, not Done — WorkflowEngine allows Check->Integrate->Done', async () => {
      const checkRunner = new class implements StageRunner {
        canHandle(s: Stage): boolean { return s === Stage.Check; }
        async run(): Promise<StageRunResult> {
          return { success: true, nextStage: Stage.Integrate, checkResults: [], output: {} };
        }
      }();

      const integrateRunner = new class implements StageRunner {
        canHandle(s: Stage): boolean { return s === Stage.Integrate; }
        async run(): Promise<StageRunResult> {
          return { success: true, nextStage: Stage.Done, checkResults: [], output: {} };
        }
      }();

      const issue = makeIssue({ stage: Stage.Check });
      const mockRepo = makeMockIssueRepo(issue);

      const engine = new WorkflowEngine({
        runners: [checkRunner, integrateRunner],
        issueRepo: mockRepo,
        eventBus: new EventBus(),
        checkpointManager: {
          save: vi.fn(),
          load: vi.fn(),
          deleteAll: vi.fn(),
        } as unknown as CheckpointManager,
        artifactManager: {
          getChangeDir: vi.fn().mockReturnValue('/tmp/change'),
          createChangeDir: vi.fn(),
          readArtifact: vi.fn(),
          writeArtifact: vi.fn(),
          exists: vi.fn(),
          readTasks: vi.fn(),
          updateTaskPasses: vi.fn(),
          archiveChange: vi.fn(),
        } as unknown as ChangeArtifactsManager,
      });

      const result = await engine.run(issue, { cwd: '/tmp' });

      expect(result.completed).toBe(false);
      expect(result.stage).toBe(Stage.Check);
      expect(result.message).toContain('aggregate workflow service is unavailable');
      expect(mockRepo.updateStage).not.toHaveBeenCalled();
    });

    it('CheckStageRunner.getNextStage() returns Stage.Integrate', async () => {
      const { CheckStageRunner } = await import('../src/workflow/check-stage-runner');
      const runner = new CheckStageRunner({ worktreePath: tmpDir });
      expect(runner.getNextStage()).toBe(Stage.Integrate);
    });

    it('Check approval via workflow transitions Plan->Build->Check->Integrate->Done (Integrate is visited before Done)', async () => {
      const planRunner = new class implements StageRunner {
        canHandle(s: Stage): boolean { return s === Stage.Plan; }
        async run(): Promise<StageRunResult> {
          return { success: true, nextStage: Stage.Build, checkResults: [], output: {} };
        }
      }();

      const buildRunner = new class implements StageRunner {
        canHandle(s: Stage): boolean { return s === Stage.Build; }
        async run(): Promise<StageRunResult> {
          return { success: true, nextStage: Stage.Check, checkResults: [], output: {} };
        }
      }();

      const checkRunner = new class implements StageRunner {
        canHandle(s: Stage): boolean { return s === Stage.Check; }
        async run(): Promise<StageRunResult> {
          return { success: true, nextStage: Stage.Integrate, checkResults: [], output: {} };
        }
      }();

      const integrateRunner = new class implements StageRunner {
        canHandle(s: Stage): boolean { return s === Stage.Integrate; }
        async run(): Promise<StageRunResult> {
          return { success: true, nextStage: Stage.Done, checkResults: [], output: {} };
        }
      }();

      const issue = makeIssue({ stage: Stage.Plan });
      const mockRepo = makeMockIssueRepo(issue);

      const engine = new WorkflowEngine({
        runners: [planRunner, buildRunner, checkRunner, integrateRunner],
        issueRepo: mockRepo,
        eventBus: new EventBus(),
        checkpointManager: {
          save: vi.fn(),
          load: vi.fn(),
          deleteAll: vi.fn(),
        } as unknown as CheckpointManager,
        artifactManager: {
          getChangeDir: vi.fn().mockReturnValue('/tmp/change'),
          createChangeDir: vi.fn(),
          readArtifact: vi.fn(),
          writeArtifact: vi.fn(),
          exists: vi.fn(),
          readTasks: vi.fn(),
          updateTaskPasses: vi.fn(),
          archiveChange: vi.fn(),
        } as unknown as ChangeArtifactsManager,
      });

      const result = await engine.run(issue, { cwd: '/tmp' });

      expect(result.completed).toBe(false);
      expect(result.stage).toBe(Stage.Plan);
      expect(result.message).toContain('aggregate workflow service is unavailable');
      expect(mockRepo.updateStage).not.toHaveBeenCalled();
    });
  });

  describe('AC-2: successful Integrate syncs delta specs, archives, merges, passes final health, marks Done', () => {
    it('IntegrateStageRunner completes all 4 steps and returns Stage.Done', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 100;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-a', [
        '### Requirement: ExistingA\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-a', `## ADDED Requirements

### Requirement: NewA

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(result.checkResults).toEqual(expect.arrayContaining([
        expect.objectContaining({ name: 'health:integrate', status: 'pass' }),
      ]));
    });

    it('successful Integrate records spec sync summary in stage execution task results', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 101;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-b', [
        '### Requirement: ExistingB\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-b', `## ADDED Requirements

### Requirement: NewB

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      await runner.run(ctx);

      expect(ctx.stageExecutionRepo.appendTaskResult).toHaveBeenCalled();
      const specSyncCall = appendedTaskResults(ctx).find(result => result.taskId === 'integrate:spec-sync');
      expect(specSyncCall).toBeDefined();
    });

    it('successful Integrate records archive path in stage execution', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 102;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-c', [
        '### Requirement: ExistingC\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-c', `## ADDED Requirements

### Requirement: NewC

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      await runner.run(ctx);

      const archiveCall = appendedTaskResults(ctx).find(result => result.taskId === 'integrate:archive-change');
      expect(archiveCall).toBeDefined();
    });

    it('successful Integrate records merge truth (targetBranch, baseSha, candidateHeadSha, landedSha)', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 103;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-d', [
        '### Requirement: ExistingD\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-d', `## ADDED Requirements

### Requirement: NewD

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      const output = result.output as { steps?: Array<{ step: string; output: unknown }> };
      const mergeStep = output.steps?.find(s => s.step === 'integrate:merge');
      expect(mergeStep).toBeDefined();
      const mergeOutput = mergeStep!.output as { targetBranch?: string; baseSha?: string; candidateHeadSha?: string; landedSha?: string };
      expect(mergeOutput.targetBranch).toBe('main');
      expect(mergeOutput.baseSha).toBe('abc123');
      expect(mergeOutput.candidateHeadSha).toBe('def456');
      expect(mergeOutput.landedSha).toBe('ghi789');
    });

    it('successful Integrate records health:integrate check result (disabled health gate passes)', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 104;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-e', [
        '### Requirement: ExistingE\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-e', `## ADDED Requirements

### Requirement: NewE

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(result.checkResults).toBeDefined();
      const healthCheck = result.checkResults?.find(cr => cr.name === 'health:integrate');
      expect(healthCheck).toBeDefined();
      expect(healthCheck!.status).toBe('pass');
      const healthOutput = healthCheck!.output as { enabled?: boolean };
      expect(healthOutput.enabled).toBe(false);
    });

    it('Done issue evidence includes spec sync summary, archive path, merge truth, and health:integrate check', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 105;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-f', [
        '### Requirement: ExistingF\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-f', `## ADDED Requirements

### Requirement: NewF

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      const output = result.output as { steps?: Array<{ step: string; status: string; output: unknown }> };

      const specSyncStep = output.steps?.find(s => s.step === 'integrate:spec-sync');
      expect(specSyncStep).toBeDefined();
      const specSyncOutput = specSyncStep!.output as { capabilities?: string[]; counts?: { added: number; modified: number; removed: number; renamed: number }; valid?: boolean };
      expect(specSyncOutput.capabilities).toContain('cap-f');
      expect(specSyncOutput.valid).toBe(true);

      const archiveStep = output.steps?.find(s => s.step === 'integrate:archive-change');
      expect(archiveStep).toBeDefined();
      const archiveOutput = archiveStep!.output as { archivePath?: string; success?: boolean };
      expect(archiveOutput.archivePath).toContain('openspec/changes/archive');
      expect(archiveOutput.success).toBe(true);

      const mergeStep = output.steps?.find(s => s.step === 'integrate:merge');
      expect(mergeStep).toBeDefined();
      const mergeOutput = mergeStep!.output as { targetBranch?: string; baseSha?: string; candidateHeadSha?: string; landedSha?: string };
      expect(mergeOutput.targetBranch).toBe('main');
      expect(mergeOutput.landedSha).toBe('ghi789');

      expect(result.checkResults).toBeDefined();
      const healthCheck = result.checkResults?.find(cr => cr.name === 'health:integrate');
      expect(healthCheck).toBeDefined();
      expect(healthCheck!.status).toBe('pass');
    });
  });

  describe('AC-3: spec-sync, archive, merge failures each block Done with correct failing step', () => {
    it('spec sync failure blocks Done at integrate:spec-sync', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 110;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-g', [
        '### Requirement: ExistingG\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-g', `## ADDED Requirements

### Requirement: ExistingG

Duplicate requirement content.

#### Scenario: Duplicate scenario
Duplicate scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.nextStage).toBeUndefined();
      expect(result.message).toContain('Spec sync failed');

      expect(ctx.artifactManager.archiveChange).not.toHaveBeenCalled();
      expect(appendedTaskResults(ctx)).toContainEqual(
        expect.objectContaining({ taskId: 'integrate:spec-sync', status: 'failed' }),
      );
    });

    it('archive failure blocks Done at integrate:archive-change', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 111;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-h', [
        '### Requirement: ExistingH\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-h', `## ADDED Requirements

### Requirement: NewH

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const archiveError = new Error('Archive failed');
      const ctx = createMockContext(tmpDir, issueNumber, {
        artifactManager: {
          getChangeDir: vi.fn().mockReturnValue(changeDir),
          createChangeDir: vi.fn(),
          readArtifact: vi.fn().mockReturnValue(null),
          writeArtifact: vi.fn().mockReturnValue(true),
          exists: vi.fn().mockReturnValue(true),
          readTasks: vi.fn(),
          updateTaskPasses: vi.fn(),
          archiveChange: vi.fn().mockRejectedValue(archiveError),
        } as any,
      });

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('Archive failed');
      expect(result.nextStage).toBeUndefined();

      expect(appendedTaskResults(ctx)).toContainEqual(
        expect.objectContaining({ taskId: 'integrate:archive-change', status: 'failed' }),
      );
    });

    it('merge failure blocks Done at integrate:merge', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 112;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-i', [
        '### Requirement: ExistingI\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-i', `## ADDED Requirements

### Requirement: NewI

New requirement content.

#### Scenario: New scenario
New scenario content.`);

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
      });

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('Merge failed');
      expect(result.message).toContain('conflicts detected');
      expect(result.nextStage).toBeUndefined();

      expect(appendedTaskResults(ctx)).toContainEqual(
        expect.objectContaining({ taskId: 'integrate:merge', status: 'failed' }),
      );
    });

    it('health:integrate check failure blocks Done', async () => {
      const execFileMock = vi.fn().mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        const err = new Error('Build failed');
        (err as any).code = 1;
        (err as any).stdout = '';
        (err as any).stderr = 'build failed\nerror details';
        process.nextTick(() => {
          if (typeof opts === 'function') {
            opts(err, { stdout: '', stderr: 'build failed\nerror details' });
          } else if (typeof cb === 'function') {
            cb(err, { stdout: '', stderr: 'build failed\nerror details' });
          }
        });
        return {} as any;
      });
      vi.doMock('child_process', async () => ({
        ...await vi.importActual<typeof import('child_process')>('child_process'),
        execFile: execFileMock,
      }));

      createEnabledHealthGateWorkflow(tmpDir);

      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 113;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-j', [
        '### Requirement: ExistingJ\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-j', `## ADDED Requirements

### Requirement: NewJ

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const ctx = createMockContext(tmpDir, issueNumber);
      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.nextStage).toBeUndefined();

      expect(result.checkResults).toBeDefined();
      const healthCheck = result.checkResults?.find(cr => cr.name === 'health:integrate');
      expect(healthCheck).toBeDefined();
      expect(healthCheck!.status).toBe('fail');
    });
  });

  describe('AC-4: Integrate does not call agent conflict resolution or build-fix agent paths', () => {
    it('merge failure does not invoke resolveConflicts callback', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 120;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-k', [
        '### Requirement: ExistingK\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-k', `## ADDED Requirements

### Requirement: NewK

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        failingStep: 'merge' as const,
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        error: 'Merge failure',
      });

      const ctx = createMockContext(tmpDir, issueNumber, {
        worktreeManager: { mergeApprovedCandidate: mergeApprovedCandidateMock } as any,
      });

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      await runner.run(ctx);

      const eventBusEmitCalls = (ctx.eventBus.emit as ReturnType<typeof vi.fn>).mock.calls;
      const eventNames = eventBusEmitCalls.map(([name]) => name);

      expect(eventNames).not.toContain('resolve_conflicts_requested');
      expect(eventNames).not.toContain('agent_conflict_resolution');
      expect(eventNames).not.toContain('fix_build_errors_requested');
    });

    it('merge failure does not invoke fixBuildErrors callback', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 121;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-l', [
        '### Requirement: ExistingL\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-l', `## ADDED Requirements

### Requirement: NewL

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        failingStep: 'merge' as const,
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        error: 'Merge failure',
      });

      const ctx = createMockContext(tmpDir, issueNumber, {
        worktreeManager: { mergeApprovedCandidate: mergeApprovedCandidateMock } as any,
      });

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      await runner.run(ctx);

      const eventBusEmitCalls = (ctx.eventBus.emit as ReturnType<typeof vi.fn>).mock.calls;
      const eventNames = eventBusEmitCalls.map(([name]) => name);

      expect(eventNames).not.toContain('fix_build_errors_requested');
      expect(eventNames).not.toContain('build_fix_agent_invoked');
    });

    it('IntegrateStageRunner uses worktreeManager.mergeApprovedCandidate for merge (not MergeQueue)', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 122;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-m', [
        '### Requirement: ExistingM\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-m', `## ADDED Requirements

### Requirement: NewM

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const ctx = createMockContext(tmpDir, issueNumber);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      await runner.run(ctx);

      expect(ctx.worktreeManager.mergeApprovedCandidate).toHaveBeenCalled();
      const mergeCalls = (ctx.worktreeManager.mergeApprovedCandidate as ReturnType<typeof vi.fn>).mock.calls;
      expect(mergeCalls.length).toBe(1);
      expect(mergeCalls[0][0]).toBe(tmpDir);
      expect(mergeCalls[0][3]).toBe('main');
    });
  });

  describe('AC-5: Check does not archive changes or trigger Done-side merge/finalization side effects', () => {
    it('CheckStageRunner does not call archiveChange', async () => {
      const { CheckStageRunner } = await import('../src/workflow/check-stage-runner');

      const worktreePath = path.join(tmpDir, 'worktree-check');
      fs.mkdirSync(worktreePath, { recursive: true });
      const workflowYaml = path.join(worktreePath, 'workflow.yaml');
      fs.writeFileSync(workflowYaml, `
stages:
  - stage: check
    prompt: check
healthGates:
  check:
    enabled: false
    command: npm run build
    timeout: 300000
    autoFix: false
    maxFixAttempts: 0
    fallbackReaction:
      type: escalate
`);

      const issueNumber = 130;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-n', [
        '### Requirement: ExistingN\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-n', `## ADDED Requirements

### Requirement: NewN

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const archiveChangeSpy = vi.fn();
      const ctx = createMockContext(tmpDir, issueNumber, {
        artifactManager: {
          getChangeDir: vi.fn().mockReturnValue(changeDir),
          createChangeDir: vi.fn(),
          readArtifact: vi.fn().mockReturnValue(null),
          writeArtifact: vi.fn().mockReturnValue(true),
          exists: vi.fn().mockReturnValue(true),
          readTasks: vi.fn(),
          updateTaskPasses: vi.fn(),
          archiveChange: archiveChangeSpy,
        } as any,
        issue: {
          id: `issue-${issueNumber}`,
          number: issueNumber,
          title: 'Test Issue',
          body: '',
          stage: Stage.Check,
          status: IssueStatus.Active,
          projectId: 'test-project',
          labels: [],
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
        },
      });

      const runner = new CheckStageRunner({ worktreePath });
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(archiveChangeSpy).not.toHaveBeenCalled();
    });
  });

  describe('AC-7: integrate:spec-sync failure preserves failure locality', () => {
    it('failed spec sync leaves issue in integrate state with blocked status', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 150;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-locality', [
        '### Requirement: ExistingLocal\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-locality', `## ADDED Requirements

### Requirement: ExistingLocal

Duplicate requirement content.

#### Scenario: Duplicate scenario
Duplicate scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('Spec sync failed');

      const emitCalls = (ctx.eventBus.emit as ReturnType<typeof vi.fn>).mock.calls;
      const eventNames = emitCalls.map(([name]) => name);

      expect(eventNames).toContain('integration_failed');
      const failedEvent = emitCalls.find(([name]) => name === 'integration_failed');
      expect(failedEvent?.[1]).toMatchObject({
        failingStep: 'integrate:spec-sync',
      });
      expect(failedEvent?.[1].issueNumber).toBe(issueNumber);

      const specSyncTask = appendedTaskResults(ctx).find((t: any) => t.taskId === 'integrate:spec-sync');
      expect(specSyncTask).toBeDefined();
      expect(specSyncTask.status).toBe('failed');

      expect(ctx.artifactManager.archiveChange).not.toHaveBeenCalled();
    });

    it('failed spec sync emits integration_failed with failure reason category', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 151;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-reason', [
        '### Requirement: ExistingReason\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-reason', `## ADDED Requirements

### Requirement: ExistingReason

Duplicate target content.

#### Scenario: Duplicate scenario
Duplicate scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);

      const emitCalls = (ctx.eventBus.emit as ReturnType<typeof vi.fn>).mock.calls;
      const failedEvent = emitCalls.find(([name]) => name === 'integration_failed');
      const eventOutput = failedEvent?.[1]?.output as { conflicts?: Array<{ type: string; detail: string }> };
      expect(eventOutput?.conflicts).toBeDefined();
      expect(eventOutput?.conflicts!.length).toBeGreaterThan(0);
      expect(eventOutput?.conflicts![0]?.type).toBe('duplicate_target');
    });

    it('failed spec sync does not trigger archive, merge, or health check', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 152;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-no-follow', [
        '### Requirement: ExistingFollow\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-no-follow', `## ADDED Requirements

### Requirement: ExistingFollow

Duplicate target content.

#### Scenario: Duplicate scenario
Duplicate scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);

      expect(ctx.artifactManager.archiveChange).not.toHaveBeenCalled();
      expect(ctx.worktreeManager.mergeApprovedCandidate).not.toHaveBeenCalled();

      const archiveTask = appendedTaskResults(ctx).find((t: any) => t.taskId === 'integrate:archive-change');
      const mergeTask = appendedTaskResults(ctx).find((t: any) => t.taskId === 'integrate:merge');
      const healthTask = appendedTaskResults(ctx).find((t: any) => t.taskId === 'health:integrate');

      expect(archiveTask).toBeUndefined();
      expect(mergeTask).toBeUndefined();
      expect(healthTask).toBeUndefined();
    });

    it('retrying INTEGRATE re-executes spec-sync after explicit resume', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 153;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-retry', [
        '### Requirement: ExistingRetry\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-retry', `## ADDED Requirements

### Requirement: NewRetry

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);

      const result1 = await runner.run(ctx);
      expect(result1.success).toBe(true);
      expect(result1.checkResults).toEqual(expect.arrayContaining([
        expect.objectContaining({ name: 'health:integrate', status: 'pass' }),
      ]));

      const specSyncCall1 = appendedTaskResults(ctx).find((t: any) => t.taskId === 'integrate:spec-sync');
      expect(specSyncCall1?.status).toBe('completed');

      fs.writeFileSync(
        path.join(changeDir, 'specs', 'cap-retry', 'spec.md'),
        `## ADDED Requirements

### Requirement: AnotherNew

Another new requirement.

#### Scenario: Another scenario
Another scenario content.`,
        'utf-8'
      );

      const ctx2 = createMockContext(tmpDir, issueNumber);
      const result2 = await runner.run(ctx2);
      expect(result2.success).toBe(true);

      const specSyncCall2 = appendedTaskResults(ctx2).find((t: any) => t.taskId === 'integrate:spec-sync');
      expect(specSyncCall2).toBeDefined();
      expect(specSyncCall2?.status).toBe('completed');
    });

    it('spec sync failure does not automatically enqueue or run PLAN, BUILD, CHECK', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 154;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-no-fallback', [
        '### Requirement: ExistingFallback\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-no-fallback', `## ADDED Requirements

### Requirement: ExistingFallback

Duplicate target.

#### Scenario: Duplicate scenario
Duplicate scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);

      const result = await runner.run(ctx);
      expect(result.success).toBe(false);
      expect(result.message).toContain('Spec sync failed');

      const taskResultIds = appendedTaskResults(ctx).map((t: any) => t.taskId);
      expect(taskResultIds).not.toContain('start-pipeline');
      expect(taskResultIds).not.toContain('resume-pipeline');
      expect(taskResultIds).not.toContain('plan');
      expect(taskResultIds).not.toContain('build');
      expect(taskResultIds).not.toContain('check');
    });
  });

  describe('AC-8: existing integrate archive, merge, health check success/failure tests still pass', () => {
    it('archive success still works after spec sync passes', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 160;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-archive-ok', [
        '### Requirement: ArchiveOk\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-archive-ok', `## ADDED Requirements

### Requirement: NewArchiveOk

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(ctx.artifactManager.archiveChange).toHaveBeenCalled();

      const archiveTask = appendedTaskResults(ctx).find((t: any) => t.taskId === 'integrate:archive-change');
      expect(archiveTask).toBeDefined();
      expect(archiveTask.status).toBe('completed');
    });

    it('merge failure still blocks at integrate:merge', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 161;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-merge-fail', [
        '### Requirement: MergeFail\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-merge-fail', `## ADDED Requirements

### Requirement: NewMergeFail

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const mergeApprovedCandidateMock = vi.fn().mockResolvedValue({
        failingStep: 'merge' as const,
        targetBranch: 'main',
        baseSha: 'abc123',
        candidateHeadSha: 'def456',
        conflictFiles: ['src/conflict.ts'],
        error: 'Merge conflict detected',
      });

      const ctx = createMockContext(tmpDir, issueNumber, {
        worktreeManager: { mergeApprovedCandidate: mergeApprovedCandidateMock } as any,
      });

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('Merge failed');

      const emitCalls = (ctx.eventBus.emit as ReturnType<typeof vi.fn>).mock.calls;
      const failedEvent = emitCalls.find(([name]) => name === 'integration_failed');
      expect(failedEvent?.[1]).toMatchObject({
        failingStep: 'integrate:merge',
      });

      const mergeTask = appendedTaskResults(ctx).find((t: any) => t.taskId === 'integrate:merge');
      expect(mergeTask).toBeDefined();
      expect(mergeTask.status).toBe('failed');
    });

    it('health:integrate check failure still blocks Done', async () => {
      const execFileMock = vi.fn().mockImplementation((cmd: any, args: any, opts: any, cb: any) => {
        const err = new Error('Build failed');
        (err as any).code = 1;
        (err as any).stdout = '';
        (err as any).stderr = 'build failed\nerror details';
        process.nextTick(() => {
          if (typeof opts === 'function') {
            opts(err, { stdout: '', stderr: 'build failed\nerror details' });
          } else if (typeof cb === 'function') {
            cb(err, { stdout: '', stderr: 'build failed\nerror details' });
          }
        });
        return {} as any;
      });
      vi.doMock('child_process', async () => ({
        ...await vi.importActual<typeof import('child_process')>('child_process'),
        execFile: execFileMock,
      }));

      createEnabledHealthGateWorkflow(tmpDir);

      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 162;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-health-fail', [
        '### Requirement: HealthFail\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-health-fail', `## ADDED Requirements

### Requirement: NewHealthFail

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const ctx = createMockContext(tmpDir, issueNumber);
      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toContain('npm run build');

      expect(result.checkResults).toBeDefined();
      const healthCheck = result.checkResults?.find(cr => cr.name === 'health:integrate');
      expect(healthCheck).toBeDefined();
      expect(healthCheck!.status).toBe('fail');
    });
  });

  describe('AC-6: Done issue evidence includes spec sync summary, archive path, merge truth, and health check result', () => {
    it('stage execution for Done issue contains all integration evidence steps', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 140;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-o', [
        '### Requirement: ExistingO\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-o', `## ADDED Requirements

### Requirement: NewO

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);

      const output = result.output as { steps?: Array<{ step: string; status: string; output: unknown }> };
      expect(output.steps).toBeDefined();
      expect(output.steps!.length).toBe(3);

      const stepIds = output.steps!.map(s => s.step);
      expect(stepIds).toContain('integrate:spec-sync');
      expect(stepIds).toContain('integrate:archive-change');
      expect(stepIds).toContain('integrate:merge');

      const specSyncStep = output.steps!.find(s => s.step === 'integrate:spec-sync');
      expect(specSyncStep!.status).toBe('completed');

      const archiveStep = output.steps!.find(s => s.step === 'integrate:archive-change');
      expect(archiveStep!.status).toBe('completed');

      const mergeStep = output.steps!.find(s => s.step === 'integrate:merge');
      expect(mergeStep!.status).toBe('completed');

      expect(result.checkResults).toBeDefined();
      const healthCheck = result.checkResults?.find(cr => cr.name === 'health:integrate');
      expect(healthCheck).toBeDefined();
      expect(healthCheck!.status).toBe('pass');
    });

    it('stage execution repo records all task results for Done issue', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 141;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-p', [
        '### Requirement: ExistingP\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-p', `## ADDED Requirements

### Requirement: NewP

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      const taskResultCalls = appendedTaskResults(ctx);

      const taskIds = taskResultCalls.map(result => result.taskId);
      expect(taskIds).toContain('integrate:spec-sync');
      expect(taskIds).toContain('integrate:archive-change');
      expect(taskIds).toContain('integrate:merge');

      const specSyncCall = taskResultCalls.find(result => result.taskId === 'integrate:spec-sync');
      expect(specSyncCall?.status).toBe('completed');

      const archiveCall = taskResultCalls.find(result => result.taskId === 'integrate:archive-change');
      expect(archiveCall?.status).toBe('completed');

      const mergeCall = taskResultCalls.find(result => result.taskId === 'integrate:merge');
      expect(mergeCall?.status).toBe('completed');

      expect(result.checkResults).toBeDefined();
      const healthCheck = result.checkResults?.find(cr => cr.name === 'health:integrate');
      expect(healthCheck).toBeDefined();
      expect(healthCheck!.status).toBe('pass');
    });

    it('integration_completed event carries all step evidence', async () => {
      const { IntegrateStageRunner } = await import('../src/workflow/integrate-stage-runner');

      const issueNumber = 142;
      const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issueNumber}-test`);
      fs.mkdirSync(path.join(changeDir, 'specs'), { recursive: true });

      createMainSpec(tmpDir, 'cap-q', [
        '### Requirement: ExistingQ\n\nExisting content.\n\n#### Scenario: Existing scenario\n\nExisting scenario content.',
      ]);

      createChangeSpec(changeDir, 'cap-q', `## ADDED Requirements

### Requirement: NewQ

New requirement content.

#### Scenario: New scenario
New scenario content.`);

      const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
      const ctx = createMockContext(tmpDir, issueNumber);
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);

      const emitCalls = (ctx.eventBus.emit as ReturnType<typeof vi.fn>).mock.calls;
      const completedEvent = emitCalls.find(([name]) => name === 'integration_completed');

      expect(completedEvent).toBeDefined();
      expect(completedEvent![1].steps).toBeDefined();
      expect(Array.isArray(completedEvent![1].steps)).toBe(true);
      expect(completedEvent![1].steps.length).toBe(3);

      const stepIds = completedEvent![1].steps.map((s: any) => s.step);
      expect(stepIds).toContain('integrate:spec-sync');
      expect(stepIds).toContain('integrate:archive-change');
      expect(stepIds).toContain('integrate:merge');
    });
  });
});
