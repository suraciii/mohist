import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { resetDatabase, closeDatabase } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { DatabaseManager } from '../src/db/database';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { TaskRepo } from '../src/db/task-repo';
import { WorkflowEngine } from '../src/workflow/engine';
import { AgentRunner } from '../src/agent/runner';
import { Stage, IssueStatus } from '../src/types';

interface MockAgentControl {
  resolve: (taskId: string) => void;
  reject: (taskId: string, err: Error) => void;
}

function createMockAgentRunner(): { runner: AgentRunner; control: MockAgentControl } {
  const pendingCalls = new Map<string, { resolve: () => void; reject: (err: Error) => void }>();

  const runAgent = async (taskId: string) => {
    return new Promise<void>((resolve, reject) => {
      pendingCalls.set(taskId, { resolve, reject });
    });
  };

  const runner = {
    spawnAgent: vi.fn(runAgent),
    runDesignerAgent: vi.fn((_issue: any, task: any) => runAgent(task.id)),
    runImplementerAgent: vi.fn((_issue: any, task: any) => runAgent(task.id)),
    killAgent: vi.fn((taskId: string) => {
      const pending = pendingCalls.get(taskId);
      if (pending) {
        pendingCalls.delete(taskId);
        pending.reject(new Error('Agent killed'));
        return true;
      }
      return false;
    }),
    killAll: vi.fn(() => {
      for (const [, pending] of pendingCalls) {
        pending.reject(new Error('Agent killed'));
      }
      pendingCalls.clear();
    }),
    getRunningCount: () => pendingCalls.size,
    isRunning: (taskId: string) => pendingCalls.has(taskId),
  } as unknown as AgentRunner;

  return {
    runner,
    control: {
      resolve(taskId: string) {
        const pending = pendingCalls.get(taskId);
        if (pending) {
          pendingCalls.delete(taskId);
          pending.resolve();
        }
      },
      reject(taskId: string, err: Error) {
        const pending = pendingCalls.get(taskId);
        if (pending) {
          pendingCalls.delete(taskId);
          pending.reject(err);
        }
      }
    }
  };
}

function createMockWorktreeManager() {
  return {
    create: vi.fn().mockResolvedValue('/tmp/test-worktree'),
    remove: vi.fn().mockResolvedValue(undefined),
    list: vi.fn().mockResolvedValue([]),
    exists: vi.fn().mockReturnValue(false),
    prune: vi.fn().mockResolvedValue(undefined),
  };
}

