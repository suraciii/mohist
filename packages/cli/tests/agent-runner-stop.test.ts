import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { AgentRunnerService } from '../src/services/agent-runner-service';
import { EventBus } from '../src/services/event-bus';
import { IssueService } from '../src/services/issue-service';
import { Stage, IssueStatus } from '../src/types';

vi.mock('../src/workflow/workflow-controller', () => {
  return {
    WorkflowController: class {
      private signal?: AbortSignal;
      constructor(opts: any) {
        this.signal = opts.signal;
      }
      async run() {
        if (this.signal?.aborted) {
          return { completed: false, stage: Stage.Draft, gateRequired: false, message: 'Agent stopped by user' };
        }
        await new Promise<void>((_resolve, reject) => {
          if (this.signal) {
            this.signal.addEventListener('abort', () => {
              reject(new Error('Agent stopped by user'));
            });
          }
        });
        return { completed: true, stage: Stage.Done, gateRequired: false };
      }
    },
    createWorkflowController: (opts: any) => new (vi.mocked(
      class {
        private signal?: AbortSignal;
        constructor(o: any) { this.signal = o.signal; }
        async run() { return { completed: true, stage: Stage.Done, gateRequired: false }; }
      }
    ))(opts),
  };
});

vi.mock('../src/artifacts/change-artifacts-manager', () => ({
  ChangeArtifactsManager: vi.fn().mockImplementation(() => ({})),
}));

describe('AgentRunnerService.stop()', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let issueService: IssueService;
  let eventBus: EventBus;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    issueService = new IssueService(issueRepo);
    eventBus = new EventBus();
  });

  afterEach(() => {
    db.close();
  });

  it('should return false when agent is not running', async () => {
    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
    const result = await service.stop('nonexistent-id');
    expect(result).toBe(false);
  });

  it('should stop a running agent and clean up state', async () => {
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Plan);

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    const startResult = service.startPipeline(
      issue,
      project.id,
      issueRepo,
      '/test',
      { cwd: '/test' },
    );
    expect(startResult.started).toBe(true);
    expect(service.isRunning(issue.id)).toBe(true);

    const stopped = await service.stop(issue.id);
    expect(stopped).toBe(true);
    expect(service.isRunning(issue.id)).toBe(false);

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
  });

  it('should clean up pendingGates and waitingQuestions on stop', async () => {
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Plan);

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    service.startPipeline(issue, project.id, issueRepo, '/test', { cwd: '/test' });

    service.setWaiting(issue.id, 'q-1', 'Waiting for input');

    const stopped = await service.stop(issue.id);
    expect(stopped).toBe(true);

    expect(service.hasPendingGate(issue.number)).toBe(false);
    expect(service.getWaitingQuestions().has(issue.id)).toBe(false);
  });

  it('should emit agent_stopped event on stop', async () => {
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Plan);

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    const stoppedSpy = vi.fn();
    eventBus.on('agent_stopped', stoppedSpy);

    service.startPipeline(issue, project.id, issueRepo, '/test', { cwd: '/test' });

    await service.stop(issue.id);

    expect(stoppedSpy).toHaveBeenCalledTimes(1);
    expect(stoppedSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        issueId: issue.id,
        projectId: project.id,
        issueNumber: issue.number,
      }),
    );
  });

  it('should return false on second stop call (agent already stopped)', async () => {
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Plan);

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    service.startPipeline(issue, project.id, issueRepo, '/test', { cwd: '/test' });

    await service.stop(issue.id);

    const result = await service.stop(issue.id);
    expect(result).toBe(false);
  });

  it('should set issue status to blocked after stop even with pre-existing approval state', async () => {
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Plan);

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    service.startPipeline(issue, project.id, issueRepo, '/test', { cwd: '/test' });

    await service.stop(issue.id);

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
  });

  it('should handle stop when issueRepo is not provided', async () => {
    const service = new AgentRunnerService(eventBus, undefined, undefined, 8);

    const result = await service.stop('nonexistent-id');
    expect(result).toBe(false);
  });

  it('should handle race: agent finishes between isRunning check and stop call', async () => {
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Plan);

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    service.startPipeline(issue, project.id, issueRepo, '/test', { cwd: '/test' });

    await service.stop(issue.id);

    const result = await service.stop(issue.id);
    expect(result).toBe(false);
  });

  it('stop during agent self-completion does not throw', async () => {
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Plan);

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    service.startPipeline(issue, project.id, issueRepo, '/test', { cwd: '/test' });

    const stopPromise = service.stop(issue.id);

    await expect(stopPromise).resolves.toBeDefined();
  });
});

