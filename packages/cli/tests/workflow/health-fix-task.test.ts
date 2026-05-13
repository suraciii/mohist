import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus } from '../../src/types';
import type { StageContext } from '../../src/workflow/stage-context';
import { EventBus } from '../../src/services/event-bus';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

const executeMock = vi.fn();
const closeMock = vi.fn();
const createMock = vi.fn();

vi.mock('../../src/agent-runtime/agent-session', () => ({
  AgentSession: {
    create: createMock,
  },
}));

vi.mock('../../src/agent-runtime', () => ({
  createWorkflowSessionObservers: vi.fn().mockReturnValue([]),
}));

function makeContext(changeDir?: string): StageContext {
  return {
    issue: {
      id: 'issue-1',
      number: 159,
      title: 'Test Issue',
      body: 'Fix the build without changing the requested behavior.',
      stage: Stage.Build,
      status: IssueStatus.Active,
      projectId: 'project-1',
      labels: [],
      priority: 'p1',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
    acpOptions: { cwd: '/tmp/worktree', model: 'test-model' } as any,
    artifactManager: {
      getChangeDir: vi.fn().mockReturnValue(changeDir ?? null),
    } as any,
    worktreeManager: {} as any,
    projectRepo: {} as any,
    eventBus: new EventBus() as any,
    checkpointManager: {} as any,
    issueRepo: {} as any,
  };
}

describe('runHealthFixTask', () => {
  beforeEach(() => {
    executeMock.mockReset();
    closeMock.mockReset();
    createMock.mockReset();
    createMock.mockResolvedValue({
      execute: executeMock,
      close: closeMock,
    });
    executeMock.mockResolvedValue({
      success: true,
      text: 'fixed',
      acpSessionId: 'ses-health-fix',
    });
    closeMock.mockResolvedValue(undefined);
  });

  it('runs health fix as an explicit stage task with transient output and no artifacts', async () => {
    const { runHealthFixTask } = await import('../../src/workflow/health-fix-task');
    const changeDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-health-fix-'));
    fs.writeFileSync(path.join(changeDir, 'proposal.md'), '# Proposal');
    fs.writeFileSync(path.join(changeDir, 'design.md'), '# Design');
    fs.writeFileSync(path.join(changeDir, 'tasks.json'), '{"tasks":[]}');
    const ctx = makeContext(changeDir);
    const emitSpy = vi.spyOn(ctx.eventBus, 'emit');

    try {
      const result = await runHealthFixTask(ctx, {
        taskId: 'fix-build-health',
        title: 'Fix build health',
        stage: 'build',
        worktreePath: '/tmp/worktree',
        healthCommand: 'npm run build',
        failedCheck: {
          name: 'health:build',
          status: 'fail',
          message: 'npm run build failed',
          output: { logExcerpt: 'TypeScript error' },
        },
        attempt: 1,
      });

      expect(result).toMatchObject({
        taskId: 'fix-build-health',
        title: 'Fix build health',
        status: 'completed',
        artifacts: [],
        attempts: 1,
        output: {
          kind: 'health-fix-task',
          stage: 'build',
          checkName: 'health:build',
          healthCommand: 'npm run build',
          success: true,
          acpSessionId: 'ses-health-fix',
        },
      });
      expect(createMock).toHaveBeenCalledWith(expect.objectContaining({
        cwd: '/tmp/worktree',
        issueId: 'issue-1',
        issueNumber: 159,
        stage: 'build',
        title: 'Fix build health',
      }));
      const prompt = executeMock.mock.calls[0][0] as string;
      expect(prompt).toContain('<mohist-task>');
      expect(prompt).toContain('Issue #159: Test Issue');
      expect(prompt).toContain('Fix the build without changing the requested behavior.');
      expect(prompt).toContain(`@${path.join(changeDir, 'proposal.md')}`);
      expect(prompt).toContain(`@${path.join(changeDir, 'design.md')}`);
      expect(prompt).toContain(`@${path.join(changeDir, 'tasks.json')}`);
      expect(prompt).toContain('Health command: npm run build');
      expect(executeMock).toHaveBeenCalledWith(
        expect.any(String),
        { kind: 'recovery', title: 'Fix build health' },
      );
      expect(emitSpy).toHaveBeenCalledWith('stage_task_update', expect.objectContaining({
        taskId: 'fix-build-health',
        status: 'started',
        artifacts: [],
      }));
      expect(emitSpy).toHaveBeenCalledWith('stage_task_update', expect.objectContaining({
        taskId: 'fix-build-health',
        status: 'completed',
        artifacts: [],
      }));
    } finally {
      fs.rmSync(changeDir, { recursive: true, force: true });
    }
  });
});
