import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { execFile } from 'child_process';
import { promisify } from 'util';
import { Stage, IssueStatus } from '../../src/types';
import type { StageContext } from '../../src/workflow/stage-context';
import { EventBus } from '../../src/services/event-bus';
import { slugify } from '../../src/utils/slugify';

const execFileAsync = promisify(execFile);

const ORIGINAL_HOME = process.env.HOME ?? '/home/surac';

async function initGitRepo(dir: string, email = 'test@test.com', name = 'Test User'): Promise<void> {
  await execFileAsync('git', ['init'], { cwd: dir });
  await execFileAsync('git', ['config', 'user.email', email], { cwd: dir });
  await execFileAsync('git', ['config', 'user.name', name], { cwd: dir });
  await execFileAsync('git', ['commit', '--allow-empty', '-m', 'initial'], { cwd: dir });
  try {
    await execFileAsync('git', ['checkout', '-b', 'main'], { cwd: dir });
  } catch {
    try {
      await execFileAsync('git', ['checkout', 'main'], { cwd: dir });
    } catch {
      // main may already be the current branch
    }
  }
}

async function createFile(dir: string, filePath: string, content: string): Promise<void> {
  const fullPath = path.join(dir, filePath);
  fs.mkdirSync(path.dirname(fullPath), { recursive: true });
  fs.writeFileSync(fullPath, content, 'utf-8');
}

async function gitCommit(dir: string, message: string): Promise<string> {
  await execFileAsync('git', ['add', '.'], { cwd: dir });
  await execFileAsync('git', ['commit', '-m', message], { cwd: dir });
  const { stdout } = await execFileAsync('git', ['rev-parse', 'HEAD'], { cwd: dir });
  return stdout.trim();
}

function mohistWorktreesPath(home: string, projectName: string): string {
  const slug = slugify(projectName);
  return path.join(home, '.mohist', 'projects', slug, 'worktrees');
}

