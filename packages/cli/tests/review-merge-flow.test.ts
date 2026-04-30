import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus, MergeState, type Issue } from '../src/types';

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

import { WorkflowController, type ChangeArtifactsManager, type MergeBackResult } from '../src/workflow/workflow-controller';
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
    status: IssueStatus.Active,
    projectId: 'proj-1',
    labels: [],
    priority: 0,
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
      setMergeState: vi.fn(),
    } as unknown as IssueRepo,
    eventBus: {
      on: vi.fn(),
      off: vi.fn(),
      emit: vi.fn(),
      removeAllListeners: vi.fn(),
    } as unknown as EventBus,
  };
}

function setupMockAcpConnection() {
  const mockConn = {
    prompt: vi.fn().mockResolvedValue({ text: 'review report', success: true, acpSessionId: 's1' }),
    close: vi.fn().mockResolvedValue(undefined),
  };
  (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);
  return mockConn;
}

describe('WorkflowController Review merge flow', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('default path: no approval, no resolving', () => {
    it('should execute review agent and set approval gate', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const mockConn = setupMockAcpConnection();

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
      });

      const issue = createMockIssue(Stage.Check);
      const result = await ctrl.run(issue, { cwd: '/tmp/worktree' });

      expect(mockConn.prompt).toHaveBeenCalledTimes(2);
      expect(result.gateRequired).toBe(true);
      expect(result.completed).toBe(false);
      expect(result.stage).toBe(Stage.Check);
      expect(result.message).toBe('Review completed, awaiting approval');
      expect(issueRepo.setApprovalState).toHaveBeenCalledWith(
        'issue-1',
        expect.objectContaining({
          stage: Stage.Check,
          status: 'awaiting',
        }),
      );
      expect(eventBus.emit).toHaveBeenCalledWith(
        'approval_requested',
        expect.objectContaining({
          issueId: 'issue-1',
          stage: Stage.Check,
        }),
      );
    });
  });

  describe('approved path: mergeBackFn succeeds', () => {
    it('should skip review agent, mergeBack, set Merged, and advance to Done', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const mockConn = setupMockAcpConnection();
      const mergeBackFn = vi.fn().mockResolvedValue({ success: true, message: 'merged' } as MergeBackResult);

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
        mergeBackFn,
      });

      const issue = createMockIssue(Stage.Check, {
        approvalState: { stage: Stage.Check, status: 'approved', output: null, requestedAt: '2024-01-01T00:00:00Z' },
      });
      const result = await ctrl.run(issue, { cwd: '/tmp/worktree' });

      expect(mockConn.prompt).not.toHaveBeenCalled();
      expect(mergeBackFn).toHaveBeenCalledWith(1);
      expect(issueRepo.setMergeState).toHaveBeenCalledWith('issue-1', MergeState.Merged);
      expect(issueRepo.updateStage).toHaveBeenCalledWith('issue-1', Stage.Done);
      expect(result.completed).toBe(true);
      expect(result.stage).toBe(Stage.Done);
    });
  });

  describe('approved path: mergeBackFn fails with onMergeConflictFn', () => {
    it('should call onMergeConflictFn and not advance to Done', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      setupMockAcpConnection();
      const mergeBackFn = vi.fn().mockResolvedValue({ success: false, message: 'conflict in foo.ts' } as MergeBackResult);
      const onMergeConflictFn = vi.fn().mockResolvedValue(undefined);

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
        mergeBackFn,
        onMergeConflictFn,
      });

      const issue = createMockIssue(Stage.Check, {
        approvalState: { stage: Stage.Check, status: 'approved', output: null, requestedAt: '2024-01-01T00:00:00Z' },
      });
      const result = await ctrl.run(issue, { cwd: '/tmp/worktree' });

      expect(mergeBackFn).toHaveBeenCalledWith(1);
      expect(onMergeConflictFn).toHaveBeenCalledWith(1);
      expect(issueRepo.setMergeState).not.toHaveBeenCalled();
      expect(issueRepo.updateStage).not.toHaveBeenCalledWith('issue-1', Stage.Done);
      expect(result.completed).toBe(false);
      expect(result.stage).toBe(Stage.Check);
      expect(result.message).toContain('conflict in foo.ts');
      expect(result.message).toContain('Conflict resolution triggered');
    });
  });

  describe('approved path: mergeBackFn fails without onMergeConflictFn', () => {
    it('should set Blocked state and not advance to Done', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      setupMockAcpConnection();
      const mergeBackFn = vi.fn().mockResolvedValue({ success: false, message: 'conflict' } as MergeBackResult);

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
        mergeBackFn,
      });

      const issue = createMockIssue(Stage.Check, {
        approvalState: { stage: Stage.Check, status: 'approved', output: null, requestedAt: '2024-01-01T00:00:00Z' },
      });
      const result = await ctrl.run(issue, { cwd: '/tmp/worktree' });

      expect(issueRepo.setMergeState).toHaveBeenCalledWith('issue-1', MergeState.Blocked);
      expect(result.completed).toBe(false);
      expect(result.stage).toBe(Stage.Check);
      expect(result.message).toContain('conflict');
    });
  });

  describe('approved path: mergeBackFn throws', () => {
    it('should call onMergeConflictFn when mergeBackFn throws', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      setupMockAcpConnection();
      const mergeBackFn = vi.fn().mockRejectedValue(new Error('git merge failed'));
      const onMergeConflictFn = vi.fn().mockResolvedValue(undefined);

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
        mergeBackFn,
        onMergeConflictFn,
      });

      const issue = createMockIssue(Stage.Check, {
        approvalState: { stage: Stage.Check, status: 'approved', output: null, requestedAt: '2024-01-01T00:00:00Z' },
      });
      const result = await ctrl.run(issue, { cwd: '/tmp/worktree' });

      expect(onMergeConflictFn).toHaveBeenCalledWith(1);
      expect(result.completed).toBe(false);
      expect(result.message).toContain('git merge failed');
      expect(result.message).toContain('Conflict resolution triggered');
    });

    it('should set Blocked when mergeBackFn throws and no onMergeConflictFn', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      setupMockAcpConnection();
      const mergeBackFn = vi.fn().mockRejectedValue(new Error('git error'));

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
        mergeBackFn,
      });

      const issue = createMockIssue(Stage.Check, {
        approvalState: { stage: Stage.Check, status: 'approved', output: null, requestedAt: '2024-01-01T00:00:00Z' },
      });
      const result = await ctrl.run(issue, { cwd: '/tmp/worktree' });

      expect(issueRepo.setMergeState).toHaveBeenCalledWith('issue-1', MergeState.Blocked);
      expect(result.message).toContain('git error');
    });
  });

  describe('Resolving path: mergeBackFn succeeds', () => {
    it('should skip approval, mergeBack, set Merged, and advance to Done', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const mockConn = setupMockAcpConnection();
      const mergeBackFn = vi.fn().mockResolvedValue({ success: true, message: 'merged' } as MergeBackResult);

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
        mergeBackFn,
      });

      const issue = createMockIssue(Stage.Check, {
        mergeState: MergeState.Resolving,
      });
      const result = await ctrl.run(issue, { cwd: '/tmp/worktree' });

      expect(mockConn.prompt).not.toHaveBeenCalled();
      expect(mergeBackFn).toHaveBeenCalledWith(1);
      expect(issueRepo.setMergeState).toHaveBeenCalledWith('issue-1', MergeState.Merged);
      expect(issueRepo.updateStage).toHaveBeenCalledWith('issue-1', Stage.Done);
      expect(result.completed).toBe(true);
      expect(result.stage).toBe(Stage.Done);
    });
  });

  describe('Resolving path: mergeBackFn fails', () => {
    it('should call onMergeConflictFn and stay at Review', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      setupMockAcpConnection();
      const mergeBackFn = vi.fn().mockResolvedValue({ success: false, message: 'still conflicting' } as MergeBackResult);
      const onMergeConflictFn = vi.fn().mockResolvedValue(undefined);

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
        mergeBackFn,
        onMergeConflictFn,
      });

      const issue = createMockIssue(Stage.Check, {
        mergeState: MergeState.Resolving,
      });
      const result = await ctrl.run(issue, { cwd: '/tmp/worktree' });

      expect(mergeBackFn).toHaveBeenCalledWith(1);
      expect(onMergeConflictFn).toHaveBeenCalledWith(1);
      expect(result.completed).toBe(false);
      expect(result.stage).toBe(Stage.Check);
      expect(result.message).toContain('still conflicting');
    });

    it('should set Blocked without onMergeConflictFn', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      setupMockAcpConnection();
      const mergeBackFn = vi.fn().mockResolvedValue({ success: false, message: 'blocked' } as MergeBackResult);

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
        mergeBackFn,
      });

      const issue = createMockIssue(Stage.Check, {
        mergeState: MergeState.Resolving,
      });
      const result = await ctrl.run(issue, { cwd: '/tmp/worktree' });

      expect(issueRepo.setMergeState).toHaveBeenCalledWith('issue-1', MergeState.Blocked);
      expect(result.completed).toBe(false);
    });
  });

  describe('no mergeBackFn: backward compatible behavior', () => {
    it('should skip to Done for approved issue without mergeBackFn', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const mockConn = setupMockAcpConnection();

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
      });

      const issue = createMockIssue(Stage.Check, {
        approvalState: { stage: Stage.Check, status: 'approved', output: null, requestedAt: '2024-01-01T00:00:00Z' },
      });
      const result = await ctrl.run(issue, { cwd: '/tmp/worktree' });

      expect(mockConn.prompt).not.toHaveBeenCalled();
      expect(issueRepo.updateStage).toHaveBeenCalledWith('issue-1', Stage.Done);
      expect(result.completed).toBe(true);
      expect(result.stage).toBe(Stage.Done);
    });

    it('should skip approval gate and go to Done for Resolving issue without mergeBackFn', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const mockConn = setupMockAcpConnection();

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
      });

      const issue = createMockIssue(Stage.Check, {
        mergeState: MergeState.Resolving,
      });
      const result = await ctrl.run(issue, { cwd: '/tmp/worktree' });

      expect(mockConn.prompt).not.toHaveBeenCalled();
      expect(issueRepo.updateStage).toHaveBeenCalledWith('issue-1', Stage.Done);
      expect(result.completed).toBe(true);
      expect(result.stage).toBe(Stage.Done);
    });
  });

  describe('both approved and resolving', () => {
    it('should prioritize mergeBack over review when both approved and resolving', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const mockConn = setupMockAcpConnection();
      const mergeBackFn = vi.fn().mockResolvedValue({ success: true, message: 'merged' } as MergeBackResult);

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
        mergeBackFn,
      });

      const issue = createMockIssue(Stage.Check, {
        approvalState: { stage: Stage.Check, status: 'approved', output: null, requestedAt: '2024-01-01T00:00:00Z' },
        mergeState: MergeState.Resolving,
      });
      const result = await ctrl.run(issue, { cwd: '/tmp/worktree' });

      expect(mockConn.prompt).not.toHaveBeenCalled();
      expect(mergeBackFn).toHaveBeenCalledWith(1);
      expect(issueRepo.setMergeState).toHaveBeenCalledWith('issue-1', MergeState.Merged);
      expect(result.completed).toBe(true);
      expect(result.stage).toBe(Stage.Done);
    });
  });
});
