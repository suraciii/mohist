import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase, getSchemaVersion } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { Stage, IssueStatus, MergeState } from '../src/types';
import { WorktreeManager } from '../src/git/worktree-manager';
import { WorkflowController, type ChangeArtifactsManager } from '../src/workflow/workflow-controller';
import { EventBus } from '../src/services/event-bus';
import { execFile } from 'child_process';

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

vi.mock('../src/agents/artifact-prompt', () => ({
  buildArtifactPrompt: vi.fn().mockReturnValue('mock-prompt'),
  buildSelfReviewPrompt: vi.fn().mockReturnValue('mock-self-review-prompt'),
  buildReviewerPrompt: vi.fn().mockReturnValue('mock-reviewer-prompt'),
  buildConflictResolutionPrompt: vi.fn().mockReturnValue('mock-conflict-resolution-prompt'),
}));

vi.mock('child_process', async (importOriginal) => {
  const actual = await importOriginal<typeof import('child_process')>();
  return {
    ...actual,
    execFile: vi.fn(),
  };
});

vi.mock('fs', () => ({
  existsSync: vi.fn().mockReturnValue(true),
  readdirSync: vi.fn().mockReturnValue([]),
  rmSync: vi.fn(),
  mkdirSync: vi.fn(),
  writeFileSync: vi.fn(),
  readFileSync: vi.fn().mockReturnValue('{"tasks":[]}'),
  statSync: vi.fn(),
}));

import { createAcpConnection } from '../src/agent-runtime/acp-session';

function createMockIssue(overrides?: Partial<import('../src/types').Issue>): import('../src/types').Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    body: 'Test body',
    stage: Stage.Build,
    status: IssueStatus.Active,
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
      update: vi.fn().mockImplementation((_id: string, data: any) => createMockIssue(data)),
      delete: vi.fn(),
      updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) => createMockIssue({ stage })),
      updateStatus: vi.fn().mockImplementation((_id: string, status: unknown) => createMockIssue({ status: status as IssueStatus })),
      updateMergeState: vi.fn().mockImplementation((_id: string, ms: MergeState) => createMockIssue({ mergeState: ms })),
      updateConflictRetryCount: vi.fn().mockImplementation((_id: string, count: number) => createMockIssue({ conflictRetryCount: count })),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      findPendingApprovalByIssueId: vi.fn().mockReturnValue(null),
    } as unknown as import('../src/db/issue-repo').IssueRepo,
    eventBus: {
      on: vi.fn(),
      off: vi.fn(),
      emit: vi.fn(),
      removeAllListeners: vi.fn(),
    } as unknown as import('../src/services/event-bus').EventBus,
  };
}

describe('MergeState enum', () => {
  it('should contain all expected values', () => {
    const values = Object.values(MergeState);
    expect(values).toContain('pending');
    expect(values).toContain('merging');
    expect(values).toContain('merged');
    expect(values).toContain('build-failed');
    expect(values).toContain('conflict');
    expect(values).toContain('resolving');
    expect(values).toContain('blocked');
  });

  it('should have exactly 7 values', () => {
    const values = Object.values(MergeState);
    expect(values).toHaveLength(7);
  });

  it('should have Resolving value equal to "resolving"', () => {
    expect(MergeState.Resolving).toBe('resolving');
  });

  it('should have Blocked value equal to "blocked"', () => {
    expect(MergeState.Blocked).toBe('blocked');
  });

  it('should have Pending value equal to "pending"', () => {
    expect(MergeState.Pending).toBe('pending');
  });
});

