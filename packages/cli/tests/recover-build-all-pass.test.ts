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

describe('recoverBuildStageIssue — all-pass resumes review pipeline', () => {
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
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'recover-allpass-test-'));
  });

  afterEach(() => {
    db.close();
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it('all-pass tasks.json auto-advances to Check stage with approval gate', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'All Pass Pipeline' });

    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    createChangeDirWithTasks(
      tmpDir,
      issue.number,
      makeTasksJson([
        { id: 'T-001', passes: true },
        { id: 'T-002', passes: true },
      ]),
    );

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

    const spy = vi.spyOn(service, 'enqueue');

    service.recoverIssues();

    expect(spy).not.toHaveBeenCalled();

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.stage).toBe(Stage.Check);
    expect(recovered?.status).toBe(IssueStatus.Active);
    expect(recovered?.approvalState?.status).toBe('awaiting');
  });

  it('all-pass sets Check stage with awaiting approval (enqueue not needed)', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'Pipeline Full' });

    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    createChangeDirWithTasks(
      tmpDir,
      issue.number,
      makeTasksJson([{ id: 'T-001', passes: true }]),
    );

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
    expect(recovered?.stage).toBe(Stage.Check);
    expect(recovered?.status).toBe(IssueStatus.Active);
    expect(recovered?.approvalState?.status).toBe('awaiting');
  });

  it('partial-pass auto-retries by enqueuing resume-pipeline', () => {
    const project = projectRepo.create({ name: 'TestProject', path: tmpDir });
    const issue = issueService.create({ projectId: project.id, title: 'Partial' });

    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Build);

    createChangeDirWithTasks(
      tmpDir,
      issue.number,
      makeTasksJson([
        { id: 'T-001', passes: true },
        { id: 'T-002', passes: false },
      ]),
    );

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

    const spy = vi.spyOn(service, 'enqueue').mockReturnValue({ taskId: 'fake', status: 'pending' });

    service.recoverIssues();

    expect(spy).toHaveBeenCalledWith(issue.id, 'resume-pipeline');

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.stage).toBe(Stage.Build);
    expect(recovered?.retryCount).toBe(1);
  });
});
