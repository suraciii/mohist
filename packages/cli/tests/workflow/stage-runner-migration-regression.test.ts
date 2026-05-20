import { describe, it, expect, vi, beforeEach } from 'vitest';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { Stage, MergeState } from '../../src/types';
import type { StageContext } from '../../src/workflow/stage-context';
import type { StageRunner } from '../../src/workflow/stage-runner';
import type { CheckRegistry, CheckContext } from '../../src/workflow/checks';
import type { TaskLoaderRegistry, TaskHandlerRegistry, ExecutableTask } from '../../src/workflow/task-runtime';
import type { StageDefinition, WorkflowRun as DomainWorkflowRun } from '../../src/workflow/domain';
import { GenericStageRunner, GENERIC_STAGE_RUNNER_REQUIRES_WORK_MESSAGE } from '../../src/workflow/generic-stage-runner';
import { WorkflowEngine } from '../../src/workflow/workflow-engine';
import { createAgentSessionTaskHandler } from '../../src/workflow/task-runtime/agent-session-task-handler';
import { createRalphTaskHandler } from '../../src/workflow/task-runtime/ralph-task-handler';
import { createRalphTaskLoader } from '../../src/workflow/task-runtime/ralph-task-loader';
import { defaultServiceCallTaskHandler } from '../../src/workflow/task-runtime/service-call-task-handler';
import { EventBus } from '../../src/services/event-bus';
import { WorkflowRun } from '../../src/workflow/domain';
import * as RalphExecutor from '../../src/openspec/ralph-executor';

function mergeReadyOutput(candidateHeadSha: string): Record<string, unknown> {
  return {
    kind: 'merge-ready',
    targetBranch: 'master',
    strategy: 'squash',
    baseSha: 'base-sha',
    candidateHeadSha,
    mergeBaseSha: 'base-sha',
    canMerge: true,
    conflictFiles: [],
  };
}

