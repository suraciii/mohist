import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { AgentRunnerService } from '../src/services/agent-runner-service';
import { EventBus } from '../src/services/event-bus';
import { IssueService } from '../src/services/issue-service';
import { Stage, IssueStatus } from '../src/types';

function makeTasksJson(tasks: Array<{ id: string; passes: boolean }>) {
  return JSON.stringify({ version: 1, tasks });
}

function createChangeDirWithTasks(
  worktreeDir: string,
  issueNumber: number,
  tasksContent: string,
): string {
  const changeDir = path.join(
    worktreeDir,
    'openspec',
    'changes',
    `${issueNumber}-test-change`,
  );
  fs.mkdirSync(changeDir, { recursive: true });
  fs.writeFileSync(path.join(changeDir, 'tasks.json'), tasksContent, 'utf-8');
  return changeDir;
}

function createWorktreeMock(worktreePath: string | null) {
  return {
    getPath: () => worktreePath,
  } as any;
}

describe('recoverIssues — orphan-recovery scenarios', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let issueService: IssueService;
  let eventBus: EventBus;
  let tmpDir: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    issueService = new IssueService(issueRepo);
    eventBus = new EventBus();
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'recover-test-'));
  });

  afterEach(() => {
    db.close();
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it('build-stage all-pass: auto-advance to review', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'All Pass' });

    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    const tasksJson = makeTasksJson([
      { id: 'T-001', passes: true },
      { id: 'T-002', passes: true },
      { id: 'T-003', passes: true },
    ]);
    createChangeDirWithTasks(tmpDir, issue.number, tasksJson);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    vi.spyOn(service, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.stage).toBe(Stage.Check);
  });

  it('build-stage partial: auto-retry triggered', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'Partial' });

    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    const tasksJson = makeTasksJson([
      { id: 'T-001', passes: true },
      { id: 'T-002', passes: true },
      { id: 'T-003', passes: false },
    ]);
    createChangeDirWithTasks(tmpDir, issue.number, tasksJson);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    vi.spyOn(service, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.stage).toBe(Stage.Build);
    expect(recovered?.retryCount).toBe(1);
    expect(recovered?.blockedReason).toContain('2/3');
  });

  it('build-stage no tasks.json: blocked', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'No Tasks' });

    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    const changeDir = path.join(
      tmpDir,
      'openspec',
      'changes',
      `${issue.number}-test-change`,
    );
    fs.mkdirSync(changeDir, { recursive: true });

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.stage).toBe(Stage.Build);
    expect(recovered?.blockedReason).toContain('tasks.json');
  });

  it('build-stage no change directory: blocked', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'No ChangeDir' });

    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.blockedReason).toContain('变更目录');
  });

  it('build-stage invalid JSON: blocked', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'Bad JSON' });

    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    createChangeDirWithTasks(tmpDir, issue.number, '{ not valid json }');

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.stage).toBe(Stage.Build);
    expect(recovered?.blockedReason).toContain('格式损坏');
  });

  it('build-stage tasks.json missing tasks array: blocked', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'No Array' });

    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    createChangeDirWithTasks(tmpDir, issue.number, JSON.stringify({ version: 1 }));

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.blockedReason).toContain('tasks 数组');
  });

  it('plan-stage orphan: interrupted (non-build stage)', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'Plan Orphan' });

    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Plan);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Interrupted);
    expect(recovered?.stage).toBe(Stage.Plan);
    expect(recovered?.approvalState).toBeUndefined();
  });

  it('awaiting approval: gate restored, status active', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'Awaiting' });

    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Plan);
    issueRepo.setApprovalState(issue.id, {
      stage: Stage.Plan,
      status: 'awaiting',
      requestedAt: new Date().toISOString(),
    });

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Active);
    expect(recovered?.stage).toBe(Stage.Plan);
    expect(recovered?.approvalState?.status).toBe('awaiting');
    expect(service.isIssueAtApprovalGate(issue.id)).toBe(true);
  });

  it('no ProjectRepo/WorktreeManager: build-stage falls back to interrupted', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'Fallback' });

    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Interrupted);
    expect(recovered?.stage).toBe(Stage.Build);
  });

  it('no worktree found: build-stage blocked', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'No Worktree' });

    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(null),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.blockedReason).toContain('Worktree');
  });

  it('mixed orphans: awaiting preserved, build all-pass advanced, plan interrupted', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });

    const awaitingIssue = issueService.create({ projectId: project.id, title: 'Awaiting' });
    issueRepo.updateStatus(awaitingIssue.id, IssueStatus.Active);
    issueRepo.updateStage(awaitingIssue.id, Stage.Plan);
    issueRepo.setApprovalState(awaitingIssue.id, {
      stage: Stage.Plan,
      status: 'awaiting',
      requestedAt: new Date().toISOString(),
    });

    const buildIssue = issueService.create({ projectId: project.id, title: 'Build All Pass' });
    issueRepo.updateStatus(buildIssue.id, IssueStatus.Active);
    issueRepo.updateStage(buildIssue.id, Stage.Build);
    const tasksJson = makeTasksJson([
      { id: 'T-001', passes: true },
      { id: 'T-002', passes: true },
    ]);
    createChangeDirWithTasks(tmpDir, buildIssue.number, tasksJson);

    const planIssue = issueService.create({ projectId: project.id, title: 'Plan Orphan' });
    issueRepo.updateStatus(planIssue.id, IssueStatus.Active);
    issueRepo.updateStage(planIssue.id, Stage.Plan);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    vi.spyOn(service, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

    service.recoverIssues();

    const rAwaiting = issueRepo.findById(awaitingIssue.id);
    expect(rAwaiting?.status).toBe(IssueStatus.Active);
    expect(service.isIssueAtApprovalGate(awaitingIssue.id)).toBe(true);

    const rBuild = issueRepo.findById(buildIssue.id);
    expect(rBuild?.stage).toBe(Stage.Check);

    const rPlan = issueRepo.findById(planIssue.id);
    expect(rPlan?.status).toBe(IssueStatus.Interrupted);
    expect(rPlan?.stage).toBe(Stage.Plan);
  });

  it('build-stage empty tasks array: all-pass edge case advances to review', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'Empty Tasks' });

    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    createChangeDirWithTasks(tmpDir, issue.number, makeTasksJson([]));

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    vi.spyOn(service, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.stage).toBe(Stage.Check);
  });
});

