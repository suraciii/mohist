import { describe, expect, it, vi } from 'vitest';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { Stage, IssueStatus } from '../../../src/types';
import type { StageContext } from '../../../src/workflow/stage-context';
import type { ExecutableTask } from '../../../src/workflow/task-runtime';
import { createDefaultTaskDispatchFactoryRegistry } from '../../../src/workflow/task-runtime';

function makeContext(changeDir: string, requestedTask?: StageContext['requestedTask']): StageContext {
  return {
    issue: {
      id: 'issue-1',
      number: 159,
      title: 'Test Issue',
      body: 'Test body',
      stage: Stage.Check,
      status: IssueStatus.Active,
      projectId: 'project-1',
      labels: [],
      priority: 'p1',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
    acpOptions: { cwd: path.dirname(path.dirname(path.dirname(changeDir))) } as any,
    artifactManager: {
      getChangeDir: vi.fn().mockReturnValue(changeDir),
      createChangeDir: vi.fn().mockReturnValue(changeDir),
    } as any,
    worktreeManager: {} as any,
    projectRepo: {} as any,
    eventBus: { emit: vi.fn() } as any,
    checkpointManager: {
      getResumeSteps: vi.fn().mockReturnValue(['ai-review']),
      markStepComplete: vi.fn(),
      deleteStep: vi.fn(),
      delete: vi.fn(),
    } as any,
    issueRepo: {} as any,
    workflowRunService: undefined,
    workflowApplicationService: undefined,
    workflowRun: undefined,
    requestedWork: { kind: 'task', stage: Stage.Check, taskId: 'ai-review' },
    requestedTask,
    emit: vi.fn(),
    log: vi.fn(),
  };
}

function pendingAiReviewTask(causedBy: StageContext['requestedTask']['causedBy'] = null): StageContext['requestedTask'] {
  return {
    id: 'ai-review',
    title: 'AI review',
    status: 'pending',
    order: 0,
    dependsOn: [],
    attempts: 0,
    duration: 0,
    artifacts: [],
    events: [],
    output: null,
    reason: null,
    causedBy,
    latestAttempt: null,
  };
}

describe('DefaultTaskDispatchFactoryRegistry restore behavior', () => {
  it('restores a normal pending ai-review task from checkpoint and artifact', () => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-dispatch-restore-'));
    const changeDir = path.join(tmpRoot, 'openspec', 'changes', '159-test');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'review.md'), '# Review\n<promise>PASS</promise>\n');

    try {
      const task: ExecutableTask = { taskId: 'ai-review', title: 'AI review', kind: 'agent-session' };
      const dispatchable = createDefaultTaskDispatchFactoryRegistry().build({
        ctx: makeContext(changeDir, pendingAiReviewTask()),
        task,
        executionKind: 'agent-session',
        attempt: 1,
        worktreePath: tmpRoot,
      });

      expect(dispatchable).toMatchObject({
        taskId: 'ai-review',
        kind: 'service-call',
      });
    } finally {
      fs.rmSync(tmpRoot, { recursive: true, force: true });
    }
  });

  it('reruns a workflow-policy-reset ai-review task instead of restoring stale review output', () => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-dispatch-reset-'));
    const changeDir = path.join(tmpRoot, 'openspec', 'changes', '159-test');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'review.md'), '# Stale review\n<promise>PASS</promise>\n');

    try {
      const task: ExecutableTask = { taskId: 'ai-review', title: 'AI review', kind: 'agent-session' };
      const dispatchable = createDefaultTaskDispatchFactoryRegistry().build({
        ctx: makeContext(changeDir, pendingAiReviewTask({
          type: 'system-policy',
          taskId: 'fix-review-findings',
          message: 'code.changed reset',
        })),
        task,
        executionKind: 'agent-session',
        attempt: 1,
        worktreePath: tmpRoot,
      });

      expect(dispatchable).toMatchObject({
        taskId: 'ai-review',
        kind: 'agent-session',
      });
    } finally {
      fs.rmSync(tmpRoot, { recursive: true, force: true });
    }
  });
});
