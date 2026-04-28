import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, type Issue } from '../src/types';

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
  buildReviewSelfCheckPrompt: vi.fn().mockReturnValue('mock-review-self-check-prompt'),
  buildAutoFixPrompt: vi.fn().mockReturnValue('mock-auto-fix-prompt'),
  buildReVerifyPrompt: vi.fn().mockReturnValue('mock-re-verify-prompt'),
}));

import {
  WorkflowController,
  parseVerdict,
  type ChangeArtifactsManager,
  type StageResult,
} from '../src/workflow/workflow-controller';
import { createAcpConnection } from '../src/agent-runtime/acp-session';
import type { IssueRepo } from '../src/db/issue-repo';
import type { EventBus } from '../src/services/event-bus';
import type { CommentRepo } from '../src/db/comment-repo';
import type { PipelineCheckpointRepo } from '../src/db/pipeline-checkpoint-repo';
import * as fs from 'fs';

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
    priority: 'p1',
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
    commentRepo: {
      create: vi.fn().mockReturnValue({ id: 'c1', issueId: 'issue-1', body: '', createdAt: '' }),
      findById: vi.fn(),
      findByIssue: vi.fn().mockReturnValue([]),
      delete: vi.fn(),
      deleteByIssue: vi.fn(),
    } as unknown as CommentRepo,
    checkpointRepo: {
      get: vi.fn().mockReturnValue(null),
      upsert: vi.fn(),
      delete: vi.fn(),
      deleteAll: vi.fn(),
    } as unknown as PipelineCheckpointRepo,
  };
}

const FAIL_REPORT = '# Review\n\n## Verdict: FAIL\n\n## Fix Suggestions\n- Fix X at line 10\n- Fix Y at line 20';
const PASS_REPORT = '# Review\n\n## Verdict: PASS\n';

describe('parseVerdict', () => {
  it('should parse exact PASS', () => {
    expect(parseVerdict('## Verdict: PASS')).toBe('PASS');
  });

  it('should parse exact FAIL', () => {
    expect(parseVerdict('## Verdict: FAIL')).toBe('FAIL');
  });

  it('should parse case-insensitive verdict', () => {
    expect(parseVerdict('## Verdict: pass')).toBe('PASS');
    expect(parseVerdict('## Verdict: fail')).toBe('FAIL');
    expect(parseVerdict('## verdict: Pass')).toBe('PASS');
    expect(parseVerdict('## VERDICT: FAIL')).toBe('FAIL');
  });

  it('should handle whitespace variations', () => {
    expect(parseVerdict('##   Verdict:   PASS')).toBe('PASS');
    expect(parseVerdict('## Verdict: FAIL  ')).toBe('FAIL');
    expect(parseVerdict('## Verdict : PASS')).toBe('PASS');
  });

  it('should return null when no verdict found', () => {
    expect(parseVerdict('Some random text')).toBeNull();
    expect(parseVerdict('')).toBeNull();
    expect(parseVerdict('Verdict: MAYBE')).toBeNull();
  });

  it('should return first match when multiple verdicts exist', () => {
    const content = `## Verdict: FAIL\n\nSome text\n\n## Verdict: PASS`;
    expect(parseVerdict(content)).toBe('FAIL');
  });

  it('should parse verdict within multi-line content', () => {
    const content = `# Review Report\n\n## Summary\nCode looks fine.\n\n## Verdict: PASS\n\n## Details\nNo issues found.`;
    expect(parseVerdict(content)).toBe('PASS');
  });

  it('should parse verdict from FAIL with fix suggestions', () => {
    const content = `# Review Report\n\n## Summary\nIssues found.\n\n## Verdict: FAIL\n\n## Fix Suggestions\n- Fix typo at line 10`;
    expect(parseVerdict(content)).toBe('FAIL');
  });
});

async function runReviewStage(
  repos: ReturnType<typeof createMockRepos>,
  promptResults: Array<{ text: string; success: boolean; error?: string; acpSessionId?: string }>,
  readFileSequence: string[],
): Promise<StageResult> {
  const ctrl = new WorkflowController({
    artifactManager: createMockArtifactManager(),
    worktreePath: '/tmp/worktree',
    issueRepo: repos.issueRepo,
    eventBus: repos.eventBus,
    projectId: 'proj-1',
    commentRepo: repos.commentRepo,
    checkpointRepo: repos.checkpointRepo,
  });

  const mockConn = {
    prompt: vi.fn(),
    close: vi.fn().mockResolvedValue(undefined),
  };
  for (const r of promptResults) {
    mockConn.prompt.mockResolvedValueOnce({ ...r, acpSessionId: r.acpSessionId ?? 's1' });
  }
  (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);

  let readIdx = 0;
  (fs.readFileSync as ReturnType<typeof vi.fn>).mockImplementation(() => {
    return readFileSequence[Math.min(readIdx++, readFileSequence.length - 1)];
  });

  return (ctrl as any).runPipelineReviewStage(
    createMockIssue(Stage.Review),
    { cwd: '/tmp/worktree' },
  );
}