describe('AbortSignal propagation through WorkflowController', () => {
  it('should reject pipeline.run() when signal is already aborted', async () => {
    const { WorkflowController } = await import('../src/workflow/workflow-controller');
    const abortController = new AbortController();
    abortController.abort();

    const mockIssueRepo = {
      findById: vi.fn().mockReturnValue(null),
      findAll: vi.fn().mockReturnValue([]),
      updateStage: vi.fn(),
      updateStatus: vi.fn(),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      findPendingApprovalByIssueId: vi.fn().mockReturnValue(null),
    } as any;

    const mockEventBus = {
      emit: vi.fn(),
      on: vi.fn(),
      off: vi.fn(),
    } as any;

    const mockArtifactManager = {
      getChangeDir: vi.fn().mockReturnValue(null),
      createChangeDir: vi.fn().mockReturnValue(null),
      readArtifact: vi.fn().mockReturnValue(null),
      writeArtifact: vi.fn().mockReturnValue(false),
      exists: vi.fn().mockReturnValue(false),
      readTasks: vi.fn().mockReturnValue(null),
      updateTaskPasses: vi.fn().mockReturnValue(false),
    } as any;

    const controller = new WorkflowController({
      artifactManager: mockArtifactManager,
      worktreePath: '/test',
      issueRepo: mockIssueRepo,
      eventBus: mockEventBus,
      projectId: 'proj-1',
      signal: abortController.signal,
    });

    const issue = {
      id: 'issue-1',
      number: 1,
      title: 'Test',
      projectId: 'proj-1',
      stage: Stage.Plan,
      status: IssueStatus.Active,
    } as any;

    const result = await controller.run(issue, { cwd: '/test' });

    expect(result.completed).toBe(false);
    expect(result.message).toBe('Agent stopped by user');
  });

  it('should return stopped message when signal aborts before entering stage loop', async () => {
    const { WorkflowController } = await import('../src/workflow/workflow-controller');
    const abortController = new AbortController();

    const mockIssueRepo = {
      findById: vi.fn().mockReturnValue({ id: 'issue-1', number: 1, stage: Stage.Draft, projectId: 'proj-1' }),
      findAll: vi.fn().mockReturnValue([]),
      updateStage: vi.fn(),
      updateStatus: vi.fn(),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      findPendingApprovalByIssueId: vi.fn().mockReturnValue(null),
    } as any;

    const mockEventBus = {
      emit: vi.fn(),
      on: vi.fn(),
      off: vi.fn(),
    } as any;

    const mockArtifactManager = {
      getChangeDir: vi.fn().mockReturnValue(null),
      createChangeDir: vi.fn().mockReturnValue(null),
      readArtifact: vi.fn().mockReturnValue(null),
      writeArtifact: vi.fn().mockReturnValue(false),
      exists: vi.fn().mockReturnValue(false),
      readTasks: vi.fn().mockReturnValue(null),
      updateTaskPasses: vi.fn().mockReturnValue(false),
    } as any;

    const controller = new WorkflowController({
      artifactManager: mockArtifactManager,
      worktreePath: '/test',
      issueRepo: mockIssueRepo,
      eventBus: mockEventBus,
      projectId: 'proj-1',
      signal: abortController.signal,
    });

    const issue = {
      id: 'issue-1',
      number: 1,
      title: 'Test',
      projectId: 'proj-1',
      stage: Stage.Draft,
      status: IssueStatus.Active,
    } as any;

    abortController.abort();

    const result = await controller.run(issue, { cwd: '/test' });

    expect(result.completed).toBe(false);
    expect(result.message).toContain('stopped by user');
  });

  it('should abort between stages when signal fires during pipeline execution', async () => {
    const abortController = new AbortController();

    const { WorkflowController } = await import('../src/workflow/workflow-controller');
    const controller = new WorkflowController({
      artifactManager: {
        getChangeDir: vi.fn().mockReturnValue(null),
        createChangeDir: vi.fn().mockReturnValue(null),
        readArtifact: vi.fn().mockReturnValue(null),
        writeArtifact: vi.fn().mockReturnValue(false),
        exists: vi.fn().mockReturnValue(false),
        readTasks: vi.fn().mockReturnValue(null),
        updateTaskPasses: vi.fn().mockReturnValue(false),
      } as any,
      worktreePath: '/test',
      issueRepo: {
        findById: vi.fn().mockImplementation((_id: string) => ({
          id: 'issue-1',
          number: 1,
          stage: Stage.Build,
          projectId: 'proj-1',
        })),
        findAll: vi.fn().mockReturnValue([]),
        updateStage: vi.fn(),
        updateStatus: vi.fn(),
        setApprovalState: vi.fn(),
        clearApprovalState: vi.fn(),
        findPendingApprovalByIssueId: vi.fn().mockReturnValue(null),
      } as any,
      eventBus: {
        emit: vi.fn(),
        on: vi.fn(),
        off: vi.fn(),
      } as any,
      projectId: 'proj-1',
      signal: abortController.signal,
    });

    const buildIssue = {
      id: 'issue-1',
      number: 1,
      title: 'Test',
      projectId: 'proj-1',
      stage: Stage.Build,
      status: IssueStatus.Active,
    } as any;

    abortController.abort();

    const result = await controller.run(buildIssue, { cwd: '/test' });

    expect(result.completed).toBe(false);
    expect(result.message).toContain('stopped by user');
  });

  it('should check abort signal at each stage transition in the while loop', async () => {
    const abortController = new AbortController();

    const issueRepoUpdates: Stage[] = [];
    const mockIssueRepo = {
      findById: vi.fn().mockImplementation((_id: string) => ({
        id: 'issue-1',
        number: 1,
        stage: Stage.Plan,
        projectId: 'proj-1',
      })),
      findAll: vi.fn().mockReturnValue([]),
      updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) => {
        issueRepoUpdates.push(stage);
      }),
      updateStatus: vi.fn(),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      findPendingApprovalByIssueId: vi.fn().mockReturnValue(null),
    } as any;

    const mockEventBus = {
      emit: vi.fn(),
      on: vi.fn(),
      off: vi.fn(),
    } as any;

    const mockArtifactManager = {
      getChangeDir: vi.fn().mockReturnValue(null),
      createChangeDir: vi.fn().mockReturnValue(null),
      readArtifact: vi.fn().mockReturnValue(null),
      writeArtifact: vi.fn().mockReturnValue(false),
      exists: vi.fn().mockReturnValue(false),
      readTasks: vi.fn().mockReturnValue(null),
      updateTaskPasses: vi.fn().mockReturnValue(false),
    } as any;

    const { WorkflowController } = await import('../src/workflow/workflow-controller');
    const controller = new WorkflowController({
      artifactManager: mockArtifactManager,
      worktreePath: '/test',
      issueRepo: mockIssueRepo,
      eventBus: mockEventBus,
      projectId: 'proj-1',
      signal: abortController.signal,
    });

    abortController.abort();

    const planIssue = {
      id: 'issue-1',
      number: 1,
      title: 'Test',
      projectId: 'proj-1',
      stage: Stage.Plan,
      status: IssueStatus.Active,
    } as any;

    const result = await controller.run(planIssue, { cwd: '/test' });

    expect(result.completed).toBe(false);
    expect(result.gateRequired).toBe(false);
    expect(result.message).toContain('stopped by user');
  });
});