describe('WorkflowEngine Integration', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let taskRepo: TaskRepo;
  let mockAgent: ReturnType<typeof createMockAgentRunner>;
  let mockWorktree: ReturnType<typeof createMockWorktreeManager>;
  let engine: WorkflowEngine;
  let projectId: string;

  beforeEach(() => {
    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    taskRepo = new TaskRepo(db);

    const project = projectRepo.create({ name: 'test-project', path: '/tmp/test' });
    projectId = project.id;

    mockAgent = createMockAgentRunner();
    mockWorktree = createMockWorktreeManager();

    engine = new WorkflowEngine(
      taskRepo,
      issueRepo,
      projectRepo,
      mockAgent.runner,
      mockWorktree as any,
      { maxConcurrentAgents: 2, pollInterval: 50 }
    );
  });

  afterEach(async () => {
    try { await engine.stop(500); } catch {}
    closeDatabase();
  });

  it('should claim pending task and execute stage handler', async () => {
    const issue = issueRepo.create({
      number: 1, projectId, title: 'Test',
      stage: Stage.Designing, status: IssueStatus.Active,
    });

    const task = taskRepo.create({ issueId: issue.id, projectId, stage: Stage.Designing });
    engine.registerWorktree(issue.id, '/tmp/test-worktree');

    await engine.start();

    await new Promise<void>(resolve => setTimeout(resolve, 150));
    expect(engine.getActiveWorkerCount()).toBe(1);

    mockAgent.control.resolve(task.id);

    await new Promise<void>(resolve => setTimeout(resolve, 150));

    const updatedTask = taskRepo.findById(task.id);
    expect(updatedTask?.status).toBe('completed');

    const freshIssue = issueRepo.findById(issue.id);
    expect(freshIssue?.stage).toBe(Stage.WaitingDesignReview);

    await engine.stop(500);
  });

  it('should block issue on agent failure', async () => {
    const issue = issueRepo.create({
      number: 1, projectId, title: 'Test',
      stage: Stage.Designing, status: IssueStatus.Active,
    });

    const task = taskRepo.create({ issueId: issue.id, projectId, stage: Stage.Designing });
    engine.registerWorktree(issue.id, '/tmp/test-worktree');

    await engine.start();

    await new Promise<void>(resolve => setTimeout(resolve, 150));
    expect(engine.getActiveWorkerCount()).toBe(1);

    mockAgent.control.reject(task.id, new Error('Agent failed'));

    await new Promise<void>(resolve => setTimeout(resolve, 150));

    const updatedTask = taskRepo.findById(task.id);
    expect(updatedTask?.status).toBe('failed');

    const freshIssue = issueRepo.findById(issue.id);
    expect(freshIssue?.status).toBe(IssueStatus.Blocked);

    await engine.stop(500);
  });

  it('should enforce same-issue single-task constraint', () => {
    const issue = issueRepo.create({
      number: 1, projectId, title: 'Test',
      stage: Stage.Designing, status: IssueStatus.Active,
    });

    taskRepo.create({ issueId: issue.id, projectId, stage: Stage.Designing });
    taskRepo.create({ issueId: issue.id, projectId, stage: Stage.Designing });

    const claimed = taskRepo.findAndClaim();
    expect(claimed).not.toBeNull();

    const secondClaim = taskRepo.findAndClaim();
    expect(secondClaim).toBeNull();
  });

  it('should allow parallel tasks for different issues', () => {
    const issue1 = issueRepo.create({
      number: 1, projectId, title: 'Test 1',
      stage: Stage.Designing, status: IssueStatus.Active,
    });
    const issue2 = issueRepo.create({
      number: 2, projectId, title: 'Test 2',
      stage: Stage.Designing, status: IssueStatus.Active,
    });

    taskRepo.create({ issueId: issue1.id, projectId, stage: Stage.Designing });
    taskRepo.create({ issueId: issue2.id, projectId, stage: Stage.Designing });

    const claimed1 = taskRepo.findAndClaim();
    expect(claimed1).not.toBeNull();

    const claimed2 = taskRepo.findAndClaim();
    expect(claimed2).not.toBeNull();
    expect(claimed1!.issueId).not.toBe(claimed2!.issueId);
  });

  it('should stop gracefully and mark running tasks as failed', async () => {
    const issue = issueRepo.create({
      number: 1, projectId, title: 'Test',
      stage: Stage.Designing, status: IssueStatus.Active,
    });

    const task = taskRepo.create({ issueId: issue.id, projectId, stage: Stage.Designing });
    engine.registerWorktree(issue.id, '/tmp/test-worktree');

    await engine.start();

    await new Promise<void>(resolve => setTimeout(resolve, 150));
    expect(engine.getActiveWorkerCount()).toBe(1);

    await engine.stop(500);

    const updatedTask = taskRepo.findById(task.id);
    expect(updatedTask?.status).toBe('failed');

    await new Promise<void>(resolve => setTimeout(resolve, 100));
    expect(engine.getActiveWorkerCount()).toBe(0);
  });

  it('should not advance past user-approval stages', async () => {
    const issue = issueRepo.create({
      number: 1, projectId, title: 'Test',
      stage: Stage.Designing, status: IssueStatus.Active,
    });

    const task = taskRepo.create({ issueId: issue.id, projectId, stage: Stage.Designing });
    engine.registerWorktree(issue.id, '/tmp/test-worktree');

    await engine.start();

    await new Promise<void>(resolve => setTimeout(resolve, 150));

    mockAgent.control.resolve(task.id);

    await new Promise<void>(resolve => setTimeout(resolve, 150));

    const freshIssue = issueRepo.findById(issue.id);
    expect(freshIssue?.stage).toBe(Stage.WaitingDesignReview);

    const pendingTasks = taskRepo.findPending();
    expect(pendingTasks).toHaveLength(0);

    await engine.stop(500);
  });

  it('getActiveWorkerCount should return actual active count, not total workers', async () => {
    expect(engine.getActiveWorkerCount()).toBe(0);

    const issue = issueRepo.create({
      number: 1, projectId, title: 'Test',
      stage: Stage.Designing, status: IssueStatus.Active,
    });

    const task = taskRepo.create({ issueId: issue.id, projectId, stage: Stage.Designing });
    engine.registerWorktree(issue.id, '/tmp/test-worktree');

    await engine.start();

    await new Promise<void>(resolve => setTimeout(resolve, 150));
    expect(engine.getActiveWorkerCount()).toBe(1);

    mockAgent.control.resolve(task.id);

    await new Promise<void>(resolve => setTimeout(resolve, 150));
    expect(engine.getActiveWorkerCount()).toBe(0);

    await engine.stop(500);
  });

  it('should recover issueTaskMap on start from running tasks', async () => {
    const issue = issueRepo.create({
      number: 1, projectId, title: 'Test',
      stage: Stage.Designing, status: IssueStatus.Active,
    });

    const task = taskRepo.create({ issueId: issue.id, projectId, stage: Stage.Designing });
    taskRepo.updateStatus(task.id, 'running');

    const engine2 = new WorkflowEngine(
      taskRepo,
      issueRepo,
      projectRepo,
      mockAgent.runner,
      mockWorktree as any,
      { maxConcurrentAgents: 2, pollInterval: 50 }
    );

    await engine2.start();

    expect(engine2.getActiveWorkerCount()).toBe(1);

    await engine2.stop(500);
  });

  it('killAgentByIssueId should mark task failed and prevent further execution', async () => {
    const issue = issueRepo.create({
      number: 1, projectId, title: 'Test',
      stage: Stage.Designing, status: IssueStatus.Active,
    });

    const task = taskRepo.create({ issueId: issue.id, projectId, stage: Stage.Designing });
    engine.registerWorktree(issue.id, '/tmp/test-worktree');

    await engine.start();

    await new Promise<void>(resolve => setTimeout(resolve, 150));
    expect(engine.getActiveWorkerCount()).toBe(1);

    engine.killAgentByIssueId(issue.id);

    await new Promise<void>(resolve => setTimeout(resolve, 100));

    expect(engine.getActiveWorkerCount()).toBe(0);

    const updatedTask = taskRepo.findById(task.id);
    expect(updatedTask?.status).toBe('failed');
    expect(updatedTask?.error).toBe('user_paused');

    await engine.stop(500);
  });
});