describe('recoverIssues — migration v16 + blockedReason + retryCount', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let issueService: IssueService;
  let eventBus: EventBus;
  let tmpDir: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    issueService = new IssueService(issueRepo);
    eventBus = new EventBus();
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'recover-blocked-test-'));
  });

  afterEach(() => {
    db.close();
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it('migration v16: blocked_reason and retry_count columns exist', () => {
    const tableInfo = db.all<{ name: string }>('PRAGMA table_info(issues)');
    const colNames = tableInfo.map(c => c.name);
    expect(colNames).toContain('blocked_reason');
    expect(colNames).toContain('retry_count');
  });

  it('migration v16: default values are null/0', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'Defaults' });

    const row = db.get<{ blocked_reason: string | null; retry_count: number | null }>(
      'SELECT blocked_reason, retry_count FROM issues WHERE id = ?',
      [issue.id],
    );
    expect(row?.blocked_reason).toBeNull();
    expect(row?.retry_count).toBe(0);
  });

  it('migration v16: idempotent — duplicate ADD COLUMN does not fail when guarded', () => {
    const tableInfo = db.all<{ name: string }>('PRAGMA table_info(issues)');
    const hasBlockedReason = tableInfo.some(col => col.name === 'blocked_reason');
    const hasRetryCount = tableInfo.some(col => col.name === 'retry_count');
    expect(hasBlockedReason).toBe(true);
    expect(hasRetryCount).toBe(true);

    const guardedAlter = () => {
      const info = db.all<{ name: string }>('PRAGMA table_info(issues)');
      if (!info.some(col => col.name === 'blocked_reason')) {
        db.exec('ALTER TABLE issues ADD COLUMN blocked_reason TEXT DEFAULT NULL');
      }
    };

    expect(guardedAlter).not.toThrow();
    expect(guardedAlter).not.toThrow();

    const tableInfo2 = db.all<{ name: string }>('PRAGMA table_info(issues)');
    const blockedReasonCols = tableInfo2.filter(c => c.name === 'blocked_reason');
    expect(blockedReasonCols).toHaveLength(1);
  });

  it('blockIssue sets both status and blocked_reason atomically', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'BlockTest' });

    const result = issueRepo.blockIssue(issue.id, 'Test reason — something broke');
    expect(result?.status).toBe(IssueStatus.Blocked);
    expect(result?.blockedReason).toBe('Test reason — something broke');

    const found = issueRepo.findById(issue.id);
    expect(found?.status).toBe(IssueStatus.Blocked);
    expect(found?.blockedReason).toBe('Test reason — something broke');
  });

  it('updateBlockedReason can set and clear reason', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'ReasonTest' });

    issueRepo.blockIssue(issue.id, 'Initial reason');
    let found = issueRepo.findById(issue.id);
    expect(found?.blockedReason).toBe('Initial reason');

    issueRepo.updateBlockedReason(issue.id, 'Updated reason');
    found = issueRepo.findById(issue.id);
    expect(found?.blockedReason).toBe('Updated reason');

    issueRepo.updateBlockedReason(issue.id, null);
    found = issueRepo.findById(issue.id);
    expect(found?.blockedReason).toBeUndefined();
  });

  it('updateRetryCount persists and retrieves count', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'RetryTest' });

    expect(issueRepo.findById(issue.id)?.retryCount).toBe(0);

    issueRepo.updateRetryCount(issue.id, 1);
    expect(issueRepo.findById(issue.id)?.retryCount).toBe(1);

    issueRepo.updateRetryCount(issue.id, 3);
    expect(issueRepo.findById(issue.id)?.retryCount).toBe(3);

    issueRepo.updateRetryCount(issue.id, 0);
    expect(issueRepo.findById(issue.id)?.retryCount).toBe(0);
  });
});