describe('Migration v14', () => {
  let db: DatabaseManager;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
  });

  afterEach(() => {
    db.close();
  });

  it('should add merge_state column after migration', () => {
    initializeDatabase(db);

    const version = getSchemaVersion(db);
    expect(version).toBe(14);

    const tableInfo = db.all<{ name: string }>('PRAGMA table_info(issues)');
    const columnNames = tableInfo.map(c => c.name);
    expect(columnNames).toContain('merge_state');
  });

  it('should add conflict_retry_count column after migration', () => {
    initializeDatabase(db);

    const tableInfo = db.all<{ name: string }>('PRAGMA table_info(issues)');
    const columnNames = tableInfo.map(c => c.name);
    expect(columnNames).toContain('conflict_retry_count');
  });

  it('should set merge_state default to pending', () => {
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });
    const issueRepo = new IssueRepo(db);
    const issue = issueRepo.create({ number: 1, projectId: project.id, title: 'Test' });

    const row = db.get<{ merge_state: string }>(
      'SELECT merge_state FROM issues WHERE id = ?',
      [issue.id]
    );
    expect(row?.merge_state).toBe('pending');
  });

  it('should set conflict_retry_count default to 0', () => {
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });
    const issueRepo = new IssueRepo(db);
    const issue = issueRepo.create({ number: 1, projectId: project.id, title: 'Test' });

    const row = db.get<{ conflict_retry_count: number }>(
      'SELECT conflict_retry_count FROM issues WHERE id = ?',
      [issue.id]
    );
    expect(row?.conflict_retry_count).toBe(0);
  });

  it('should be idempotent — running migration twice does not error', () => {
    initializeDatabase(db);
    initializeDatabase(db);

    const version = getSchemaVersion(db);
    expect(version).toBe(14);
  });
});

describe('IssueRepo merge state methods', () => {
  let db: DatabaseManager;
  let repo: IssueRepo;
  let projectId: string;
  let issueId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });
    projectId = project.id;

    repo = new IssueRepo(db);
    const issue = repo.create({ number: 1, projectId, title: 'Test Issue' });
    issueId = issue.id;
  });

  afterEach(() => {
    db.close();
  });

  describe('updateMergeState', () => {
    it('should update merge state to resolving', () => {
      const updated = repo.updateMergeState(issueId, MergeState.Resolving);
      expect(updated?.mergeState).toBe(MergeState.Resolving);
    });

    it('should update merge state to blocked', () => {
      const updated = repo.updateMergeState(issueId, MergeState.Blocked);
      expect(updated?.mergeState).toBe(MergeState.Blocked);
    });

    it('should update merge state to merged', () => {
      const updated = repo.updateMergeState(issueId, MergeState.Merged);
      expect(updated?.mergeState).toBe(MergeState.Merged);
    });

    it('should update merge state to conflict', () => {
      const updated = repo.updateMergeState(issueId, MergeState.Conflict);
      expect(updated?.mergeState).toBe(MergeState.Conflict);
    });

    it('should return null for non-existent issue', () => {
      const updated = repo.updateMergeState('nonexistent', MergeState.Resolving);
      expect(updated).toBeNull();
    });

    it('should persist merge state across reads', () => {
      repo.updateMergeState(issueId, MergeState.Resolving);
      const found = repo.findById(issueId);
      expect(found?.mergeState).toBe(MergeState.Resolving);
    });
  });

  describe('updateConflictRetryCount', () => {
    it('should set retry count to 1', () => {
      const updated = repo.updateConflictRetryCount(issueId, 1);
      expect(updated?.conflictRetryCount).toBe(1);
    });

    it('should increment retry count', () => {
      repo.updateConflictRetryCount(issueId, 1);
      const updated = repo.updateConflictRetryCount(issueId, 2);
      expect(updated?.conflictRetryCount).toBe(2);
    });

    it('should set retry count to 3 (max)', () => {
      const updated = repo.updateConflictRetryCount(issueId, 3);
      expect(updated?.conflictRetryCount).toBe(3);
    });

    it('should return null for non-existent issue', () => {
      const updated = repo.updateConflictRetryCount('nonexistent', 1);
      expect(updated).toBeNull();
    });

    it('should persist retry count across reads', () => {
      repo.updateConflictRetryCount(issueId, 2);
      const found = repo.findById(issueId);
      expect(found?.conflictRetryCount).toBe(2);
    });
  });

  describe('combined state updates for conflict resolution', () => {
    it('should track full conflict resolution lifecycle', () => {
      repo.updateMergeState(issueId, MergeState.Resolving);
      repo.updateConflictRetryCount(issueId, 1);

      let found = repo.findById(issueId);
      expect(found?.mergeState).toBe(MergeState.Resolving);
      expect(found?.conflictRetryCount).toBe(1);

      repo.updateMergeState(issueId, MergeState.Merged);
      found = repo.findById(issueId);
      expect(found?.mergeState).toBe(MergeState.Merged);
      expect(found?.conflictRetryCount).toBe(1);
    });

    it('should track escalation to blocked after max retries', () => {
      repo.updateConflictRetryCount(issueId, 3);
      repo.updateMergeState(issueId, MergeState.Blocked);

      const found = repo.findById(issueId);
      expect(found?.mergeState).toBe(MergeState.Blocked);
      expect(found?.conflictRetryCount).toBe(3);
    });
  });
});

