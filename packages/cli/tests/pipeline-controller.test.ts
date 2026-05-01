import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
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

const writtenFiles = new Set<string>();

vi.mock('fs', () => ({
  existsSync: vi.fn((p: string) => {
    if (typeof p === 'string' && (p.endsWith('review.md') || p.endsWith('review-self-check.md'))) return false;
    return true;
  }),
  readdirSync: vi.fn().mockReturnValue([]),
  rmSync: vi.fn(),
  mkdirSync: vi.fn(),
  writeFileSync: vi.fn(),
  readFileSync: vi.fn((p: string) => {
    if (typeof p === 'string' && (p.endsWith('review.md') || p.endsWith('review-self-check.md'))) {
      return '## Result: PASS\nAll checks passed.';
    }
    if (typeof p === 'string' && p.endsWith('tasks.json')) {
      return JSON.stringify({ version: 1, tasks: [{ id: 'T-001', title: 'Test task', passes: true, attempts: 0 }] });
    }
    return 'artifact content';
  }),
  statSync: vi.fn().mockReturnValue({ size: 100, isFile: () => true, isDirectory: () => false }),
}));

vi.mock('child_process', () => ({
  execFile: vi.fn((...args: unknown[]) => {
    const lastArg = args[args.length - 1];
    const callback = typeof lastArg === 'function' ? lastArg : undefined;
    if (callback) callback(null, { stdout: '', stderr: '' });
  }),
}));

vi.mock('../src/config/config-loader', () => ({
  load: vi.fn().mockReturnValue({}),
  clearConfigCache: vi.fn(),
  getAgentTimeoutConfig: vi.fn().mockReturnValue({ taskTimeout: 600, stageTimeout: 3600, maxGracePeriods: 2 }),
}));

vi.mock('../src/agents/artifact-prompt', () => ({
  buildArtifactPrompt: vi.fn().mockReturnValue('mock-prompt'),
  buildSelfReviewPrompt: vi.fn().mockReturnValue('mock-self-review-prompt'),
  buildReviewerPrompt: vi.fn().mockReturnValue('mock-reviewer-prompt'),
  buildReviewSelfCheckPrompt: vi.fn().mockReturnValue('mock-review-self-check-prompt'),
}));

vi.mock('../src/config/config-loader', () => ({
  load: vi.fn().mockReturnValue({}),
  getAgentTimeoutConfig: vi.fn().mockReturnValue({
    taskTimeout: 600,
    stageTimeout: 3600,
    maxGracePeriods: 2,
  }),
  clearConfigCache: vi.fn(),
  getProviderConfig: vi.fn().mockReturnValue({
    sdk: 'openai-compatible',
    name: 'test',
    apiKey: null,
    baseURL: null,
    envVars: [],
    source: 'none',
  }),
  getServerConfig: vi.fn().mockReturnValue({ port: 3456, host: '127.0.0.1' }),
  getLogConfig: vi.fn().mockReturnValue({ level: 'INFO' }),
  getConfigPath: vi.fn().mockReturnValue('/tmp/test-config.jsonc'),
  getConfigDir: vi.fn().mockReturnValue('/tmp/test-config-dir'),
  resolveOpencodeBinPath: vi.fn().mockReturnValue(undefined),
  writeConfig: vi.fn(),
}));

import {
  WorkflowEngine,
  type ChangeArtifactsManager,
  createCheckpointManager,
  PlanStageRunner,
  BuildStageRunner,
  CheckStageRunner,
  BuildTestCheck,
  AiReviewCheck,
  type StageRunner,
  type StageContext,
  type StageRunResult,
} from '../src/workflow';
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

