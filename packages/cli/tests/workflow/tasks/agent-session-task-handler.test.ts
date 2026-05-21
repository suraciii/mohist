import { describe, it, expect, vi, beforeEach } from 'vitest';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { Stage, IssueStatus } from '../../../src/types';
import type { StageContext } from '../../../src/workflow/stage-context';
import type { AgentSessionTaskInput } from '../../../src/workflow/tasks/types';
import { createAgentSessionTaskHandler } from '../../../src/workflow/tasks/agent-session-task-handler';

const { executeMock, closeMock, createMock } = vi.hoisted(() => ({
  executeMock: vi.fn(),
  closeMock: vi.fn(),
  createMock: vi.fn(),
}));

vi.mock('../../../src/agent-runtime', () => ({
  AgentSession: {
    create: createMock,
  },
  createWorkflowSessionObservers: vi.fn().mockReturnValue([]),
}));

function makeContext(): StageContext {
  return {
    issue: {
      id: 'issue-1',
      number: 159,
      title: 'Test Issue',
      body: 'Test body',
      stage: Stage.Plan,
      status: IssueStatus.Active,
      projectId: 'project-1',
      labels: [],
      priority: 'p1',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
    acpOptions: { cwd: '/tmp/worktree' } as any,
    artifactManager: {} as any,
    worktreeManager: {} as any,
    projectRepo: {} as any,
    eventBus: { emit: vi.fn() } as any,
    checkpointManager: {} as any,
    issueRepo: {} as any,
    workflowLogRepo: undefined,
    sessionStreamLogRepo: undefined,
    coderSessionRepo: undefined,
    stageExecutionRepo: undefined,
    checkSuiteRepo: undefined,
    stageStateService: undefined,
    workflowRunService: undefined,
    workflowApplicationService: undefined,
    workflowRun: undefined,
    requestedWork: undefined,
    requestedTask: undefined,
    signal: undefined,
    emit: vi.fn(),
    log: vi.fn(),
  };
}

describe('AgentSessionTaskHandler', () => {
  beforeEach(() => {
    executeMock.mockReset();
    closeMock.mockReset();
    createMock.mockReset();
    createMock.mockResolvedValue({
      execute: executeMock,
      close: closeMock,
    });
    closeMock.mockResolvedValue(undefined);
  });

  it('normalizes success result with stage_task_update events', async () => {
    executeMock.mockResolvedValue({
      success: true,
      text: 'done',
      acpSessionId: 'ses-123',
    });

    const handler = createAgentSessionTaskHandler();
    const ctx = makeContext();
    const input: AgentSessionTaskInput = {
      taskId: 'plan-artifact-task',
      title: 'Plan artifact task',
      prompt: 'Generate plan artifacts',
      cwd: '/tmp/worktree',
      stage: 'plan',
      attempt: 1,
    };

    const result = await handler(input, ctx);

    expect(result).toMatchObject({
      taskId: 'plan-artifact-task',
      title: 'Plan artifact task',
      status: 'completed',
      attempts: 1,
      output: expect.objectContaining({
        kind: 'agent-session-task',
        stage: 'plan',
        success: true,
        acpSessionId: 'ses-123',
      }),
    });

    expect(ctx.eventBus.emit).toHaveBeenCalledWith(
      'stage_task_update',
      expect.objectContaining({ taskId: 'plan-artifact-task', status: 'started' }),
    );
    expect(ctx.eventBus.emit).toHaveBeenCalledWith(
      'stage_task_update',
      expect.objectContaining({ taskId: 'plan-artifact-task', status: 'completed' }),
    );
    expect(createMock).toHaveBeenCalledWith(
      expect.objectContaining({
        cwd: '/tmp/worktree',
        stage: 'plan',
        title: 'Plan artifact task',
      }),
    );
    expect(executeMock).toHaveBeenCalledWith('Generate plan artifacts', { kind: 'task', title: 'Plan artifact task' });
  });

  it('normalizes failure result with stage_task_update events', async () => {
    executeMock.mockResolvedValue({
      success: false,
      error: 'Session failed',
      failureKind: 'session_failed',
    });

    const handler = createAgentSessionTaskHandler();
    const ctx = makeContext();
    const input: AgentSessionTaskInput = {
      taskId: 'plan-artifact-task',
      title: 'Plan artifact task',
      prompt: 'Generate plan artifacts',
      cwd: '/tmp/worktree',
      stage: 'plan',
      attempt: 2,
    };

    const result = await handler(input, ctx);

    expect(result).toMatchObject({
      taskId: 'plan-artifact-task',
      title: 'Plan artifact task',
      status: 'failed',
      attempts: 2,
      output: expect.objectContaining({
        kind: 'agent-session-task',
        success: false,
        error: 'Session failed',
      }),
    });

    expect(ctx.eventBus.emit).toHaveBeenCalledWith(
      'stage_task_update',
      expect.objectContaining({ taskId: 'plan-artifact-task', status: 'failed' }),
    );
  });

  it('handles retry-after-missing-artifact style result by returning completed with artifact verification', async () => {
    executeMock.mockResolvedValue({
      success: true,
      text: 'created artifacts',
      acpSessionId: 'ses-456',
    });

    const verifyArtifacts = vi.fn().mockReturnValue(['proposal.md', 'design.md']);
    const handler = createAgentSessionTaskHandler();
    const ctx = makeContext();
    const input: AgentSessionTaskInput = {
      taskId: 'plan-artifact-task',
      title: 'Plan artifact task',
      prompt: 'Generate plan artifacts',
      cwd: '/tmp/worktree',
      stage: 'plan',
      attempt: 1,
      artifactVerification: verifyArtifacts,
    };

    const result = await handler(input, ctx);

    expect(result.status).toBe('completed');
    expect(result.artifacts).toEqual(['proposal.md', 'design.md']);
    expect(verifyArtifacts).toHaveBeenCalled();
  });

  it('returns code.changed when the worktree signature changes', async () => {
    executeMock.mockResolvedValue({
      success: true,
      text: 'done',
      acpSessionId: 'ses-code-changed',
    });
    const ctx = makeContext();
    ctx.worktreeManager = {
      getHeadSha: vi.fn().mockResolvedValue('same-head'),
      isWorktreeClean: vi.fn().mockResolvedValue(false),
      getWorktreeChangeSignature: vi.fn()
        .mockResolvedValueOnce(' M existing.ts')
        .mockResolvedValueOnce(' M existing.ts\n M new-change.ts'),
    } as any;

    const handler = createAgentSessionTaskHandler();
    const result = await handler({
      taskId: 'fix-review-findings',
      title: 'Fix review findings',
      prompt: 'Fix review findings',
      cwd: '/tmp/worktree',
      stage: 'check',
      attempt: 1,
    }, ctx);

    expect(result.events).toEqual(['code.changed']);
  });

  it('returns custom events from explicit workflow event markers', async () => {
    executeMock.mockResolvedValue({
      success: true,
      text: '<workflow-event>docs.updated</workflow-event>\n<workflow-event>unknown.event</workflow-event>',
      acpSessionId: 'ses-custom-event',
    });

    const handler = createAgentSessionTaskHandler();
    const result = await handler({
      taskId: 'docs-task',
      title: 'Docs task',
      prompt: 'Update docs',
      cwd: '/tmp/worktree',
      stage: 'build',
      attempt: 1,
    }, makeContext());
    expect(result.events).toEqual(['docs.updated', 'unknown.event']);
  });

  it('returns custom events from JSON output events', async () => {
    executeMock.mockResolvedValue({
      success: true,
      text: JSON.stringify({ events: ['docs.updated', 'unknown.event'] }),
      acpSessionId: 'ses-json-event',
    });

    const handler = createAgentSessionTaskHandler();
    const result = await handler({
      taskId: 'docs-task',
      title: 'Docs task',
      prompt: 'Update docs',
      cwd: '/tmp/worktree',
      stage: 'build',
      attempt: 1,
    }, makeContext());
    expect(result.events).toEqual(['docs.updated', 'unknown.event']);
  });

  it('continues the same agent session when a required marker is missing', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-required-marker-'));
    const markerPath = path.join(tempDir, 'self-review.md');
    executeMock.mockImplementation(async (_prompt: string) => {
      if (executeMock.mock.calls.length === 2) {
        fs.writeFileSync(markerPath, '<promise>PASS</promise>', 'utf-8');
      }
      return {
        success: true,
        text: 'done',
        acpSessionId: 'ses-marker',
      };
    });

    try {
      const handler = createAgentSessionTaskHandler();
      const ctx = makeContext();
      const input: AgentSessionTaskInput = {
        taskId: 'self-review',
        title: 'Self review',
        prompt: 'Generate self review',
        cwd: tempDir,
        stage: 'plan',
        attempt: 1,
        requiredMarkers: [
          {
            path: markerPath,
            markers: ['<promise>PASS</promise>', '<promise>FAIL</promise>'],
            onMissing: { action: 'continue-session', maxAttempts: 1 },
          },
        ],
      };

      const result = await handler(input, ctx);

      expect(result.status).toBe('completed');
      expect(executeMock).toHaveBeenCalledTimes(2);
      expect(executeMock.mock.calls[1][0]).toContain('Allowed markers: <promise>PASS</promise>, <promise>FAIL</promise>');
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('fails an agent task when a required marker remains missing after configured attempts', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-missing-marker-'));
    const markerPath = path.join(tempDir, 'self-review.md');
    executeMock.mockResolvedValue({
      success: true,
      text: 'done',
      acpSessionId: 'ses-marker',
    });

    try {
      const handler = createAgentSessionTaskHandler();
      const ctx = makeContext();
      const input: AgentSessionTaskInput = {
        taskId: 'self-review',
        title: 'Self review',
        prompt: 'Generate self review',
        cwd: tempDir,
        stage: 'plan',
        attempt: 1,
        requiredMarkers: [
          {
            path: markerPath,
            markers: ['<promise>PASS</promise>', '<promise>FAIL</promise>'],
            onMissing: { action: 'continue-session', maxAttempts: 1 },
          },
        ],
      };

      const result = await handler(input, ctx);

      expect(result.status).toBe('failed');
      expect(result.output).toMatchObject({
        kind: 'agent-session-task',
        success: true,
        error: expect.stringContaining('Missing required marker'),
      });
      expect(executeMock).toHaveBeenCalledTimes(2);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('does not accept duplicate promise markers as a satisfied required marker', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-duplicate-marker-'));
    const markerPath = path.join(tempDir, 'self-review.md');
    executeMock.mockImplementation(async () => {
      fs.writeFileSync(markerPath, '<promise>PASS</promise>\n<promise>FAIL</promise>', 'utf-8');
      return {
        success: true,
        text: 'done',
        acpSessionId: 'ses-marker',
      };
    });

    try {
      const handler = createAgentSessionTaskHandler();
      const ctx = makeContext();
      const input: AgentSessionTaskInput = {
        taskId: 'self-review',
        title: 'Self review',
        prompt: 'Generate self review',
        cwd: tempDir,
        stage: 'plan',
        attempt: 1,
        requiredMarkers: [
          {
            path: markerPath,
            markers: ['<promise>PASS</promise>', '<promise>FAIL</promise>'],
            onMissing: { action: 'continue-session', maxAttempts: 1 },
          },
        ],
      };

      const result = await handler(input, ctx);

      expect(result.status).toBe('failed');
      expect(executeMock).toHaveBeenCalledTimes(2);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('emits started then completed/failed stage_task_update events in correct order', async () => {
    const emitCalls: string[] = [];
    const ctx = makeContext();
    ctx.eventBus.emit = vi.fn().mockImplementation((event: string, data: any) => {
      if (event === 'stage_task_update') {
        emitCalls.push(data.status);
      }
    });

    executeMock.mockResolvedValue({
      success: true,
      text: 'done',
      acpSessionId: 'ses-789',
    });

    const handler = createAgentSessionTaskHandler();
    const input: AgentSessionTaskInput = {
      taskId: 'plan-artifact-task',
      title: 'Plan artifact task',
      prompt: 'Generate plan artifacts',
      cwd: '/tmp/worktree',
      stage: 'plan',
      attempt: 1,
    };

    await handler(input, ctx);

    expect(emitCalls).toEqual(['started', 'completed']);
  });

  it('closes session in finally block even on exception', async () => {
    createMock.mockResolvedValue({
      execute: executeMock,
      close: closeMock,
    });
    executeMock.mockRejectedValue(new Error('Unexpected error'));

    const handler = createAgentSessionTaskHandler();
    const ctx = makeContext();
    const input: AgentSessionTaskInput = {
      taskId: 'plan-artifact-task',
      title: 'Plan artifact task',
      prompt: 'Generate plan artifacts',
      cwd: '/tmp/worktree',
      stage: 'plan',
      attempt: 1,
    };

    const result = await handler(input, ctx);

    expect(result.status).toBe('failed');
    expect(closeMock).toHaveBeenCalled();
  });
});