describe('Review stage Verdict PASS skips auto-fix', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  it('should return requiresApproval without auto-fix when verdict is PASS', async () => {
    const repos = createMockRepos();
    const result = await runReviewStage(repos, [
      { text: 'review output', success: true },
      { text: PASS_REPORT, success: true },
    ], [PASS_REPORT]);

    expect(result.success).toBe(true);
    expect(result.requiresApproval).toBe(true);
    expect(result.escalateToStage).toBeUndefined();
    expect(result.message).toContain('awaiting');
  });
});

describe('Review stage Verdict FAIL enters auto-fix loop', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  it('should enter auto-fix loop on FAIL and succeed on first attempt', async () => {
    const repos = createMockRepos();
    const result = await runReviewStage(repos, [
      { text: 'review output', success: true },
      { text: FAIL_REPORT, success: true },
      { text: 'auto-fix output', success: true },
      { text: PASS_REPORT, success: true },
    ], [FAIL_REPORT, PASS_REPORT]);

    expect(result.success).toBe(true);
    expect(result.requiresApproval).toBe(true);
    expect(result.escalateToStage).toBeUndefined();
    expect(result.message).toContain('auto-fix attempt 1 succeeded');
    expect(repos.commentRepo.create).toHaveBeenCalledWith(
      expect.objectContaining({
        issueId: 'issue-1',
        body: expect.stringContaining('Auto-fix applied'),
      }),
    );
  });

  it('should exhaust 2 attempts and return escalateToStage: Stage.Build', async () => {
    const repos = createMockRepos();
    const result = await runReviewStage(repos, [
      { text: 'review output', success: true },
      { text: FAIL_REPORT, success: true },
      { text: 'auto-fix 1', success: true },
      { text: FAIL_REPORT, success: true },
      { text: 'auto-fix 2', success: true },
      { text: FAIL_REPORT, success: true },
    ], [FAIL_REPORT, FAIL_REPORT, FAIL_REPORT]);

    expect(result.success).toBe(true);
    expect(result.escalateToStage).toBe(Stage.Build);
    expect(result.message).toContain('escalating');
    expect(repos.checkpointRepo.upsert).toHaveBeenCalledWith(1, 'no-auto-fix', ['exhausted'], null);
    expect(repos.commentRepo.create).not.toHaveBeenCalled();
  });

  it('should succeed on second auto-fix attempt', async () => {
    const repos = createMockRepos();
    const result = await runReviewStage(repos, [
      { text: 'review output', success: true },
      { text: FAIL_REPORT, success: true },
      { text: 'auto-fix 1', success: true },
      { text: FAIL_REPORT, success: true },
      { text: 'auto-fix 2', success: true },
      { text: PASS_REPORT, success: true },
    ], [FAIL_REPORT, FAIL_REPORT, PASS_REPORT]);

    expect(result.success).toBe(true);
    expect(result.requiresApproval).toBe(true);
    expect(result.escalateToStage).toBeUndefined();
    expect(result.message).toContain('attempt 2 succeeded');
    expect(repos.commentRepo.create).toHaveBeenCalledWith(
      expect.objectContaining({
        issueId: 'issue-1',
        body: expect.stringContaining('attempt 2'),
      }),
    );
  });

  it('should add comment on successful auto-fix', async () => {
    const repos = createMockRepos();
    await runReviewStage(repos, [
      { text: 'review output', success: true },
      { text: FAIL_REPORT, success: true },
      { text: 'auto-fix output with details', success: true },
      { text: PASS_REPORT, success: true },
    ], [FAIL_REPORT, PASS_REPORT]);

    expect(repos.commentRepo.create).toHaveBeenCalledTimes(1);
    const commentCall = (repos.commentRepo.create as ReturnType<typeof vi.fn>).mock.calls[0][0];
    expect(commentCall.issueId).toBe('issue-1');
    expect(commentCall.body).toContain('Auto-fix applied (attempt 1)');
    expect(commentCall.body).toContain('auto-fix output with details');
    expect(commentCall.body).toContain('FAIL to PASS');
  });
});

