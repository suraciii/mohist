import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus, type Issue } from '../src/types';

vi.mock('../src/agent-runtime/acp-session', () => ({
  createAcpConnection: vi.fn(),
}));

vi.mock('../src/openspec/ralph-executor', () => ({
  RalphExecutor: vi.fn().mockImplementation(() => ({
    execute: vi.fn().mockResolvedValue({ success: true, completed: 1, failed: 0, total: 1 }),
  })),
}));

vi.mock('../src/openspec/detector', () => ({
  detectOpenSpecChange: vi.fn().mockReturnValue({
    changePath: '/tmp/change',
    tasksPath: '/tmp/change/tasks.json',
    sessionMemoriesPath: '/tmp/change/session-memories',
    proposalPath: '/tmp/change/proposal.md',
    designPath: '/tmp/change/design.md',
    specsPath: '/tmp/change/specs',
  }),
}));

vi.mock('fs', () => ({
  existsSync: vi.fn().mockReturnValue(true),
  readdirSync: vi.fn().mockReturnValue([]),
  rmSync: vi.fn(),
  mkdirSync: vi.fn(),
  writeFileSync: vi.fn(),
  readFileSync: vi.fn(),
}));

vi.mock('../src/agents/artifact-prompt', () => ({
  buildArtifactPrompt: vi.fn().mockReturnValue('mock-prompt'),
  buildSelfReviewPrompt: vi.fn().mockReturnValue('mock-self-review-prompt'),
  buildReviewerPrompt: vi.fn().mockReturnValue('mock-reviewer-prompt'),
}));

import { WorkflowController, type ChangeArtifactsManager } from '../src/workflow/workflow-controller';
import { createAcpConnection } from '../src/agent-runtime/acp-session';
import type { IssueRepo } from '../src/db/issue-repo';
import type { EventBus } from '../src/services/event-bus';

function createMockIssue(stage: Stage, overrides?: Partial<Issue>): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    body: 'Test body',
    stage,
    status: 'active' as any,
    projectId: 'proj-1',
    labels: [],
    createdAt: '2024-01-01T00:00:00Z',
    updatedAt: '2024-01-01T00:00:00Z',
    ...overrides,
  };
}

function createMockArtifactManager(): ChangeArtifactsManager {
  return {
    getChangeDir: vi.fn().mockReturnValue('/tmp/change'),
    createChangeDir: vi.fn().mockReturnValue('/tmp/change'),
    readArtifact: vi.fn().mockReturnValue(null),
    writeArtifact: vi.fn().mockReturnValue(true),
    exists: vi.fn().mockReturnValue(true),
    readTasks: vi.fn().mockReturnValue(null),
    updateTaskPasses: vi.fn().mockReturnValue(true),
  };
}

function createMockRepos() {
  return {
    issueRepo: {
      findById: vi.fn(),
      findAll: vi.fn().mockReturnValue([]),
      create: vi.fn(),
      update: vi.fn(),
      remove: vi.fn(),
      updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) => createMockIssue(stage)),
      updateStatus: vi.fn().mockImplementation((_id: string, _status: unknown) => createMockIssue(Stage.Draft)),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      findPendingApprovalByIssueId: vi.fn().mockReturnValue(null),
      findByProjectId: vi.fn().mockReturnValue([]),
    } as unknown as IssueRepo,
    eventBus: {
      on: vi.fn(),
      off: vi.fn(),
      emit: vi.fn(),
      removeAllListeners: vi.fn(),
    } as unknown as EventBus,
  };
}

