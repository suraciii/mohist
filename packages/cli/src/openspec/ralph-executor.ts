import * as fs from 'fs';
import * as path from 'path';
import { execFile } from 'child_process';
import { promisify } from 'util';
import type { OpenSpecChange } from './detector';
import type { Task } from './context-assembler';
import type { AgentConfig } from '../agents/agent-config';
import { withSession } from '../agent-runtime/agent-session';
import type { AgentSessionOptions, AcpSessionResult } from '../agent-runtime/agent-session';
import { WorktreeManager } from '../git/worktree-manager';
import { Log } from '../util/log';
import { load as loadConfig, getAgentTimeoutConfig } from '../config/config-loader';
import {
  FAILURE_CATEGORY_CONFIGS,
  RalphTaskLoader,
  categorizeFailure,
  executeRalphTask,
  findNextPendingTask,
  getOrderValue,
  readTasks,
  sortTasksByOrder,
  validateTaskDependencies,
  type DependencyValidationResult,
  type FailureCategory,
  type FailureCategoryConfig,
  type RalphTaskHandlerDeps,
  type RalphTaskHandlerOptions,
} from './ralph';
import { createDefaultTaskHandlerRegistry } from '../workflow/tasks';

const execFileAsync = promisify(execFile);

const log = Log.create({ service: 'ralph' });

type SessionRunner = (options: AgentSessionOptions) => Promise<AcpSessionResult>;

let _acpSessionRunner: SessionRunner = withSession;

export function setAcpSessionRunner(runner: SessionRunner): void {
  _acpSessionRunner = runner;
}

export function resetAcpSessionRunner(): void {
  _acpSessionRunner = withSession;
}
import type { EventBus } from '../services/event-bus';
import type { StageTaskResult } from '../workflow/stage-context';
import type { StageExecutionRepo } from '../db/stage-execution-repo';
import type { SessionObserver } from '../agent-runtime/session-observer';
import { createWorkflowSessionObservers } from '../agent-runtime';
import type { WorkflowApplicationRuntime } from '../workflow/stage-context';
import { Stage } from '../types';

export {
  FAILURE_CATEGORY_CONFIGS,
  categorizeFailure,
  findNextPendingTask,
  getOrderValue,
  readTasks,
  sortTasksByOrder,
  validateTaskDependencies,
};
export type { DependencyValidationResult, FailureCategory, FailureCategoryConfig };

export interface RalphExecutorContext {
  worktreePath: string;
  projectPath: string;
  issueId?: string;
  projectId?: string;
  eventBus?: EventBus;
  executionId?: string;
  onTaskStart?: (task: Task) => void;
  onTaskComplete?: (task: Task, success: boolean, output: string) => void;
  onLoopComplete?: (results: RalphLoopResult) => void;
  onAskUser?: (question: string, taskId: string) => Promise<string>;
  issueNumber?: number;
  issueTitle?: string;
  issueBody?: string;
  stageTimeoutMs?: number;
  onProcessSpawned?: (proc: import('child_process').ChildProcess) => void;
  worktreeManager?: WorktreeManager;
  stage?: string;
  model?: string;
  agentConfig?: AgentConfig;
  stageExecutionId?: string;
  stageExecutionRepo?: StageExecutionRepo;
  workflowLogRepo?: import('../db/workflow-log-repo').WorkflowLogRepo;
  sessionStreamLogRepo?: import('../db/session-stream-log-repo').SessionStreamLogRepo;
  coderSessionRepo?: import('../db/coder-session-repo').CoderSessionRepo;
  observers?: SessionObserver[];
  syncTasksToStageState?: () => void;
  syncTasksToWorkflowRun?: () => void;
  workflowApplicationService?: Pick<WorkflowApplicationRuntime, 'completeTask' | 'startTaskAttempt'>;
}

export interface RalphLoopResult {
  completed: number;
  failed: number;
  skipped: number;
  total: number;
  taskResults: TaskResult[];
  success: boolean;
  paused?: boolean;
  pausedTaskId?: string;
  pauseReason?: string;
}