function createMockCheckpointManager() {
  return {
    getResumeSteps: vi.fn().mockReturnValue([]),
    markStepComplete: vi.fn(),
    delete: vi.fn(),
    deleteAll: vi.fn(),
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

function createDefaultRunners(): StageRunner[] {
  return [
    new PlanStageRunner(),
    new BuildStageRunner({ worktreePath: '/tmp/worktree', projectId: 'proj-1' }),
    new CheckStageRunner([
      new BuildTestCheck({ worktreePath: '/tmp/worktree' }),
      new AiReviewCheck(),
    ]),
  ];
}

function createEngine(opts: {
  issueRepo: IssueRepo;
  eventBus: EventBus;
  artifactManager?: ChangeArtifactsManager;
  runners?: StageRunner[];
  signal?: AbortSignal;
}) {
  const checkpointManager = createMockCheckpointManager() as any;
  return new WorkflowEngine({
    runners: opts.runners ?? createDefaultRunners(),
    artifactManager: opts.artifactManager ?? createMockArtifactManager(),
    issueRepo: opts.issueRepo,
    eventBus: opts.eventBus,
    projectId: 'proj-1',
    checkpointManager,
    signal: opts.signal,
  });
}

describe('WorkflowEngine pipeline stage ordering', () => {
  beforeEach(() => {
    writtenFiles.clear();
    vi.clearAllMocks();
  });

  it('should require gate after plan stage', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    const mockConn = {
      prompt: vi.fn().mockResolvedValue({ text: 'ok', success: true, acpSessionId: 's1' }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const ctrl = createEngine({ issueRepo, eventBus });

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

    const ctrl = createEngine({ issueRepo, eventBus });

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

    const ctrl = createEngine({ issueRepo, eventBus });

    const result = await ctrl.run(createMockIssue(Stage.Build), { cwd: '/tmp/worktree' });

    expect(issueRepo.updateStage).toHaveBeenCalledWith('issue-1', Stage.Check);
    expect(result.stage).toBe(Stage.Check);
    expect(result.gateRequired).toBe(true);
  });
});

describe('AgentRunnerService pipeline gate management', () => {
  it('should return false for approval gate check without issue repo', async () => {
    const { AgentRunnerService } = await import('../src/services/agent-runner-service');
    const { EventBus } = await import('../src/services/event-bus');
    const eventBus = new EventBus();

    const service = new AgentRunnerService(eventBus, undefined, undefined, 8);

    expect(service.isIssueAtApprovalGate('non-existent')).toBe(false);
  });
});

describe('WorkflowEngine done stage sets Completed status', () => {
  beforeEach(() => {
    writtenFiles.clear();
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

    const ctrl = createEngine({ issueRepo, eventBus });

    const doneIssue = createMockIssue(Stage.Done);
    const result = await ctrl.run(doneIssue, { cwd: '/tmp/worktree' });

    expect(result.completed).toBe(true);
    expect(result.stage).toBe(Stage.Done);
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

    const ctrl = createEngine({ issueRepo, eventBus });

    const planIssue = createMockIssue(Stage.Plan);
    const result = await ctrl.run(planIssue, { cwd: '/tmp/worktree' });

    expect(result.completed).toBe(false);
    expect(result.gateRequired).toBe(true);
    expect(issueRepo.updateStatus).not.toHaveBeenCalledWith('issue-1', IssueStatus.Completed);
  });
});

describe('WorkflowEngine build stage git commit', () => {
  beforeEach(() => {
    writtenFiles.clear();
    vi.clearAllMocks();
  });

  it('should commit changes after successful build', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    (issueRepo.updateStage as ReturnType<typeof vi.fn>).mockImplementation(
      (_id: string, stage: Stage) => createMockIssue(stage),
    );

    const ctrl = createEngine({ issueRepo, eventBus });

    const result = await ctrl.run(createMockIssue(Stage.Build), { cwd: '/tmp/worktree' });

    expect(result.gateRequired).toBe(true);
  });

  it('should skip commit when no changes after build', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    (issueRepo.updateStage as ReturnType<typeof vi.fn>).mockImplementation(
      (_id: string, stage: Stage) => createMockIssue(stage),
    );

    const ctrl = createEngine({ issueRepo, eventBus });

    const result = await ctrl.run(createMockIssue(Stage.Build), { cwd: '/tmp/worktree' });

    expect(result.gateRequired).toBe(true);
  });
});

describe('WorkflowEngine review stage multi-round', () => {
  beforeEach(() => {
    writtenFiles.clear();
    vi.clearAllMocks();
  });

  it('should send 2 prompts in review stage (review + self-check)', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    const mockConn = {
      prompt: vi.fn().mockImplementation((_prompt: string) => {
        const calls = mockConn.prompt.mock.calls.length;
        if (calls === 1) writtenFiles.add('/tmp/change/review.md');
        if (calls === 2) writtenFiles.add('/tmp/change/review-self-check.md');
        return Promise.resolve({ text: 'review report content', success: true, acpSessionId: 's1' });
      }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const ctrl = createEngine({ issueRepo, eventBus });

    const result = await ctrl.run(createMockIssue(Stage.Check), { cwd: '/tmp/worktree' });

    expect(mockConn.prompt).toHaveBeenCalledTimes(2);
    expect(mockConn.close).toHaveBeenCalled();
    expect(result.gateRequired).toBe(true);
    expect(result.stage).toBe(Stage.Check);
  });

  it('should not send self-check prompt when review round fails', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    const mockConn = {
      prompt: vi.fn().mockResolvedValue({ text: '', success: false, error: 'review failed', acpSessionId: 's1' }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const ctrl = createEngine({ issueRepo, eventBus });

    const result = await ctrl.run(createMockIssue(Stage.Check), { cwd: '/tmp/worktree' });

    expect(mockConn.prompt).toHaveBeenCalledTimes(1);
    expect(result.gateRequired).toBe(false);
    expect(result.message).toContain('review failed');
  });

  it('should return error when self-check round fails', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    const mockConn = {
      prompt: vi.fn().mockImplementation((_prompt: string) => {
        const calls = mockConn.prompt.mock.calls.length;
        if (calls === 1) writtenFiles.add('/tmp/change/review.md');
        return calls === 1
          ? Promise.resolve({ text: 'review content', success: true, acpSessionId: 's1' })
          : Promise.resolve({ text: '', success: false, error: 'self-check failed', acpSessionId: 's2' });
      }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const ctrl = createEngine({ issueRepo, eventBus });

    const result = await ctrl.run(createMockIssue(Stage.Check), { cwd: '/tmp/worktree' });

    expect(mockConn.prompt).toHaveBeenCalledTimes(2);
    expect(result.gateRequired).toBe(false);
    expect(result.message).toContain('self-check');
  });

  it('should return error when report is empty after self-check', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    const mockConn = {
      prompt: vi.fn().mockImplementation((_prompt: string) => {
        const calls = mockConn.prompt.mock.calls.length;
        if (calls === 1) writtenFiles.add('/tmp/change/review.md');
        return Promise.resolve({ text: '', success: true, acpSessionId: 's1' });
      }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const ctrl = createEngine({ issueRepo, eventBus });

    const result = await ctrl.run(createMockIssue(Stage.Check), { cwd: '/tmp/worktree' });

    expect(mockConn.prompt).toHaveBeenCalledTimes(2);
    expect(result.gateRequired).toBe(false);
    expect(result.message).toContain('not found');
  });

  it('should emit plan_round_start for both review and self-check rounds', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    const mockConn = {
      prompt: vi.fn().mockImplementation((_prompt: string) => {
        const calls = mockConn.prompt.mock.calls.length;
        if (calls === 1) writtenFiles.add('/tmp/change/review.md');
        if (calls === 2) writtenFiles.add('/tmp/change/review-self-check.md');
        return Promise.resolve({ text: 'review report', success: true, acpSessionId: 's1' });
      }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const ctrl = createEngine({ issueRepo, eventBus });

    await ctrl.run(createMockIssue(Stage.Check), { cwd: '/tmp/worktree' });

    expect(eventBus.emit).toHaveBeenCalledWith(
      'plan_round_start',
      expect.objectContaining({ roundType: 'review', roundIndex: 0 }),
    );
    expect(eventBus.emit).toHaveBeenCalledWith(
      'plan_round_start',
      expect.objectContaining({ roundType: 'review-self-check', roundIndex: 1 }),
    );
  });
});