describe('WorktreeManager.mergeMasterInWorktree', () => {
  const execFileMock = vi.mocked(execFile);
  let manager: WorktreeManager;

  beforeEach(() => {
    vi.clearAllMocks();
    manager = new WorktreeManager();
  });

  it('should return success when git merge succeeds', async () => {
    (execFileMock as any).mockImplementation((...args: unknown[]) => {
      const cb = args[args.length - 1] as (err: any, stdout: string, stderr: string) => void;
      cb(null, 'Already up to date.', '');
    });

    const result = await manager.mergeMasterInWorktree('test-project', 1, 'main');

    expect(result.success).toBe(true);
    expect(result.conflictFiles).toBeUndefined();
  });

  it('should return conflict files when merge has conflicts', async () => {
    (execFileMock as any).mockImplementation((...args: unknown[]) => {
      const cmdArgs = args[1] as string[];
      const cb = args[args.length - 1] as (err: any, stdout: string, stderr: string) => void;
      if (cmdArgs[0] === 'merge') {
        cb(new Error('CONFLICT (content): Merge conflict in src/foo.ts'), '', '');
      } else if (cmdArgs[0] === 'diff') {
        cb(null, 'src/foo.ts\nsrc/bar.ts\n', '');
      } else {
        cb(null, '', '');
      }
    });

    const result = await manager.mergeMasterInWorktree('test-project', 1, 'main');

    expect(result.success).toBe(false);
    expect(result.conflictFiles).toEqual(['src/foo.ts', 'src/bar.ts']);
  });

  it('should return failure with message when worktree does not exist', async () => {
    const result = await manager.mergeMasterInWorktree('test-project-nonexist', 999, 'main');

    expect(result.success).toBe(false);
    expect(result.message).toBe('Worktree not found');
  });

  it('should abort and return failure on non-conflict merge error', async () => {
    (execFileMock as any).mockImplementation((...args: unknown[]) => {
      const cmdArgs = args[1] as string[];
      const cb = args[args.length - 1] as (err: any, stdout: string, stderr: string) => void;
      if (cmdArgs[0] === 'merge') {
        cb(new Error('fatal: not a git repository'), '', '');
      } else {
        cb(null, '', '');
      }
    });

    const result = await manager.mergeMasterInWorktree('test-project', 1, 'main');

    expect(result.success).toBe(false);
    expect(result.message).toContain('Merge failed');
  });

  it('should handle empty conflict file list gracefully', async () => {
    (execFileMock as any).mockImplementation((...args: unknown[]) => {
      const cmdArgs = args[1] as string[];
      const cb = args[args.length - 1] as (err: any, stdout: string, stderr: string) => void;
      if (cmdArgs[0] === 'merge') {
        cb(new Error('CONFLICT: merge conflict'), '', '');
      } else if (cmdArgs[0] === 'diff') {
        cb(null, '\n\n', '');
      } else {
        cb(null, '', '');
      }
    });

    const result = await manager.mergeMasterInWorktree('test-project', 1, 'main');

    expect(result.success).toBe(false);
    expect(result.conflictFiles).toEqual([]);
  });
});

