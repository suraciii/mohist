import { describe, expect, it, vi } from 'vitest';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { Stage, IssueStatus } from '../../../src/types';
import type { StageContext } from '../../../src/workflow/stage-context';
import type { ExecutableTask, StageTaskResult } from '../../../src/workflow/tasks';
import { createDefaultTaskDispatchFactoryRegistry, createTaskDispatchFactoryRegistry } from '../../../src/workflow/tasks';
import type { AgentSessionTaskInput, ProviderTaskInput } from '../../../src/workflow/tasks/types';
import type { TaskDefinition } from '../../../src/workflow/model';

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

function aiReviewSourceTask(): TaskDefinition {
  return {
    id: 'ai-review',
    title: 'AI review',
    uses: 'mohist/check/ai-review',
    with: {},
  };
}

function pendingAiReviewTask(
  input: {
    causedBy?: StageContext['requestedTask']['causedBy'];
    resetBy?: StageContext['requestedTask']['resetBy'];
  } = {},
): StageContext['requestedTask'] {
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
    causedBy: input.causedBy ?? null,
    resetBy: input.resetBy ?? null,
    latestAttempt: null,
  };
}

function createRegistryWithFakeAgentHandler() {
  const agentSessionHandler = vi.fn(async (input: AgentSessionTaskInput): Promise<StageTaskResult> => ({
    taskId: input.taskId,
    title: input.title,
    status: 'completed',
    artifacts: input.artifactVerification?.([]) ?? [],
    attempts: input.attempt,
    duration: 1,
    events: [],
  }));
  return {
    registry: createDefaultTaskDispatchFactoryRegistry({ agentSessionHandler }),
    agentSessionHandler,
  };
}

describe('DefaultTaskDispatchFactoryRegistry restore behavior', () => {
  it('dispatches custom task uses through a registered provider', () => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-dispatch-provider-'));
    const changeDir = path.join(tmpRoot, 'openspec', 'changes', '159-test');
    fs.mkdirSync(changeDir, { recursive: true });

    try {
      const provider = {
        id: 'acme/custom-task',
        build: vi.fn((input) => ({
          taskId: input.task.taskId,
          title: input.task.title,
          kind: 'service-call' as const,
          stage: input.ctx.issue.stage,
          attempt: input.attempt,
          serviceFn: async () => ({ custom: true }),
        })),
      };
      const task: ExecutableTask = { taskId: 'custom', title: 'Custom task', kind: 'agent-session' };
      const dispatchable = createTaskDispatchFactoryRegistry([provider]).build({
        ctx: makeContext(changeDir),
        task,
        executionKind: 'agent-session',
        attempt: 2,
        worktreePath: tmpRoot,
        sourceTask: {
          id: 'custom',
          title: 'Custom task',
          uses: 'acme/custom-task',
          with: { message: 'hello' },
        },
      });

      expect(provider.build).toHaveBeenCalledOnce();
      expect(dispatchable).toMatchObject({
        taskId: 'custom',
        kind: 'service-call',
        attempt: 2,
      });
    } finally {
      fs.rmSync(tmpRoot, { recursive: true, force: true });
    }
  });

  it('restores a normal pending ai-review task from checkpoint and artifact', () => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-dispatch-restore-'));
    const changeDir = path.join(tmpRoot, 'openspec', 'changes', '159-test');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'review.md'), '# Review\n<promise>PASS</promise>\n');

    try {
      const task: ExecutableTask = { taskId: 'ai-review', title: 'AI review', kind: 'agent-session' };
      const { registry } = createRegistryWithFakeAgentHandler();
      const dispatchable = registry.build({
        ctx: makeContext(changeDir, pendingAiReviewTask()),
        task,
        executionKind: 'agent-session',
        attempt: 1,
        worktreePath: tmpRoot,
        sourceTask: aiReviewSourceTask(),
      });

      expect(dispatchable).toMatchObject({
        taskId: 'ai-review',
        kind: 'service-call',
      });
    } finally {
      fs.rmSync(tmpRoot, { recursive: true, force: true });
    }
  });

  it('reruns a workflow-policy-reset ai-review task instead of restoring stale review output', async () => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-dispatch-reset-'));
    const changeDir = path.join(tmpRoot, 'openspec', 'changes', '159-test');
    fs.mkdirSync(changeDir, { recursive: true });
    const reviewPath = path.join(changeDir, 'review.md');
    fs.writeFileSync(reviewPath, '# Stale review\n<promise>PASS</promise>\n');

    try {
      const task: ExecutableTask = { taskId: 'ai-review', title: 'AI review', kind: 'agent-session' };
      const { registry, agentSessionHandler } = createRegistryWithFakeAgentHandler();
      const dispatchable = registry.build({
        ctx: makeContext(changeDir, pendingAiReviewTask({
          resetBy: {
            type: 'workflow-policy',
            taskId: 'fix-review-findings',
            eventName: 'code.changed',
            message: 'code.changed reset',
          },
        })),
        task,
        executionKind: 'agent-session',
        attempt: 1,
        worktreePath: tmpRoot,
        sourceTask: aiReviewSourceTask(),
      });

      expect(dispatchable).toMatchObject({
        taskId: 'ai-review',
        kind: 'provider-task',
      });
      expect(fs.existsSync(reviewPath)).toBe(true);
      if (!dispatchable || dispatchable.kind !== 'provider-task') {
        throw new Error('Expected provider-task');
      }
      const providerInput = dispatchable.input as ProviderTaskInput;
      const result = await providerInput.run(makeContext(changeDir));
      expect(fs.existsSync(reviewPath)).toBe(false);
      expect(result.status).toBe('completed');
      expect(agentSessionHandler).toHaveBeenCalledOnce();
    } finally {
      fs.rmSync(tmpRoot, { recursive: true, force: true });
    }
  });

  it('does not remove review output when restoring ai-review from checkpoint', () => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-dispatch-restore-keeps-review-'));
    const changeDir = path.join(tmpRoot, 'openspec', 'changes', '159-test');
    fs.mkdirSync(changeDir, { recursive: true });
    const reviewPath = path.join(changeDir, 'review.md');
    fs.writeFileSync(reviewPath, '# Review\n<promise>PASS</promise>\n');

    try {
      const task: ExecutableTask = { taskId: 'ai-review', title: 'AI review', kind: 'agent-session' };
      const { registry } = createRegistryWithFakeAgentHandler();
      const dispatchable = registry.build({
        ctx: makeContext(changeDir, pendingAiReviewTask()),
        task,
        executionKind: 'agent-session',
        attempt: 1,
        worktreePath: tmpRoot,
        sourceTask: aiReviewSourceTask(),
      });

      expect(dispatchable).toMatchObject({
        taskId: 'ai-review',
        kind: 'service-call',
      });
      expect(fs.existsSync(reviewPath)).toBe(true);
    } finally {
      fs.rmSync(tmpRoot, { recursive: true, force: true });
    }
  });
});