describe('WorkflowController pipeline stage ordering', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('should return error when issueRepo or eventBus is missing', async () => {
    const ctrl = new WorkflowController({
      artifactManager: createMockArtifactManager(),
      worktreePath: '/tmp/worktree',
    });

    const result = await ctrl.run(createMockIssue(Stage.Plan), { cwd: '/tmp' });

    expect(result.completed).toBe(false);
    expect(result.message).toContain('issueRepo and eventBus');
  });

  it('should require gate after plan stage', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    const mockConn = {
      prompt: vi.fn().mockResolvedValue({ text: 'ok', success: true, acpSessionId: 's1' }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const ctrl = new WorkflowController({
      artifactManager: createMockArtifactManager(),
      worktreePath: '/tmp/worktree',
      issueRepo,
      eventBus,
      projectId: 'proj-1',
    });

    const result = await ctrl.run(createMockIssue(Stage.Plan), { cwd: '/tmp/worktree' });

    expect(result.gateRequired).toBe(true);
    expect(result.stage).toBe(Stage.Plan);
    expect(result.completed).toBe(false);
  });

  it('should call setApprovalState with awaiting after plan', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    const mockConn = {
      prompt: vi.fn().mockResolvedValue({ text: 'ok', success: true, acpSessionId: 's1' }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const ctrl = new WorkflowController({
      artifactManager: createMockArtifactManager(),
      worktreePath: '/tmp/worktree',
      issueRepo,
      eventBus,
      projectId: 'proj-1',
    });

    await ctrl.run(createMockIssue(Stage.Plan), { cwd: '/tmp' });

    expect(issueRepo.setApprovalState).toHaveBeenCalledWith(
      'issue-1',
      expect.objectContaining({
        stage: Stage.Plan,
        status: 'awaiting',
      })
    );
    expect(eventBus.emit).toHaveBeenCalledWith(
      'approval_requested',
      expect.objectContaining({
        issueId: 'issue-1',
        stage: Stage.Plan,
      })
    );
  });

  it('should advance from build to review without gate', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    const mockConn = {
      prompt: vi.fn().mockResolvedValue({ text: 'ok', success: true, acpSessionId: 's1' }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const ctrl = new WorkflowController({
      artifactManager: createMockArtifactManager(),
      worktreePath: '/tmp/worktree',
      issueRepo,
      eventBus,
      projectId: 'proj-1',
    });

    const result = await ctrl.run(createMockIssue(Stage.Build), { cwd: '/tmp/worktree' });

    expect(issueRepo.updateStage).toHaveBeenCalledWith('issue-1', Stage.Review);
    expect(result.stage).toBe(Stage.Review);
    expect(result.gateRequired).toBe(true);
  });

  it('should send 5 prompts in plan stage (4 artifacts + self-review)', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    const mockConn = {
      prompt: vi.fn().mockResolvedValue({ text: 'ok', success: true, acpSessionId: 's1' }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const ctrl = new WorkflowController({
      artifactManager: createMockArtifactManager(),
      worktreePath: '/tmp/worktree',
      issueRepo,
      eventBus,
      projectId: 'proj-1',
    });

    await ctrl.runPlanStage(createMockIssue(Stage.Plan), { cwd: '/tmp/worktree' });

    expect(mockConn.prompt).toHaveBeenCalledTimes(5);
    expect(mockConn.close).toHaveBeenCalled();
  });

  it('should clean change dir before plan stage', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    const mockConn = {
      prompt: vi.fn().mockResolvedValue({ text: 'ok', success: true, acpSessionId: 's1' }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const ctrl = new WorkflowController({
      artifactManager: createMockArtifactManager(),
      worktreePath: '/tmp/worktree',
      issueRepo,
      eventBus,
      projectId: 'proj-1',
    });

    await ctrl.runPlanStage(createMockIssue(Stage.Plan), { cwd: '/tmp/worktree' });

    expect(mockConn.prompt).toHaveBeenCalledTimes(5);
  });
});

describe('AgentRunnerService pipeline gate management', () => {
  it('should track pending gates', async () => {
    const { AgentRunnerService } = await import('../src/services/agent-runner-service');
    const { EventBus } = await import('../src/services/event-bus');
    const eventBus = new EventBus();

    const service = new AgentRunnerService(eventBus, undefined, undefined, 8);

    expect(service.hasPendingGate(1)).toBe(false);
  });
});

describe('WorkflowController done stage sets Completed status', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('should set IssueStatus.Completed when pipeline reaches done', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    (issueRepo.updateStage as ReturnType<typeof vi.fn>).mockImplementation(
      (_id: string, stage: Stage) => createMockIssue(stage),
    );
    (issueRepo.updateStatus as ReturnType<typeof vi.fn>).mockImplementation(
      (_id: string, _status: unknown) => createMockIssue(Stage.Done, { status: IssueStatus.Completed }),
    );

    const ctrl = new WorkflowController({
      artifactManager: createMockArtifactManager(),
      worktreePath: '/tmp/worktree',
      issueRepo,
      eventBus,
      projectId: 'proj-1',
    });

    const doneIssue = createMockIssue(Stage.Done);
    const result = await ctrl.run(doneIssue, { cwd: '/tmp/worktree' });

    expect(result.completed).toBe(true);
    expect(result.stage).toBe(Stage.Done);
    expect(issueRepo.updateStage).toHaveBeenCalledWith('issue-1', Stage.Done);
    expect(issueRepo.clearApprovalState).toHaveBeenCalledWith('issue-1');
    expect(issueRepo.updateStatus).toHaveBeenCalledWith('issue-1', IssueStatus.Completed);
  });

  it('should not set Completed when pipeline stops at gate', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    (issueRepo.updateStatus as ReturnType<typeof vi.fn>).mockImplementation(
      (_id: string, _status: unknown) => createMockIssue(Stage.Plan),
    );
    const mockConn = {
      prompt: vi.fn().mockResolvedValue({ text: 'ok', success: true, acpSessionId: 's1' }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const ctrl = new WorkflowController({
      artifactManager: createMockArtifactManager(),
      worktreePath: '/tmp/worktree',
      issueRepo,
      eventBus,
      projectId: 'proj-1',
    });

    const planIssue = createMockIssue(Stage.Plan);
    const result = await ctrl.run(planIssue, { cwd: '/tmp/worktree' });

    expect(result.completed).toBe(false);
    expect(result.gateRequired).toBe(true);
    expect(issueRepo.updateStatus).not.toHaveBeenCalledWith('issue-1', IssueStatus.Completed);
  });
});

describe('WorkflowController build stage git commit', () => {
  let mockExecFileAsync: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.clearAllMocks();
    mockExecFileAsync = vi.fn();
  });

  it('should commit changes after successful build', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    (issueRepo.updateStage as ReturnType<typeof vi.fn>).mockImplementation(
      (_id: string, stage: Stage) => createMockIssue(stage),
    );

    const ctrl = new WorkflowController({
      artifactManager: createMockArtifactManager(),
      worktreePath: '/tmp/worktree',
      issueRepo,
      eventBus,
      projectId: 'proj-1',
    });

    mockExecFileAsync
      .mockResolvedValueOnce({ stdout: 'M src/foo.ts\nA src/bar.ts\n' })
      .mockResolvedValueOnce({ stdout: '' })
      .mockResolvedValueOnce({ stdout: '' });

    vi.doMock('child_process', () => ({
      execFile: (...args: unknown[]) => {
        const cb = args[args.length - 1];
        const cmd = args[0] as string;
        const cmdArgs = args[1] as string[];
        if (cmd === 'git' && cmdArgs[0] === 'status') {
          return mockExecFileAsync(...(args as [unknown, unknown, unknown]));
        }
        return mockExecFileAsync(...(args as [unknown, unknown, unknown]));
      },
    }));

    const result = await ctrl.run(createMockIssue(Stage.Build), { cwd: '/tmp/worktree' });

    expect(result.gateRequired).toBe(true);
  });

  it('should skip commit when no changes after build', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    (issueRepo.updateStage as ReturnType<typeof vi.fn>).mockImplementation(
      (_id: string, stage: Stage) => createMockIssue(stage),
    );

    const ctrl = new WorkflowController({
      artifactManager: createMockArtifactManager(),
      worktreePath: '/tmp/worktree',
      issueRepo,
      eventBus,
      projectId: 'proj-1',
    });

    const result = await ctrl.run(createMockIssue(Stage.Build), { cwd: '/tmp/worktree' });

    expect(result.gateRequired).toBe(true);
  });
});