describe('WorkflowController conflict resolution path', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('should dispatch to conflict resolution when mergeState is resolving', async () => {
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

    const issue = createMockIssue({ mergeState: MergeState.Resolving });

    await ctrl.run(issue, { cwd: '/tmp/worktree' });

    expect(createAcpConnection).toHaveBeenCalledWith(
      expect.objectContaining({
        executionId: 'conflict-resolve-1',
      })
    );
  });

  it('should skip approval gate during conflict resolution at review stage', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    (issueRepo.updateStage as ReturnType<typeof vi.fn>).mockImplementation(
      (_id: string, stage: Stage) => createMockIssue({ stage, mergeState: MergeState.Resolving }),
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

    const resolvingIssue = createMockIssue({
      stage: Stage.Review,
      mergeState: MergeState.Resolving,
    });

    await ctrl.run(resolvingIssue, { cwd: '/tmp/worktree' });

    expect(issueRepo.setApprovalState).not.toHaveBeenCalled();
    expect(issueRepo.updateStage).toHaveBeenCalledWith('issue-1', Stage.Done);
  });

  it('should set mergeState to Pending after successful conflict resolution', async () => {
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

    const issue = createMockIssue({ mergeState: MergeState.Resolving });

    await ctrl.run(issue, { cwd: '/tmp/worktree' });

    expect(issueRepo.updateMergeState).toHaveBeenCalledWith('issue-1', MergeState.Pending);
  });

  it('should emit build_stage_started for conflict resolution', async () => {
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

    const issue = createMockIssue({ mergeState: MergeState.Resolving });

    await ctrl.run(issue, { cwd: '/tmp/worktree' });

    expect(eventBus.emit).toHaveBeenCalledWith(
      'build_stage_started',
      expect.objectContaining({
        issueId: 'issue-1',
        stage: 'build',
        tasksCount: 0,
      })
    );
  });
});

describe('agent_completed handler conflict logic', () => {
  it('should emit merge_conflict_requiring_resolution when conflict detected', () => {
    const eventBus = new EventBus();
    const listener = vi.fn();
    eventBus.on('merge_conflict_requiring_resolution', listener);

    eventBus.emit('merge_conflict_requiring_resolution', {
      issueId: 'issue-1',
      projectId: 'proj-1',
      conflictFiles: ['src/foo.ts', 'src/bar.ts'],
    });

    expect(listener).toHaveBeenCalledWith({
      issueId: 'issue-1',
      projectId: 'proj-1',
      conflictFiles: ['src/foo.ts', 'src/bar.ts'],
    });
  });

  it('should emit merge_blocked event with retry count', () => {
    const eventBus = new EventBus();
    const listener = vi.fn();
    eventBus.on('merge_blocked', listener);

    eventBus.emit('merge_blocked', {
      issueId: 'issue-1',
      projectId: 'proj-1',
      retryCount: 3,
    });

    expect(listener).toHaveBeenCalledWith({
      issueId: 'issue-1',
      projectId: 'proj-1',
      retryCount: 3,
    });
  });

  it('should handle retry count increment correctly for 3 retries', () => {
    const { issueRepo } = createMockRepos();

    for (let i = 1; i <= 3; i++) {
      const currentRetryCount = i;
      (issueRepo.updateConflictRetryCount as ReturnType<typeof vi.fn>).mockImplementation(
        (_id: string, count: number) => createMockIssue({ conflictRetryCount: count }),
      );
      issueRepo.updateConflictRetryCount('issue-1', currentRetryCount);

      if (currentRetryCount >= 3) {
        (issueRepo.updateMergeState as ReturnType<typeof vi.fn>).mockImplementation(
          (_id: string, ms: MergeState) => createMockIssue({ mergeState: ms }),
        );
        issueRepo.updateMergeState('issue-1', MergeState.Blocked);
      } else {
        (issueRepo.update as ReturnType<typeof vi.fn>).mockImplementation(
          (_id: string, data: any) => createMockIssue(data),
        );
        issueRepo.update('issue-1', { stage: Stage.Build, status: IssueStatus.Active });

        (issueRepo.updateMergeState as ReturnType<typeof vi.fn>).mockImplementation(
          (_id: string, ms: MergeState) => createMockIssue({ mergeState: ms }),
        );
        issueRepo.updateMergeState('issue-1', MergeState.Resolving);
      }
    }

    expect(issueRepo.updateConflictRetryCount).toHaveBeenCalledWith('issue-1', 1);
    expect(issueRepo.updateConflictRetryCount).toHaveBeenCalledWith('issue-1', 2);
    expect(issueRepo.updateConflictRetryCount).toHaveBeenCalledWith('issue-1', 3);
    expect(issueRepo.updateMergeState).toHaveBeenCalledWith('issue-1', MergeState.Blocked);
  });

  it('should regress issue state from Done to Build on conflict', () => {
    const { issueRepo } = createMockRepos();

    (issueRepo.update as ReturnType<typeof vi.fn>).mockImplementation(
      (_id: string, data: any) => createMockIssue(data),
    );

    issueRepo.update('issue-1', { stage: Stage.Build, status: IssueStatus.Active });

    expect(issueRepo.update).toHaveBeenCalledWith('issue-1', {
      stage: Stage.Build,
      status: IssueStatus.Active,
    });
  });

  it('should set mergeState to resolving after state regression', () => {
    const { issueRepo } = createMockRepos();

    (issueRepo.updateMergeState as ReturnType<typeof vi.fn>).mockImplementation(
      (_id: string, ms: MergeState) => createMockIssue({ mergeState: ms }),
    );

    issueRepo.updateMergeState('issue-1', MergeState.Resolving);

    expect(issueRepo.updateMergeState).toHaveBeenCalledWith('issue-1', MergeState.Resolving);
  });

  it('should clear approval state when regressing to build', () => {
    const { issueRepo } = createMockRepos();

    issueRepo.clearApprovalState('issue-1');

    expect(issueRepo.clearApprovalState).toHaveBeenCalledWith('issue-1');
  });
});