export interface TaskResult {
  taskId: string;
  status: 'completed' | 'failed' | 'skipped';
  attempts: number;
  output?: string;
  error?: string;
}

export interface RalphTaskOptions {
  maxRetries?: number;
  onRetry?: (task: Task, attempt: number, error: string) => void;
}

function writeTasksFile(tasksPath: string, tasks: Task[]): void {
  try {
    const tasksFile = { version: 1, tasks };
    fs.writeFileSync(tasksPath, JSON.stringify(tasksFile, null, 2), 'utf-8');
  } catch (e) {
    log.error('writeTasksFile failed', { tasksPath, error: e instanceof Error ? e.message : String(e) });
  }
}

function restoreUnrequestedTaskProgress(
  tasksPath: string,
  originalTasks: Task[],
  allowedTaskIds: Set<string>,
): void {
  if (originalTasks.length === 0) return;

  const latestTasks = readTasks(tasksPath);
  if (!latestTasks) return;

  const originalById = new Map(originalTasks.map(task => [task.id, task]));
  let changed = false;

  for (const task of latestTasks) {
    if (allowedTaskIds.has(task.id)) continue;

    const original = originalById.get(task.id);
    if (!original) continue;

    if (task.passes !== original.passes) {
      task.passes = original.passes;
      changed = true;
    }
    if (task.error !== original.error) {
      task.error = original.error;
      changed = true;
    }
    if (task.attempts !== original.attempts) {
      task.attempts = original.attempts;
      changed = true;
    }
    const originalDurations = original.durations ?? [];
    const taskDurations = task.durations ?? [];
    if (JSON.stringify(taskDurations) !== JSON.stringify(originalDurations)) {
      task.durations = [...originalDurations];
      changed = true;
    }
  }

  if (changed) {
    writeTasksFile(tasksPath, latestTasks);
  }
}

function reportTaskToAggregate(
  context: RalphExecutorContext,
  task: Task,
  result: {
    status: 'completed' | 'failed' | 'skipped';
    attempts: number;
    duration: number;
    output?: unknown;
    reason?: string;
  },
): void {
  if (!context.workflowApplicationService || !context.issueId) return;
  context.workflowApplicationService.completeTask({
    issueId: context.issueId,
    stage: Stage.Build,
    taskId: task.id,
    result: {
      status: result.status,
      attempts: result.attempts,
      duration: result.duration,
      artifacts: [],
      output: result.output,
      reason: result.reason,
    },
  });
}

async function commitTasksFile(
  tasksPath: string,
  worktreePath: string,
  taskId: string,
  passes: boolean
): Promise<void> {
  try {
    const relPath = path.relative(worktreePath, tasksPath);
    await execFileAsync('git', ['add', '--', relPath], { cwd: worktreePath });
    const status = passes ? 'passes=true' : 'passes=false';
    await execFileAsync('git', ['commit', '-m', `chore(tasks): update ${taskId} ${status}`, '--no-verify'], { cwd: worktreePath });
    log.info('Committed tasks.json', { taskId, status });
  } catch (e) {
    log.warn('commitTasksFile failed', { taskId, error: e instanceof Error ? e.message : String(e) });
  }
}

async function commitAggregateTaskChanges(
  worktreePath: string,
  taskId: string,
  issueNumber?: number,
): Promise<string | undefined> {
  try {
    const { stdout: statusOut } = await execFileAsync(
      'git',
      ['status', '--porcelain', '--ignore-submodules'],
      { cwd: worktreePath },
    );
    const changedLines = statusOut
      .split('\n')
      .filter(line => line.trim() !== '')
      .filter(line => !line.includes('openspec/changes/') && !line.includes('.opencode/'));
    if (changedLines.length === 0) return undefined;

    await execFileAsync('git', ['add', '--', ':!openspec/changes/', ':!.opencode/'], { cwd: worktreePath });
    const issuePrefix = issueNumber ? `issue-${issueNumber}` : 'aggregate';
    await execFileAsync('git', ['commit', '-m', `build(${issuePrefix}): complete ${taskId}`], { cwd: worktreePath });
    const { stdout } = await execFileAsync('git', ['rev-parse', 'HEAD'], { cwd: worktreePath });
    const commitSha = stdout.trim();
    log.info('Committed aggregate task changes', { taskId, commitSha });
    return commitSha;
  } catch (e) {
    log.warn('commitAggregateTaskChanges failed', { taskId, error: e instanceof Error ? e.message : String(e) });
    return undefined;
  }
}