describe('no-auto-fix checkpoint skips auto-fix loop', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  it('should skip auto-fix loop when no-auto-fix checkpoint exists', async () => {
    const repos = createMockRepos();
    (repos.checkpointRepo.get as ReturnType<typeof vi.fn>).mockImplementation(
      (_issueNumber: number, stage: string) => {
        if (stage === 'no-auto-fix') {
          return { issueNumber: 1, stage: 'no-auto-fix', completedSteps: ['exhausted'], nextStep: null };
        }
        return null;
      },
    );

    const result = await runReviewStage(repos, [
      { text: 'review output', success: true },
      { text: FAIL_REPORT, success: true },
    ], [FAIL_REPORT]);

    expect(result.success).toBe(true);
    expect(result.requiresApproval).toBe(true);
    expect(result.escalateToStage).toBeUndefined();
    expect(result.message).toContain('auto-fix already attempted');
    expect(repos.commentRepo.create).not.toHaveBeenCalled();
  });
});

describe('auto-fix round failure', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  it('should return error when auto-fix round fails', async () => {
    const repos = createMockRepos();
    const result = await runReviewStage(repos, [
      { text: 'review output', success: true },
      { text: FAIL_REPORT, success: true },
      { text: '', success: false, error: 'auto-fix ACP error' },
    ], [FAIL_REPORT]);

    expect(result.success).toBe(false);
    expect(result.message).toContain('Auto-fix attempt 1 failed');
  });

  it('should return error when re-verify round fails', async () => {
    const repos = createMockRepos();
    const result = await runReviewStage(repos, [
      { text: 'review output', success: true },
      { text: FAIL_REPORT, success: true },
      { text: 'auto-fix output', success: true },
      { text: '', success: false, error: 're-verify ACP error' },
    ], [FAIL_REPORT, FAIL_REPORT]);

    expect(result.success).toBe(false);
    expect(result.message).toContain('Re-verify attempt 1 failed');
  });
});

describe('run() loop handles escalateToStage', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  it('should transition to Build stage when review escalates', async () => {
    const repos = createMockRepos();
    const mockConn = {
      prompt: vi.fn()
        .mockResolvedValueOnce({ text: 'review output', success: true, acpSessionId: 's1' })
        .mockResolvedValueOnce({ text: FAIL_REPORT, success: true, acpSessionId: 's2' })
        .mockResolvedValueOnce({ text: 'auto-fix 1', success: true, acpSessionId: 's3' })
        .mockResolvedValueOnce({ text: FAIL_REPORT, success: true, acpSessionId: 's4' })
        .mockResolvedValueOnce({ text: 'auto-fix 2', success: true, acpSessionId: 's5' })
        .mockResolvedValueOnce({ text: FAIL_REPORT, success: true, acpSessionId: 's6' }),
      close: vi.fn().mockResolvedValue(undefined),
    };
    (createAcpConnection as ReturnType<typeof vi.fn>).mockResolvedValue(mockConn);
    (fs.readFileSync as ReturnType<typeof vi.fn>).mockReturnValue(FAIL_REPORT);

    const ctrl = new WorkflowController({
      artifactManager: createMockArtifactManager(),
      worktreePath: '/tmp/worktree',
      issueRepo: repos.issueRepo,
      eventBus: repos.eventBus,
      projectId: 'proj-1',
      commentRepo: repos.commentRepo,
      checkpointRepo: repos.checkpointRepo,
    });

    const result = await ctrl.run(createMockIssue(Stage.Review), { cwd: '/tmp/worktree' });

    expect(repos.issueRepo.updateStage).toHaveBeenCalledWith('issue-1', Stage.Build);
  });
});

describe('auto-fix SSE events', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  it('should emit plan_round_start for auto-fix and re-verify rounds', async () => {
    const repos = createMockRepos();
    await runReviewStage(repos, [
      { text: 'review output', success: true },
      { text: FAIL_REPORT, success: true },
      { text: 'auto-fix output', success: true },
      { text: PASS_REPORT, success: true },
    ], [FAIL_REPORT, PASS_REPORT]);

    expect(repos.eventBus.emit).toHaveBeenCalledWith(
      'plan_round_start',
      expect.objectContaining({ roundType: 'review', roundIndex: 0 }),
    );
    expect(repos.eventBus.emit).toHaveBeenCalledWith(
      'plan_round_start',
      expect.objectContaining({ roundType: 'review-self-check', roundIndex: 1 }),
    );
    expect(repos.eventBus.emit).toHaveBeenCalledWith(
      'plan_round_start',
      expect.objectContaining({ roundType: 'auto-fix', roundIndex: 2 }),
    );
    expect(repos.eventBus.emit).toHaveBeenCalledWith(
      'plan_round_start',
      expect.objectContaining({ roundType: 're-verify', roundIndex: 3 }),
    );
  });
});
