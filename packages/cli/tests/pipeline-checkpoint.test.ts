import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { PipelineCheckpointRepo } from '../src/db/pipeline-checkpoint-repo';
import { Stage, type Issue } from '../src/types';
import * as path from 'path';

vi.mock('../src/agent-runtime/agent-session', () => ({
  AgentSession: Object.assign(vi.fn(), {
    create: vi.fn(),
  }),
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
  existsSync: vi.fn().mockReturnValue(false),
  readdirSync: vi.fn((p: string) => {
    if (typeof p === 'string' && p.endsWith('specs')) {
      return ['spec.md'];
    }
    if (typeof p === 'string' && p.includes('specs') && !p.endsWith('specs')) {
      return [];
    }
    return [];
  }),
  rmSync: vi.fn(),
  mkdirSync: vi.fn(),
  writeFileSync: vi.fn(),
  readFileSync: vi.fn((p: string) => {
    if (typeof p === 'string' && p.endsWith('tasks.json')) {
      return JSON.stringify({ version: 1, tasks: [{ id: 'T-001', title: 'Test task', passes: true, attempts: 0 }] });
    }
    if (typeof p === 'string' && p.endsWith('self-review.md')) {
      return '<promise>PASS</promise>';
    }
    return 'artifact content';
  }),
  statSync: vi.fn((p: string) => {
    if (typeof p === 'string' && p.endsWith('specs')) {
      return { size: 0, isFile: () => false, isDirectory: () => true };
    }
    return { size: 100, isFile: () => true, isDirectory: () => false };
  }),
}));

vi.mock('../src/agents/artifact-prompt', () => ({
  buildArtifactPrompt: vi.fn().mockReturnValue('mock-prompt'),
  buildSelfReviewPrompt: vi.fn().mockReturnValue('mock-self-review-prompt'),
  buildReviewerPrompt: vi.fn().mockReturnValue('mock-reviewer-prompt'),
}));

vi.mock('child_process', () => ({
  execFile: vi.fn((...args: unknown[]) => {
    const lastArg = args[args.length - 1];
    const callback = typeof lastArg === 'function' ? lastArg : undefined;
    if (callback) callback(null, { stdout: '', stderr: '' });
  }),
}));

import * as fs from 'fs';
import { type StageContext, createCheckpointManager, type ChangeArtifactsManager } from '../src/workflow';
import { PlanStageRunner } from '../src/workflow/plan-stage-runner';
import { AgentSession } from '../src/agent-runtime/agent-session';
import { buildArtifactPrompt, buildSelfReviewPrompt } from '../src/agents/artifact-prompt';

function createMockIssue(overrides?: Partial<Issue>): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    body: 'Test body',
    stage: Stage.Plan,
    status: 'active' as any,
    projectId: 'proj-1',
    labels: [],
    createdAt: '2024-01-01T00:00:00Z',
    updatedAt: '2024-01-01T00:00:00Z',
    ...overrides,
  };
}

function createMockArtifactManager(changeDir: string): ChangeArtifactsManager {
  return {
    getChangeDir: vi.fn().mockReturnValue(changeDir),
    createChangeDir: vi.fn().mockReturnValue(changeDir),
    readArtifact: vi.fn().mockReturnValue(null),
    writeArtifact: vi.fn().mockReturnValue(true),
    exists: vi.fn().mockReturnValue(true),
    readTasks: vi.fn().mockReturnValue(null),
    updateTaskPasses: vi.fn().mockReturnValue(true),
  };
}

const CHANGE_DIR = '/tmp/change-checkpoint-test';

