import { describe, expect, it, vi } from 'vitest';
import fs from 'fs';
import os from 'node:os';
import path from 'node:path';
import { Stage, IssueStatus } from '../../../../src/types';
import type { StageContext } from '../../../../src/workflow/stage-context';
import type { ExecutableTask } from '../../../../src/workflow/tasks';
import type { StageTaskResult } from '../../../../src/workflow/stage-context';
import { createBuiltinTaskDispatchRegistry, createMohistBuiltinTaskDispatchRegistry } from '../../../../src/workflow/builtins/tasks';
import type { AgentSessionTaskInput } from '../../../../src/workflow/builtins/tasks';
import type { TaskDefinition } from '../../../../src/workflow/model';

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
    registry: createMohistBuiltinTaskDispatchRegistry({ agentSessionHandler }),
    agentSessionHandler,
  };
}

describe('Mohist builtin task dispatch registry restore behavior', () => {
  it('dispatches custom task uses through a registered provider', async () => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-dispatch-provider-'));
    const changeDir = path.join(tmpRoot, 'openspec', 'changes', '159-test');
    fs.mkdirSync(changeDir, { recursive: true });

    try {
      const provider = {
        id: 'acme/custom-task',
        run: vi.fn(async (input) => ({
          taskId: input.task.taskId,
          title: input.task.title,
          status: 'completed' as const,
          attempts: input.attempt,
          duration: 1,
          artifacts: [],
          events: [],
          output: { custom: true },
        })),
      };
      const task: ExecutableTask = { taskId: 'custom', title: 'Custom task' };
      const result = await createBuiltinTaskDispatchRegistry([provider]).run({
        ctx: makeContext(changeDir),
        task,
        attempt: 2,
        worktreePath: tmpRoot,
        sourceTask: {
          id: 'custom',
          title: 'Custom task',
          uses: 'acme/custom-task',
          with: { message: 'hello' },
        },
      });

      expect(provider.run).toHaveBeenCalledOnce();
      expect(result).toMatchObject({
        taskId: 'custom',
        attempts: 2,
        status: 'completed',
      });
    } finally {
      fs.rmSync(tmpRoot, { recursive: true, force: true });
    }
  });

  it('restores a normal pending ai-review task from checkpoint and artifact', async () => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-dispatch-restore-'));
    const changeDir = path.join(tmpRoot, 'openspec', 'changes', '159-test');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'review.md'), '# Review\n<promise>PASS</promise>\n');

    try {
      const task: ExecutableTask = { taskId: 'ai-review', title: 'AI review' };
      const { registry } = createRegistryWithFakeAgentHandler();
      const result = await registry.run({
        ctx: makeContext(changeDir, pendingAiReviewTask()),
        task,
        attempt: 1,
        worktreePath: tmpRoot,
        sourceTask: aiReviewSourceTask(),
      });

      expect(result).toMatchObject({
        taskId: 'ai-review',
        status: 'completed',
        output: { restoredFromCheckpoint: true },
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
      const task: ExecutableTask = { taskId: 'ai-review', title: 'AI review' };
      const { registry, agentSessionHandler } = createRegistryWithFakeAgentHandler();
      const result = await registry.run({
        ctx: makeContext(changeDir, pendingAiReviewTask({
          resetBy: {
            type: 'workflow-policy',
            taskId: 'fix-review-findings',
            eventName: 'code.changed',
            message: 'code.changed reset',
          },
        })),
        task,
        attempt: 1,
        worktreePath: tmpRoot,
        sourceTask: aiReviewSourceTask(),
      });

      expect(result).toMatchObject({
        taskId: 'ai-review',
        status: 'completed',
      });
      expect(fs.existsSync(reviewPath)).toBe(false);
      expect(agentSessionHandler).toHaveBeenCalledOnce();
    } finally {
      fs.rmSync(tmpRoot, { recursive: true, force: true });
    }
  });

  it('does not remove review output when restoring ai-review from checkpoint', async () => {
    const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-dispatch-restore-keeps-review-'));
    const changeDir = path.join(tmpRoot, 'openspec', 'changes', '159-test');
    fs.mkdirSync(changeDir, { recursive: true });
    const reviewPath = path.join(changeDir, 'review.md');
    fs.writeFileSync(reviewPath, '# Review\n<promise>PASS</promise>\n');

    try {
      const task: ExecutableTask = { taskId: 'ai-review', title: 'AI review' };
      const { registry } = createRegistryWithFakeAgentHandler();
      const result = await registry.run({
        ctx: makeContext(changeDir, pendingAiReviewTask()),
        task,
        attempt: 1,
        worktreePath: tmpRoot,
        sourceTask: aiReviewSourceTask(),
      });

      expect(result).toMatchObject({
        taskId: 'ai-review',
        status: 'completed',
      });
      expect(fs.existsSync(reviewPath)).toBe(true);
    } finally {
      fs.rmSync(tmpRoot, { recursive: true, force: true });
    }
  });

  it('renders custom agent prompt files from inside the worktree', async () => {
    const worktreePath = path.join(path.sep, 'fake', 'worktree');
    const changeDir = path.join(worktreePath, 'openspec', 'changes', '159-test');
    const promptPath = path.join(worktreePath, '.mohist', 'prompts', 'review.md');
    const readFile = vi.fn((filePath: string, encoding: BufferEncoding) => {
      expect(filePath).toBe(promptPath);
      expect(encoding).toBe('utf-8');
      return 'Review {{ issue.number }} in {{ openspec.changeDir }}.';
    });

    const agentSessionHandler = vi.fn(async (input: AgentSessionTaskInput): Promise<StageTaskResult> => ({
      taskId: input.taskId,
      title: input.title,
      status: 'completed',
      artifacts: [],
      attempts: input.attempt,
      duration: 1,
      events: [],
    }));
    const registry = createMohistBuiltinTaskDispatchRegistry({ agentSessionHandler, readFile });
    const task: ExecutableTask = { taskId: 'custom-review', title: 'Custom review' };
    const result = await registry.run({
      ctx: makeContext(changeDir),
      task,
      attempt: 1,
      worktreePath,
      sourceTask: {
        id: 'custom-review',
        title: 'Custom review',
        uses: 'mohist/agent',
        with: { prompt: { file: '.mohist/prompts/review.md' } },
      },
    });

    expect(result.status).toBe('completed');
    expect(agentSessionHandler).toHaveBeenCalledWith(expect.objectContaining({
      prompt: `Review 159 in ${changeDir}.`,
    }), expect.anything());
    expect(readFile).toHaveBeenCalledWith(promptPath, 'utf-8');
  });

  it('rejects custom agent prompt files outside the worktree', async () => {
    const worktreePath = path.join(path.sep, 'fake', 'worktree');
    const changeDir = path.join(worktreePath, 'openspec', 'changes', '159-test');
    const task: ExecutableTask = { taskId: 'custom-review', title: 'Custom review' };
    const { registry } = createRegistryWithFakeAgentHandler();

    expect(() => registry.run({
      ctx: makeContext(changeDir),
      task,
      attempt: 1,
      worktreePath,
      sourceTask: {
        id: 'custom-review',
        title: 'Custom review',
        uses: 'mohist/agent',
        with: { prompt: { file: '../outside-prompt.md' } },
      },
    })).toThrow("Agent prompt file '../outside-prompt.md' is outside worktree");
  });
});