describe('3-retry limit', () => {
  it('should mark as blocked when retry count reaches 3', () => {
    const { issueRepo } = createMockRepos();

    for (let retry = 1; retry <= 3; retry++) {
      (issueRepo.updateConflictRetryCount as ReturnType<typeof vi.fn>).mockImplementation(
        (_id: string, count: number) => createMockIssue({ conflictRetryCount: count }),
      );
      issueRepo.updateConflictRetryCount('issue-1', retry);

      if (retry >= 3) {
        (issueRepo.updateMergeState as ReturnType<typeof vi.fn>).mockImplementation(
          (_id: string, ms: MergeState) => createMockIssue({ mergeState: ms }),
        );
        issueRepo.updateMergeState('issue-1', MergeState.Blocked);
      }
    }

    expect(issueRepo.updateMergeState).toHaveBeenLastCalledWith('issue-1', MergeState.Blocked);
  });

  it('should NOT mark as blocked when retry count is 1', () => {
    const { issueRepo } = createMockRepos();

    const retryCount = 1;
    (issueRepo.updateConflictRetryCount as ReturnType<typeof vi.fn>).mockImplementation(
      (_id: string, count: number) => createMockIssue({ conflictRetryCount: count }),
    );
    issueRepo.updateConflictRetryCount('issue-1', retryCount);

    if (retryCount >= 3) {
      (issueRepo.updateMergeState as ReturnType<typeof vi.fn>).mockImplementation(
        (_id: string, ms: MergeState) => createMockIssue({ mergeState: ms }),
      );
      issueRepo.updateMergeState('issue-1', MergeState.Blocked);
    }

    expect(issueRepo.updateMergeState).not.toHaveBeenCalledWith('issue-1', MergeState.Blocked);
  });

  it('should NOT mark as blocked when retry count is 2', () => {
    const { issueRepo } = createMockRepos();

    const retryCount = 2;
    (issueRepo.updateConflictRetryCount as ReturnType<typeof vi.fn>).mockImplementation(
      (_id: string, count: number) => createMockIssue({ conflictRetryCount: count }),
    );
    issueRepo.updateConflictRetryCount('issue-1', retryCount);

    if (retryCount >= 3) {
      (issueRepo.updateMergeState as ReturnType<typeof vi.fn>).mockImplementation(
        (_id: string, ms: MergeState) => createMockIssue({ mergeState: ms }),
      );
      issueRepo.updateMergeState('issue-1', MergeState.Blocked);
    }

    expect(issueRepo.updateMergeState).not.toHaveBeenCalledWith('issue-1', MergeState.Blocked);
  });

  it('should set resolving and regress to build for retries under limit', () => {
    const { issueRepo } = createMockRepos();

    for (const retryCount of [1, 2]) {
      (issueRepo.updateConflictRetryCount as ReturnType<typeof vi.fn>).mockClear();
      (issueRepo.update as ReturnType<typeof vi.fn>).mockClear();
      (issueRepo.updateMergeState as ReturnType<typeof vi.fn>).mockClear();

      (issueRepo.updateConflictRetryCount as ReturnType<typeof vi.fn>).mockImplementation(
        (_id: string, count: number) => createMockIssue({ conflictRetryCount: count }),
      );
      issueRepo.updateConflictRetryCount('issue-1', retryCount);

      (issueRepo.update as ReturnType<typeof vi.fn>).mockImplementation(
        (_id: string, data: any) => createMockIssue(data),
      );
      issueRepo.update('issue-1', { stage: Stage.Build, status: IssueStatus.Active });

      (issueRepo.updateMergeState as ReturnType<typeof vi.fn>).mockImplementation(
        (_id: string, ms: MergeState) => createMockIssue({ mergeState: ms }),
      );
      issueRepo.updateMergeState('issue-1', MergeState.Resolving);

      expect(issueRepo.update).toHaveBeenCalledWith('issue-1', {
        stage: Stage.Build,
        status: IssueStatus.Active,
      });
      expect(issueRepo.updateMergeState).toHaveBeenCalledWith('issue-1', MergeState.Resolving);
    }
  });
});

