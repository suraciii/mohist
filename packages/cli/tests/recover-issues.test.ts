import { describe, it, expect, beforeEach, afterEach } from 'vitest';
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
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Active);
    expect(recovered?.stage).toBe(Stage.Review);
  });

  it('build-stage partial: blocked with progress summary', () => {
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
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.stage).toBe(Stage.Build);
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
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.stage).toBe(Stage.Build);
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
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
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
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.stage).toBe(Stage.Build);
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
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
  });

  it('plan-stage orphan: blocked (existing behavior)', () => {
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
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
    expect(recovered?.stage).toBe(Stage.Plan);
    expect(recovered?.approvalState).toBeUndefined();
  });

  it('awaiting approval: pendingGate restored, status active', () => {
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
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Active);
    expect(recovered?.stage).toBe(Stage.Plan);
    expect(recovered?.approvalState?.status).toBe('awaiting');
    expect(service.hasPendingGate(issue.number)).toBe(true);
  });

  it('no ProjectRepo/WorktreeManager: build-stage falls back to blocked', () => {
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
    expect(recovered?.status).toBe(IssueStatus.Blocked);
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
      projectRepo,
      createWorktreeMock(null),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
  });

  it('mixed orphans: awaiting preserved, build all-pass advanced, plan blocked', () => {
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
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const rAwaiting = issueRepo.findById(awaitingIssue.id);
    expect(rAwaiting?.status).toBe(IssueStatus.Active);
    expect(service.hasPendingGate(awaitingIssue.number)).toBe(true);

    const rBuild = issueRepo.findById(buildIssue.id);
    expect(rBuild?.status).toBe(IssueStatus.Active);
    expect(rBuild?.stage).toBe(Stage.Review);

    const rPlan = issueRepo.findById(planIssue.id);
    expect(rPlan?.status).toBe(IssueStatus.Blocked);
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
      projectRepo,
      createWorktreeMock(tmpDir),
    );

    service.recoverIssues();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Active);
    expect(recovered?.stage).toBe(Stage.Review);
  });
});