describe('merge-ready regression tests', () => {
  let origHome: string;

  beforeEach(() => {
    origHome = process.env.HOME;
  });

  afterEach(() => {
    process.env.HOME = origHome;
    vi.resetModules();
  });

  function withHome(tmpDir: string, fn: () => Promise<void>): () => Promise<void> {
    return async () => {
      process.env.HOME = tmpDir;
      try {
        await fn();
      } finally {
        process.env.HOME = origHome;
      }
    };
  }

  function createMockIssue(approvalOutput?: unknown): import('../../src/types').Issue {
    return {
      id: 'issue-1',
      number: 1,
      title: 'Test Issue',
      body: '',
      stage: Stage.Check,
      status: IssueStatus.Active,
      projectId: 'test-project',
      labels: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      ...(approvalOutput !== undefined
        ? {
            approvalState: {
              stage: Stage.Check,
              status: 'approved' as const,
              output: approvalOutput,
              requestedAt: new Date().toISOString(),
            },
          }
        : {}),
    } as import('../../src/types').Issue;
  }

  async function setupOpenSpec(tmpDir: string, changeDir: string): Promise<void> {
    const capability = 'test-cap';
    const changeSpecsDir = path.join(changeDir, 'specs', capability);
    fs.mkdirSync(changeSpecsDir, { recursive: true });
    fs.writeFileSync(
      path.join(changeSpecsDir, 'spec.md'),
      '## ADDED Requirements\n\n### Requirement: TestReq\n\nTest content.\n\n#### Scenario: Test\n\nTest scenario.',
      'utf-8'
    );

    const mainSpecsDir = path.join(tmpDir, 'openspec', 'specs', capability);
    fs.mkdirSync(mainSpecsDir, { recursive: true });
    fs.writeFileSync(
      path.join(mainSpecsDir, 'spec.md'),
      '# OpenSpec Capability: test-cap\n\n### Requirement: ExistingReq\n\nExisting content.\n\n#### Scenario: Existing\n\nExisting scenario content.',
      'utf-8'
    );
  }

  function createMockContext(tmpDir: string, issue: import('../../src/types').Issue, wtm: any, projectPath: string, changeDir: string): StageContext {
    const emitSpy = vi.fn();
    const eventBus = new EventBus();
    vi.spyOn(eventBus, 'emit').mockImplementation(emitSpy);

    return {
      issue,
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
      } as any,
      worktreeManager: wtm,
      projectRepo: {
        findById: vi.fn().mockReturnValue({
          id: 'test-project',
          name: 'project',
          baseBranch: 'main',
          path: projectPath,
        }),
      } as any,
      eventBus: eventBus as any,
      stageExecutionRepo: {
        create: vi.fn().mockReturnValue({
          id: 'exec-1',
          issueId: 'issue-1',
          stage: Stage.Integrate,
          status: 'running',
          taskResults: [],
          checkResults: [],
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
        }),
        appendTaskResult: vi.fn(),
        updateStatus: vi.fn(),
        updateCheckResults: vi.fn(),
        findByIssueId: vi.fn().mockReturnValue([
          {
            id: 'exec-1',
            issueId: 'issue-1',
            stage: Stage.Integrate,
            status: 'passed',
            taskResults: [],
            checkResults: [],
            createdAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
          },
        ]),
      } as any,
      checkpointManager: { save: vi.fn(), load: vi.fn(), deleteAll: vi.fn() } as any,
      issueRepo: {
        updateStage: vi.fn(),
        setApprovalState: vi.fn(),
        clearApprovalState: vi.fn(),
        updateStatus: vi.fn(),
      } as any,
      emit: (event: string, data: unknown) => {
        try {
          (eventBus as any)?.emit?.(event, data);
        } catch { /* fire-and-forget */ }
      },
      log: (_eventType: string, _data: object) => { /* fire-and-forget */ },
    } as StageContext;
  }

  async function setupGitProject(tmpDir: string): Promise<{ projectPath: string; worktreeRoot: string; projectName: string }> {
    const projectPath = path.join(tmpDir, 'project');
    fs.mkdirSync(projectPath, { recursive: true });
    await initGitRepo(projectPath);
    await createFile(projectPath, 'src/foo.ts', 'original content\n');
    await gitCommit(projectPath, 'initial commit');
    const projectName = 'project';
    const worktreeRoot = mohistWorktreesPath(tmpDir, projectName);
    fs.mkdirSync(worktreeRoot, { recursive: true });
    return { projectPath, worktreeRoot, projectName };
  }

  async function createIssueWorktree(
    tmpDir: string,
    projectPath: string,
    projectName: string,
    issueNumber: number,
    fileContent: string
  ): Promise<{ worktreePath: string; candidateHead: string }> {
    const branch = `mo/issue-${issueNumber}`;
    const worktreeRoot = mohistWorktreesPath(tmpDir, projectName);
    const worktreePath = path.join(worktreeRoot, `issue-${issueNumber}`);
    await execFileAsync('git', ['worktree', 'add', '-b', branch, worktreePath, 'main'], { cwd: projectPath });
    await createFile(worktreePath, 'src/foo.ts', fileContent);
    const candidateHead = await gitCommit(worktreePath, `issue-${issueNumber} commit`);
    return { worktreePath, candidateHead };
  }

  async function makeConflictingCommit(projectPath: string, fileContent: string): Promise<string> {
    await createFile(projectPath, 'src/foo.ts', fileContent);
    return gitCommit(projectPath, 'main conflicting commit');
  }

  async function getGitSha(repoPath: string, ref: string): Promise<string> {
    const { stdout } = await execFileAsync('git', ['rev-parse', ref], { cwd: repoPath });
    return stdout.trim();
  }

  async function getMergeBase(repoPath: string, branch1: string, branch2: string): Promise<string> {
    const { stdout } = await execFileAsync('git', ['merge-base', branch1, branch2], { cwd: repoPath });
    return stdout.trim();
  }

  describe('T-007 AC-1: Git regression — merge-ready fails when squash merge conflicts but worktree conflictingFiles is empty', () => {
    it('checkSquashMergeability returns canMerge=false and reports conflict files when squash merge conflicts',
      withHome('', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ac1-test-'));
        process.env.HOME = tmpDir;

        try {
          const { projectPath, worktreeRoot, projectName } = await setupGitProject(tmpDir);
          await createIssueWorktree(tmpDir, projectPath, projectName, 1, 'issue-1 content\n');
          await makeConflictingCommit(projectPath, 'conflicting content on main\n');

          const { WorktreeManager } = await import('../../src/git/worktree-manager');
          const wtm = new WorktreeManager();

          const snapshot = await wtm.checkSquashMergeability(projectPath, projectName, 1, 'main');

          expect(snapshot.kind).toBe('merge-ready');
          expect(snapshot.strategy).toBe('squash');
          expect(snapshot.targetBranch).toBe('main');
          expect(snapshot.canMerge).toBe(false);
          expect(snapshot.conflictFiles.length).toBeGreaterThan(0);
          expect(snapshot.conflictFiles).toContain('src/foo.ts');
          expect(snapshot.error).toBeDefined();
        } finally {
          process.env.HOME = origHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      })
    );

    it('MergeReadyCheck reports fail when checkSquashMergeability returns canMerge=false',
      withHome('', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ac1b-test-'));
        process.env.HOME = tmpDir;

        try {
          const { projectPath, worktreeRoot, projectName } = await setupGitProject(tmpDir);
          await createIssueWorktree(tmpDir, projectPath, projectName, 1, 'issue-1 content\n');
          await makeConflictingCommit(projectPath, 'conflicting content on main\n');

          const mockProjectRepo = {
            findById: vi.fn().mockReturnValue({
              id: 'proj-1',
              name: 'project',
              path: projectPath,
              baseBranch: 'main',
            }),
          };

          const { MergeReadyCheck } = await import('../../src/workflow/checks/merge-ready-check');
          const { WorktreeManager } = await import('../../src/git/worktree-manager');
          const wtm = new WorktreeManager();

          const check = new MergeReadyCheck();
          const result = await check.run({
            issue: {
              id: 'issue-1',
              number: 1,
              title: 'Test Issue',
              stage: Stage.Check,
              status: IssueStatus.Active,
              projectId: 'proj-1',
              labels: [],
              priority: 'p2',
              createdAt: new Date().toISOString(),
              updatedAt: new Date().toISOString(),
            },
            projectId: 'proj-1',
            worktreeManager: wtm,
            projectRepo: mockProjectRepo as any,
            eventBus: new EventBus() as any,
            changeDir: '',
            acpOptions: {} as any,
          } as any);

          expect(result.status).toBe('fail');
          expect(result.output).toMatchObject({
            kind: 'merge-ready',
            canMerge: false,
          });
          expect((result.output as any).conflictFiles).toContain('src/foo.ts');
        } finally {
          process.env.HOME = origHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      })
    );
  });

  describe('T-007 AC-2: Clean squash-mergeable candidate test — snapshot includes required mergeability facts', () => {
    it('checkSquashMergeability returns canMerge=true with all required fields for clean candidate',
      withHome('', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ac2-test-'));
        process.env.HOME = tmpDir;

        try {
          const { projectPath, worktreeRoot, projectName } = await setupGitProject(tmpDir);
          await createIssueWorktree(tmpDir, projectPath, projectName, 1, 'issue-1 content\n');

          const { WorktreeManager } = await import('../../src/git/worktree-manager');
          const wtm = new WorktreeManager();

          const snapshot = await wtm.checkSquashMergeability(projectPath, projectName, 1, 'main');

          expect(snapshot.kind).toBe('merge-ready');
          expect(snapshot.strategy).toBe('squash');
          expect(snapshot.targetBranch).toBe('main');
          expect(snapshot.baseSha).toBeTruthy();
          expect(snapshot.candidateHeadSha).toBeTruthy();
          expect(snapshot.mergeBaseSha).toBeTruthy();
          expect(snapshot.canMerge).toBe(true);
          expect(snapshot.conflictFiles).toEqual([]);
          expect(snapshot.checkedAt).toBeTruthy();
        } finally {
          process.env.HOME = origHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      })
    );

    it('MergeReadyCheck reports pass with structured output for clean candidate',
      withHome('', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ac2b-test-'));
        process.env.HOME = tmpDir;

        try {
          const { projectPath, worktreeRoot, projectName } = await setupGitProject(tmpDir);
          await createIssueWorktree(tmpDir, projectPath, projectName, 1, 'issue-1 content\n');

          const mockProjectRepo = {
            findById: vi.fn().mockReturnValue({
              id: 'proj-1',
              name: 'project',
              path: projectPath,
              baseBranch: 'main',
            }),
          };

          const { MergeReadyCheck } = await import('../../src/workflow/checks/merge-ready-check');
          const { WorktreeManager } = await import('../../src/git/worktree-manager');
          const wtm = new WorktreeManager();

          const check = new MergeReadyCheck();
          const result = await check.run({
            issue: {
              id: 'issue-1',
              number: 1,
              title: 'Test Issue',
              stage: Stage.Check,
              status: IssueStatus.Active,
              projectId: 'proj-1',
              labels: [],
              priority: 'p2',
              createdAt: new Date().toISOString(),
              updatedAt: new Date().toISOString(),
            },
            projectId: 'proj-1',
            worktreeManager: wtm,
            projectRepo: mockProjectRepo as any,
            eventBus: new EventBus() as any,
            changeDir: '',
            acpOptions: {} as any,
          } as any);

          expect(result.status).toBe('pass');
          expect(result.output).toMatchObject({
            kind: 'merge-ready',
            strategy: 'squash',
            targetBranch: 'main',
            canMerge: true,
            conflictFiles: [],
          });
          expect((result.output as any).baseSha).toBeTruthy();
          expect((result.output as any).candidateHeadSha).toBeTruthy();
          expect((result.output as any).mergeBaseSha).toBeTruthy();
          expect((result.output as any).checkedAt).toBeTruthy();
        } finally {
          process.env.HOME = origHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      })
    );
  });

  describe('T-007 AC-4: Integrate tests — missing or stale mergeability evidence stops before side-effectful spec sync/archive', () => {
    async function setupOpenSpec(tmpDir: string, changeDir: string): Promise<void> {
      const capability = 'test-cap';
      const changeSpecsDir = path.join(changeDir, 'specs', capability);
      fs.mkdirSync(changeSpecsDir, { recursive: true });
      fs.writeFileSync(
        path.join(changeSpecsDir, 'spec.md'),
        '## ADDED Requirements\n\n### Requirement: TestReq\n\nTest content.\n\n#### Scenario: Test\n\nTest scenario.',
        'utf-8'
      );

      const mainSpecsDir = path.join(tmpDir, 'openspec', 'specs', capability);
      fs.mkdirSync(mainSpecsDir, { recursive: true });
      fs.writeFileSync(
        path.join(mainSpecsDir, 'spec.md'),
        '# OpenSpec Capability: test-cap\n\n### Requirement: ExistingReq\n\nExisting content.\n\n#### Scenario: Existing\n\nExisting scenario content.',
        'utf-8'
      );
    }

    it('Integrate stops before spec-sync when approved mergeReadySnapshot is missing and preflight fails',
      withHome('', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ac4a-test-'));
        process.env.HOME = tmpDir;

        try {
          const { projectPath, projectName } = await setupGitProject(tmpDir);
          await createIssueWorktree(tmpDir, projectPath, projectName, 1, 'issue-1 content\n');
          await makeConflictingCommit(projectPath, 'conflicting content on main\n');

          const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-test');
          await setupOpenSpec(tmpDir, changeDir);

          const { WorktreeManager } = await import('../../src/git/worktree-manager');
          const wtm = new WorktreeManager();

          const issue = createMockIssue({ mergeReadySnapshot: null });

          const ctx = createMockContext(tmpDir, issue, wtm, projectPath, changeDir);

          const { IntegrateStageRunner } = await import('../../src/workflow/integrate-stage-runner');
          const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
          const result = await runner.run(ctx);

          expect(result.success).toBe(false);
          const output = result.output as { steps?: Array<{ step: string; status: string }> };
          const preflightStep = output.steps?.find(s => s.step === 'integrate:preflight');
          expect(preflightStep).toBeDefined();
          expect(preflightStep?.status).toBe('failed');

          const specSyncStep = output.steps?.find(s => s.step === 'integrate:spec-sync');
          expect(specSyncStep).toBeUndefined();
        } finally {
          process.env.HOME = origHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      })
    );

    it('Integrate stops before archive when approved mergeReadySnapshot is stale',
      withHome('', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ac4b-test-'));
        process.env.HOME = tmpDir;

        try {
          const { projectPath, projectName } = await setupGitProject(tmpDir);
          await createIssueWorktree(tmpDir, projectPath, projectName, 1, 'issue-1 content\n');
          await makeConflictingCommit(projectPath, 'conflicting content on main\n');

          const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-test');
          await setupOpenSpec(tmpDir, changeDir);

          const { WorktreeManager } = await import('../../src/git/worktree-manager');
          const wtm = new WorktreeManager();

          const staleSnapshot = {
            kind: 'merge-ready' as const,
            strategy: 'squash' as const,
            targetBranch: 'main',
            baseSha: '0000000000000000000000000000000000000000',
            candidateHeadSha: '1111111111111111111111111111111111111111',
            mergeBaseSha: '',
            canMerge: true,
            conflictFiles: [] as string[],
            checkedAt: new Date().toISOString(),
          };

          const issue = createMockIssue({ mergeReadySnapshot: staleSnapshot });

          const ctx = createMockContext(tmpDir, issue, wtm, projectPath, changeDir);

          const { IntegrateStageRunner } = await import('../../src/workflow/integrate-stage-runner');
          const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
          const result = await runner.run(ctx);

          expect(result.success).toBe(false);
          const output = result.output as { steps?: Array<{ step: string; status: string }> };
          const preflightStep = output.steps?.find(s => s.step === 'integrate:preflight');
          expect(preflightStep).toBeDefined();
          expect(preflightStep?.status).toBe('failed');

          const archiveStep = output.steps?.find(s => s.step === 'integrate:archive-change');
          expect(archiveStep).toBeUndefined();
        } finally {
          process.env.HOME = origHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      })
    );

    it('Integrate stops before spec-sync when approved snapshot is stale even if refreshed preflight can merge',
      withHome('', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ac4-stale-clean-test-'));
        process.env.HOME = tmpDir;

        try {
          const { projectPath, projectName } = await setupGitProject(tmpDir);
          const { worktreePath } = await createIssueWorktree(tmpDir, projectPath, projectName, 1, 'issue-1 content\n');

          const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-test');
          await setupOpenSpec(tmpDir, changeDir);

          const { WorktreeManager } = await import('../../src/git/worktree-manager');
          const wtm = new WorktreeManager();

          const currentBaseSha = await getGitSha(projectPath, 'main');
          const candidateHeadSha = await getGitSha(worktreePath, 'HEAD');
          const mergeBaseSha = await getMergeBase(projectPath, 'main', 'mo/issue-1');
          const staleSnapshot = {
            kind: 'merge-ready' as const,
            strategy: 'squash' as const,
            targetBranch: 'main',
            baseSha: '0000000000000000000000000000000000000000',
            candidateHeadSha,
            mergeBaseSha,
            canMerge: true,
            conflictFiles: [] as string[],
            checkedAt: new Date().toISOString(),
          };
          expect(staleSnapshot.baseSha).not.toBe(currentBaseSha);
          const refreshed = await wtm.checkSquashMergeability(projectPath, projectName, 1, 'main');
          expect(refreshed.canMerge).toBe(true);

          const issue = createMockIssue({ mergeReadySnapshot: staleSnapshot });
          const ctx = createMockContext(tmpDir, issue, wtm, projectPath, changeDir);

          const { IntegrateStageRunner } = await import('../../src/workflow/integrate-stage-runner');
          const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
          const result = await runner.run(ctx);

          expect(result.success).toBe(false);
          const output = result.output as { steps?: Array<{ step: string; status: string; output?: any }> };
          const preflightStep = output.steps?.find(s => s.step === 'integrate:preflight');
          expect(preflightStep?.status).toBe('failed');
          expect(preflightStep?.output?.refreshed).toBe(true);
          expect(preflightStep?.output?.canMerge).toBe(true);
          expect(preflightStep?.output?.error).toContain('Approved merge-ready snapshot is stale');
          expect(output.steps?.find(s => s.step === 'integrate:spec-sync')).toBeUndefined();
        } finally {
          process.env.HOME = origHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      })
    );

    it('Integrate validates approved snapshot against the configured base branch ref, not repository HEAD',
      withHome('', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ac4-base-ref-test-'));
        process.env.HOME = tmpDir;

        try {
          const { projectPath, projectName } = await setupGitProject(tmpDir);
          const { worktreePath } = await createIssueWorktree(tmpDir, projectPath, projectName, 1, 'issue-1 content\n');
          await execFileAsync('git', ['checkout', '-b', 'side'], { cwd: projectPath });
          await createFile(projectPath, 'side.txt', 'side branch only\n');
          await gitCommit(projectPath, 'side branch commit');

          const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-test');
          await setupOpenSpec(tmpDir, changeDir);

          const { WorktreeManager } = await import('../../src/git/worktree-manager');
          const wtm = new WorktreeManager();

          const baseSha = await getGitSha(projectPath, 'main');
          const repoHeadSha = await getGitSha(projectPath, 'HEAD');
          const candidateHeadSha = await getGitSha(worktreePath, 'HEAD');
          const mergeBaseSha = await getMergeBase(projectPath, 'main', 'mo/issue-1');
          expect(repoHeadSha).not.toBe(baseSha);

          const validSnapshot = {
            kind: 'merge-ready' as const,
            strategy: 'squash' as const,
            targetBranch: 'main',
            baseSha,
            candidateHeadSha,
            mergeBaseSha,
            canMerge: true,
            conflictFiles: [] as string[],
            checkedAt: new Date().toISOString(),
          };
          const issue = createMockIssue({ mergeReadySnapshot: validSnapshot });
          const ctx = createMockContext(tmpDir, issue, wtm, projectPath, changeDir);

          const { IntegrateStageRunner } = await import('../../src/workflow/integrate-stage-runner');
          const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
          const result = await runner.run(ctx);

          const output = result.output as { steps?: Array<{ step: string; status: string; output?: any }> };
          const preflightStep = output.steps?.find(s => s.step === 'integrate:preflight');
          expect(preflightStep?.status).toBe('completed');
          expect(preflightStep?.output?.baseSha).toBe(baseSha);
          expect(output.steps?.find(s => s.step === 'integrate:spec-sync')?.status).toBe('completed');
        } finally {
          process.env.HOME = origHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      })
    );

    it('Integrate proceeds to spec-sync when approved mergeReadySnapshot is still valid',
      withHome('', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ac4c-test-'));
        process.env.HOME = tmpDir;

        try {
          const { projectPath, projectName } = await setupGitProject(tmpDir);
          const { worktreePath } = await createIssueWorktree(tmpDir, projectPath, projectName, 1, 'issue-1 content\n');

          const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-test');
          await setupOpenSpec(tmpDir, changeDir);

          const { WorktreeManager } = await import('../../src/git/worktree-manager');
          const wtm = new WorktreeManager();

          const baseSha = await getGitSha(projectPath, 'main');
          const candidateHeadSha = await getGitSha(worktreePath, 'HEAD');
          const mergeBaseSha = await getMergeBase(projectPath, 'main', 'mo/issue-1');

          const validSnapshot = {
            kind: 'merge-ready' as const,
            strategy: 'squash' as const,
            targetBranch: 'main',
            baseSha,
            candidateHeadSha,
            mergeBaseSha,
            canMerge: true,
            conflictFiles: [] as string[],
            checkedAt: new Date().toISOString(),
          };

          const issue = createMockIssue({ mergeReadySnapshot: validSnapshot });

          const ctx = createMockContext(tmpDir, issue, wtm, projectPath, changeDir);

          const { IntegrateStageRunner } = await import('../../src/workflow/integrate-stage-runner');
          const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
          const result = await runner.run(ctx);

          const output = result.output as { steps?: Array<{ step: string; status: string }> };
          const preflightStep = output.steps?.find(s => s.step === 'integrate:preflight');
          expect(preflightStep?.status).toBe('completed');

          const specSyncStep = output.steps?.find(s => s.step === 'integrate:spec-sync');
          expect(specSyncStep).toBeDefined();
          expect(specSyncStep?.status).toBe('completed');

          const archiveStep = output.steps?.find(s => s.step === 'integrate:archive-change');
          expect(archiveStep).toBeDefined();
          expect(archiveStep?.status).toBe('completed');

          const mergeStep = output.steps?.find(s => s.step === 'integrate:merge');
          expect(mergeStep).toBeDefined();
          expect(mergeStep?.status).toBe('completed');
        } finally {
          process.env.HOME = origHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      })
    );
  });

  describe('T-007 AC-5: Final merge race coverage — structured conflict files reported when authoritative merge fails after preflight', () => {
    it('mergeApprovedCandidate reports structured conflict files when final merge fails after preflight passes',
      withHome('', async () => {
        const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-ac5-test-'));
        process.env.HOME = tmpDir;

        try {
          const { projectPath, projectName } = await setupGitProject(tmpDir);
          const { worktreePath } = await createIssueWorktree(tmpDir, projectPath, projectName, 1, 'issue-1 content\n');

          const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-test');
          await setupOpenSpec(tmpDir, changeDir);

          const { WorktreeManager } = await import('../../src/git/worktree-manager');
          const wtm = new WorktreeManager();

          const baseShaBeforeConflict = await getGitSha(projectPath, 'main');
          const candidateHeadSha = await getGitSha(worktreePath, 'HEAD');
          const mergeBaseSha = await getMergeBase(projectPath, 'main', 'mo/issue-1');

          const issue = {
            id: 'issue-1',
            number: 1,
            title: 'Test Issue',
            body: '',
            stage: Stage.Integrate,
            status: IssueStatus.Active,
            projectId: 'test-project',
            labels: [],
            createdAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
            approvalState: {
              stage: Stage.Check,
              status: 'approved' as const,
              output: {
                mergeReadySnapshot: {
                  kind: 'merge-ready' as const,
                  strategy: 'squash' as const,
                  targetBranch: 'main',
                  baseSha: baseShaBeforeConflict,
                  candidateHeadSha,
                  mergeBaseSha,
                  canMerge: true,
                  conflictFiles: [] as string[],
                  checkedAt: new Date().toISOString(),
                },
              },
              requestedAt: new Date().toISOString(),
            },
          };

          const ctx = {
            issue,
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
            } as any,
            worktreeManager: wtm,
            projectRepo: {
              findById: vi.fn().mockReturnValue({
                id: 'test-project',
                name: 'project',
                baseBranch: 'main',
                path: projectPath,
              }),
            } as any,
            eventBus: new EventBus() as any,
            stageExecutionRepo: {
              create: vi.fn().mockReturnValue({
                id: 'exec-1',
                issueId: 'issue-1',
                stage: Stage.Integrate,
                status: 'running',
                taskResults: [],
                checkResults: [],
                createdAt: new Date().toISOString(),
                updatedAt: new Date().toISOString(),
              }),
              appendTaskResult: vi.fn(),
              updateStatus: vi.fn(),
              updateCheckResults: vi.fn(),
              findByIssueId: vi.fn().mockReturnValue([
                {
                  id: 'exec-1',
                  issueId: 'issue-1',
                  stage: Stage.Integrate,
                  status: 'passed',
                  taskResults: [],
                  checkResults: [],
                  createdAt: new Date().toISOString(),
                  updatedAt: new Date().toISOString(),
                },
              ]),
            } as any,
            checkpointManager: { save: vi.fn(), load: vi.fn(), deleteAll: vi.fn() } as any,
            issueRepo: {
              updateStage: vi.fn(),
              setApprovalState: vi.fn(),
              clearApprovalState: vi.fn(),
              updateStatus: vi.fn(),
            } as any,
            emit: (event: string, data: unknown) => {
              try {
                (new EventBus() as any)?.emit?.(event, data);
              } catch { /* fire-and-forget */ }
            },
            log: (_eventType: string, _data: object) => { /* fire-and-forget */ },
          } as StageContext;

          const mergeApprovedCandidate = vi.spyOn(wtm, 'mergeApprovedCandidate').mockImplementation(async (...args) => {
            await makeConflictingCommit(projectPath, 'conflicting content on main\n');
            mergeApprovedCandidate.mockRestore();
            return wtm.mergeApprovedCandidate(...args);
          });

          const preflightSnapshot = await wtm.checkSquashMergeability(projectPath, projectName, 1, 'main');
          expect(preflightSnapshot.canMerge).toBe(true);
          expect(preflightSnapshot.conflictFiles).toEqual([]);

          const { IntegrateStageRunner } = await import('../../src/workflow/integrate-stage-runner');
          const runner = new IntegrateStageRunner({ worktreePath: tmpDir });
          let mergeOutput: {
            targetBranch?: string;
            strategy?: string;
            baseSha?: string;
            candidateHeadSha?: string;
            mergeBaseSha?: string;
            conflictFiles?: string[];
            error?: string;
          };

          try {
            await runner.executeTaskWork(ctx, 'integrate:merge');
            throw new Error('Expected integrate:merge to fail after post-preflight race');
          } catch (error) {
            const mergeStep = (error as { mergeStep?: { output?: unknown } }).mergeStep;
            expect(mergeStep).toBeDefined();
            mergeOutput = (mergeStep?.output ?? {}) as typeof mergeOutput;
          }

          expect(mergeOutput?.targetBranch).toBe('main');
          expect(mergeOutput?.strategy).toBe('squash');
          expect(mergeOutput?.baseSha).toBeTruthy();
          expect(mergeOutput?.candidateHeadSha).toBe(candidateHeadSha);
          expect(mergeOutput?.mergeBaseSha).toBe(mergeBaseSha);
          expect(mergeOutput?.conflictFiles).toContain('src/foo.ts');
          expect(mergeOutput?.error).toContain('Squash merge failed');
        } finally {
          process.env.HOME = origHome;
          fs.rmSync(tmpDir, { recursive: true, force: true });
        }
      })
    );
  });
});