describe('PipelineCheckpointRepo', () => {
  let db: DatabaseManager;
  let repo: PipelineCheckpointRepo;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    repo = new PipelineCheckpointRepo(db);
  });

  afterEach(() => {
    db.close();
  });

  describe('get', () => {
    it('should return null when no checkpoint exists', () => {
      expect(repo.get(1, 'plan')).toBeNull();
    });

    it('should return checkpoint after upsert', () => {
      repo.upsert(1, 'plan', ['proposal'], 'specs');
      const result = repo.get(1, 'plan');
      expect(result).not.toBeNull();
      expect(result!.issueNumber).toBe(1);
      expect(result!.stage).toBe('plan');
      expect(result!.completedSteps).toEqual(['proposal']);
      expect(result!.nextStep).toBe('specs');
      expect(result!.updatedAt).toBeDefined();
    });
  });

  describe('upsert', () => {
    it('should create a new checkpoint record', () => {
      repo.upsert(1, 'plan', ['proposal'], 'specs');

      const row = db.get<{ completed_steps: string; next_step: string | null }>(
        'SELECT completed_steps, next_step FROM pipeline_checkpoint WHERE issue_number = ? AND stage = ?',
        [1, 'plan']
      );
      expect(row).toBeDefined();
      expect(JSON.parse(row!.completed_steps)).toEqual(['proposal']);
      expect(row!.next_step).toBe('specs');
    });

    it('should update existing checkpoint (UPSERT semantics)', () => {
      repo.upsert(1, 'plan', ['proposal'], 'specs');
      repo.upsert(1, 'plan', ['proposal', 'specs', 'design'], 'tasks');

      const result = repo.get(1, 'plan');
      expect(result!.completedSteps).toEqual(['proposal', 'specs', 'design']);
      expect(result!.nextStep).toBe('tasks');
    });

    it('should handle empty completedSteps', () => {
      repo.upsert(1, 'plan', [], 'proposal');
      const result = repo.get(1, 'plan');
      expect(result!.completedSteps).toEqual([]);
      expect(result!.nextStep).toBe('proposal');
    });

    it('should handle null nextStep', () => {
      repo.upsert(1, 'plan', ['proposal', 'specs', 'design', 'tasks'], null);
      const result = repo.get(1, 'plan');
      expect(result!.nextStep).toBeNull();
    });

    it('should maintain separate records per stage', () => {
      repo.upsert(1, 'plan', ['proposal'], 'specs');
      repo.upsert(1, 'build', ['T-001'], 'T-002');

      expect(repo.get(1, 'plan')!.completedSteps).toEqual(['proposal']);
      expect(repo.get(1, 'build')!.completedSteps).toEqual(['T-001']);
    });

    it('should maintain separate records per issue', () => {
      repo.upsert(1, 'plan', ['proposal'], 'specs');
      repo.upsert(2, 'plan', ['proposal', 'specs'], 'design');

      expect(repo.get(1, 'plan')!.completedSteps).toEqual(['proposal']);
      expect(repo.get(2, 'plan')!.completedSteps).toEqual(['proposal', 'specs']);
    });
  });

  describe('delete', () => {
    it('should remove checkpoint for specific issue and stage', () => {
      repo.upsert(1, 'plan', ['proposal'], 'specs');
      repo.upsert(1, 'build', ['T-001'], 'T-002');

      repo.delete(1, 'plan');

      expect(repo.get(1, 'plan')).toBeNull();
      expect(repo.get(1, 'build')).not.toBeNull();
    });

    it('should be a no-op when checkpoint does not exist', () => {
      expect(() => repo.delete(999, 'nonexistent')).not.toThrow();
    });
  });

  describe('deleteAll', () => {
    it('should remove all checkpoints for an issue', () => {
      repo.upsert(1, 'plan', ['proposal'], 'specs');
      repo.upsert(1, 'build', ['T-001'], 'T-002');
      repo.upsert(2, 'plan', ['proposal'], 'specs');

      repo.deleteAll(1);

      expect(repo.get(1, 'plan')).toBeNull();
      expect(repo.get(1, 'build')).toBeNull();
      expect(repo.get(2, 'plan')).not.toBeNull();
    });

    it('should be a no-op when no checkpoints exist', () => {
      expect(() => repo.deleteAll(999)).not.toThrow();
    });
  });
});