export interface RalphExecutorOptions extends RalphTaskOptions {
  resumeFromTaskIndex?: number;
  skipTaskIds?: string[];
  onTaskCompleted?: (taskId: string) => void;
  ignoreTaskFileProgress?: boolean;
  onlyTaskId?: string;
}

function writeTaskLog(
  workflowLogRepo: import('../db/workflow-log-repo').WorkflowLogRepo | undefined,
  issueId: string,
  eventType: string,
  data: object
): void {
  if (!workflowLogRepo) return;
  try {
    workflowLogRepo.insert(issueId, null, eventType, data);
  } catch (e) {
    log.warn('workflowLogRepo.insert failed', { eventType, issueId, error: e instanceof Error ? e.message : String(e) });
  }
}

export async function runRalphLoop(
  change: OpenSpecChange,
  context: RalphExecutorContext,
  options: RalphExecutorOptions = {}
): Promise<RalphLoopResult> {
  const originalTasks = options.ignoreTaskFileProgress && options.onlyTaskId
    ? (readTasks(change.tasksPath) ?? [])
    : [];
  const loader = new RalphTaskLoader();
  const loaderResult = loader.load(change, { ignoreTaskFileProgress: options.ignoreTaskFileProgress });

  if (loaderResult.tasks.length === 0) {
    return {
      completed: 0,
      failed: 0,
      skipped: 0,
      total: 0,
      taskResults: [],
      success: false,
    };
  }

  if (!loaderResult.validation.valid) {
    for (const err of loaderResult.validation.errors) {
      log.error('Task dependency validation failed', { issueId: context.issueId || '', error: err });
    }
    const result: RalphLoopResult = {
      completed: 0,
      failed: loaderResult.tasks.length,
      skipped: 0,
      total: loaderResult.tasks.length,
      taskResults: [],
      success: false,
      pauseReason: `Task dependency validation failed: ${loaderResult.validation.errors.join('; ')}`,
    };
    const firstTask = loaderResult.sortedTasks[0];
    if (firstTask) {
      reportTaskToAggregate(context, firstTask, {
        status: 'failed',
        attempts: firstTask.attempts,
        duration: 0,
        output: { validationErrors: loaderResult.validation.errors },
        reason: result.pauseReason,
      });
    }
    context.onLoopComplete?.(result);
    return result;
  }

  const sortedTasks = loaderResult.sortedTasks;
  const requestedTask = options.onlyTaskId ? sortedTasks.find(task => task.id === options.onlyTaskId) : undefined;
  const nextPendingTask = options.onlyTaskId ? findNextPendingTask(sortedTasks) : null;
  if (options.onlyTaskId && !requestedTask) {
    return {
      completed: 0,
      failed: 1,
      skipped: 0,
      total: 1,
      taskResults: [{ taskId: options.onlyTaskId, status: 'failed', attempts: 0, error: `Task ${options.onlyTaskId} not found` }],
      success: false,
      pauseReason: `Task ${options.onlyTaskId} not found`,
    };
  }

  if (options.onlyTaskId && requestedTask && !options.ignoreTaskFileProgress && nextPendingTask?.id !== requestedTask.id) {
    const failureReason = requestedTask.passes
      ? `Task ${options.onlyTaskId} is already passed and cannot be executed again`
      : `Task ${options.onlyTaskId} is not ready; next pending task is ${nextPendingTask?.id ?? 'none'}`;
    return {
      completed: 0,
      failed: 1,
      skipped: 0,
      total: 1,
      taskResults: [{ taskId: options.onlyTaskId, status: 'failed', attempts: requestedTask.attempts, error: failureReason }],
      success: false,
      pauseReason: failureReason,
    };
  }

  const skipTaskIds = new Set(options.skipTaskIds ?? []);

  if (!options.onlyTaskId && sortedTasks.length > 0 && sortedTasks.every(t => t.passes)) {
    log.info('All tasks already passed, returning success', {
      issueId: context.issueId || '',
      total: sortedTasks.length,
    });
    const alreadyPassedResult: RalphLoopResult = {
      completed: sortedTasks.length,
      failed: 0,
      skipped: 0,
      total: sortedTasks.length,
      taskResults: [],
      success: true,
    };
    context.onLoopComplete?.(alreadyPassedResult);
    return alreadyPassedResult;
  }

  for (const task of sortedTasks) {
    if (skipTaskIds.has(task.id) && !task.passes) {
      task.passes = true;
      task.error = null;
      reportTaskToAggregate(context, task, {
        status: 'completed',
        attempts: task.attempts || 1,
        duration: 0,
        reason: 'Recovered from checkpoint',
      });
    }
  }
  if (skipTaskIds.size > 0) {
    writeTasksFile(change.tasksPath, sortedTasks);
    log.info('Restored completed tasks from checkpoint', {
      issueId: context.issueId || '',
      completedIds: [...skipTaskIds],
    });
  }

  if (skipTaskIds.size > 0 && sortedTasks.every(t => t.passes)) {
    log.info('recovered-from-checkpoint', {
      issueId: context.issueId || '',
      total: sortedTasks.length,
    });
    const recoveredResult: RalphLoopResult = {
      completed: sortedTasks.length,
      failed: 0,
      skipped: 0,
      total: sortedTasks.length,
      taskResults: [],
      success: true,
    };
    context.onLoopComplete?.(recoveredResult);
    return recoveredResult;
  }

  const taskResults: TaskResult[] = [];
  let completed = 0;
  let failed = 0;
  let skipped = 0;

  const sseIssueId = context.issueId || String(context.issueNumber ?? '');
  const logIssueId = context.issueId || '';

  const pending = sortedTasks.filter(t => !t.passes).length;
  const passed = sortedTasks.filter(t => t.passes).length;
  log.info('Ralph loop entry', {
    issueId: logIssueId,
    total: sortedTasks.length,
    pending,
    passed,
  });

  const emitTaskUpdate = (
    taskExecutionId: string,
    taskId: string,
    taskTitle: string,
    taskIndex: number,
    totalTasks: number,
    status: 'started' | 'completed' | 'failed' | 'retrying',
    attempt?: number,
    error?: string
  ) => {
    if (!context.eventBus) return;
    try {
      context.eventBus.emit('ralph_task_update', {
        issueId: sseIssueId,
        projectId: context.projectId ?? '',
        executionId: taskExecutionId,
        taskId,
        taskIndex,
        totalTasks,
        status,
        attempt,
        error,
      });
    } catch (e) {
      log.warn('eventBus.emit failed for ralph_task_update', { taskId, status, error: e instanceof Error ? e.message : String(e) });
    }
    try {
      context.eventBus.emit('stage_task_update', {
        issueId: sseIssueId,
        projectId: context.projectId ?? '',
        stage: 'build',
        taskId,
        taskTitle,
        status,
        attempt: attempt ?? 1,
        artifacts: [] as string[],
      });
    } catch (e) {
      log.warn('eventBus.emit failed for stage_task_update', { taskId, status, error: e instanceof Error ? e.message : String(e) });
    }
  };

  const emitLoopProgress = (completedCount: number, failedCount: number, total: number) => {
    if (!context.eventBus) return;
    try {
      context.eventBus.emit('ralph_loop_progress', {
        issueId: sseIssueId,
        projectId: context.projectId ?? '',
        executionId: context.executionId ?? '',
        completed: completedCount,
        failed: failedCount,
        total,
      });
    } catch (e) {
      log.warn('eventBus.emit failed for ralph_loop_progress', { error: e instanceof Error ? e.message : String(e) });
    }
  };

  const appendStageTaskResult = (
    taskId: string,
    title: string,
    status: 'completed' | 'failed',
    attempts: number,
    duration: number,
  ) => {
    if (!context.stageExecutionId || !context.stageExecutionRepo) return;
    try {
      const result: StageTaskResult = {
        taskId,
        title,
        status,
        artifacts: [],
        attempts,
        duration,
      };
      context.stageExecutionRepo.appendTaskResult(context.stageExecutionId, result);
    } catch (e) {
      log.warn('appendStageTaskResult failed', { taskId, status, error: e instanceof Error ? e.message : String(e) });
    }
  };

  const processedTaskIds = new Set<string>();
  const taskObservers = context.observers ?? createWorkflowSessionObservers({
    eventBus: context.eventBus,
    workflowLogRepo: context.workflowLogRepo,
    sessionStreamLogRepo: context.sessionStreamLogRepo,
    coderSessionRepo: context.coderSessionRepo,
    stage: context.stage,
    title: 'Build stage',
  });

  const persistLoopTaskState = (
    taskId: string,
    updates: Partial<Pick<Task, 'passes' | 'attempts' | 'error' | 'durations'>>,
  ) => {
    const latestTasks = readTasks(change.tasksPath) ?? sortedTasks;
    const task = latestTasks.find(candidate => candidate.id === taskId);
    if (!task) return;

    if (updates.passes !== undefined) task.passes = updates.passes;
    if (updates.attempts !== undefined) task.attempts = updates.attempts;
    if (updates.error !== undefined) task.error = updates.error;
    if (updates.durations !== undefined) task.durations = [...updates.durations];

    writeTasksFile(change.tasksPath, latestTasks);
    sortedTasks.splice(0, sortedTasks.length, ...latestTasks);
    context.syncTasksToStageState?.();
  };

  while (true) {
    const nextTask = options.onlyTaskId
      ? sortedTasks.find(task => task.id === options.onlyTaskId && !processedTaskIds.has(task.id))
      : findNextPendingTask(sortedTasks);
    if (!nextTask || processedTaskIds.has(nextTask.id)) {
      if (!nextTask) {
        const remainingPending = sortedTasks.filter(t => !t.passes);
        if (remainingPending.length > 0) {
          const blockedIds = remainingPending.map(t => {
            const deps = (t.dependsOn ?? []).filter(d => !sortedTasks.find(x => x.id === d)?.passes);
            return `${t.id} (blocked by: ${deps.length > 0 ? deps.join(', ') : 'unknown'})`;
          });
          log.warn('Deadlock detected: pending tasks remain but none are ready', {
            issueId: logIssueId,
            blockedTasks: blockedIds,
          });
          failed += remainingPending.length;
          for (const t of remainingPending) {
            taskResults.push({
              taskId: t.id,
              status: 'failed',
              attempts: t.attempts,
              error: `Deadlock: task blocked by unmet dependencies`,
            });
          }
          const deadlockResult: RalphLoopResult = {
            completed,
            failed,
            skipped,
            total: sortedTasks.length,
            taskResults,
            success: false,
            pauseReason: `Deadlock: ${remainingPending.length} task(s) blocked by unmet dependencies: ${blockedIds.join('; ')}`,
          };
          context.onLoopComplete?.(deadlockResult);
          return deadlockResult;
        }
      }
      if (completed === 0 && failed === 0) {
        log.warn('No pending tasks found — all tasks have passes=true', {
          issueId: logIssueId,
          total: sortedTasks.length,
        });
      }
      break;
    }

    context.onTaskStart?.(nextTask);

    const taskExecutionId = context.executionId
      ? `${context.executionId}-${nextTask.id}`
      : undefined;

    if (context.workflowApplicationService && context.issueId) {
      try {
        context.workflowApplicationService.startTaskAttempt({
          issueId: context.issueId,
          stage: Stage.Build,
          taskId: nextTask.id,
          evidence: { executionId: taskExecutionId ?? context.executionId },
        });
      } catch (e) {
        log.warn('startTaskAttempt failed for Ralph task', { taskId: nextTask.id, error: e instanceof Error ? e.message : String(e) });
      }
    }

    const timeoutConfig = getAgentTimeoutConfig(loadConfig());
    const perTaskTimeout = timeoutConfig.taskTimeout * 1000;

    const handlerOptions: RalphTaskHandlerOptions = {
      maxRetries: options.maxRetries,
      onRetry: options.onRetry,
      onRetryLog: (taskId: string, attempt: number, category: any, error: string, executionId?: string) => {
        writeTaskLog(context.workflowLogRepo, logIssueId, 'task_retrying', {
          taskId,
          attempt,
          category,
          error: error?.slice(0, 500),
          executionId,
        });
      },
      onTasksPersisted: () => {
        const latestTasks = readTasks(change.tasksPath);
        if (latestTasks) {
          sortedTasks.splice(0, sortedTasks.length, ...latestTasks);
        }
        context.syncTasksToStageState?.();
      },
      emitTaskUpdate: emitTaskUpdate,
      executionId: context.executionId,
      stage: context.stage,
      model: context.model,
      agentConfig: context.agentConfig,
      taskTimeout: perTaskTimeout,
    };

    const mockCtx = {
      issue: { id: context.issueId ?? '', number: context.issueNumber ?? 0, title: context.issueTitle ?? '', body: context.issueBody ?? '', projectId: context.projectId ?? '' },
      acpOptions: {},
      worktreeManager: context.worktreeManager,
    } as any;

    const deps: RalphTaskHandlerDeps = {
      worktreePath: context.worktreePath,
      acpSessionRunner: _acpSessionRunner,
      worktreeManager: context.worktreeManager,
      observers: taskObservers,
      onBeforeKill: context.worktreeManager
        ? async (_cwd: string) => {
            return false;
          }
        : undefined,
    };

    writeTaskLog(context.workflowLogRepo, logIssueId, 'task_started', {
      taskId: nextTask.id,
      attempt: nextTask.attempts + 1,
      executionId: taskExecutionId,
    });

    const loadedTask = {
      task: nextTask,
      totalTasks: sortedTasks.length,
      change,
    };

    const executableTask = loaderResult.executableTasks.find(task => task.taskId === nextTask.id);
    const taskRegistry = createDefaultTaskHandlerRegistry({
      ralphTask: {
        ...deps,
        createOptions: () => handlerOptions,
      },
    });

    const taskHandler = taskRegistry.get('ralph-task');
    const taskStartTime = Date.now();
    const directHandlerResult = options.onlyTaskId && executableTask && taskHandler
      ? undefined
      : await executeRalphTask(loadedTask, mockCtx, handlerOptions, deps);
    const stageTaskResult = options.onlyTaskId && executableTask && taskHandler
      ? await taskHandler(executableTask, mockCtx)
      : directHandlerResult!.stageTaskResult;

    const persistedTask = readTasks(change.tasksPath)?.find(task => task.id === nextTask.id);
    const handlerResult = {
      stageTaskResult,
      paused: directHandlerResult?.paused ?? (stageTaskResult.status === 'failed' && !persistedTask?.passes),
      pauseReason: directHandlerResult?.pauseReason ?? stageTaskResult.reason,
      lastError: directHandlerResult?.lastError ?? (stageTaskResult.output as { error?: string } | undefined)?.error,
      lastCategory: directHandlerResult?.lastCategory ?? stageTaskResult.failureCategory,
    };

    const measuredDuration = Math.max(1, handlerResult.stageTaskResult.duration || Date.now() - taskStartTime);

    if (handlerResult.stageTaskResult.status === 'completed') {
      const implementationCommitSha = options.onlyTaskId && context.workflowApplicationService
        ? await commitAggregateTaskChanges(context.worktreePath, nextTask.id, context.issueNumber)
        : undefined;
      if (options.onlyTaskId && options.ignoreTaskFileProgress) {
        restoreUnrequestedTaskProgress(change.tasksPath, originalTasks, new Set([nextTask.id]));
        const latestTasks = readTasks(change.tasksPath);
        if (latestTasks) {
          sortedTasks.splice(0, sortedTasks.length, ...latestTasks);
        }
        context.syncTasksToStageState?.();
      }
      await commitTasksFile(change.tasksPath, context.worktreePath, nextTask.id, true);
      if (!handlerResult.stageTaskResult.alreadyReported) {
        emitTaskUpdate(taskExecutionId ?? '', nextTask.id, nextTask.title, handlerResult.stageTaskResult.attempts, sortedTasks.length, 'completed', handlerResult.stageTaskResult.attempts);
      }
      completed++;
      emitLoopProgress(completed, failed, sortedTasks.length);
      context.onTaskComplete?.(nextTask, true, handlerResult.stageTaskResult.output as string ?? '');

      log.info('Task completed', {
        issueId: logIssueId,
        taskId: nextTask.id,
        attempt: handlerResult.stageTaskResult.attempts,
      });

      writeTaskLog(context.workflowLogRepo, logIssueId, 'task_completed', {
        taskId: nextTask.id,
        attempt: handlerResult.stageTaskResult.attempts,
        executionId: taskExecutionId,
      });

      options.onTaskCompleted?.(nextTask.id);

      appendStageTaskResult(nextTask.id, nextTask.title, 'completed', handlerResult.stageTaskResult.attempts, measuredDuration);
      reportTaskToAggregate(context, nextTask, {
        status: 'completed',
        attempts: handlerResult.stageTaskResult.attempts,
        duration: handlerResult.stageTaskResult.duration,
        output: handlerResult.stageTaskResult.output || implementationCommitSha ? { text: handlerResult.stageTaskResult.output, implementationCommitSha } : undefined,
      });
      taskResults.push({
        taskId: nextTask.id,
        status: 'completed',
        attempts: handlerResult.stageTaskResult.attempts,
      });
    } else {
      const shouldPause = handlerResult.paused;
      const pauseReason = handlerResult.pauseReason;
      const lastError = handlerResult.lastError ?? 'Unknown error';

      const failureCategory = handlerResult.lastCategory ?? categorizeFailure(lastError, {});
      persistLoopTaskState(nextTask.id, {
        passes: false,
        attempts: handlerResult.stageTaskResult.attempts,
        error: `Task was not executed (attemptsUsed=${handlerResult.stageTaskResult.attempts}, no attempts made)`,
      });

      writeTaskLog(context.workflowLogRepo, logIssueId, 'task_failed', {
        taskId: nextTask.id,
        attempt: handlerResult.stageTaskResult.attempts,
        category: failureCategory,
        error: lastError?.slice(0, 500),
        executionId: taskExecutionId,
      });

      if (shouldPause && context.onAskUser) {
        const question = `Task ${nextTask.id} failed and requires user intervention.\n\nReason: ${pauseReason}\n\nOptions:\n1. Retry this task\n2. Abort the build\n\nWhat would you like to do?`;
        const answer = await context.onAskUser(question, nextTask.id);

        if (answer.toLowerCase().includes('skip')) {
          persistLoopTaskState(nextTask.id, {
            passes: false,
            attempts: handlerResult.stageTaskResult.attempts,
            error: lastError,
          });
          failed++;
          const skipPauseReason = `Task ${nextTask.id} was not completed: ${lastError}`;
          reportTaskToAggregate(context, nextTask, {
            status: 'failed',
            attempts: handlerResult.stageTaskResult.attempts,
            duration: handlerResult.stageTaskResult.duration,
            output: { error: lastError, requestedAction: 'skip' },
            reason: skipPauseReason,
          });
          const result: RalphLoopResult = {
            completed,
            failed,
            skipped,
            total: sortedTasks.length,
            taskResults,
            success: false,
            paused: true,
            pausedTaskId: nextTask.id,
            pauseReason: skipPauseReason,
          };
          context.onLoopComplete?.(result);
          return result;
        } else if (answer.toLowerCase().includes('retry')) {
          continue;
        } else {
          persistLoopTaskState(nextTask.id, {
            passes: false,
            attempts: handlerResult.stageTaskResult.attempts,
            error: lastError,
          });
          taskResults.push({
            taskId: nextTask.id,
            status: 'failed',
            attempts: handlerResult.stageTaskResult.attempts,
            error: lastError,
          });
          failed++;
          reportTaskToAggregate(context, nextTask, {
            status: 'failed',
            attempts: handlerResult.stageTaskResult.attempts,
            duration: handlerResult.stageTaskResult.duration,
            output: { error: lastError, pauseReason },
            reason: pauseReason ?? lastError,
          });
          const result: RalphLoopResult = {
            completed,
            failed,
            skipped,
            total: sortedTasks.length,
            taskResults,
            success: false,
            paused: true,
            pausedTaskId: nextTask.id,
            pauseReason: pauseReason ?? lastError,
          };
          context.onLoopComplete?.(result);
          return result;
        }
      } else if (shouldPause && !context.onAskUser) {
        persistLoopTaskState(nextTask.id, {
          passes: false,
          attempts: handlerResult.stageTaskResult.attempts,
          error: lastError,
        });
        reportTaskToAggregate(context, nextTask, {
          status: 'failed',
          attempts: handlerResult.stageTaskResult.attempts,
          duration: handlerResult.stageTaskResult.duration,
          output: { error: lastError },
          reason: lastError,
        });
        failed++;
        processedTaskIds.add(nextTask.id);
      } else {
        persistLoopTaskState(nextTask.id, {
          passes: false,
          attempts: handlerResult.stageTaskResult.attempts,
          error: lastError,
        });
        reportTaskToAggregate(context, nextTask, {
          status: 'failed',
          attempts: handlerResult.stageTaskResult.attempts,
          duration: handlerResult.stageTaskResult.duration,
          output: { error: lastError },
          reason: lastError,
        });
        failed++;
        processedTaskIds.add(nextTask.id);
      }

      taskResults.push({
        taskId: nextTask.id,
        status: 'failed',
        attempts: handlerResult.stageTaskResult.attempts,
        error: lastError,
      });
      if (!handlerResult.stageTaskResult.alreadyReported) {
        emitTaskUpdate(taskExecutionId ?? '', nextTask.id, nextTask.title, handlerResult.stageTaskResult.attempts, sortedTasks.length, 'failed', handlerResult.stageTaskResult.attempts, lastError);
      }
      appendStageTaskResult(nextTask.id, nextTask.title, 'failed', handlerResult.stageTaskResult.attempts, measuredDuration);
      context.onTaskComplete?.(nextTask, false, lastError ?? 'Max retries exceeded');
    }

    if (options.onlyTaskId) break;
  }

  const result: RalphLoopResult = {
    completed,
    failed,
    skipped,
    total: options.onlyTaskId ? 1 : sortedTasks.length,
    taskResults,
    success: failed === 0,
  };

  context.onLoopComplete?.(result);
  return result;
}

export class RalphExecutor {
  private context: RalphExecutorContext;

  constructor(context: RalphExecutorContext) {
    this.context = context;
  }

  async execute(change: OpenSpecChange, options: RalphExecutorOptions = {}): Promise<RalphLoopResult> {
    return runRalphLoop(change, this.context, options);
  }
}