describe('recoverIssues — auto-retry and blockedReason scenarios', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let issueService: IssueService;
  let eventBus: EventBus;
  let tmpDir: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    issueService = new IssueService(issueRepo);
    eventBus = new EventBus();
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'recover-retry-test-'));
  });

  afterEach(() => {
    db.close();
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  function setupPartialTasksIssue(
    retryCount: number = 0,
    passed: number = 2,
    total: number = 8,
  ) {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'AutoRetry' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);
    if (retryCount > 0) {
      issueRepo.updateRetryCount(issue.id, retryCount);
    }

    const tasks = [];
    for (let i = 0; i < passed; i++) tasks.push({ id: `T-${String(i + 1).padStart(3, '0')}`, passes: true });
    for (let i = passed; i < total; i++) tasks.push({ id: `T-${String(i + 1).padStart(3, '0')}`, passes: false });
    createChangeDirWithTasks(tmpDir, issue.number, makeTasksJson(tasks));

    return { project, issue };
  }

  it('first auto-retry (retryCount 0→1): status stays active, retryCount=1, blockedReason set', () => {
    const { project, issue } = setupPartialTasksIssue(0, 2, 8);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );
    vi.spyOn(service, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Active);
    expect(recovered?.stage).toBe(Stage.Build);
    expect(recovered?.retryCount).toBe(1);
    expect(recovered?.blockedReason).toContain('2/8');
    expect(recovered?.blockedReason).toContain('第 1/3 次');
  });

  it('second auto-retry (retryCount 1→2): increments correctly', () => {
    const { project, issue } = setupPartialTasksIssue(1, 3, 8);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );
    vi.spyOn(service, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Active);
    expect(recovered?.retryCount).toBe(2);
    expect(recovered?.blockedReason).toContain('3/8');
    expect(recovered?.blockedReason).toContain('第 2/3 次');
  });

  it('third auto-retry (retryCount 2→3): still active, final attempt', () => {
    const { project, issue } = setupPartialTasksIssue(2, 1, 5);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );
    vi.spyOn(service, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Active);
    expect(recovered?.retryCount).toBe(3);
    expect(recovered?.blockedReason).toContain('1/5');
    expect(recovered?.blockedReason).toContain('第 3/3 次');
  });

  it('retryCount=3 (max reached): marks blocked with human-readable reason', () => {
    const { project, issue } = setupPartialTasksIssue(3, 2, 8);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    const blockedEvents: any[] = [];
    eventBus.on('agent_blocked', (e) => blockedEvents.push(e));

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.stage).toBe(Stage.Build);
    expect(recovered?.retryCount).toBe(4);
    expect(recovered?.blockedReason).toContain('2/8');
    expect(recovered?.blockedReason).toContain('已自动重试 3 次仍失败');
    expect(recovered?.blockedReason).toContain('人工介入');
  });

  it('retryCount>3: also marks blocked', () => {
    const { project, issue } = setupPartialTasksIssue(5, 1, 4);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.retryCount).toBe(6);
  });

  it('auto-retry pipeline start failure: marks blocked immediately', () => {
    const { project, issue } = setupPartialTasksIssue(0, 2, 8);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );
    vi.spyOn(service, 'enqueue').mockImplementation(() => {
      throw new Error('Concurrent agent limit reached (8)');
    });

    const blockedEvents: any[] = [];
    eventBus.on('agent_blocked', (e) => blockedEvents.push(e));

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.blockedReason).toContain('自动重试启动失败');
    expect(recovered?.blockedReason).toContain('2/8');
    expect(blockedEvents).toHaveLength(1);
    expect(blockedEvents[0].issueNumber).toBe(issue.number);
    expect(blockedEvents[0].blockedReason).toContain('自动重试启动失败');
  });

  it('non-retryable failure (project deleted after issue created): blocked with reason', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'Orphan' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    const mockProjectRepo = {
      findById: vi.fn().mockReturnValue(null),
    } as any;

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      mockProjectRepo,
      createWorktreeMock(tmpDir),
    );

    const blockedEvents: any[] = [];
    eventBus.on('agent_blocked', (e) => blockedEvents.push(e));

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.blockedReason).toContain('Project 已不存在');
    expect(recovered?.retryCount).toBe(0);
    expect(blockedEvents).toHaveLength(1);
    expect(blockedEvents[0].retryCount).toBe(0);
  });

  it('non-retryable failure (no worktree): blocked with reason', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'NoWT' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(null),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.blockedReason).toContain('Worktree 已不存在');
  });

  it('non-retryable failure (no tasks.json): blocked with reason', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'NoTasksFile' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issue.number}-test-change`);
    fs.mkdirSync(changeDir, { recursive: true });

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.blockedReason).toContain('tasks.json 不存在');
  });

  it('non-retryable failure (invalid JSON): blocked with reason', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'BadJson' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    createChangeDirWithTasks(tmpDir, issue.number, 'not json at all');

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.blockedReason).toContain('tasks.json 格式损坏');
  });

  it('all-pass clears retryCount and blockedReason', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'AllPassReset' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);
    issueRepo.updateRetryCount(issue.id, 2);
    issueRepo.updateBlockedReason(issue.id, 'Old reason');

    createChangeDirWithTasks(tmpDir, issue.number, makeTasksJson([
      { id: 'T-001', passes: true },
      { id: 'T-002', passes: true },
    ]));

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );
    vi.spyOn(service, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Active);
    expect(recovered?.stage).toBe(Stage.Check);
    expect(recovered?.retryCount).toBe(0);
    expect(recovered?.blockedReason).toBeUndefined();
  });

  it('agent_blocked event emitted with correct payload on non-retryable failure', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'EventPayload' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);
    issueRepo.updateRetryCount(issue.id, 1);

    const changeDir = path.join(tmpDir, 'openspec', 'changes', `${issue.number}-test-change`);
    fs.mkdirSync(changeDir, { recursive: true });

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    const blockedEvents: any[] = [];
    eventBus.on('agent_blocked', (e) => blockedEvents.push(e));

    service.recoverIssues();

    expect(blockedEvents).toHaveLength(1);
    const evt = blockedEvents[0];
    expect(evt.issueId).toBe(issue.id);
    expect(evt.projectId).toBe(issue.projectId);
    expect(evt.issueNumber).toBe(issue.number);
    expect(evt.blockedReason).toContain('tasks.json 不存在');
    expect(evt.retryCount).toBe(1);
  });

  it('agent_blocked event emitted with retryCount on max retries reached', () => {
    const { project, issue } = setupPartialTasksIssue(3, 2, 8);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
      undefined,
      undefined,
      undefined,
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    const blockedEvents: any[] = [];
    eventBus.on('agent_blocked', (e) => blockedEvents.push(e));

    service.recoverIssues();

    expect(blockedEvents).toHaveLength(1);
    expect(blockedEvents[0].retryCount).toBe(4);
    expect(blockedEvents[0].issueNumber).toBe(issue.number);
  });

  it('getBlockedIssues returns all blocked issues with reasons', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });

    const issue1 = issueService.create({ projectId: project.id, title: 'B1' });
    issueRepo.updateStatus(issue1.id, IssueStatus.Blocked);
    issueRepo.blockIssue(issue1.id, 'Reason A');

    const issue2 = issueService.create({ projectId: project.id, title: 'B2' });
    issueRepo.updateStatus(issue2.id, IssueStatus.Blocked);
    issueRepo.blockIssue(issue2.id, 'Reason B');
    issueRepo.updateRetryCount(issue2.id, 3);

    const service = new AgentRunnerService(
      eventBus,
      undefined,
      issueRepo,
      8,
    );

    const blocked = service.getBlockedIssues();
    expect(blocked).toHaveLength(2);
    expect(blocked.map(b => b.issueNumber).sort()).toEqual([1, 2]);

    const b1 = blocked.find(b => b.issueNumber === 1)!;
    expect(b1.blockedReason).toBe('Reason A');
    expect(b1.retryCount).toBe(0);

    const b2 = blocked.find(b => b.issueNumber === 2)!;
    expect(b2.blockedReason).toBe('Reason B');
    expect(b2.retryCount).toBe(3);
  });

  it('getBlockedIssues returns empty when no blocked issues', () => {
    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
    expect(service.getBlockedIssues()).toEqual([]);
  });

  it('getStatus includes blockedIssues array', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'Blocked' });
    issueRepo.blockIssue(issue.id, 'Some reason');

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
    const status = service.getStatus();

    expect(status.blockedIssues).toBeDefined();
    expect(status.blockedIssues).toHaveLength(1);
    expect(status.blockedIssues[0].issueNumber).toBe(1);
    expect(status.blockedIssues[0].blockedReason).toBe('Some reason');
    expect(status.blockedIssues[0].retryCount).toBe(0);
  });
});