describe('PlanStageRunner runPlanStage checkpoint resume', () => {
  let db: DatabaseManager;
  let checkpointRepo: PipelineCheckpointRepo;

  beforeEach(() => {
    vi.clearAllMocks();
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    checkpointRepo = new PipelineCheckpointRepo(db);
    vi.mocked(fs.existsSync).mockReturnValue(false);
    vi.mocked(fs.readdirSync).mockReturnValue([]);
  });

  afterEach(() => {
    db.close();
  });

  function setupMockConn() {
    const mockSession = {
      execute: vi.fn().mockResolvedValue({ text: 'ok', success: true, acpSessionId: 's1' }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (AgentSession as any).create.mockResolvedValue(mockSession);
    return mockSession;
  }

  async function runPlanStage(issue: Issue, artifactManager: ChangeArtifactsManager) {
    const runner = new PlanStageRunner();
    const checkpointManager = createCheckpointManager(checkpointRepo);
    const ctx: StageContext = {
      issue,
      acpOptions: { cwd: '/tmp/worktree' },
      artifactManager,
      worktreeManager: {} as any,
      projectRepo: {} as any,
      eventBus: { emit: vi.fn() } as any,
      checkpointManager,
      issueRepo: { setApprovalState: vi.fn() } as any,
      emit: (event: string, data: unknown) => {
        try {
          ctx.eventBus.emit(event as never, data as never);
        } catch {
          // fire-and-forget
        }
      },
      log: vi.fn(),
    };
    return runner.run(ctx);
  }

  function createRejectedPlanWorkflowRun(feedback = 'Please make the proposal more specific') {
    return {
      stageRuns: [
        {
          stage: 'plan',
          approvalStatus: 'rejected',
          approvalOutput: feedback,
        },
      ],
    } as StageContext['workflowRun'];
  }

  async function runAggregatePlanTask(
    runner: PlanStageRunner,
    issue: Issue,
    artifactManager: ChangeArtifactsManager,
    taskId: string,
    workflowApplicationService: any,
  ) {
    const checkpointManager = createCheckpointManager(checkpointRepo);
    const ctx: StageContext = {
      issue,
      acpOptions: { cwd: '/tmp/worktree' },
      artifactManager,
      worktreeManager: {} as any,
      projectRepo: {} as any,
      eventBus: { emit: vi.fn() } as any,
      checkpointManager,
      issueRepo: { setApprovalState: vi.fn() } as any,
      workflowApplicationService,
      requestedWork: { kind: 'task', stage: Stage.Plan, taskId },
      emit: (event: string, data: unknown) => {
        try {
          ctx.eventBus.emit(event as never, data as never);
        } catch {
          // fire-and-forget
        }
      },
      log: vi.fn(),
    };
    return runner.run(ctx);
  }

  it('should skip proposal round when checkpoint has completedSteps=["proposal"]', async () => {
    checkpointRepo.upsert(1, 'plan', ['proposal'], 'specs');

    const existingArtifacts = new Set<string>([path.join(CHANGE_DIR, 'proposal.md')]);
    vi.mocked(fs.existsSync).mockImplementation((p: unknown) => {
      if (typeof p === 'string' && existingArtifacts.has(p)) return true;
      return false;
    });

    const mockSession = {
      execute: vi.fn().mockImplementation(() => {
        const callCount = mockSession.execute.mock.calls.length;
        if (callCount >= 1) existingArtifacts.add(path.join(CHANGE_DIR, 'specs'));
        if (callCount >= 2) existingArtifacts.add(path.join(CHANGE_DIR, 'design.md'));
        if (callCount >= 3) existingArtifacts.add(path.join(CHANGE_DIR, 'tasks.json'));
        if (callCount >= 4) existingArtifacts.add(path.join(CHANGE_DIR, 'self-review.md'));
        return Promise.resolve({ text: 'ok', success: true, acpSessionId: 's1' });
      }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (AgentSession as any).create.mockResolvedValue(mockSession);

    const artifactManager = createMockArtifactManager(CHANGE_DIR);

    const result = await runPlanStage(createMockIssue(), artifactManager);

    const roundTypes = (buildArtifactPrompt as ReturnType<typeof vi.fn>).mock.calls.map(
      (c: unknown[]) => c[0]
    );
    expect(roundTypes).not.toContain('proposal');
    expect(roundTypes).toContain('specs');
    expect(roundTypes).toContain('design');
    expect(roundTypes).toContain('tasks');
    expect(mockSession.execute).toHaveBeenCalledTimes(4);
  });

  it('reuses one agent session across aggregate Plan artifact tasks', async () => {
    const existingArtifacts = new Set<string>();
    vi.mocked(fs.existsSync).mockImplementation((p: unknown) => {
      return typeof p === 'string' && existingArtifacts.has(p);
    });

    const taskArtifacts: Record<string, string> = {
      proposal: path.join(CHANGE_DIR, 'proposal.md'),
      specs: path.join(CHANGE_DIR, 'specs'),
      design: path.join(CHANGE_DIR, 'design.md'),
      tasks: path.join(CHANGE_DIR, 'tasks.json'),
      'self-review': path.join(CHANGE_DIR, 'self-review.md'),
    };
    const aggregateTaskOrder = ['proposal', 'specs', 'design', 'tasks', 'self-review'];
    const execute = vi.fn().mockImplementation(async () => {
      const taskId = aggregateTaskOrder[execute.mock.calls.length - 1];
      existingArtifacts.add(taskArtifacts[taskId]);
      return { text: 'ok', success: true, acpSessionId: 's1' };
    });
    const close = vi.fn().mockResolvedValue(undefined);
    (AgentSession as any).create.mockResolvedValue({
      execute,
      close,
      canClose: vi.fn().mockReturnValue(true),
    });

    const artifactManager = createMockArtifactManager(CHANGE_DIR);
    const runner = new PlanStageRunner();
    const workflowApplicationService = {
      completeTask: vi.fn(),
    };

    for (const taskId of aggregateTaskOrder) {
      const result = await runAggregatePlanTask(
        runner,
        createMockIssue(),
        artifactManager,
        taskId,
        workflowApplicationService,
      );
      expect(result.success).toBe(true);
    }

    expect(AgentSession.create).toHaveBeenCalledTimes(1);
    expect(execute).toHaveBeenCalledTimes(5);
    expect(close).toHaveBeenCalledTimes(1);
    expect(workflowApplicationService.completeTask).toHaveBeenCalledTimes(5);
  });

  it('should re-run round when checkpoint marks complete but artifact is missing', async () => {
    checkpointRepo.upsert(1, 'plan', ['proposal'], 'specs');

    const existingArtifacts = new Set<string>();
    vi.mocked(fs.existsSync).mockImplementation((p: unknown) => {
      if (typeof p === 'string') {
        if (existingArtifacts.has(p)) return true;
      }
      return false;
    });

    const mockSession = {
      execute: vi.fn().mockImplementation(() => {
        return Promise.resolve({ text: 'ok', success: true, acpSessionId: 's1' });
      }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (AgentSession as any).create.mockResolvedValue(mockSession);

    const artifactManager = createMockArtifactManager(CHANGE_DIR);

    mockSession.execute.mockImplementation(() => {
      if (mockSession.execute.mock.calls.length >= 1) {
        existingArtifacts.add(path.join(CHANGE_DIR, 'proposal.md'));
      }
      if (mockSession.execute.mock.calls.length >= 2) {
        existingArtifacts.add(path.join(CHANGE_DIR, 'specs'));
      }
      if (mockSession.execute.mock.calls.length >= 3) {
        existingArtifacts.add(path.join(CHANGE_DIR, 'design.md'));
      }
      if (mockSession.execute.mock.calls.length >= 4) {
        existingArtifacts.add(path.join(CHANGE_DIR, 'tasks.json'));
      }
      if (mockSession.execute.mock.calls.length >= 5) {
        existingArtifacts.add(path.join(CHANGE_DIR, 'self-review.md'));
      }
      return Promise.resolve({ text: 'ok', success: true, acpSessionId: 's1' });
    });

    const result = await runPlanStage(createMockIssue(), artifactManager);

    const roundTypes = (buildArtifactPrompt as ReturnType<typeof vi.fn>).mock.calls.map(
      (c: unknown[]) => c[0]
    );
    expect(roundTypes).toContain('proposal');
  });

  it('should force a fresh Plan run after rejected approval even when artifacts already exist', async () => {
    const allArtifacts = [
      path.join(CHANGE_DIR, 'proposal.md'),
      path.join(CHANGE_DIR, 'specs'),
      path.join(CHANGE_DIR, 'design.md'),
      path.join(CHANGE_DIR, 'tasks.json'),
      path.join(CHANGE_DIR, 'self-review.md'),
    ];
    const existingArtifacts = new Set<string>(allArtifacts);
    vi.mocked(fs.existsSync).mockImplementation((p: unknown) => {
      return typeof p === 'string' && existingArtifacts.has(p);
    });

    const mockSession = setupMockConn();
    mockSession.execute.mockImplementation(async () => ({ text: 'ok', success: true, acpSessionId: 's1' }));
    const artifactManager = createMockArtifactManager(CHANGE_DIR);

    const runner = new PlanStageRunner();
    const checkpointManager = createCheckpointManager(checkpointRepo);
    const ctx: StageContext = {
      issue: createMockIssue(),
      acpOptions: { cwd: '/tmp/worktree' },
      artifactManager,
      worktreeManager: {} as any,
      projectRepo: {} as any,
      eventBus: { emit: vi.fn() } as any,
      checkpointManager,
      issueRepo: { setApprovalState: vi.fn() } as any,
      workflowRun: createRejectedPlanWorkflowRun(),
      emit: (event: string, data: unknown) => {
        try {
          ctx.eventBus.emit(event as never, data as never);
        } catch {
          // fire-and-forget
        }
      },
      log: vi.fn(),
    };

    const result = await runner.run(ctx);

    expect(result.checkResults.some((check) => check.status === 'pending')).toBe(true);
    expect(AgentSession.create).toHaveBeenCalledTimes(1);
    expect(mockSession.execute).toHaveBeenCalledTimes(5);
    expect(existingArtifacts).toEqual(new Set(allArtifacts));
  });

  it('should include rejection feedback in fresh Plan retry prompts', async () => {
    const existingArtifacts = new Set<string>([
      path.join(CHANGE_DIR, 'proposal.md'),
      path.join(CHANGE_DIR, 'specs'),
      path.join(CHANGE_DIR, 'design.md'),
      path.join(CHANGE_DIR, 'tasks.json'),
      path.join(CHANGE_DIR, 'self-review.md'),
    ]);
    vi.mocked(fs.existsSync).mockImplementation((p: unknown) => {
      return typeof p === 'string' && existingArtifacts.has(p);
    });

    setupMockConn();
    const artifactManager = createMockArtifactManager(CHANGE_DIR);

    const runner = new PlanStageRunner();
    const checkpointManager = createCheckpointManager(checkpointRepo);
    const feedback = 'Please address the rejection feedback';
    const ctx: StageContext = {
      issue: createMockIssue(),
      acpOptions: { cwd: '/tmp/worktree' },
      artifactManager,
      worktreeManager: {} as any,
      projectRepo: {} as any,
      eventBus: { emit: vi.fn() } as any,
      checkpointManager,
      issueRepo: { setApprovalState: vi.fn() } as any,
      workflowRun: createRejectedPlanWorkflowRun(feedback),
      emit: (event: string, data: unknown) => {
        try {
          ctx.eventBus.emit(event as never, data as never);
        } catch {
          // fire-and-forget
        }
      },
      log: vi.fn(),
    };

    await runner.run(ctx);

    expect(buildArtifactPrompt).toHaveBeenCalledWith('proposal', expect.anything(), CHANGE_DIR, undefined, { feedback });
    expect(buildSelfReviewPrompt).toHaveBeenCalledWith(expect.anything(), CHANGE_DIR, undefined, feedback);
  });

  it('should not clean artifacts when checkpoint has completedSteps', async () => {
    checkpointRepo.upsert(1, 'plan', ['proposal'], 'specs');

    vi.mocked(fs.existsSync).mockImplementation((p: unknown) => {
      if (typeof p === 'string') {
        if (p === path.join(CHANGE_DIR, 'proposal.md')) return true;
        if (p === CHANGE_DIR) return true;
      }
      return false;
    });

    setupMockConn();
    const artifactManager = createMockArtifactManager(CHANGE_DIR);

    await runPlanStage(createMockIssue(), artifactManager);

    expect(fs.rmSync).not.toHaveBeenCalled();
  });

  it('should not clean artifacts when no checkpoint exists', async () => {
    vi.mocked(fs.existsSync).mockImplementation((p: unknown) => {
      if (typeof p === 'string' && p === CHANGE_DIR) return true;
      if (typeof p === 'string' && existingArtifacts.has(p)) return true;
      return false;
    });

    setupMockConn();
    const artifactManager = createMockArtifactManager(CHANGE_DIR);

    await runPlanStage(createMockIssue(), artifactManager);

    expect(fs.rmSync).not.toHaveBeenCalled();
  });

  it('should delete checkpoint on stage success', async () => {
    const existingArtifacts = new Set<string>([
      path.join(CHANGE_DIR, 'proposal.md'),
      path.join(CHANGE_DIR, 'specs'),
      path.join(CHANGE_DIR, 'design.md'),
      path.join(CHANGE_DIR, 'tasks.json'),
      path.join(CHANGE_DIR, 'self-review.md'),
    ]);
    vi.mocked(fs.existsSync).mockImplementation((p: unknown) => {
      if (typeof p === 'string' && existingArtifacts.has(p)) return true;
      return false;
    });

    const artifactManager = createMockArtifactManager(CHANGE_DIR);

    vi.mocked(fs.readdirSync).mockImplementation((p: unknown) => {
      if (typeof p === 'string' && p.endsWith('specs')) return ['spec.md'];
      return [];
    });

    const result = await runPlanStage(createMockIssue({
      approvalState: { stage: Stage.Plan, status: 'approved', output: null, requestedAt: '2024-01-01T00:00:00Z' },
    }), artifactManager);

    expect(result.success).toBe(true);
    expect(checkpointRepo.get(1, 'plan')).toBeNull();
  });

  it('should re-execute all tasks on rerun when checkpoint is cleared even if artifact files exist', async () => {
    const existingArtifacts = new Set<string>([
      path.join(CHANGE_DIR, 'proposal.md'),
      path.join(CHANGE_DIR, 'specs'),
      path.join(CHANGE_DIR, 'design.md'),
      path.join(CHANGE_DIR, 'tasks.json'),
      path.join(CHANGE_DIR, 'self-review.md'),
    ]);
    vi.mocked(fs.existsSync).mockImplementation((p: unknown) => {
      if (typeof p === 'string' && existingArtifacts.has(p)) return true;
      return false;
    });

    vi.mocked(fs.readdirSync).mockImplementation((p: unknown) => {
      if (typeof p === 'string' && (p as string).endsWith('specs')) return ['spec.md'];
      return [];
    });

    const taskArtifacts: Record<string, string> = {
      proposal: path.join(CHANGE_DIR, 'proposal.md'),
      specs: path.join(CHANGE_DIR, 'specs'),
      design: path.join(CHANGE_DIR, 'design.md'),
      tasks: path.join(CHANGE_DIR, 'tasks.json'),
      'self-review': path.join(CHANGE_DIR, 'self-review.md'),
    };
    const aggregateTaskOrder = ['proposal', 'specs', 'design', 'tasks', 'self-review'];
    const mockSession = {
      execute: vi.fn().mockImplementation(async () => {
        const taskId = aggregateTaskOrder[mockSession.execute.mock.calls.length - 1];
        existingArtifacts.add(taskArtifacts[taskId]);
        return { text: 'ok', success: true, acpSessionId: 's1' };
      }),
      close: vi.fn().mockResolvedValue(undefined),
      canClose: vi.fn().mockReturnValue(true),
    };
    (AgentSession as any).create.mockResolvedValue(mockSession);

    const artifactManager = createMockArtifactManager(CHANGE_DIR);
    const runner = new PlanStageRunner();
    const workflowApplicationService = {
      completeTask: vi.fn(),
    };

    for (const taskId of aggregateTaskOrder) {
      const result = await runAggregatePlanTask(
        runner,
        createMockIssue(),
        artifactManager,
        taskId,
        workflowApplicationService,
      );
      expect(result.success).toBe(true);
    }

    const roundTypes = (buildArtifactPrompt as ReturnType<typeof vi.fn>).mock.calls.map(
      (c: unknown[]) => c[0]
    );
    expect(roundTypes).toContain('proposal');
    expect(roundTypes).toContain('specs');
    expect(roundTypes).toContain('design');
    expect(roundTypes).toContain('tasks');
    expect(mockSession.execute).toHaveBeenCalledTimes(5);
    expect(workflowApplicationService.completeTask).toHaveBeenCalledTimes(5);
  });

  it('should preserve checkpoint on stage failure', async () => {
    const existingArtifacts = new Set<string>();
    vi.mocked(fs.existsSync).mockImplementation((p: unknown) => {
      if (typeof p === 'string') {
        if (existingArtifacts.has(p)) return true;
      }
      return false;
    });

    let executeCallCount = 0;
    const mockSession = {
      execute: vi.fn().mockImplementation(() => {
        executeCallCount++;
        if (executeCallCount <= 2) {
          if (executeCallCount >= 1) existingArtifacts.add(path.join(CHANGE_DIR, 'proposal.md'));
          if (executeCallCount >= 2) existingArtifacts.add(path.join(CHANGE_DIR, 'specs'));
          return Promise.resolve({ text: 'ok', success: true, acpSessionId: 's1' });
        }
        return Promise.resolve({ success: false, error: 'agent failed', text: '', acpSessionId: 's1' });
      }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (AgentSession as any).create.mockResolvedValue(mockSession);

    const artifactManager = createMockArtifactManager(CHANGE_DIR);

    const result = await runPlanStage(createMockIssue(), artifactManager);

    expect(result.success).toBe(false);
    const checkpoint = checkpointRepo.get(1, 'plan');
    expect(checkpoint).not.toBeNull();
  });
});