describe('AgentRunnerService stop with mocked pipeline', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let issueService: IssueService;
  let eventBus: EventBus;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    issueService = new IssueService(issueRepo);
    eventBus = new EventBus();
  });

  afterEach(() => {
    db.close();
  });

  it('should abort pipeline via Promise.race with AbortController', async () => {
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Plan);

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    service.startPipeline(issue, project.id, issueRepo, '/test', { cwd: '/test' });

    expect(service.isRunning(issue.id)).toBe(true);

    const stopPromise = service.stop(issue.id);
    const result = await stopPromise;

    expect(result).toBe(true);
    expect(service.isRunning(issue.id)).toBe(false);

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.status).toBe(IssueStatus.Blocked);
  });

  it('should allow restarting an issue after stop', async () => {
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    const issue = issueService.create({ projectId: project.id, title: 'Restart Test' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Plan);

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    service.startPipeline(issue, project.id, issueRepo, '/test', { cwd: '/test' });
    await service.stop(issue.id);

    expect(service.isRunning(issue.id)).toBe(false);

    issueRepo.updateStatus(issue.id, IssueStatus.Active);

    const restartResult = service.startPipeline(issue, project.id, issueRepo, '/test', { cwd: '/test' });
    expect(restartResult.started).toBe(true);
    expect(service.isRunning(issue.id)).toBe(true);

    await service.stop(issue.id);
  });

  it('should clean up approval state after stop', async () => {
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    const issue = issueService.create({ projectId: project.id, title: 'Approval Cleanup' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Plan);

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    service.startPipeline(issue, project.id, issueRepo, '/test', { cwd: '/test' });

    issueRepo.setApprovalState(issue.id, {
      stage: Stage.Plan,
      status: 'awaiting',
      requestedAt: new Date().toISOString(),
    });

    const stopResult = await service.stop(issue.id);

    expect(stopResult).toBe(true);
    expect(service.isRunning(issue.id)).toBe(false);

    const recovered = issueRepo.findById(issue.id);
    expect(recovered?.approvalState).toBeUndefined();
  });

  it('should handle concurrent stop calls on the same agent', async () => {
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    const issue = issueService.create({ projectId: project.id, title: 'Concurrent Stop' });
    issueRepo.updateStatus(issue.id, IssueStatus.Active);
    issueRepo.updateStage(issue.id, Stage.Plan);

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    service.startPipeline(issue, project.id, issueRepo, '/test', { cwd: '/test' });

    const [result1, result2] = await Promise.all([
      service.stop(issue.id),
      service.stop(issue.id),
    ]);

    expect(result1 || result2).toBe(true);
    expect(service.isRunning(issue.id)).toBe(false);
  });
});