describe('EventBus merge conflict events', () => {
  let eventBus: EventBus;

  beforeEach(() => {
    eventBus = new EventBus();
  });

  afterEach(() => {
    eventBus.removeAllListeners();
  });

  it('should support merge_conflict_requiring_resolution event type', () => {
    const listener = vi.fn();
    eventBus.on('merge_conflict_requiring_resolution', listener);

    eventBus.emit('merge_conflict_requiring_resolution', {
      issueId: 'i-1',
      projectId: 'p-1',
      conflictFiles: ['a.ts'],
    });

    expect(listener).toHaveBeenCalledTimes(1);
  });

  it('should support merge_blocked event type', () => {
    const listener = vi.fn();
    eventBus.on('merge_blocked', listener);

    eventBus.emit('merge_blocked', {
      issueId: 'i-1',
      projectId: 'p-1',
      retryCount: 3,
    });

    expect(listener).toHaveBeenCalledTimes(1);
  });

  it('should not throw when emitting to event with no listeners', () => {
    expect(() => {
      eventBus.emit('merge_conflict_requiring_resolution', {
        issueId: 'i-1',
        projectId: 'p-1',
        conflictFiles: [],
      });
    }).not.toThrow();
  });

  it('should allow unsubscribing from merge conflict events', () => {
    const listener = vi.fn();
    eventBus.on('merge_conflict_requiring_resolution', listener);
    eventBus.off('merge_conflict_requiring_resolution', listener);

    eventBus.emit('merge_conflict_requiring_resolution', {
      issueId: 'i-1',
      projectId: 'p-1',
      conflictFiles: [],
    });

    expect(listener).not.toHaveBeenCalled();
  });
});

describe('IssueRepo issue with merge state regression', () => {
  let db: DatabaseManager;
  let repo: IssueRepo;
  let projectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });
    projectId = project.id;

    repo = new IssueRepo(db);
  });

  afterEach(() => {
    db.close();
  });

  it('should allow regressing from Done to Build stage via direct update', () => {
    const issue = repo.create({ number: 1, projectId, title: 'Test' });
    repo.updateStage(issue.id, Stage.Done);
    repo.updateStatus(issue.id, IssueStatus.Completed);

    const doneIssue = repo.findById(issue.id);
    expect(doneIssue?.stage).toBe(Stage.Done);
    expect(doneIssue?.status).toBe(IssueStatus.Completed);

    repo.update(issue.id, { stage: Stage.Build, status: IssueStatus.Active });
    repo.updateMergeState(issue.id, MergeState.Resolving);

    const regressedIssue = repo.findById(issue.id);
    expect(regressedIssue?.stage).toBe(Stage.Build);
    expect(regressedIssue?.status).toBe(IssueStatus.Active);
    expect(regressedIssue?.mergeState).toBe(MergeState.Resolving);
  });

  it('should preserve conflict retry count through stage transitions', () => {
    const issue = repo.create({ number: 1, projectId, title: 'Test' });
    repo.updateConflictRetryCount(issue.id, 2);
    repo.updateMergeState(issue.id, MergeState.Resolving);

    repo.updateStage(issue.id, Stage.Done);
    repo.updateStatus(issue.id, IssueStatus.Completed);

    repo.update(issue.id, { stage: Stage.Build, status: IssueStatus.Active });

    const found = repo.findById(issue.id);
    expect(found?.conflictRetryCount).toBe(2);
  });
});
