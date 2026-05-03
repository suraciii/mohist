import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { PipelineCheckpointRepo } from '../src/db/pipeline-checkpoint-repo';
import { Stage, type Issue } from '../src/types';
import * as path from 'path';

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
import { PlanStageRunner, type StageContext, createCheckpointManager, type ChangeArtifactsManager } from '../src/workflow';
import { createAcpConnection } from '../src/agent-runtime/acp-session';
import { buildArtifactPrompt } from '../src/agents/artifact-prompt';

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
    const mockConn = {
      prompt: vi.fn().mockResolvedValue({ text: 'ok', success: true, acpSessionId: 's1' }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);
    return mockConn;
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

    const mockConn = {
      prompt: vi.fn().mockImplementation(() => {
        const callCount = mockConn.prompt.mock.calls.length;
        if (callCount >= 1) existingArtifacts.add(path.join(CHANGE_DIR, 'specs'));
        if (callCount >= 2) existingArtifacts.add(path.join(CHANGE_DIR, 'design.md'));
        if (callCount >= 3) existingArtifacts.add(path.join(CHANGE_DIR, 'tasks.json'));
        if (callCount >= 4) existingArtifacts.add(path.join(CHANGE_DIR, 'self-review.md'));
        return Promise.resolve({ text: 'ok', success: true, acpSessionId: 's1' });
      }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const artifactManager = createMockArtifactManager(CHANGE_DIR);

    const result = await runPlanStage(createMockIssue(), artifactManager);

    const roundTypes = (buildArtifactPrompt as ReturnType<typeof vi.fn>).mock.calls.map(
      (c: unknown[]) => c[0]
    );
    expect(roundTypes).not.toContain('proposal');
    expect(roundTypes).toContain('specs');
    expect(roundTypes).toContain('design');
    expect(roundTypes).toContain('tasks');
    expect(mockConn.prompt).toHaveBeenCalledTimes(4);
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

    const mockConn = {
      prompt: vi.fn().mockImplementation(() => {
        return Promise.resolve({ text: 'ok', success: true, acpSessionId: 's1' });
      }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const artifactManager = createMockArtifactManager(CHANGE_DIR);

    mockConn.prompt.mockImplementation(() => {
      if (mockConn.prompt.mock.calls.length >= 1) {
        existingArtifacts.add(path.join(CHANGE_DIR, 'proposal.md'));
      }
      if (mockConn.prompt.mock.calls.length >= 2) {
        existingArtifacts.add(path.join(CHANGE_DIR, 'specs'));
      }
      if (mockConn.prompt.mock.calls.length >= 3) {
        existingArtifacts.add(path.join(CHANGE_DIR, 'design.md'));
      }
      if (mockConn.prompt.mock.calls.length >= 4) {
        existingArtifacts.add(path.join(CHANGE_DIR, 'tasks.json'));
      }
      if (mockConn.prompt.mock.calls.length >= 5) {
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

  it('should preserve checkpoint on stage failure', async () => {
    const existingArtifacts = new Set<string>();
    vi.mocked(fs.existsSync).mockImplementation((p: unknown) => {
      if (typeof p === 'string') {
        if (existingArtifacts.has(p)) return true;
      }
      return false;
    });

    let promptCallCount = 0;
    const mockConn = {
      prompt: vi.fn().mockImplementation(() => {
        promptCallCount++;
        if (promptCallCount <= 2) {
          if (promptCallCount >= 1) existingArtifacts.add(path.join(CHANGE_DIR, 'proposal.md'));
          if (promptCallCount >= 2) existingArtifacts.add(path.join(CHANGE_DIR, 'specs'));
          return Promise.resolve({ text: 'ok', success: true, acpSessionId: 's1' });
        }
        return Promise.resolve({ success: false, error: 'agent failed', text: '', acpSessionId: 's1' });
      }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

    const artifactManager = createMockArtifactManager(CHANGE_DIR);

    const result = await runPlanStage(createMockIssue(), artifactManager);

    expect(result.success).toBe(false);
    const checkpoint = checkpointRepo.get(1, 'plan');
    expect(checkpoint).not.toBeNull();
  });
});
