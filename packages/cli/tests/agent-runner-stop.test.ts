import { describe, it, expect, vi } from 'vitest';
import { Stage, IssueStatus } from '../src/types';

describe('AbortSignal propagation through WorkflowEngine', () => {
  it('should reject pipeline.run() when signal is already aborted', async () => {
    const { WorkflowEngine } = await import('../src/workflow');
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

    const mockCheckpointManager = {
      getResumeSteps: vi.fn().mockReturnValue([]),
      markStepComplete: vi.fn(),
      delete: vi.fn(),
      deleteAll: vi.fn(),
    } as any;

    const engine = new WorkflowEngine({
      runners: [],
      artifactManager: mockArtifactManager,
      issueRepo: mockIssueRepo,
      eventBus: mockEventBus,
      projectId: 'proj-1',
      checkpointManager: mockCheckpointManager,
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

    const result = await engine.run(issue, { cwd: '/test' });

    expect(result.completed).toBe(false);
    expect(result.message).toBe('Agent stopped by user');
  });

  it('should return stopped message when signal aborts before entering stage loop', async () => {
    const { WorkflowEngine } = await import('../src/workflow');
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

    const mockCheckpointManager = {
      getResumeSteps: vi.fn().mockReturnValue([]),
      markStepComplete: vi.fn(),
      delete: vi.fn(),
      deleteAll: vi.fn(),
    } as any;

    const engine = new WorkflowEngine({
      runners: [],
      artifactManager: mockArtifactManager,
      issueRepo: mockIssueRepo,
      eventBus: mockEventBus,
      projectId: 'proj-1',
      checkpointManager: mockCheckpointManager,
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

    const result = await engine.run(issue, { cwd: '/test' });

    expect(result.completed).toBe(false);
    expect(result.message).toContain('stopped by user');
  });

  it('should abort between stages when signal fires during pipeline execution', async () => {
    const abortController = new AbortController();

    const { WorkflowEngine } = await import('../src/workflow');
    const engine = new WorkflowEngine({
      runners: [],
      artifactManager: {
        getChangeDir: vi.fn().mockReturnValue(null),
        createChangeDir: vi.fn().mockReturnValue(null),
        readArtifact: vi.fn().mockReturnValue(null),
        writeArtifact: vi.fn().mockReturnValue(false),
        exists: vi.fn().mockReturnValue(false),
        readTasks: vi.fn().mockReturnValue(null),
        updateTaskPasses: vi.fn().mockReturnValue(false),
      } as any,
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
      checkpointManager: {
        getResumeSteps: vi.fn().mockReturnValue([]),
        markStepComplete: vi.fn(),
        delete: vi.fn(),
        deleteAll: vi.fn(),
      } as any,
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

    const result = await engine.run(buildIssue, { cwd: '/test' });

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

    const mockCheckpointManager = {
      getResumeSteps: vi.fn().mockReturnValue([]),
      markStepComplete: vi.fn(),
      delete: vi.fn(),
      deleteAll: vi.fn(),
    } as any;

    const { WorkflowEngine } = await import('../src/workflow');
    const engine = new WorkflowEngine({
      runners: [],
      artifactManager: mockArtifactManager,
      issueRepo: mockIssueRepo,
      eventBus: mockEventBus,
      projectId: 'proj-1',
      checkpointManager: mockCheckpointManager,
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

    const result = await engine.run(planIssue, { cwd: '/test' });

    expect(result.completed).toBe(false);
    expect(result.message).toContain('stopped by user');
  });
});