function makeMockContext(stage: Stage = Stage.Integrate, overrides?: Partial<StageContext>): StageContext {
  const eventBus = new EventBus();
  const emitSpy = vi.fn();
  vi.spyOn(eventBus, 'emit').mockImplementation(emitSpy);

  return {
    issue: {
      id: 'issue-1',
      number: 1,
      title: 'Test Issue',
      body: '',
      stage,
      status: 'active' as const,
      projectId: 'proj-1',
      labels: [],
      priority: 'p2' as const,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
    acpOptions: { worktreePath: '/tmp' } as any,
    artifactManager: {
      getChangeDir: vi.fn().mockReturnValue('/tmp/change'),
      createChangeDir: vi.fn(),
      readArtifact: vi.fn(),
      writeArtifact: vi.fn(),
      exists: vi.fn().mockReturnValue(true),
      readTasks: vi.fn().mockReturnValue(null),
      updateTaskPasses: vi.fn(),
      archiveChange: vi.fn(),
    } as any,
    worktreeManager: {} as any,
    projectRepo: {
      findById: vi.fn().mockReturnValue({ id: 'proj-1', name: 'test-project', path: '/tmp', baseBranch: 'main' }),
    } as any,
    eventBus: eventBus as any,
    checkpointManager: {
      save: vi.fn(),
      load: vi.fn(),
      deleteAll: vi.fn(),
      getResumeSteps: vi.fn().mockReturnValue([]),
      upsert: vi.fn(),
      markStepComplete: vi.fn(),
      deleteStep: vi.fn(),
      delete: vi.fn(),
    } as any,
    issueRepo: {
      updateStage: vi.fn(),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      updateStatus: vi.fn(),
      findById: vi.fn(),
    } as any,
    stageExecutionRepo: {
      create: vi.fn().mockReturnValue({ id: 'exec-1' }),
      updateCheckResults: vi.fn(),
      appendTaskResult: vi.fn(),
      updateStatus: vi.fn(),
    } as any,
    workflowApplicationService: {
      completeTask: vi.fn(),
      recordCheckResult: vi.fn(),
      approveStage: vi.fn(),
    } as any,
    workflowRunService: {} as any,
    workflowRun: undefined,
    requestedWork: undefined,
    requestedTask: undefined,
    signal: undefined,
    emit: (event: string, data: unknown) => {
      try { eventBus.emit(event as keyof EventMap, data as never); } catch { /* fire-and-forget */ }
    },
    log: (_et: string, _d: object) => { /* fire-and-forget */ },
    ...overrides,
  } as StageContext;
}

function createBasicTaskLoaderRegistry(): TaskLoaderRegistry {
  return {
    get: vi.fn().mockImplementation((kind: string) => {
      if (kind === 'static') {
        return {
          kind: 'static' as const,
          load: (ctx: StageContext) => {
            const definition = createStageDefinition(ctx.issue.stage);
            return definition.tasks.map(task => ({
              taskId: task.id,
              title: task.title,
              kind: definition.taskExecutionPolicies?.find(policy => policy.taskId === task.id)?.kind ?? 'agent-session',
            }));
          },
        };
      }
      if (kind === 'ralph') {
        return {
          kind: 'ralph' as const,
          load: (ctx: StageContext) => {
            const stageRun = ctx.workflowRun?.stageRuns.find(candidate => candidate.stage === ctx.issue.stage);
            return (stageRun?.tasks ?? []).map(task => ({
              taskId: (task as any).id ?? (task as any).taskId,
              title: task.title,
              kind: 'ralph-task' as const,
              input: (task as any).id ?? (task as any).taskId,
            }));
          },
        };
      }
      return null;
    }),
    list: vi.fn().mockReturnValue([]),
  };
}

function createBasicTaskHandlerRegistry(handlers: Record<string, ReturnType<typeof vi.fn>> = {}): TaskHandlerRegistry {
  const map = new Map(Object.entries(handlers));
  return {
    get: (kind: string) => map.get(kind) ?? undefined,
    register: vi.fn(),
  };
}

function createBasicCheckRegistry(checks: Record<string, (ctx: CheckContext) => { name: string; status: 'pass' | 'fail' | 'error' | 'pending'; message?: string; output?: unknown }>): CheckRegistry {
  const map = new Map(Object.entries(checks));
  return {
    get: (name: string) => {
      const fn = map.get(name);
      if (!fn) return undefined;
      return () => Promise.resolve({
        name: name,
        async run(_ctx: CheckContext) { return fn(_ctx); },
      });
    },
    register: vi.fn(),
  };
}

function createStageDefinition(stage: Stage): StageDefinition {
  const defs: Record<Stage, StageDefinition> = {
    [Stage.Plan]: {
      stage: Stage.Plan,
      tasks: [
        { id: 'proposal', title: 'Generate proposal' },
        { id: 'specs', title: 'Write specs' },
        { id: 'design', title: 'Create design' },
        { id: 'tasks', title: 'Generate tasks' },
        { id: 'self-review', title: 'Self review' },
      ],
      checks: [
        { name: 'proposal-complete', title: 'Proposal complete' },
        { name: 'specs-complete', title: 'Specs complete' },
        { name: 'design-complete', title: 'Design complete' },
        { name: 'tasks-valid', title: 'Tasks valid' },
        { name: 'self-review-passed', title: 'Self review passed' },
        { name: 'health:plan', title: 'Plan health gate' },
      ],
      requiresApproval: true,
      approvalCheckName: 'user-approval',
      workSources: [{ kind: 'static', taskIds: ['proposal', 'specs', 'design', 'tasks', 'self-review'] }],
      taskExecutionPolicies: [
        { taskId: 'proposal', kind: 'agent-session' },
        { taskId: 'specs', kind: 'agent-session' },
        { taskId: 'design', kind: 'agent-session' },
        { taskId: 'tasks', kind: 'agent-session' },
        { taskId: 'self-review', kind: 'agent-session' },
      ],
      checkPolicies: [
        { checkName: 'proposal-complete', phase: 'post-task' },
        { checkName: 'specs-complete', phase: 'post-task' },
        { checkName: 'design-complete', phase: 'post-task' },
        { checkName: 'tasks-valid', phase: 'post-task' },
        { checkName: 'self-review-passed', phase: 'post-task' },
        { checkName: 'health:plan', phase: 'post-task' },
      ],
      approvalPolicy: { checkName: 'user-approval' },
      repairPolicies: [
        { checkName: 'self-review-passed', fixTaskId: 'fix-plan-review', fixTaskTitle: 'Fix plan review findings', maxAttempts: 1 },
      ],
      invalidationPolicy: { entries: [] },
    },
    [Stage.Build]: {
      stage: Stage.Build,
      tasks: [],
      checks: [{ name: 'health:build', title: 'Build health gate' }],
      workSources: [{ kind: 'ralph' }, { kind: 'runtime' }],
      taskExecutionPolicies: [],
      checkPolicies: [{ checkName: 'health:build', phase: 'post-task' }],
      repairPolicies: [{ checkName: 'health:build', fixTaskId: 'fix-build-health', fixTaskTitle: 'Fix build health', maxAttempts: 1 }],
      invalidationPolicy: { entries: [] },
    },
    [Stage.Check]: {
      stage: Stage.Check,
      on: {
        'code.changed': { reset: { tasks: ['ai-review'], checks: 'all', approval: true } },
      },
      tasks: [{ id: 'ai-review', title: 'AI review' }],
      checks: [
        {
          name: 'health:check',
          title: 'Check health gate',
          onFailure: {
            retry: {
              limit: 1,
              task: { id: 'fix-check-health', title: 'Fix check health', uses: 'mohist/agent' },
            },
          },
        },
        {
          name: 'review-passed',
          title: 'Review passed',
          onFailure: {
            retry: {
              limit: 1,
              task: {
                id: 'fix-review-findings',
                title: 'Fix review findings',
                uses: 'mohist/agent',
                with: { prompt: { inline: 'Fix findings in {{ openspec.changeDir }}/review.md' } },
              },
            },
          },
        },
        {
          name: 'merge-ready',
          title: 'Merge ready',
          onFailure: {
            retry: {
              limit: 1,
              task: { id: 'fix-merge-readiness', title: 'Fix merge readiness', uses: 'mohist/rebase' },
            },
          },
        },
      ],
      requiresApproval: true,
      approvalCheckName: 'user-approval',
      workSources: [{ kind: 'static', taskIds: ['ai-review'] }, { kind: 'runtime' }],
      taskExecutionPolicies: [
        { taskId: 'ai-review', kind: 'agent-session' },
        { taskId: 'fix-check-health', kind: 'repair-task', workSourceKind: 'runtime' },
        { taskId: 'fix-review-findings', kind: 'agent-session', workSourceKind: 'runtime' },
        { taskId: 'fix-merge-readiness', kind: 'rebase-task', workSourceKind: 'runtime' },
        { taskId: 'rebase-branch', kind: 'agent-session', workSourceKind: 'runtime' },
      ],
      checkPolicies: [
        { checkName: 'health:check', phase: 'post-task' },
        { checkName: 'review-passed', phase: 'post-task' },
        { checkName: 'merge-ready', phase: 'post-task' },
      ],
      approvalPolicy: { checkName: 'user-approval' },
      repairPolicies: [
        { checkName: 'health:check', fixTaskId: 'fix-check-health', fixTaskTitle: 'Fix check health', maxAttempts: 1 },
        { checkName: 'review-passed', fixTaskId: 'fix-review-findings', fixTaskTitle: 'Fix review findings', maxAttempts: 1 },
        { checkName: 'merge-ready', fixTaskId: 'fix-merge-readiness', fixTaskTitle: 'Fix merge readiness', maxAttempts: 1 },
      ],
      invalidationPolicy: {
        entries: [
          {
            trigger: 'task-completion',
            eventName: 'code.changed',
            reason: 'code.changed reset',
            invalidates: { tasks: ['ai-review'], checks: ['health:check', 'review-passed', 'merge-ready'], approval: true },
          },
        ],
      },
    },
    [Stage.Integrate]: {
      stage: Stage.Integrate,
      tasks: [
        { id: 'integrate:spec-sync', title: 'Spec sync' },
        { id: 'integrate:archive-change', title: 'Archive change' },
        { id: 'integrate:merge', title: 'Merge to main' },
      ],
      checks: [{ name: 'health:integrate', title: 'Integrate health gate' }],
      workSources: [{ kind: 'static', taskIds: ['integrate:spec-sync', 'integrate:archive-change', 'integrate:merge'] }],
      taskExecutionPolicies: [
        { taskId: 'integrate:spec-sync', kind: 'service-call' },
        { taskId: 'integrate:archive-change', kind: 'service-call' },
        { taskId: 'integrate:merge', kind: 'service-call' },
      ],
      checkPolicies: [{ checkName: 'health:integrate', phase: 'post-task' }],
      repairPolicies: [],
      invalidationPolicy: { entries: [] },
    },
    [Stage.Explore]: { stage: Stage.Explore, tasks: [], checks: [], workSources: [], taskExecutionPolicies: [], checkPolicies: [], repairPolicies: [], invalidationPolicy: { entries: [] } },
    [Stage.Done]: { stage: Stage.Done, tasks: [], checks: [], workSources: [], taskExecutionPolicies: [], checkPolicies: [], repairPolicies: [], invalidationPolicy: { entries: [] } },
    [Stage.Backlog]: { stage: Stage.Backlog, tasks: [], checks: [], workSources: [], taskExecutionPolicies: [], checkPolicies: [], repairPolicies: [], invalidationPolicy: { entries: [] } },
  };
  return defs[stage];
}

describe('StageRunner migration regression coverage', () => {
  describe('AC-1: generic stage execution sequences', () => {
    it('GenericStageRunner handles Integrate stage with service-call tasks', async () => {
      const taskHandler = vi.fn().mockImplementation(async (task: ExecutableTask, _ctx: StageContext) => {
        if (task.taskId === 'integrate:spec-sync' || task.taskId === 'integrate:archive-change' || task.taskId === 'integrate:merge') {
          return {
            taskId: task.taskId,
            title: task.title,
            status: 'completed',
            artifacts: [],
            attempts: 1,
            duration: 100,
            output: { success: true },
          };
        }
        return null;
      });

      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'service-call': taskHandler }),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: '/tmp',
      });

      const ctx = makeMockContext(Stage.Integrate);
      ctx.requestedWork = { kind: 'task', stage: Stage.Integrate, taskId: 'integrate:spec-sync' };
      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(taskHandler).toHaveBeenCalledTimes(1);
    });

    it('GenericStageRunner mirrors aggregate-rejected task completion as failed execution evidence', async () => {
      const taskHandler = vi.fn().mockResolvedValue({
        taskId: 'integrate:merge',
        title: 'Merge to main',
        status: 'completed',
        artifacts: [],
        attempts: 1,
        duration: 100,
        output: { targetBranch: 'main' },
      });
      const run = WorkflowRun.startWorkflow({
        id: 'run-reject-task',
        issueId: 'issue-1',
        issueNumber: 1,
        definitions: [createStageDefinition(Stage.Integrate)],
      }).run;
      run.completeTask(Stage.Integrate, 'integrate:spec-sync', { status: 'completed' });
      run.completeTask(Stage.Integrate, 'integrate:archive-change', { status: 'completed', output: { archivePath: 'openspec/changes/archive/1-test' } });
      const stageExecutionRepo = {
        create: vi.fn().mockReturnValue({ id: 'exec-1' }),
        findById: vi.fn().mockReturnValue({ checkResults: [] }),
        updateCheckResults: vi.fn(),
        appendTaskResult: vi.fn(),
        updateStatus: vi.fn(),
      };
      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'service-call': taskHandler }),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: '/tmp',
      });
      const ctx = makeMockContext(Stage.Integrate, {
        stageExecutionRepo: stageExecutionRepo as any,
        workflowApplicationService: {
          completeTask: vi.fn(({ stage, taskId, result }) => ({ run, decision: run.completeTask(stage, taskId, result) })),
          recordCheckResult: vi.fn(),
          approveStage: vi.fn(),
          materializeTasks: vi.fn(),
        } as any,
      });
      ctx.requestedWork = { kind: 'task', stage: Stage.Integrate, taskId: 'integrate:merge' };

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toBe('Missing required evidence for mohist/merge: landedSha');
      expect(stageExecutionRepo.appendTaskResult).toHaveBeenCalledWith('exec-1', expect.objectContaining({
        taskId: 'integrate:merge',
        status: 'failed',
        reason: 'Missing required evidence for mohist/merge: landedSha',
      }));
      expect(stageExecutionRepo.updateStatus).toHaveBeenCalledWith('exec-1', 'failed');
    });

    it('GenericStageRunner mirrors aggregate-rejected check pass as failed execution evidence', async () => {
      const checkHandler = vi.fn().mockReturnValue({ name: 'pr-merged', status: 'pass' as const });
      const definition: StageDefinition = {
        stage: Stage.Integrate,
        tasks: [],
        checks: [{ name: 'pr-merged', title: 'PR merged', uses: 'mohist/pr-merged' }],
        checkPolicies: [{ checkName: 'pr-merged', phase: 'post-task' }],
        requiresApproval: false,
      };
      const run = WorkflowRun.startWorkflow({
        id: 'run-reject-check',
        issueId: 'issue-1',
        issueNumber: 1,
        definitions: [definition],
      }).run;
      const stageExecutionRepo = {
        create: vi.fn().mockReturnValue({ id: 'exec-1' }),
        findById: vi.fn().mockReturnValue({ checkResults: [] }),
        updateCheckResults: vi.fn(),
        appendTaskResult: vi.fn(),
        updateStatus: vi.fn(),
      };
      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry({}),
        checkRegistry: createBasicCheckRegistry({ 'pr-merged': checkHandler }),
        getStageDefinition: stage => stage === Stage.Integrate ? definition : createStageDefinition(stage),
        worktreePath: '/tmp',
      });
      const ctx = makeMockContext(Stage.Integrate, {
        stageExecutionRepo: stageExecutionRepo as any,
        workflowApplicationService: {
          completeTask: vi.fn(),
          recordCheckResult: vi.fn(({ stage, result }) => ({ run, decision: run.recordCheckResult(stage, result) })),
          approveStage: vi.fn(),
          materializeTasks: vi.fn(),
        } as any,
      });
      ctx.requestedWork = { kind: 'check', stage: Stage.Integrate, checkName: 'pr-merged' };

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(result.message).toBe('Missing required evidence for mohist/pr-merged: mergedSha');
      expect(stageExecutionRepo.updateCheckResults).toHaveBeenCalledWith('exec-1', [expect.objectContaining({
        name: 'pr-merged',
        status: 'fail',
        message: 'Missing required evidence for mohist/pr-merged: mergedSha',
      })]);
      expect(stageExecutionRepo.updateStatus).toHaveBeenCalledWith('exec-1', 'failed');
    });

    it('GenericStageRunner handles Plan stage with agent-session tasks', async () => {
      let tmpDir = require('fs').mkdtempSync(require('path').join(require('os').tmpdir(), 'mohist-plan-test-'));
      require('fs').mkdirSync(require('path').join(tmpDir, 'change'), { recursive: true });

      const taskHandler = vi.fn().mockImplementation(async (task: ExecutableTask, _ctx: StageContext) => {
        if (task.taskId === 'proposal' || task.taskId === 'specs' || task.taskId === 'design' || task.taskId === 'tasks' || task.taskId === 'self-review') {
          if (task.taskId === 'proposal') {
            fs.writeFileSync(path.join(tmpDir, 'proposal.md'), '# proposal\n', 'utf-8');
          }
          return {
            taskId: task.taskId,
            title: task.title,
            status: 'completed',
            artifacts: [`${task.taskId}.md`],
            attempts: 1,
            duration: 100,
            output: { done: true },
          };
        }
        return null;
      });

      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'agent-session': taskHandler }),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: tmpDir,
      });

      const ctx = makeMockContext(Stage.Plan, {
        artifactManager: {
          getChangeDir: vi.fn().mockReturnValue(tmpDir),
          createChangeDir: vi.fn(),
          readArtifact: vi.fn(),
          writeArtifact: vi.fn(),
          exists: vi.fn().mockReturnValue(true),
          readTasks: vi.fn().mockReturnValue(null),
          updateTaskPasses: vi.fn(),
          archiveChange: vi.fn(),
        } as any,
        checkpointManager: {
          save: vi.fn(),
          load: vi.fn(),
          deleteAll: vi.fn(),
          getResumeSteps: vi.fn().mockReturnValue([]),
          upsert: vi.fn(),
          markStepComplete: vi.fn(),
          deleteStep: vi.fn(),
          delete: vi.fn(),
        } as any,
      });

      ctx.requestedWork = { kind: 'task', stage: Stage.Plan, taskId: 'proposal' };
      const result = await runner.run(ctx);

      try { require('fs').rmSync(tmpDir, { recursive: true, force: true }); } catch {}

      expect(result.success).toBe(true);
      expect(taskHandler).toHaveBeenCalledTimes(1);
    });

    it('GenericStageRunner handles Check stage with ai-review and checks', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-check-test-'));
      let checkRunCount = 0;
      const checkHandler = vi.fn().mockImplementation(async (_ctx: CheckContext) => {
        checkRunCount++;
        return { name: 'review-passed', status: checkRunCount > 0 ? 'pass' : 'fail' as const, message: 'review check' };
      });

      const taskHandler = vi.fn().mockImplementation(async (task: ExecutableTask, _ctx: StageContext) => {
        if (task.taskId === 'ai-review') {
          fs.writeFileSync(path.join(tmpDir, 'review.md'), '## Findings\n\n<promise>PASS</promise>\n', 'utf-8');
          return {
            taskId: task.taskId,
            title: task.title,
            status: 'completed',
            artifacts: ['review.md'],
            attempts: 1,
            duration: 100,
            output: { done: true },
          };
        }
        return null;
      });

      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'agent-session': taskHandler }),
        checkRegistry: createBasicCheckRegistry({ 'review-passed': checkHandler }),
        getStageDefinition: createStageDefinition,
        worktreePath: tmpDir,
      });

      const ctx = makeMockContext(Stage.Check, {
        acpOptions: { cwd: tmpDir, worktreePath: tmpDir } as any,
        artifactManager: {
          getChangeDir: vi.fn().mockReturnValue(tmpDir),
          createChangeDir: vi.fn(),
          readArtifact: vi.fn(),
          writeArtifact: vi.fn(),
          exists: vi.fn().mockReturnValue(true),
          readTasks: vi.fn().mockReturnValue(null),
          updateTaskPasses: vi.fn(),
          archiveChange: vi.fn(),
        } as any,
      });
      ctx.requestedWork = { kind: 'task', stage: Stage.Check, taskId: 'ai-review' };
      const result = await runner.run(ctx);

      try {
        expect(result.success).toBe(true);
        expect(taskHandler).toHaveBeenCalledTimes(1);
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('Plan agent tasks provide full agent-session input on the default handler path', async () => {
      const createSession = vi.fn().mockResolvedValue({
        execute: vi.fn().mockImplementation(async (prompt: string) => {
          if (prompt.includes('proposal')) {
            fs.writeFileSync(path.join(tmpDir, 'proposal.md'), '# proposal\n', 'utf-8');
          }
          return { success: true, acpSessionId: 'session-1' };
        }),
        close: vi.fn().mockResolvedValue(undefined),
      });
      const genericHandler = createAgentSessionTaskHandler({
        createSession,
        createObservers: () => ({ onEvent: vi.fn(), close: vi.fn() }) as any,
      });

      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-plan-default-handler-'));
      try {
        const runner = new GenericStageRunner({
          taskLoaderRegistry: createBasicTaskLoaderRegistry(),
          taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'agent-session': genericHandler as any }),
          checkRegistry: createBasicCheckRegistry({}),
          getStageDefinition: createStageDefinition,
          worktreePath: tmpDir,
        });

        const ctx = makeMockContext(Stage.Plan, {
          artifactManager: {
            getChangeDir: vi.fn().mockReturnValue(tmpDir),
            createChangeDir: vi.fn(),
            readArtifact: vi.fn(),
            writeArtifact: vi.fn(),
            exists: vi.fn().mockReturnValue(true),
            readTasks: vi.fn().mockReturnValue(null),
            updateTaskPasses: vi.fn(),
            archiveChange: vi.fn(),
          } as any,
          checkpointManager: {
            save: vi.fn(),
            load: vi.fn(),
            deleteAll: vi.fn(),
            getResumeSteps: vi.fn().mockReturnValue([]),
            upsert: vi.fn(),
            markStepComplete: vi.fn(),
            deleteStep: vi.fn(),
            delete: vi.fn(),
          } as any,
        });

        ctx.requestedWork = { kind: 'task', stage: Stage.Plan, taskId: 'proposal' };
        const result = await runner.run(ctx);

        expect(result.success).toBe(true);
        expect(createSession).toHaveBeenCalledTimes(1);
        expect(createSession.mock.calls[0]?.[0]).toMatchObject({
          cwd: tmpDir,
          stage: 'plan',
          title: 'Generate proposal',
          executionId: 'plan-1-proposal-1',
        });
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('Check ai-review provides full agent-session input on the default handler path', async () => {
      const createSession = vi.fn().mockResolvedValue({
        execute: vi.fn().mockImplementation(async () => {
          fs.writeFileSync(path.join(tmpDir, 'review.md'), '## Findings\n\n<promise>PASS</promise>\n', 'utf-8');
          return { success: true, acpSessionId: 'session-1' };
        }),
        close: vi.fn().mockResolvedValue(undefined),
      });
      const genericHandler = createAgentSessionTaskHandler({
        createSession,
        createObservers: () => ({ onEvent: vi.fn(), close: vi.fn() }) as any,
      });

      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-check-default-handler-'));
      try {
        const runner = new GenericStageRunner({
          taskLoaderRegistry: createBasicTaskLoaderRegistry(),
          taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'agent-session': genericHandler as any }),
          checkRegistry: createBasicCheckRegistry({}),
          getStageDefinition: createStageDefinition,
          worktreePath: tmpDir,
        });

        const ctx = makeMockContext(Stage.Check, {
          acpOptions: { cwd: tmpDir, worktreePath: tmpDir } as any,
          artifactManager: {
            getChangeDir: vi.fn().mockReturnValue(tmpDir),
            createChangeDir: vi.fn(),
            readArtifact: vi.fn(),
            writeArtifact: vi.fn(),
            exists: vi.fn().mockImplementation((artifactPath: string) => artifactPath.endsWith('review.md')),
            readTasks: vi.fn().mockReturnValue(null),
            updateTaskPasses: vi.fn(),
            archiveChange: vi.fn(),
          } as any,
        });

        ctx.requestedWork = { kind: 'task', stage: Stage.Check, taskId: 'ai-review' };
        const result = await runner.run(ctx);

        expect(result.success).toBe(true);
        expect(createSession).toHaveBeenCalledTimes(1);
        expect(createSession.mock.calls[0]?.[0]).toMatchObject({
          cwd: tmpDir,
          stage: 'check',
          title: 'AI review',
          executionId: 'check-1-ai-review-1',
        });
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('custom agent task prompt file is resolved before ACP execution', async () => {
      const execute = vi.fn().mockResolvedValue({ success: true, acpSessionId: 'session-1' });
      const createSession = vi.fn().mockResolvedValue({
        execute,
        close: vi.fn().mockResolvedValue(undefined),
      });
      const genericHandler = createAgentSessionTaskHandler({
        createSession,
        createObservers: () => ({ onEvent: vi.fn(), close: vi.fn() }) as any,
      });

      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-custom-prompt-file-'));
      try {
        fs.mkdirSync(path.join(tmpDir, '.mohist', 'prompts'), { recursive: true });
        fs.writeFileSync(path.join(tmpDir, '.mohist', 'prompts', 'handoff.md'), 'Write a custom handoff report.', 'utf-8');
        const customBuildStage: StageDefinition = {
          stage: Stage.Build,
          tasks: [
            {
              id: 'handoff',
              title: 'Write handoff',
              source: 'project',
              uses: 'mohist/agent',
              with: { prompt: { file: '.mohist/prompts/handoff.md' } },
            },
          ],
          checks: [],
          workSources: [{ kind: 'static', taskIds: ['handoff'] }],
          taskExecutionPolicies: [{ taskId: 'handoff', kind: 'agent-session', workSourceKind: 'static' }],
          checkPolicies: [],
          repairPolicies: [],
          invalidationPolicy: { entries: [] },
        };

        const runner = new GenericStageRunner({
          taskLoaderRegistry: {
            get: vi.fn().mockImplementation((kind: string) => {
              if (kind !== 'static') return undefined;
              return {
                kind: 'static' as const,
                load: () => customBuildStage.tasks.map(task => ({
                  taskId: task.id,
                  title: task.title,
                  kind: 'agent-session' as const,
                })),
              };
            }),
            list: vi.fn().mockReturnValue([]),
          },
          taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'agent-session': genericHandler as any }),
          checkRegistry: createBasicCheckRegistry({}),
          getStageDefinition: stage => stage === Stage.Build ? customBuildStage : createStageDefinition(stage),
          worktreePath: tmpDir,
        });
        const ctx = makeMockContext(Stage.Build, { acpOptions: { cwd: tmpDir, worktreePath: tmpDir } as any });
        ctx.requestedWork = { kind: 'task', stage: Stage.Build, taskId: 'handoff' };

        const result = await runner.run(ctx);

        expect(result.success).toBe(true);
        expect(execute).toHaveBeenCalledWith('Write a custom handoff report.', { kind: 'task', title: 'Write handoff' });
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('GenericStageRunner handles Build stage with Ralph work source', async () => {
      const ralphHandler = vi.fn().mockImplementation(async (task: ExecutableTask, _ctx: StageContext) => {
        if (task.kind === 'ralph-task') {
          return {
            taskId: task.taskId,
            title: task.title,
            status: 'completed',
            artifacts: [],
            attempts: 1,
            duration: 100,
          };
        }
        return null;
      });

      let tmpDir = require('fs').mkdtempSync(require('path').join(require('os').tmpdir(), 'mohist-build-test-'));
      require('fs').writeFileSync(
        require('path').join(tmpDir, 'workflow.yaml'),
        'stages:\n  - stage: explore\n  - stage: plan\n  - stage: build\n  - stage: check\n  - stage: integrate\n  - stage: done\n',
        'utf-8',
      );

      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'ralph-task': ralphHandler }),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: tmpDir,
      });

      const ctx = makeMockContext(Stage.Build, {
        workflowApplicationService: {
          completeTask: vi.fn(),
          recordCheckResult: vi.fn(),
          approveStage: vi.fn(),
          materializeTasks: vi.fn(),
        } as any,
        stageExecutionRepo: {
          create: vi.fn().mockReturnValue({ id: 'exec-1' }),
          updateCheckResults: vi.fn(),
          appendTaskResult: vi.fn(),
          updateStatus: vi.fn(),
        } as any,
      });
      ctx.requestedWork = { kind: 'task' as const, stage: Stage.Build, taskId: 'T-001' };
      ctx.workflowRun = {
        stageRuns: [{
          stage: Stage.Build,
          tasks: [{ id: 'T-001', title: 'Build task 1', status: 'pending' as const, taskOrder: 1 }],
        }],
      } as any;

      const result = await runner.run(ctx);

      try { require('fs').rmSync(tmpDir, { recursive: true, force: true }); } catch {}

      expect(result.success).toBe(true);
      expect(result.message ?? 'ok').toBe('ok');
    });

    it('Ralph task handler reuses active Build stage execution from unified runner', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-build-ralph-stage-exec-'));
      try {
        const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-generic-runner-build');
        fs.mkdirSync(changeDir, { recursive: true });
        fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({
          version: 1,
          tasks: [{ id: 'T-001', title: 'Build first task', description: '', order: 1, dependsOn: [], passes: false, attempts: 0 }],
        }), 'utf-8');

        let ralphContext: RalphExecutor.RalphExecutorContext | null = null;
        let ralphOptions: RalphExecutor.RalphExecutorOptions | null = null;
        const runRalphLoop = vi.spyOn(RalphExecutor, 'runRalphLoop').mockImplementation(async (_change, ctx, options) => {
          ralphContext = ctx;
          ralphOptions = options;
          return {
            completed: 1,
            failed: 0,
            skipped: 0,
            taskResults: [{ taskId: 'T-001', status: 'completed' as const, attempts: 1 }],
          } as any;
        });

        const stageExecutionRepo = {
          findActiveByIssueId: vi.fn().mockReturnValueOnce(null).mockReturnValue({ id: 'exec-1', stage: Stage.Build }),
          create: vi.fn().mockReturnValue({ id: 'exec-1', stage: Stage.Build }),
          findById: vi.fn().mockReturnValue({ checkResults: [], taskResults: [] }),
          appendTaskResult: vi.fn(),
          updateCheckResults: vi.fn(),
          updateStatus: vi.fn(),
        };
        const ralphTaskLoader = createRalphTaskLoader();
        const taskLoaderRegistry: TaskLoaderRegistry = {
          get: vi.fn().mockImplementation((kind: string) => kind === 'ralph' ? ralphTaskLoader : undefined),
          list: vi.fn().mockReturnValue([]),
        };
        const runner = new GenericStageRunner({
          taskLoaderRegistry,
          taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'ralph-task': createRalphTaskHandler() as any }),
          checkRegistry: createBasicCheckRegistry({}),
          getStageDefinition: createStageDefinition,
          worktreePath: tmpDir,
        });

        const ctx = makeMockContext(Stage.Build, {
          acpOptions: { cwd: tmpDir } as any,
          stageExecutionRepo: stageExecutionRepo as any,
          workflowApplicationService: {
            completeTask: vi.fn(),
            recordCheckResult: vi.fn(),
            approveStage: vi.fn(),
            materializeTasks: vi.fn(),
          } as any,
          workflowRun: {
            stageRuns: [{ stage: Stage.Build, tasks: [{ id: 'T-001', taskId: 'T-001', title: 'Build first task', status: 'pending' as const }] }],
          } as any,
        });
        ctx.requestedWork = { kind: 'task', stage: Stage.Build, taskId: 'T-001' };

        const result = await runner.run(ctx);

        expect(result.success).toBe(true);
        expect(stageExecutionRepo.create).toHaveBeenCalledTimes(1);
        expect(ralphContext?.stageExecutionId).toBe('exec-1');
        expect(ralphOptions?.onlyTaskId).toBe('T-001');
        expect(stageExecutionRepo.findActiveByIssueId).toHaveBeenCalledWith(ctx.issue.id);
        expect(ctx.workflowApplicationService?.completeTask).not.toHaveBeenCalled();

        runRalphLoop.mockRestore();
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('GenericStageRunner exposes Build Ralph task materialization before work selection', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-build-materialize-'));
      try {
        const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-generic-runner-build');
        fs.mkdirSync(changeDir, { recursive: true });
        fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({
          version: 1,
          tasks: [
            { id: 'T-001', title: 'Build first task', description: '', order: 1, dependsOn: [], passes: false, attempts: 0 },
          ],
        }), 'utf-8');

        const materializeTasks = vi.fn();
        const healthCheck = vi.fn().mockReturnValue({ name: 'health:build', status: 'pass' as const });
        const taskLoaderRegistry: TaskLoaderRegistry = {
          get: vi.fn().mockImplementation((kind: string) => kind === 'ralph'
            ? {
                kind: 'ralph' as const,
                load: () => [{ taskId: 'T-001', title: 'Build first task', kind: 'ralph-task' as const, input: 'T-001' }],
              }
            : undefined),
          list: vi.fn().mockReturnValue([]),
        };
        const runner = new GenericStageRunner({
          taskLoaderRegistry,
          taskHandlerRegistry: createBasicTaskHandlerRegistry(),
          checkRegistry: createBasicCheckRegistry({ 'health:build': healthCheck }),
          getStageDefinition: createStageDefinition,
          worktreePath: tmpDir,
        });

        const ctx = makeMockContext(Stage.Build, {
          acpOptions: { cwd: tmpDir } as any,
          workflowApplicationService: {
            completeTask: vi.fn(),
            recordCheckResult: vi.fn(),
            approveStage: vi.fn(),
            materializeTasks,
          } as any,
          workflowRun: {
            stageRuns: [{ stage: Stage.Build, tasks: [] }],
          } as any,
        });

        const materialized = runner.materializeWork(ctx);

        expect(materialized).toBe(true);
        expect(materializeTasks).toHaveBeenCalledWith({
          issueId: ctx.issue.id,
          stage: Stage.Build,
          tasks: [{ id: 'T-001', title: 'Build first task', order: 1, dependsOn: [] }],
        });
        expect(healthCheck).not.toHaveBeenCalled();
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('GenericStageRunner materializes missing Ralph tasks even when runtime tasks already exist', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-build-materialize-missing-'));
      try {
        const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-generic-runner-build');
        fs.mkdirSync(changeDir, { recursive: true });
        fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({
          version: 1,
          tasks: [
            { id: 'T-001', title: 'Build first task', description: '', order: 1, dependsOn: [], passes: false, attempts: 0 },
            { id: 'T-002', title: 'Build second task', description: '', order: 2, dependsOn: ['T-001'], passes: false, attempts: 0 },
          ],
        }), 'utf-8');

        const materializeTasks = vi.fn();
        const taskLoaderRegistry: TaskLoaderRegistry = {
          get: vi.fn().mockImplementation((kind: string) => kind === 'ralph'
            ? {
                kind: 'ralph' as const,
                load: () => [
                  { taskId: 'T-001', title: 'Build first task', kind: 'ralph-task' as const, input: 'T-001' },
                  { taskId: 'T-002', title: 'Build second task', kind: 'ralph-task' as const, input: 'T-002' },
                ],
              }
            : undefined),
          list: vi.fn().mockReturnValue([]),
        };
        const runner = new GenericStageRunner({
          taskLoaderRegistry,
          taskHandlerRegistry: createBasicTaskHandlerRegistry(),
          checkRegistry: createBasicCheckRegistry({}),
          getStageDefinition: createStageDefinition,
          worktreePath: tmpDir,
        });

        const ctx = makeMockContext(Stage.Build, {
          acpOptions: { cwd: tmpDir } as any,
          workflowApplicationService: {
            completeTask: vi.fn(),
            recordCheckResult: vi.fn(),
            approveStage: vi.fn(),
            materializeTasks,
          } as any,
          workflowRun: {
            stageRuns: [{ stage: Stage.Build, tasks: [{ id: 'rebase-branch', title: 'Rebase branch', status: 'pending' as const }] }],
          } as any,
        });

        const materialized = runner.materializeWork(ctx);

        expect(materialized).toBe(true);
        expect(materializeTasks).toHaveBeenCalledWith({
          issueId: ctx.issue.id,
          stage: Stage.Build,
          tasks: [
            { id: 'T-001', title: 'Build first task', order: 1, dependsOn: [] },
            { id: 'T-002', title: 'Build second task', order: 2, dependsOn: ['T-001'] },
          ],
        });
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('GenericStageRunner does not rematerialize persisted Ralph tasks that expose taskId', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-build-materialize-persisted-'));
      try {
        const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-generic-runner-build');
        fs.mkdirSync(changeDir, { recursive: true });
        fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({
          version: 1,
          tasks: [
            { id: 'T-001', title: 'Build first task', description: '', order: 1, dependsOn: [], passes: false, attempts: 0 },
            { id: 'T-002', title: 'Build second task', description: '', order: 2, dependsOn: [], passes: false, attempts: 0 },
          ],
        }), 'utf-8');

        const materializeTasks = vi.fn();
        const taskLoaderRegistry: TaskLoaderRegistry = {
          get: vi.fn().mockImplementation((kind: string) => kind === 'ralph'
            ? {
                kind: 'ralph' as const,
                load: () => [
                  { taskId: 'T-001', title: 'Build first task', kind: 'ralph-task' as const, input: 'T-001' },
                  { taskId: 'T-002', title: 'Build second task', kind: 'ralph-task' as const, input: 'T-002' },
                ],
              }
            : undefined),
          list: vi.fn().mockReturnValue([]),
        };
        const runner = new GenericStageRunner({
          taskLoaderRegistry,
          taskHandlerRegistry: createBasicTaskHandlerRegistry(),
          checkRegistry: createBasicCheckRegistry({}),
          getStageDefinition: createStageDefinition,
          worktreePath: tmpDir,
        });

        const ctx = makeMockContext(Stage.Build, {
          acpOptions: { cwd: tmpDir } as any,
          workflowApplicationService: {
            completeTask: vi.fn(),
            recordCheckResult: vi.fn(),
            approveStage: vi.fn(),
            materializeTasks,
          } as any,
          workflowRun: {
            stageRuns: [{
              stage: Stage.Build,
              buildWorkSourceState: { evaluated: true, tasks: ['T-001', 'T-002'] },
              tasks: [
                { id: 'row-1', taskId: 'T-001', title: 'Build first task', status: 'completed' as const },
                { id: 'row-2', taskId: 'T-002', title: 'Build second task', status: 'pending' as const },
              ],
            }],
          } as any,
        });

        expect(runner.materializeWork(ctx)).toBe(false);
        expect(materializeTasks).not.toHaveBeenCalled();
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('GenericStageRunner records missing Build source evidence before checks', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-build-materialize-source-missing-'));
      try {
        const materializeTasks = vi.fn();
        const runner = new GenericStageRunner({
          taskLoaderRegistry: createBasicTaskLoaderRegistry(),
          taskHandlerRegistry: createBasicTaskHandlerRegistry(),
          checkRegistry: createBasicCheckRegistry({}),
          getStageDefinition: createStageDefinition,
          worktreePath: tmpDir,
        });

        const ctx = makeMockContext(Stage.Build, {
          acpOptions: { cwd: tmpDir } as any,
          workflowApplicationService: {
            completeTask: vi.fn(),
            recordCheckResult: vi.fn(),
            approveStage: vi.fn(),
            materializeTasks,
          } as any,
          workflowRun: {
            stageRuns: [{ stage: Stage.Build, tasks: [], buildWorkSourceState: { evaluated: false } }],
          } as any,
        });

        expect(runner.materializeWork(ctx)).toBe(true);
        expect(materializeTasks).toHaveBeenCalledWith({
          issueId: ctx.issue.id,
          stage: Stage.Build,
          tasks: [],
          workSourceState: 'missing',
        });
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('GenericStageRunner records invalid Build source evidence when task loading throws', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-build-materialize-source-invalid-'));
      try {
        const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-generic-runner-build');
        fs.mkdirSync(changeDir, { recursive: true });
        fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [{ id: 'T-001', order: 1, title: 'Build first task' }] }), 'utf-8');

        const materializeTasks = vi.fn();
        const taskLoaderRegistry: TaskLoaderRegistry = {
          get: vi.fn().mockImplementation((kind: string) => kind === 'ralph'
            ? {
                kind: 'ralph' as const,
                load: () => { throw new Error('load failed'); },
              }
            : undefined),
          list: vi.fn().mockReturnValue([]),
        };
        const runner = new GenericStageRunner({
          taskLoaderRegistry,
          taskHandlerRegistry: createBasicTaskHandlerRegistry(),
          checkRegistry: createBasicCheckRegistry({}),
          getStageDefinition: createStageDefinition,
          worktreePath: tmpDir,
        });

        const ctx = makeMockContext(Stage.Build, {
          acpOptions: { cwd: tmpDir } as any,
          workflowApplicationService: {
            completeTask: vi.fn(),
            recordCheckResult: vi.fn(),
            approveStage: vi.fn(),
            materializeTasks,
          } as any,
          workflowRun: {
            stageRuns: [{ stage: Stage.Build, tasks: [], buildWorkSourceState: { evaluated: false } }],
          } as any,
        });

        expect(runner.materializeWork(ctx)).toBe(true);
        expect(materializeTasks).toHaveBeenCalledWith({
          issueId: ctx.issue.id,
          stage: Stage.Build,
          tasks: [],
          workSourceState: 'invalid',
        });
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('GenericStageRunner records empty Build source evidence before checks', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-build-materialize-source-empty-'));
      try {
        const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-generic-runner-build');
        fs.mkdirSync(changeDir, { recursive: true });
        fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ version: 1, tasks: [] }), 'utf-8');

        const materializeTasks = vi.fn();
        const taskLoaderRegistry: TaskLoaderRegistry = {
          get: vi.fn().mockImplementation((kind: string) => kind === 'ralph'
            ? {
                kind: 'ralph' as const,
                load: () => [],
              }
            : undefined),
          list: vi.fn().mockReturnValue([]),
        };
        const runner = new GenericStageRunner({
          taskLoaderRegistry,
          taskHandlerRegistry: createBasicTaskHandlerRegistry(),
          checkRegistry: createBasicCheckRegistry({}),
          getStageDefinition: createStageDefinition,
          worktreePath: tmpDir,
        });

        const ctx = makeMockContext(Stage.Build, {
          acpOptions: { cwd: tmpDir } as any,
          workflowApplicationService: {
            completeTask: vi.fn(),
            recordCheckResult: vi.fn(),
            approveStage: vi.fn(),
            materializeTasks,
          } as any,
          workflowRun: {
            stageRuns: [{ stage: Stage.Build, tasks: [], buildWorkSourceState: { evaluated: false } }],
          } as any,
        });

        expect(runner.materializeWork(ctx)).toBe(true);
        expect(materializeTasks).toHaveBeenCalledWith({
          issueId: ctx.issue.id,
          stage: Stage.Build,
          tasks: [],
          workSourceState: 'empty',
        });
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });
  });

  describe('AC-2: aggregate single work execution', () => {
    it('requested task work executes exactly one task and reports result', async () => {
      let executedTaskId: string | null = null;
      const taskHandler = vi.fn().mockImplementation(async (task: ExecutableTask, _ctx: StageContext) => {
        executedTaskId = task.taskId;
        return {
          taskId: task.taskId,
          title: task.title,
          status: 'completed',
          artifacts: [],
          attempts: 1,
          duration: 50,
        };
      });

      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'service-call': taskHandler }),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: '/tmp',
      });

      const ctx = makeMockContext(Stage.Integrate);
      ctx.requestedWork = { kind: 'task', stage: Stage.Integrate, taskId: 'integrate:spec-sync' };

      const result = await runner.run(ctx);

      expect(executedTaskId).toBe('integrate:spec-sync');
      expect(result.success).toBe(true);
    });

    it('requested task work obtains dispatchable task from the dispatch factory registry', async () => {
      let executedTitle: string | null = null;
      const taskHandler = vi.fn().mockImplementation(async (task: ExecutableTask, _ctx: StageContext) => {
        executedTitle = task.title;
        return {
          taskId: task.taskId,
          title: task.title,
          status: 'completed',
          artifacts: [],
          attempts: 1,
          duration: 1,
        };
      });
      const taskDispatchFactoryRegistry = {
        build: vi.fn().mockImplementation(({ task }) => ({ ...task, title: 'Factory-built task', kind: 'service-call' })),
      };

      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'service-call': taskHandler }),
        checkRegistry: createBasicCheckRegistry({}),
        taskDispatchFactoryRegistry,
        getStageDefinition: createStageDefinition,
        worktreePath: '/tmp',
      });

      const ctx = makeMockContext(Stage.Integrate);
      ctx.requestedWork = { kind: 'task', stage: Stage.Integrate, taskId: 'integrate:spec-sync' };

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(taskDispatchFactoryRegistry.build).toHaveBeenCalledWith(expect.objectContaining({
        executionKind: 'service-call',
        worktreePath: '/tmp',
      }));
      expect(executedTitle).toBe('Factory-built task');
    });

    it('service-call dispatch selects delivery behavior by uses for custom task ids', async () => {
      const worktreePath = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-custom-service-dispatch-'));
      try {
        const changeDir = path.join(worktreePath, 'openspec', 'changes', 'custom-service');
        fs.mkdirSync(changeDir, { recursive: true });
        const stageDefinition: StageDefinition = {
          stage: Stage.Integrate,
          tasks: [{ id: 'archive-spec-change', title: 'Archive spec change', uses: 'mohist/archive-change' }],
          checks: [],
          workSources: [{ kind: 'static', taskIds: ['archive-spec-change'] }],
          taskExecutionPolicies: [{ taskId: 'archive-spec-change', kind: 'service-call', workSourceKind: 'static' }],
        };
        const taskLoaderRegistry: TaskLoaderRegistry = {
          get: vi.fn().mockImplementation((kind: string) => kind === 'static'
            ? {
                kind: 'static' as const,
                load: () => [{ taskId: 'archive-spec-change', title: 'Archive spec change', kind: 'service-call' as const }],
              }
            : undefined),
          list: vi.fn().mockReturnValue([]),
        };
        const runner = new GenericStageRunner({
          taskLoaderRegistry,
          taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'service-call': defaultServiceCallTaskHandler as any }),
          checkRegistry: createBasicCheckRegistry({}),
          getStageDefinition: () => stageDefinition,
          worktreePath,
        });
        const ctx = makeMockContext(Stage.Integrate, {
          acpOptions: { cwd: worktreePath } as any,
          artifactManager: {
            getChangeDir: vi.fn().mockReturnValue(changeDir),
            createChangeDir: vi.fn(),
            archiveChange: vi.fn(),
          } as any,
          workflowApplicationService: undefined,
          requestedWork: { kind: 'task', stage: Stage.Integrate, taskId: 'archive-spec-change' },
        });

        const result = await runner.run(ctx);
        const output = result.output as { kind: string; result: { step: string; archivePath: string; success: boolean } };

        expect(result.success).toBe(true);
        expect(ctx.artifactManager.archiveChange).toHaveBeenCalledWith(ctx.issue.number);
        expect(output.result).toMatchObject({
          step: 'archive-spec-change',
          archivePath: path.relative(worktreePath, changeDir),
          success: true,
        });
      } finally {
        fs.rmSync(worktreePath, { recursive: true, force: true });
      }
    });

    it('requested static task resolves through the task loader registry', async () => {
      let staticLoaderCalled = false;
      let executedTaskTitle: string | null = null;
      const taskLoaderRegistry: TaskLoaderRegistry = {
        get: vi.fn().mockImplementation((kind: string) => kind === 'static'
          ? {
              kind: 'static' as const,
              load: () => {
                staticLoaderCalled = true;
                return [{ taskId: 'integrate:spec-sync', title: 'Loaded static spec sync', kind: 'service-call' as const }];
              },
            }
          : undefined),
        list: vi.fn().mockReturnValue([]),
      };
      const taskHandler = vi.fn().mockImplementation(async (task: ExecutableTask, _ctx: StageContext) => {
        executedTaskTitle = task.title;
        return {
          taskId: task.taskId,
          title: task.title,
          status: 'completed',
          artifacts: [],
          attempts: 1,
          duration: 50,
        };
      });

      const runner = new GenericStageRunner({
        taskLoaderRegistry,
        taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'service-call': taskHandler }),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: '/tmp',
      });

      const ctx = makeMockContext(Stage.Integrate);
      ctx.requestedWork = { kind: 'task', stage: Stage.Integrate, taskId: 'integrate:spec-sync' };

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(staticLoaderCalled).toBe(true);
      expect(executedTaskTitle).toBe('Loaded static spec sync');
    });

    it('requested task work creates stage execution and appends task projection', async () => {
      const taskHandler = vi.fn().mockImplementation(async (task: ExecutableTask, _ctx: StageContext) => ({
        taskId: task.taskId,
        title: task.title,
        status: 'completed' as const,
        artifacts: [],
        attempts: 1,
        duration: 50,
      }));
      const stageExecutionRepo = {
        create: vi.fn().mockReturnValue({ id: 'exec-1', stage: Stage.Integrate }),
        findActiveByIssueId: vi.fn().mockReturnValue(null),
        findById: vi.fn().mockReturnValue({ checkResults: [] }),
        appendTaskResult: vi.fn(),
        updateCheckResults: vi.fn(),
        updateStatus: vi.fn(),
      };

      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'service-call': taskHandler }),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: '/tmp',
      });

      const ctx = makeMockContext(Stage.Integrate, { stageExecutionRepo: stageExecutionRepo as any });
      ctx.requestedWork = { kind: 'task', stage: Stage.Integrate, taskId: 'integrate:spec-sync' };

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(stageExecutionRepo.create).toHaveBeenCalledWith(ctx.issue.id, Stage.Integrate);
      expect(stageExecutionRepo.appendTaskResult).toHaveBeenCalledWith('exec-1', expect.objectContaining({
        taskId: 'integrate:spec-sync',
        status: 'completed',
      }));
    });

    it('requested check work executes exactly one check and reports result', async () => {
      let executedCheckName: string | null = null;
      const checkHandler = vi.fn().mockImplementation(async (_ctx: CheckContext) => {
        return { name: 'health:integrate', status: 'pass' as const };
      });
      const recordCheckResult = vi.fn();

      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry(),
        checkRegistry: createBasicCheckRegistry({ 'health:integrate': checkHandler }),
        getStageDefinition: createStageDefinition,
        worktreePath: '/tmp',
      });

      const ctx = makeMockContext(Stage.Integrate);
      ctx.requestedWork = { kind: 'check', stage: Stage.Integrate, checkName: 'health:integrate' };
      ctx.workflowApplicationService = {
        completeTask: vi.fn(),
        recordCheckResult,
        approveStage: vi.fn(),
      } as any;

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(result.checkResults).toHaveLength(1);
      expect(result.checkResults[0].name).toBe('health:integrate');
      expect(recordCheckResult).toHaveBeenCalledWith({
        issueId: ctx.issue.id,
        stage: Stage.Integrate,
        result: {
          name: 'health:integrate',
          status: 'pass',
          message: undefined,
          output: undefined,
        },
      });
    });

    it('requested check work updates stage execution check projection and terminal status', async () => {
      const checkHandler = vi.fn().mockImplementation(async (_ctx: CheckContext) => {
        return { name: 'health:integrate', status: 'pass' as const };
      });
      const stageExecutionRepo = {
        create: vi.fn().mockReturnValue({ id: 'exec-1', stage: Stage.Integrate }),
        findActiveByIssueId: vi.fn().mockReturnValue(null),
        findById: vi.fn().mockReturnValue({ checkResults: [{ name: 'previous', status: 'pass' }] }),
        appendTaskResult: vi.fn(),
        updateCheckResults: vi.fn(),
        updateStatus: vi.fn(),
      };

      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry(),
        checkRegistry: createBasicCheckRegistry({ 'health:integrate': checkHandler }),
        getStageDefinition: createStageDefinition,
        worktreePath: '/tmp',
      });

      const ctx = makeMockContext(Stage.Integrate, { stageExecutionRepo: stageExecutionRepo as any });
      ctx.requestedWork = { kind: 'check', stage: Stage.Integrate, checkName: 'health:integrate' };

      const result = await runner.run(ctx);

      expect(result.success).toBe(true);
      expect(stageExecutionRepo.create).toHaveBeenCalledWith(ctx.issue.id, Stage.Integrate);
      expect(stageExecutionRepo.updateCheckResults).toHaveBeenCalledWith('exec-1', [
        { name: 'previous', status: 'pass' },
        { name: 'health:integrate', status: 'pass', message: undefined, output: undefined },
      ]);
      expect(stageExecutionRepo.updateStatus).toHaveBeenCalledWith('exec-1', 'passed');
    });

    it('repairable failed check keeps stage execution running while WorkflowRun schedules repair', async () => {
      const checkHandler = vi.fn().mockImplementation(async (_ctx: CheckContext) => {
        return { name: 'review-passed', status: 'fail' as const, message: 'Review failed' };
      });
      const stageExecutionRepo = {
        create: vi.fn().mockReturnValue({ id: 'exec-1', stage: Stage.Check }),
        findActiveByIssueId: vi.fn().mockReturnValue(null),
        findById: vi.fn().mockReturnValue({ checkResults: [] }),
        appendTaskResult: vi.fn(),
        updateCheckResults: vi.fn(),
        updateStatus: vi.fn(),
      };
      const recordCheckResult = vi.fn().mockReturnValue({
        decision: { nextWork: { kind: 'task', stage: Stage.Check, taskId: 'fix-review-findings' }, events: [] },
      });

      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry(),
        checkRegistry: createBasicCheckRegistry({ 'review-passed': checkHandler }),
        getStageDefinition: createStageDefinition,
        worktreePath: '/tmp',
      });

      const ctx = makeMockContext(Stage.Check, {
        stageExecutionRepo: stageExecutionRepo as any,
        workflowApplicationService: {
          completeTask: vi.fn(),
          recordCheckResult,
          approveStage: vi.fn(),
        } as any,
        workflowRun: {
          stageRuns: [{ stage: Stage.Check, tasks: [{ id: 'ai-review', title: 'AI review', status: 'completed' }], checks: [] }],
        } as any,
      });
      ctx.requestedWork = { kind: 'check', stage: Stage.Check, checkName: 'review-passed' };

      const result = await runner.run(ctx);

      expect(result.success).toBe(false);
      expect(stageExecutionRepo.updateStatus).toHaveBeenCalledWith('exec-1', 'running');
      expect(stageExecutionRepo.updateStatus).not.toHaveBeenCalledWith('exec-1', 'failed');
    });

    it('generic Check resolves scheduled fix-check-health as an executable repair task', () => {
      const completeTask = vi.fn().mockReturnValue({ decision: { events: [] } });

      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry(),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: '/tmp',
      });

      const ctx = makeMockContext(Stage.Check, {
        workflowApplicationService: {
          completeTask,
          recordCheckResult: vi.fn(),
          approveStage: vi.fn(),
        } as any,
        requestedTask: {
          id: 'fix-check-health',
          taskId: 'fix-check-health',
          title: 'Fix check health',
          status: 'pending',
          attempts: 0,
          order: 1,
          artifacts: [],
          output: null,
          reason: 'health failed',
          causedBy: { type: 'check-failure', checkName: 'health:check', message: 'build failed' },
        } as any,
        workflowRun: {
          stageRuns: [{
            stage: Stage.Check,
            tasks: [
              { id: 'ai-review', taskId: 'ai-review', title: 'AI review', status: 'completed' },
              { id: 'fix-check-health', taskId: 'fix-check-health', title: 'Fix check health', status: 'pending' },
            ],
            checks: [{ name: 'health:check', status: 'pending', message: 'build failed' }],
          }],
        } as any,
      });
      ctx.requestedWork = { kind: 'task', stage: Stage.Check, taskId: 'fix-check-health' };

      const stageDefinition = createStageDefinition(Stage.Check);
      const task = (runner as any).resolveRuntimeTask(stageDefinition, 'fix-check-health');
      const dispatchable = (runner as any).buildDispatchableTask(ctx, task, {
        failedCheck: { name: 'health:check', status: 'fail', message: 'build failed' },
        attempt: 1,
      });

      expect(task).toEqual(expect.objectContaining({
        taskId: 'fix-check-health',
        kind: 'agent-session',
      }));
      expect(dispatchable).toEqual(expect.objectContaining({
        taskId: 'fix-check-health',
        kind: 'service-call',
        stage: Stage.Check,
      }));
    });

    it('generic Check resolves suffixed review repair tasks as executable runtime agent tasks', () => {
      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry(),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: '/tmp',
      });

      const ctx = makeMockContext(Stage.Check, {
        requestedTask: {
          id: 'fix-review-findings:1',
          taskId: 'fix-review-findings:1',
          title: 'Fix review findings',
          status: 'pending',
          attempts: 0,
          order: 3,
          artifacts: [],
          output: null,
          reason: 'Review failed',
          causedBy: { type: 'check-failure', checkName: 'review-passed', message: 'Review failed' },
        } as any,
        workflowRun: {
          stageRuns: [{
            stage: Stage.Check,
            tasks: [
              { id: 'ai-review', taskId: 'ai-review', title: 'AI review', status: 'completed' },
              { id: 'fix-review-findings', taskId: 'fix-review-findings', title: 'Fix review findings', status: 'completed' },
              { id: 'fix-review-findings:1', taskId: 'fix-review-findings:1', title: 'Fix review findings', status: 'pending' },
            ],
            checks: [{ name: 'review-passed', status: 'pending', message: 'Review failed' }],
          }],
        } as any,
      });
      ctx.requestedWork = { kind: 'task', stage: Stage.Check, taskId: 'fix-review-findings:1' };

      const stageDefinition = createStageDefinition(Stage.Check);
      const task = (runner as any).resolveRuntimeTask(stageDefinition, 'fix-review-findings:1');
      const dispatchable = (runner as any).buildDispatchableTask(ctx, task, {
        failedCheck: { name: 'review-passed', status: 'fail', message: 'Review failed' },
        attempt: 1,
      });

      expect(task).toEqual(expect.objectContaining({
        taskId: 'fix-review-findings:1',
        kind: 'agent-session',
      }));
      expect(dispatchable).toEqual(expect.objectContaining({
        taskId: 'fix-review-findings:1',
        kind: 'agent-session',
        prompt: 'Fix findings in /tmp/change/review.md',
        cwd: '/tmp',
        stage: Stage.Check,
        attempt: 1,
        input: expect.objectContaining({
          taskId: 'fix-review-findings:1',
          prompt: 'Fix findings in /tmp/change/review.md',
          cwd: '/tmp',
          stage: Stage.Check,
          attempt: 1,
        }),
      }));
    });

    it('generic Check resolves project-defined retry tasks without builtin task ids', () => {
      const stageDefinition: StageDefinition = {
        stage: Stage.Check,
        on: { 'code.changed': { reset: { tasks: ['ai-review'], checks: 'all', approval: true } } },
        tasks: [{ id: 'ai-review', title: 'AI review' }],
        checks: [{
          name: 'review-passed',
          title: 'Review passed',
          onFailure: {
            retry: {
              limit: 1,
              task: {
                id: 'auto-fix-review',
                title: 'Auto-fix review',
                uses: 'mohist/agent',
                with: { prompt: { inline: 'Fix {{ openspec.changeDir }}/review.md' } },
              },
            },
          },
        }],
        workSources: [{ kind: 'static', taskIds: ['ai-review'] }, { kind: 'runtime' }],
        taskExecutionPolicies: [{ taskId: 'auto-fix-review', kind: 'agent-session', workSourceKind: 'runtime' }],
        checkPolicies: [{ checkName: 'review-passed', phase: 'post-task' }],
      };
      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry(),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: () => stageDefinition,
        worktreePath: '/tmp',
      });

      const ctx = makeMockContext(Stage.Check);
      const task = (runner as any).resolveRuntimeTask(stageDefinition, 'auto-fix-review:1');
      const dispatchable = (runner as any).buildDispatchableTask(ctx, task, {
        failedCheck: { name: 'review-passed', status: 'fail', message: 'Review failed' },
        attempt: 2,
      });

      expect(task).toEqual(expect.objectContaining({
        taskId: 'auto-fix-review:1',
        title: 'Auto-fix review',
        kind: 'agent-session',
      }));
      expect(dispatchable).toEqual(expect.objectContaining({
        taskId: 'auto-fix-review:1',
        kind: 'agent-session',
        prompt: 'Fix /tmp/change/review.md',
        attempt: 2,
      }));
    });

    it('Plan self-review commit failure reports failed task state instead of completed state', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-plan-commit-fail-'));
      const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-test');
      fs.mkdirSync(changeDir, { recursive: true });
      fs.writeFileSync(path.join(changeDir, 'self-review.md'), '# Self review\n<promise>PASS</promise>\n', 'utf-8');

      const completeTask = vi.fn();
      const stageExecutionRepo = {
        create: vi.fn().mockReturnValue({ id: 'exec-1', stage: Stage.Plan }),
        findActiveByIssueId: vi.fn().mockReturnValue(null),
        findById: vi.fn().mockReturnValue({ checkResults: [] }),
        appendTaskResult: vi.fn(),
        updateCheckResults: vi.fn(),
        updateStatus: vi.fn(),
      };
      const serviceCallHandler = vi.fn().mockImplementation(async (task: any, ctx: StageContext) => {
        const output = task.serviceFn ? await task.serviceFn(ctx) : null;
        return { taskId: task.taskId, title: task.title, status: 'completed', artifacts: [], attempts: 1, duration: 1, output };
      });

      try {
        const runner = new GenericStageRunner({
          taskLoaderRegistry: createBasicTaskLoaderRegistry(),
          taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'service-call': serviceCallHandler }),
          checkRegistry: createBasicCheckRegistry({}),
          getStageDefinition: createStageDefinition,
          worktreePath: tmpDir,
        });

        const ctx = makeMockContext(Stage.Plan, {
          artifactManager: {
            getChangeDir: vi.fn().mockReturnValue(changeDir),
            createChangeDir: vi.fn(),
          } as any,
          workflowApplicationService: {
            completeTask,
            recordCheckResult: vi.fn(),
            approveStage: vi.fn(),
          } as any,
          stageExecutionRepo: stageExecutionRepo as any,
        });
        ctx.requestedWork = { kind: 'task', stage: Stage.Plan, taskId: 'self-review' };

        const result = await runner.run(ctx);

        expect(result.success).toBe(false);
        expect(result.message).toContain('Failed to commit plan artifacts');
        expect(completeTask).toHaveBeenCalledWith({
          issueId: ctx.issue.id,
          stage: Stage.Plan,
          taskId: 'self-review',
          result: expect.objectContaining({
            status: 'failed',
            reason: expect.stringContaining('Failed to commit plan artifacts'),
          }),
        });
        expect(completeTask).not.toHaveBeenCalledWith(expect.objectContaining({
          result: expect.objectContaining({ status: 'completed' }),
        }));
        expect(stageExecutionRepo.appendTaskResult).toHaveBeenCalledWith('exec-1', expect.objectContaining({
          taskId: 'self-review',
          status: 'failed',
        }));
        expect(stageExecutionRepo.updateStatus).toHaveBeenCalledWith('exec-1', 'failed');
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('rebase-branch with unchanged snapshot does not invalidate review artifacts before WorkflowRun decides', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-rebase-no-invalidate-'));
      try {
        const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-test');
        fs.mkdirSync(changeDir, { recursive: true });
        const reviewPath = path.join(changeDir, 'review.md');
        fs.writeFileSync(reviewPath, '# Review\n<promise>PASS</promise>\n', 'utf-8');

        const completeTask = vi.fn();
        const checkpointManager = {
          save: vi.fn(),
          load: vi.fn(),
          deleteAll: vi.fn(),
          getResumeSteps: vi.fn().mockReturnValue([]),
          upsert: vi.fn(),
          markStepComplete: vi.fn(),
          deleteStep: vi.fn(),
          delete: vi.fn(),
        } as any;
        const taskHandler = vi.fn().mockImplementation(async (task: ExecutableTask, _ctx: StageContext) => ({
          taskId: task.taskId,
          title: task.title,
          status: 'completed',
          artifacts: [],
          attempts: 1,
          duration: 1,
          output: {
            kind: 'service-call-task',
            success: true,
            result: { shaChanged: false, beforeBaseSha: 'base', afterBaseSha: 'base', beforeHeadSha: 'head', afterHeadSha: 'head' },
          },
        }));

        const runner = new GenericStageRunner({
          taskLoaderRegistry: createBasicTaskLoaderRegistry(),
          taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'agent-session': taskHandler }),
          checkRegistry: createBasicCheckRegistry({}),
          getStageDefinition: createStageDefinition,
          worktreePath: tmpDir,
        });

        const ctx = makeMockContext(Stage.Check, {
          artifactManager: {
            getChangeDir: vi.fn().mockReturnValue(changeDir),
            createChangeDir: vi.fn(),
          } as any,
          checkpointManager,
          workflowApplicationService: {
            completeTask,
            recordCheckResult: vi.fn(),
            approveStage: vi.fn(),
          } as any,
          workflowRun: {
            stageRuns: [{ stage: Stage.Check, tasks: [{ id: 'rebase-branch', title: 'Rebase branch', status: 'pending' }], checks: [] }],
          } as any,
        });
        ctx.requestedWork = { kind: 'task', stage: Stage.Check, taskId: 'rebase-branch' };

        const result = await runner.run(ctx);

        expect(result.success).toBe(true);
        expect(fs.existsSync(reviewPath)).toBe(true);
        expect(fs.readdirSync(changeDir).filter(name => name.startsWith('review.stale-'))).toHaveLength(0);
        expect(checkpointManager.deleteStep).not.toHaveBeenCalledWith(ctx.issue.number, 'check', 'ai-review');
        expect(completeTask).toHaveBeenCalledWith(expect.objectContaining({
          taskId: 'rebase-branch',
          result: expect.objectContaining({ status: 'completed' }),
        }));
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('rebase-branch with changed snapshot waits for WorkflowRun invalidation decision before mutating review artifacts', async () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-rebase-invalidate-'));
      try {
        const changeDir = path.join(tmpDir, 'openspec', 'changes', '1-test');
        fs.mkdirSync(changeDir, { recursive: true });
        const reviewPath = path.join(changeDir, 'review.md');
        fs.writeFileSync(reviewPath, '# Review\n<promise>PASS</promise>\n', 'utf-8');

        const checkpointManager = {
          save: vi.fn(),
          load: vi.fn(),
          deleteAll: vi.fn(),
          getResumeSteps: vi.fn().mockReturnValue([]),
          upsert: vi.fn(),
          markStepComplete: vi.fn(),
          deleteStep: vi.fn(),
          delete: vi.fn(),
        } as any;
        const taskHandler = vi.fn().mockImplementation(async (task: ExecutableTask, _ctx: StageContext) => ({
          taskId: task.taskId,
          title: task.title,
          status: 'completed',
          artifacts: [],
          attempts: 1,
          duration: 1,
          output: {
            kind: 'service-call-task',
            success: true,
            result: { shaChanged: true, beforeBaseSha: 'base-old', afterBaseSha: 'base-new', beforeHeadSha: 'head-old', afterHeadSha: 'head-new' },
          },
        }));
        const completeTask = vi.fn().mockReturnValue({ decision: { events: [], nextWork: { kind: 'task', stage: Stage.Check, taskId: 'ai-review' } } });

        const runner = new GenericStageRunner({
          taskLoaderRegistry: createBasicTaskLoaderRegistry(),
          taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'agent-session': taskHandler }),
          checkRegistry: createBasicCheckRegistry({}),
          getStageDefinition: createStageDefinition,
          worktreePath: tmpDir,
        });

        const ctx = makeMockContext(Stage.Check, {
          artifactManager: {
            getChangeDir: vi.fn().mockReturnValue(changeDir),
            createChangeDir: vi.fn(),
          } as any,
          checkpointManager,
          workflowApplicationService: {
            completeTask,
            recordCheckResult: vi.fn(),
            approveStage: vi.fn(),
          } as any,
          workflowRun: {
            stageRuns: [{ stage: Stage.Check, tasks: [{ id: 'rebase-branch', title: 'Rebase branch', status: 'pending' }], checks: [] }],
          } as any,
        });
        ctx.requestedWork = { kind: 'task', stage: Stage.Check, taskId: 'rebase-branch' };

        const result = await runner.run(ctx);

        expect(result.success).toBe(true);
        expect(fs.existsSync(reviewPath)).toBe(true);
        expect(fs.readdirSync(changeDir).filter(name => name.startsWith('review.stale-'))).toHaveLength(0);
        expect(checkpointManager.deleteStep).not.toHaveBeenCalledWith(ctx.issue.number, 'check', 'ai-review');
        expect(completeTask).toHaveBeenCalledWith(expect.objectContaining({
          taskId: 'rebase-branch',
          result: expect.objectContaining({ status: 'completed' }),
        }));
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });

    it('fails fast without requestedWork instead of running local full-stage flow', async () => {
      const taskHandler = vi.fn();
      const checkHandler = vi.fn();

      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'service-call': taskHandler }),
        checkRegistry: createBasicCheckRegistry({ 'health:integrate': checkHandler }),
        getStageDefinition: createStageDefinition,
        worktreePath: '/tmp',
      });

      const result = await runner.run(makeMockContext(Stage.Integrate));

      expect(result.success).toBe(false);
      expect(result.message).toBe(GENERIC_STAGE_RUNNER_REQUIRES_WORK_MESSAGE);
      expect(taskHandler).not.toHaveBeenCalled();
      expect(checkHandler).not.toHaveBeenCalled();
    });

    it('aggregate requested task work executes only the requested task (requestedWork overrides full stage)', async () => {
      const executedTasks: string[] = [];
      const taskHandler = vi.fn().mockImplementation(async (task: ExecutableTask, _ctx: StageContext) => {
        executedTasks.push(task.taskId);
        return {
          taskId: task.taskId,
          title: task.title,
          status: 'completed',
          artifacts: [],
          attempts: 1,
          duration: 50,
        };
      });

      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry({ 'service-call': taskHandler }),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: '/tmp',
      });

      const ctx = makeMockContext(Stage.Integrate);
      ctx.requestedWork = { kind: 'task', stage: Stage.Integrate, taskId: 'integrate:spec-sync' };

      const result = await runner.run(ctx);

      expect(executedTasks).toEqual(['integrate:spec-sync']);
      expect(result.success).toBe(true);
    });
  });

  describe('AC-3: runtime-added rebase-branch semantics', () => {
    it('rebase-branch is visible as a task in the stage run', () => {
      const run = WorkflowRun.startWorkflow({
        id: 'run-1',
        issueId: 'issue-1',
        issueNumber: 188,
        definitions: [
          createStageDefinition(Stage.Plan),
          createStageDefinition(Stage.Build),
          createStageDefinition(Stage.Check),
          createStageDefinition(Stage.Integrate),
        ],
      }).run;

      run.completeTask(Stage.Plan, 'proposal', { status: 'completed' });
      run.completeTask(Stage.Plan, 'specs', { status: 'completed' });
      run.completeTask(Stage.Plan, 'design', { status: 'completed' });
      run.completeTask(Stage.Plan, 'tasks', { status: 'completed' });
      run.completeTask(Stage.Plan, 'self-review', { status: 'completed' });
      for (const check of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        run.recordCheckResult(Stage.Plan, { name: check, status: 'pass' });
      }
      run.approveStage(Stage.Plan, { output: { approved: true } });
      run.materializeTasks(Stage.Build, [{ id: 'build-1', title: 'Build task 1', order: 1 }]);
      run.completeTask(Stage.Build, 'build-1', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });

      const decision = run.scheduleRebaseTask('Target branch moved');
      expect(decision.events).toHaveLength(0);

      const checkStage = run.stageRun(Stage.Check);
      const rebaseTasks = checkStage.tasks.filter((t: any) => t.id === 'rebase-branch');
      expect(rebaseTasks).toHaveLength(1);
    });

    it('rebase-branch blocks later tasks until terminal', () => {
      const run = WorkflowRun.startWorkflow({
        id: 'run-1',
        issueId: 'issue-1',
        issueNumber: 188,
        definitions: [
          createStageDefinition(Stage.Plan),
          createStageDefinition(Stage.Build),
          createStageDefinition(Stage.Check),
          createStageDefinition(Stage.Integrate),
        ],
      }).run;

      run.completeTask(Stage.Plan, 'proposal', { status: 'completed' });
      run.completeTask(Stage.Plan, 'specs', { status: 'completed' });
      run.completeTask(Stage.Plan, 'design', { status: 'completed' });
      run.completeTask(Stage.Plan, 'tasks', { status: 'completed' });
      run.completeTask(Stage.Plan, 'self-review', { status: 'completed' });
      for (const check of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        run.recordCheckResult(Stage.Plan, { name: check, status: 'pass' });
      }
      run.approveStage(Stage.Plan, { output: { approved: true } });
      run.materializeTasks(Stage.Build, [{ id: 'build-1', title: 'Build task 1', order: 1 }]);
      run.completeTask(Stage.Build, 'build-1', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });

      run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
      run.scheduleRebaseTask('Target branch moved');

      const rebaseTask = run.stageRun(Stage.Check).findTask('rebase-branch');
      expect(rebaseTask.status).toBe('pending');

      const nextWork = run.nextWork();
      expect(nextWork).toEqual({ kind: 'task', stage: Stage.Check, taskId: 'rebase-branch' });

      rebaseTask.status = 'completed';
      const nextAfterRebase = run.nextWork();
      expect(nextAfterRebase).toEqual({ kind: 'check', stage: Stage.Check, checkName: 'health:check' });
    });

    it('rebase-branch failure causes stage failure', () => {
      const run = WorkflowRun.startWorkflow({
        id: 'run-1',
        issueId: 'issue-1',
        issueNumber: 188,
        definitions: [
          createStageDefinition(Stage.Plan),
          createStageDefinition(Stage.Build),
          createStageDefinition(Stage.Check),
          createStageDefinition(Stage.Integrate),
        ],
      }).run;

      run.completeTask(Stage.Plan, 'proposal', { status: 'completed' });
      run.completeTask(Stage.Plan, 'specs', { status: 'completed' });
      run.completeTask(Stage.Plan, 'design', { status: 'completed' });
      run.completeTask(Stage.Plan, 'tasks', { status: 'completed' });
      run.completeTask(Stage.Plan, 'self-review', { status: 'completed' });
      for (const check of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        run.recordCheckResult(Stage.Plan, { name: check, status: 'pass' });
      }
      run.approveStage(Stage.Plan, { output: { approved: true } });
      run.materializeTasks(Stage.Build, [{ id: 'build-1', title: 'Build task 1', order: 1 }]);
      run.completeTask(Stage.Build, 'build-1', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });

      run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
      run.scheduleRebaseTask('Target branch moved');

      const decision = run.completeTask(Stage.Check, 'rebase-branch', { status: 'failed', reason: 'Rebase conflict' });

      expect(run.status).toBe('failed');
      expect(run.failure?.reason).toBe('task-failed');
      expect(run.failure?.taskId).toBe('rebase-branch');
      expect(decision.nextWork.kind).toBe('failed');
    });

    it('shaChanged=false does not invalidate checks', () => {
      const run = WorkflowRun.startWorkflow({
        id: 'run-1',
        issueId: 'issue-1',
        issueNumber: 188,
        definitions: [
          createStageDefinition(Stage.Plan),
          createStageDefinition(Stage.Build),
          createStageDefinition(Stage.Check),
          createStageDefinition(Stage.Integrate),
        ],
      }).run;

      run.completeTask(Stage.Plan, 'proposal', { status: 'completed' });
      run.completeTask(Stage.Plan, 'specs', { status: 'completed' });
      run.completeTask(Stage.Plan, 'design', { status: 'completed' });
      run.completeTask(Stage.Plan, 'tasks', { status: 'completed' });
      run.completeTask(Stage.Plan, 'self-review', { status: 'completed' });
      for (const check of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        run.recordCheckResult(Stage.Plan, { name: check, status: 'pass' });
      }
      run.approveStage(Stage.Plan, { output: { approved: true } });
      run.materializeTasks(Stage.Build, [{ id: 'build-1', title: 'Build task 1', order: 1 }]);
      run.completeTask(Stage.Build, 'build-1', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });

      run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
      run.scheduleRebaseTask('Target branch moved');

      const checkStage = run.stageRun(Stage.Check);
      checkStage.findCheck('review-passed').status = 'passed';
      checkStage.findCheck('merge-ready').status = 'passed';

      const decision = run.completeTask(Stage.Check, 'rebase-branch', {
        status: 'completed',
        output: {
          rebased: false,
          shaChanged: false,
          beforeBaseSha: 'abc123',
          afterBaseSha: 'abc123',
          beforeHeadSha: 'def456',
          afterHeadSha: 'def456',
        },
      });

      expect(checkStage.findCheck('review-passed').status).toBe('passed');
      expect(checkStage.findCheck('merge-ready').status).toBe('passed');
      expect(decision.events.filter((e: any) => e.type === 'check-invalidated')).toHaveLength(0);
    });

    it('shaChanged=true invalidates review-dependent checks', () => {
      const run = WorkflowRun.startWorkflow({
        id: 'run-1',
        issueId: 'issue-1',
        issueNumber: 188,
        definitions: [
          createStageDefinition(Stage.Plan),
          createStageDefinition(Stage.Build),
          createStageDefinition(Stage.Check),
          createStageDefinition(Stage.Integrate),
        ],
      }).run;

      run.completeTask(Stage.Plan, 'proposal', { status: 'completed' });
      run.completeTask(Stage.Plan, 'specs', { status: 'completed' });
      run.completeTask(Stage.Plan, 'design', { status: 'completed' });
      run.completeTask(Stage.Plan, 'tasks', { status: 'completed' });
      run.completeTask(Stage.Plan, 'self-review', { status: 'completed' });
      for (const check of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        run.recordCheckResult(Stage.Plan, { name: check, status: 'pass' });
      }
      run.approveStage(Stage.Plan, { output: { approved: true } });
      run.materializeTasks(Stage.Build, [{ id: 'build-1', title: 'Build task 1', order: 1 }]);
      run.completeTask(Stage.Build, 'build-1', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });

      run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
      run.scheduleRebaseTask('Target branch moved');

      const checkStage = run.stageRun(Stage.Check);
      checkStage.findCheck('review-passed').status = 'passed';
      checkStage.findCheck('merge-ready').status = 'passed';

      const decision = run.completeTask(Stage.Check, 'rebase-branch', {
        status: 'completed',
        events: ['code.changed'],
        output: {
          rebased: true,
          shaChanged: true,
          beforeBaseSha: 'abc123',
          afterBaseSha: 'def456',
          beforeHeadSha: 'ghi789',
          afterHeadSha: 'jkl012',
        },
      });

      expect(checkStage.findCheck('review-passed').status).toBe('pending');
      expect(checkStage.findCheck('merge-ready').status).toBe('pending');

      const invalidatedEvents = decision.events.filter((e: any) => e.type === 'check-invalidated');
      expect(invalidatedEvents.map((e: any) => e.checkName)).toContain('review-passed');
      expect(invalidatedEvents.map((e: any) => e.checkName)).toContain('merge-ready');
    });
  });

  describe('AC-4: approval not repairable and not blindly invalidated', () => {
    it('approval is not scheduled as repair task (rebase invalidates only after facts are reported)', () => {
      const run = WorkflowRun.startWorkflow({
        id: 'run-1',
        issueId: 'issue-1',
        issueNumber: 188,
        definitions: [
          createStageDefinition(Stage.Plan),
          createStageDefinition(Stage.Build),
          createStageDefinition(Stage.Check),
          createStageDefinition(Stage.Integrate),
        ],
      }).run;

      run.completeTask(Stage.Plan, 'proposal', { status: 'completed' });
      run.completeTask(Stage.Plan, 'specs', { status: 'completed' });
      run.completeTask(Stage.Plan, 'design', { status: 'completed' });
      run.completeTask(Stage.Plan, 'tasks', { status: 'completed' });
      run.completeTask(Stage.Plan, 'self-review', { status: 'completed' });
      for (const check of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        run.recordCheckResult(Stage.Plan, { name: check, status: 'pass' });
      }
      run.approveStage(Stage.Plan, { output: { approved: true } });
      run.materializeTasks(Stage.Build, [{ id: 'build-1', title: 'Build task 1', order: 1 }]);
      run.completeTask(Stage.Build, 'build-1', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });

      run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
      run.scheduleRebaseTask('Target branch moved');

      const checkStage = run.stageRun(Stage.Check);
      checkStage.findCheck('review-passed').status = 'passed';
      checkStage.findCheck('merge-ready').status = 'passed';

      const decision = run.completeTask(Stage.Check, 'rebase-branch', {
        status: 'completed',
        events: ['code.changed'],
        output: {
          rebased: true,
          shaChanged: true,
          beforeBaseSha: 'abc123',
          afterBaseSha: 'def456',
          beforeHeadSha: 'ghi789',
          afterHeadSha: 'jkl012',
        },
      });

      expect(checkStage.findTask('rebase-branch').status).toBe('completed');
      expect(checkStage.approval).toBeNull();
      const invalidatedEvents = decision.events.filter((e: any) => e.type === 'check-invalidated');
      expect(invalidatedEvents.map((e: any) => e.checkName)).toContain('review-passed');
      expect(invalidatedEvents.map((e: any) => e.checkName)).toContain('merge-ready');
    });

    it('rebase with shaChanged=false preserves review checks', () => {
      const run = WorkflowRun.startWorkflow({
        id: 'run-1',
        issueId: 'issue-1',
        issueNumber: 188,
        definitions: [
          createStageDefinition(Stage.Plan),
          createStageDefinition(Stage.Build),
          createStageDefinition(Stage.Check),
          createStageDefinition(Stage.Integrate),
        ],
      }).run;

      run.completeTask(Stage.Plan, 'proposal', { status: 'completed' });
      run.completeTask(Stage.Plan, 'specs', { status: 'completed' });
      run.completeTask(Stage.Plan, 'design', { status: 'completed' });
      run.completeTask(Stage.Plan, 'tasks', { status: 'completed' });
      run.completeTask(Stage.Plan, 'self-review', { status: 'completed' });
      for (const check of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        run.recordCheckResult(Stage.Plan, { name: check, status: 'pass' });
      }
      run.approveStage(Stage.Plan, { output: { approved: true } });
      run.materializeTasks(Stage.Build, [{ id: 'build-1', title: 'Build task 1', order: 1 }]);
      run.completeTask(Stage.Build, 'build-1', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });

      run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
      run.scheduleRebaseTask('Target branch moved');

      const checkStage = run.stageRun(Stage.Check);
      checkStage.findCheck('review-passed').status = 'passed';
      checkStage.findCheck('merge-ready').status = 'passed';

      const decision = run.completeTask(Stage.Check, 'rebase-branch', {
        status: 'completed',
        output: {
          rebased: false,
          shaChanged: false,
          beforeBaseSha: 'abc123',
          afterBaseSha: 'abc123',
          beforeHeadSha: 'def456',
          afterHeadSha: 'def456',
        },
      });

      expect(checkStage.findCheck('review-passed').status).toBe('passed');
      expect(checkStage.findCheck('merge-ready').status).toBe('passed');
      expect(decision.events.filter((e: any) => e.type === 'check-invalidated')).toHaveLength(0);
    });

    it('fix-review-findings invalidates ai-review, review-passed, merge-ready per Check invalidation policy', () => {
      const run = WorkflowRun.startWorkflow({
        id: 'run-1',
        issueId: 'issue-1',
        issueNumber: 188,
        definitions: [
          createStageDefinition(Stage.Plan),
          createStageDefinition(Stage.Build),
          createStageDefinition(Stage.Check),
          createStageDefinition(Stage.Integrate),
        ],
      }).run;

      run.completeTask(Stage.Plan, 'proposal', { status: 'completed' });
      run.completeTask(Stage.Plan, 'specs', { status: 'completed' });
      run.completeTask(Stage.Plan, 'design', { status: 'completed' });
      run.completeTask(Stage.Plan, 'tasks', { status: 'completed' });
      run.completeTask(Stage.Plan, 'self-review', { status: 'completed' });
      for (const check of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        run.recordCheckResult(Stage.Plan, { name: check, status: 'pass' });
      }
      run.approveStage(Stage.Plan, { output: { approved: true } });
      run.materializeTasks(Stage.Build, [{ id: 'build-1', title: 'Build task 1', order: 1 }]);
      run.completeTask(Stage.Build, 'build-1', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });

      run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
      run.recordCheckResult(Stage.Check, { name: 'health:check', status: 'pass' });
      run.recordCheckResult(Stage.Check, { name: 'review-passed', status: 'fail', message: 'review findings' });

      const checkStage = run.stageRun(Stage.Check);
      checkStage.findCheck('review-passed').status = 'pending';
      checkStage.findCheck('merge-ready').status = 'passed';
      checkStage.approval = { status: 'awaiting', requestedAt: '2026-01-01T00:00:00Z', output: null, respondedAt: null };
      checkStage.status = 'running';

      const decision = run.completeTask(Stage.Check, 'fix-review-findings', {
        status: 'completed',
        events: ['code.changed'],
        causedBy: { type: 'check-failure', checkName: 'review-passed', message: 'review findings' },
      });

      const invalidationEvents = decision.events.filter((e: any) => e.type === 'check-invalidated');
      expect(invalidationEvents.map((e: any) => e.checkName)).toContain('review-passed');
      expect(invalidationEvents.map((e: any) => e.checkName)).toContain('merge-ready');
      expect(checkStage.approval).toBeNull();
    });

    it('does not schedule review snapshot convergence before approval', () => {
      const run = WorkflowRun.startWorkflow({
        id: 'run-1',
        issueId: 'issue-1',
        issueNumber: 188,
        definitions: [
          createStageDefinition(Stage.Plan),
          createStageDefinition(Stage.Build),
          createStageDefinition(Stage.Check),
          createStageDefinition(Stage.Integrate),
        ],
      }).run;

      run.completeTask(Stage.Plan, 'proposal', { status: 'completed' });
      run.completeTask(Stage.Plan, 'specs', { status: 'completed' });
      run.completeTask(Stage.Plan, 'design', { status: 'completed' });
      run.completeTask(Stage.Plan, 'tasks', { status: 'completed' });
      run.completeTask(Stage.Plan, 'self-review', { status: 'completed' });
      for (const check of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
        run.recordCheckResult(Stage.Plan, { name: check, status: 'pass' });
      }
      run.approveStage(Stage.Plan, { output: { approved: true } });
      run.materializeTasks(Stage.Build, [{ id: 'build-1', title: 'Build task 1', order: 1 }]);
      run.completeTask(Stage.Build, 'build-1', { status: 'completed' });
      run.recordCheckResult(Stage.Build, { name: 'health:build', status: 'pass' });

      run.completeTask(Stage.Check, 'ai-review', { status: 'completed' });
      run.recordCheckResult(Stage.Check, {
        name: 'health:check',
        status: 'pass',
        output: { candidateHeadSha: 'old-head', command: 'npm test', duration: 1 },
      });
      run.recordCheckResult(Stage.Check, {
        name: 'review-passed',
        status: 'pass',
        output: { verdict: 'PASS', reviewReport: 'ok' },
      });

      const checkStage = run.stageRun(Stage.Check);
      expect(checkStage.tasks.some(task => task.id === 'check:converge-review-snapshot')).toBe(false);

      const decision = run.recordCheckResult(Stage.Check, {
        name: 'merge-ready',
        status: 'pass',
        output: mergeReadyOutput('old-head'),
      });

      expect(checkStage.findCheck('health:check').status).toBe('passed');
      expect(checkStage.findCheck('merge-ready').status).toBe('passed');
      expect(checkStage.approval?.status).toBe('awaiting');
      expect(decision.nextWork).toEqual({ kind: 'await-approval', stage: Stage.Check });
      expect(decision.events.some((event: any) => event.type === 'check-invalidated')).toBe(false);
    });

    it('generic fix-review-findings invalidates persisted review artifact only after WorkflowRun accepts invalidation', async () => {
      const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-generic-review-'));
      const changeDir = path.join(tmpRoot, 'change');
      fs.mkdirSync(changeDir, { recursive: true });
      const reviewPath = path.join(changeDir, 'review.md');
      const reviewBody = '# stale review\n<promise>FAIL</promise>\n';
      fs.writeFileSync(reviewPath, reviewBody);

      const checkpointDeletes: Array<{ stage: string; step: string }> = [];
      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry(),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: tmpRoot,
      });
      const ctx = makeMockContext(Stage.Check, {
        artifactManager: {
          getChangeDir: vi.fn().mockReturnValue(changeDir),
          createChangeDir: vi.fn(),
        } as any,
        checkpointManager: {
          getResumeSteps: vi.fn().mockReturnValue(['ai-review']),
          deleteStep: vi.fn().mockImplementation((_issueNumber: number, stage: string, step: string) => {
            checkpointDeletes.push({ stage, step });
          }),
        } as any,
      });

      (runner as any).applyAcceptedTaskSideEffects(ctx, [
        { type: 'task-invalidated', stage: Stage.Check, taskId: 'ai-review', reason: 'review changed' },
      ]);

      expect(fs.existsSync(reviewPath)).toBe(false);
      const staleReviews = fs.readdirSync(changeDir).filter(name => name.startsWith('review.stale-'));
      expect(staleReviews).toHaveLength(1);
      expect(fs.readFileSync(path.join(changeDir, staleReviews[0]), 'utf8')).toBe(reviewBody);
      expect(checkpointDeletes).toEqual([{ stage: 'check', step: 'ai-review' }]);
    });

    it('generic review repair convergence follows accepted invalidation events', async () => {
      const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-generic-review-convergence-'));
      const changeDir = path.join(tmpRoot, 'change');
      fs.mkdirSync(changeDir, { recursive: true });

      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry(),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: tmpRoot,
      });
      const run = WorkflowRun.startWorkflow({
        id: 'run-1',
        issueId: 'issue-1',
        issueNumber: 188,
        definitions: [
          createStageDefinition(Stage.Plan),
          createStageDefinition(Stage.Build),
          createStageDefinition(Stage.Check),
          createStageDefinition(Stage.Integrate),
        ],
      }).run;
      const checkStage = run.stageRun(Stage.Check);
      checkStage.start();
      checkStage.findTask('ai-review').status = 'completed';
      checkStage.findCheck('review-passed').status = 'pending';
      checkStage.findCheck('review-passed').output = { verdict: 'FAIL', blockingItems: ['F-001'] };
      checkStage.tasks.push({
        id: 'fix-review-findings',
        title: 'Fix review findings',
        status: 'completed',
        attempts: 1,
        artifacts: [],
        events: ['code.changed'],
        output: {
          attemptedItemIds: ['F-001'],
          resolvedItemIds: ['F-001'],
          unresolvedItemIds: [],
        },
        reason: null,
        causedBy: null,
        latestAttempt: null,
        duration: 1,
        terminal: true,
        resetForFreshAttempt: vi.fn(),
        startWorkAttempt: vi.fn(),
        completeWorkAttempt: vi.fn(),
        failWorkAttempt: vi.fn(),
        snapshot: vi.fn(),
      } as any);
      const ctx = makeMockContext(Stage.Check, {
        artifactManager: {
          getChangeDir: vi.fn().mockReturnValue(changeDir),
          createChangeDir: vi.fn(),
        } as any,
        workflowRun: run,
      });

      (runner as any).applyAcceptedTaskSideEffects(ctx, [
        { type: 'task-completed', stage: Stage.Check, taskId: 'fix-review-findings' },
        { type: 'check-invalidated', stage: Stage.Check, checkName: 'review-passed', reason: 'code.changed reset' },
      ]);

      const verificationContext = JSON.parse(fs.readFileSync(path.join(changeDir, '.verification-context.json'), 'utf-8'));
      expect(verificationContext).toMatchObject({
        failedCheckName: 'review-passed',
        attemptedItemIds: ['F-001'],
        resolvedItemIds: ['F-001'],
        unresolvedItemIds: [],
        reactionAttempt: 1,
      });
    });

    it('generic review artifact invalidation follows custom review producer task ids', async () => {
      const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-generic-custom-review-'));
      const changeDir = path.join(tmpRoot, 'change');
      fs.mkdirSync(changeDir, { recursive: true });
      const reviewPath = path.join(changeDir, 'review.md');
      fs.writeFileSync(reviewPath, '# stale custom review\n<promise>FAIL</promise>\n');

      const checkpointDeletes: Array<{ stage: string; step: string }> = [];
      const customCheckStage = {
        ...createStageDefinition(Stage.Check),
        tasks: [{ id: 'custom-review', title: 'Custom review', uses: 'mohist/agent' }],
        checks: [
          { name: 'verify-custom', title: 'Verify custom' },
          { name: 'custom-verdict', title: 'Custom verdict' },
          { name: 'custom-candidate', title: 'Custom candidate' },
        ],
        repairPolicies: [{ checkName: 'custom-verdict', fixTaskId: 'fix-custom-review', fixTaskTitle: 'Fix custom review', maxAttempts: 1 }],
        invalidationPolicy: {
          entries: [
            {
              trigger: 'task-completion' as const,
              triggerTaskId: 'fix-custom-review',
              invalidates: { tasks: ['custom-review'], checks: ['verify-custom', 'custom-verdict', 'custom-candidate'], approval: true },
            },
          ],
        },
      };
      const runner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry(),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: stage => stage === Stage.Check ? customCheckStage : createStageDefinition(stage),
        worktreePath: tmpRoot,
      });
      const ctx = makeMockContext(Stage.Check, {
        artifactManager: {
          getChangeDir: vi.fn().mockReturnValue(changeDir),
          createChangeDir: vi.fn(),
        } as any,
        checkpointManager: {
          getResumeSteps: vi.fn().mockReturnValue(['custom-review']),
          deleteStep: vi.fn().mockImplementation((_issueNumber: number, stage: string, step: string) => {
            checkpointDeletes.push({ stage, step });
          }),
        } as any,
      });

      (runner as any).applyAcceptedTaskSideEffects(ctx, [
        { type: 'task-invalidated', stage: Stage.Check, taskId: 'custom-review', reason: 'custom review changed' },
      ]);

      expect(fs.existsSync(reviewPath)).toBe(false);
      expect(checkpointDeletes).toEqual([{ stage: 'check', step: 'custom-review' }]);
    });
  });

  describe('AC-5: generic runner is the workflow execution path', () => {
    let tmpDir: string;

    beforeEach(() => {
      tmpDir = require('fs').mkdtempSync(require('path').join(require('os').tmpdir(), 'mohist-test-'));
      require('fs').writeFileSync(
        require('path').join(tmpDir, 'workflow.yaml'),
        'stages:\n  - stage: explore\n  - stage: plan\n  - stage: build\n  - stage: check\n  - stage: integrate\n  - stage: done\n',
        'utf-8',
      );
    });

    afterEach(() => {
      try { require('fs').rmSync(tmpDir, { recursive: true, force: true }); } catch { /* noop */ }
    });

    it('GenericStageRunner handles every compiled pipeline stage from the definition', async () => {
      const genericRunner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry(),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: tmpDir,
      });

      expect(genericRunner.canHandle(Stage.Plan)).toBe(true);
      expect(genericRunner.canHandle(Stage.Build)).toBe(true);
      expect(genericRunner.canHandle(Stage.Check)).toBe(true);
      expect(genericRunner.canHandle(Stage.Integrate)).toBe(true);
    });

    it('GenericStageRunner can be enabled for only selected stages', () => {
      const genericRunner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry(),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: tmpDir,
        enabledStages: [Stage.Build],
      });

      expect(genericRunner.canHandle(Stage.Build)).toBe(true);
      expect(genericRunner.canHandle(Stage.Plan)).toBe(false);
      expect(genericRunner.canHandle(Stage.Check)).toBe(false);
      expect(genericRunner.canHandle(Stage.Integrate)).toBe(false);
    });

    it('WorkflowEngine refuses to execute workflow without aggregate workflow service', async () => {
      const genericRunner = new GenericStageRunner({
        taskLoaderRegistry: createBasicTaskLoaderRegistry(),
        taskHandlerRegistry: createBasicTaskHandlerRegistry(),
        checkRegistry: createBasicCheckRegistry({}),
        getStageDefinition: createStageDefinition,
        worktreePath: tmpDir,
      });
      const legacyRunner: StageRunner = {
        canHandle: vi.fn().mockImplementation((stage: Stage) => stage === Stage.Plan),
        run: vi.fn().mockResolvedValue({ success: true, output: null, checkResults: [] }),
      };
      const issue = makeMockContext(Stage.Plan).issue;
      const issueRepo = {
        updateStage: vi.fn(),
        setApprovalState: vi.fn(),
        clearApprovalState: vi.fn(),
        updateStatus: vi.fn(),
        findById: vi.fn().mockReturnValue(issue),
      } as any;

      const engine = new WorkflowEngine({
        runners: [genericRunner, legacyRunner],
        issueRepo,
        eventBus: new EventBus(),
        checkpointManager: makeMockContext(Stage.Plan).checkpointManager,
        artifactManager: makeMockContext(Stage.Plan).artifactManager,
        worktreeManager: makeMockContext(Stage.Plan).worktreeManager,
        projectRepo: makeMockContext(Stage.Plan).projectRepo,
      });

      const result = await engine.run(issue, { cwd: tmpDir } as any);

      expect(legacyRunner.run).not.toHaveBeenCalled();
      expect(result).toEqual({
        completed: false,
        stage: Stage.Plan,
        message: 'WorkflowApplicationService is required for workflow execution',
      });
    });

    it('default check registry names match declared health checks', async () => {
      const { createDefaultCheckRegistry } = await import('../../src/services/agent-runner-service');
      const { DEFAULT_HEALTH_GATE_POLICIES } = await import('../../src/workflow/workflow-loader');
      const { createWorkflowDefinitionSnapshot } = await import('../../src/workflow/domain');
      const { Stage } = await import('../../src/types');

      const registry = createDefaultCheckRegistry({
        worktreePath: tmpDir,
        healthGatePolicies: DEFAULT_HEALTH_GATE_POLICIES,
      });

      for (const checkName of ['health:plan', 'health:build', 'health:check', 'health:integrate']) {
        expect(registry.get(checkName)).toBeDefined();
      }

      const snapshot = createWorkflowDefinitionSnapshot({
        definition: {
          id: 'test/health-from-definition',
          stages: [{
            stage: Stage.Build,
            tasks: [],
            checks: [{
              name: 'health:build',
              title: 'Build health',
              uses: 'mohist/health-gate',
              with: { command: 'printf ok', timeout: 1234 },
            }],
          }],
        },
      });
      const definitionRegistry = createDefaultCheckRegistry({
        worktreePath: tmpDir,
        healthGatePolicies: DEFAULT_HEALTH_GATE_POLICIES,
        workflowDefinitionSnapshot: snapshot,
      });
      const check = await definitionRegistry.get('health:build')!(makeMockContext(Stage.Build) as any);
      const result = await check.run(makeMockContext(Stage.Build) as any);

      expect(result.output).toMatchObject({ command: 'printf ok', timeout: 1234 });
    });
  });

  describe('AC-6: stage definition policy preservation', () => {
    it('Plan stage definition includes approval policy', () => {
      const def = createStageDefinition(Stage.Plan);
      expect(def.requiresApproval).toBe(true);
      expect(def.approvalCheckName).toBe('user-approval');
      expect(def.approvalPolicy).toBeDefined();
      expect(def.approvalPolicy?.checkName).toBe('user-approval');
    });

    it('Check stage definition includes repair policies', () => {
      const def = createStageDefinition(Stage.Check);
      expect(def.repairPolicies).toBeDefined();
      expect(def.repairPolicies.length).toBeGreaterThan(0);
      expect(def.repairPolicies.some((p: any) => p.checkName === 'health:check')).toBe(true);
      expect(def.repairPolicies.some((p: any) => p.checkName === 'review-passed')).toBe(true);
      expect(def.checks.map(check => check.name)).toEqual(['health:check', 'review-passed', 'merge-ready']);
    });

    it('Check stage definition includes code.changed invalidation policy', () => {
      const def = createStageDefinition(Stage.Check);
      expect(def.invalidationPolicy).toBeDefined();
      const entry = def.invalidationPolicy?.entries.find((e: any) => e.eventName === 'code.changed');
      expect(entry).toBeDefined();
      expect(entry).toMatchObject({
        trigger: 'task-completion',
        reason: 'code.changed reset',
        invalidates: {
          tasks: ['ai-review'],
          checks: ['health:check', 'review-passed', 'merge-ready'],
          approval: true,
        },
      });
      expect(entry?.triggerTaskId).toBeUndefined();
    });

    it('Build stage definition includes Ralph work source', () => {
      const def = createStageDefinition(Stage.Build);
      expect(def.workSources).toBeDefined();
      const ralphSource = def.workSources?.find((s: any) => s.kind === 'ralph');
      expect(ralphSource).toBeDefined();
    });

    it('Integrate stage definition uses service-call execution for all tasks', () => {
      const def = createStageDefinition(Stage.Integrate);
      expect(def.taskExecutionPolicies).toBeDefined();
      for (const policy of def.taskExecutionPolicies ?? []) {
        expect(policy.kind).toBe('service-call');
      }
    });
  });
});
