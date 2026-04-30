import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus, MergeState, type Issue } from '../src/types';

vi.mock('../src/agent-runtime/acp-session', () => ({
  createAcpConnection: vi.fn(),
}));

vi.mock('../src/config/config-loader', () => ({
  load: vi.fn().mockReturnValue({}),
  getAgentTimeoutConfig: vi.fn().mockReturnValue({ timeout: 1800000 }),
}));

vi.mock('../src/workflow/workflow-loader', () => ({
  loadWorkflow: vi.fn().mockReturnValue('default'),
  loadAgentConfig: vi.fn().mockReturnValue({ model: 'test-model' }),
  loadChecksConfig: vi.fn().mockReturnValue({
    buildTest: { enabled: true, command: 'npm test', timeout: 300000, autoFix: false, maxFixAttempts: 2 },
    ffMerge: { enabled: false },
    aiReview: { enabled: true },
  }),
  DEFAULT_CHECKS_CONFIG: {
    buildTest: { enabled: true, command: 'npm test', timeout: 300000, autoFix: false, maxFixAttempts: 2 },
    ffMerge: { enabled: false },
    aiReview: { enabled: true },
  },
}));

vi.mock('child_process', () => ({
  execFile: vi.fn().mockImplementation((_cmd: string, _args: string[], _opts: any, cb: Function) => {
    cb(null, { stdout: 'Tests passed', stderr: '' });
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
  buildReviewSelfCheckPrompt: vi.fn().mockReturnValue('mock-review-self-check-prompt'),
}));

import { WorkflowController, type ChangeArtifactsManager } from '../src/workflow/workflow-controller';
import { createAcpConnection } from '../src/agent-runtime/acp-session';
import type { IssueRepo } from '../src/db/issue-repo';
import type { EventBus } from '../src/services/event-bus';

const PASS_REPORT = '# Review\n\n## Result: PASS\n';

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
    prompt: vi.fn().mockResolvedValue({ text: PASS_REPORT, success: true, acpSessionId: 's1' }),
    close: vi.fn().mockResolvedValue(undefined),
  };
  (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);
  return mockConn;
}

describe('WorkflowController Review check stage', () => {
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

      expect(mockConn.prompt).toHaveBeenCalled();
      expect(result.gateRequired).toBe(true);
      expect(result.completed).toBe(false);
      expect(result.stage).toBe(Stage.Check);
      expect(result.message).toContain('awaiting');
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
});
