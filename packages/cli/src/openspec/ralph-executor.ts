import * as fs from 'fs';
import * as path from 'path';
import { execFile } from 'child_process';
import { promisify } from 'util';
import type { OpenSpecChange } from './detector';
import type { Task } from './context-assembler';
import { loadLearningsFromDir, buildTaskContext } from './context-assembler';
import type { AgentConfig } from '../workflow/workflow-loader';
import { runAcpSession as _runAcpSession } from '../agent-runtime/acp-session';
import { WorktreeManager } from '../git/worktree-manager';
import { load as loadConfig, getAgentTimeoutConfig } from '../config/config-loader';
import { Log } from '../util/log';

const execFileAsync = promisify(execFile);

const log = Log.create({ service: 'ralph' });

let _acpSessionRunner = _runAcpSession;

export function setAcpSessionRunner(runner: typeof _runAcpSession): void {
  _acpSessionRunner = runner;
}

export function resetAcpSessionRunner(): void {
  _acpSessionRunner = _runAcpSession;
}
import type { EventBus } from '../services/event-bus';

export type FailureCategory = 'ac_not_met' | 'environment' | 'dependency' | 'timeout' | 'timeout_with_wip' | 'hang_unrecoverable';

export interface FailureCategoryConfig {
  maxAttempts: number;
  retryable: boolean;
}

export const FAILURE_CATEGORY_CONFIGS: Record<FailureCategory, FailureCategoryConfig> = {
  ac_not_met: { maxAttempts: 3, retryable: true },
  environment: { maxAttempts: 2, retryable: true },
  dependency: { maxAttempts: 1, retryable: false },
  timeout: { maxAttempts: 3, retryable: true },
  timeout_with_wip: { maxAttempts: 2, retryable: true },
  hang_unrecoverable: { maxAttempts: 1, retryable: false },
};

export function categorizeFailure(error: string, options?: { wipCommitted?: boolean }): FailureCategory {
  const lowerError = error.toLowerCase();

  if (error.includes('[HANG_UNRECOVERABLE]')) {
    return 'hang_unrecoverable';
  }

  if (lowerError.includes('timeout') || lowerError.includes('timed out')) {
    return options?.wipCommitted ? 'timeout_with_wip' : 'timeout';
  }

  if (error.includes('[SPAWN_FAILED]')) {
    return 'dependency';
  }

  const dependencyPatterns = [
    'cannot find module',
    'module not found',
    'err_module_not_found',
    'no such module',
    'dependency',
    'unmet dependency',
    'peer dependency',
    'cannot find package',
    'package not found',
    'failed to resolve',
    'could not be resolved',
    'import error',
    'unresolved import',
  ];
  for (const pattern of dependencyPatterns) {
    if (lowerError.includes(pattern)) {
      return 'dependency';
    }
  }

  const environmentPatterns = [
    'npm install',
    'install failed',
    'node_modules',
    'permission denied',
    'enoent',
    'no such file or directory',
    'command not found',
    'environment',
    'econnrefused',
    'econnreset',
    'network error',
    'network request failed',
    'spawn error',
    'spawn failed',
    'spawn enoent',
    'eacces',
    'heap out of memory',
    'out of memory',
    'enospc',
    'disk full',
    'segmentation fault',
    'sigsegv',
    'sigkill',
  ];
  for (const pattern of environmentPatterns) {
    if (lowerError.includes(pattern)) {
      return 'environment';
    }
  }

  return 'ac_not_met';
}

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
  workflowLogRepo?: import('../db/workflow-log-repo').WorkflowLogRepo;
  sessionStreamLogRepo?: import('../db/session-stream-log-repo').SessionStreamLogRepo;
  coderSessionRepo?: import('../db/coder-session-repo').CoderSessionRepo;
  issueNumber?: number;
  stageTimeoutMs?: number;
  onProcessSpawned?: (proc: import('child_process').ChildProcess) => void;
  worktreeManager?: WorktreeManager;
  stage?: string;
  model?: string;
  agentConfig?: AgentConfig;
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

export function getOrderValue(order: number | undefined): number {
  if (order === undefined) return 999999;
  return order;
}

export function sortTasksByOrder(tasks: Task[]): Task[] {
  return [...tasks].sort((a, b) => {
    const orderA = getOrderValue(a.order);
    const orderB = getOrderValue(b.order);
    return orderA - orderB;
  });
}

export function readTasks(tasksPath: string): Task[] | null {
  if (!fs.existsSync(tasksPath)) {
    return null;
  }
  try {
    const content = fs.readFileSync(tasksPath, 'utf-8');
    const tasksFile = JSON.parse(content);
    if (tasksFile.tasks && Array.isArray(tasksFile.tasks)) {
      return tasksFile.tasks.map((t: Task) => ({
        ...t,
        attempts: t.attempts ?? 0,
        passes: t.passes ?? false,
        order: t.order ?? 999999,
        error: t.error ?? null,
      })) as Task[];
    }
    return null;
  } catch {
    return null;
  }
}

function writeTasksFile(tasksPath: string, tasks: Task[]): void {
  try {
    const tasksFile = { version: 1, tasks };
    fs.writeFileSync(tasksPath, JSON.stringify(tasksFile, null, 2), 'utf-8');
  } catch (e) {
    log.error('writeTasksFile failed', { tasksPath, error: e instanceof Error ? e.message : String(e) });
  }
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

export interface RalphExecutorOptions extends RalphTaskOptions {
  resumeFromTaskIndex?: number;
  skipTaskIds?: string[];
  onTaskCompleted?: (taskId: string) => void;
}

async function storeFailureLearning(
  change: OpenSpecChange,
  task: Task,
  failureReason: string,
  category: FailureCategory,
  attempt: number
): Promise<void> {
  const memoriesDir = change.sessionMemoriesPath;
  if (!fs.existsSync(memoriesDir)) {
    fs.mkdirSync(memoriesDir, { recursive: true });
  }

  const taskIdSanitized = task.id.replace(/[^a-zA-Z0-9_-]/g, '_');
  const timestamp = new Date().toISOString();

  const learningPath = path.join(memoriesDir, `${taskIdSanitized}.json`);

  const learning = {
    task_id: task.id,
    timestamp,
    insights: [],
    adjustments: generateAdjustmentsFromCategory(category, failureReason),
    success: false,
    execution_summary: `Failed on attempt ${attempt}: ${failureReason.slice(0, 200)}`,
    failure_reason: failureReason,
    failed_attempts: attempt,
    failure_category: category,
  };

  fs.writeFileSync(learningPath, JSON.stringify(learning, null, 2), 'utf-8');
}

function generateAdjustmentsFromCategory(category: FailureCategory, _error: string): string[] {
  const adjustments: string[] = [];

  switch (category) {
    case 'ac_not_met':
      adjustments.push('Review acceptance criteria carefully before implementing');
      adjustments.push('Verify implementation satisfies all AC requirements');
      break;
    case 'environment':
      adjustments.push('Check environment setup and dependencies');
      adjustments.push('Ensure npm install completes successfully before building');
      break;
    case 'dependency':
      adjustments.push('Resolve code dependencies before proceeding');
      adjustments.push('May need to restructure code to use available exports');
      break;
    case 'timeout':
      adjustments.push('Consider breaking this task into smaller subtasks');
      adjustments.push('The task may be too complex for a single execution');
      break;
    case 'timeout_with_wip':
      adjustments.push('Previous progress was saved in a WIP commit');
      adjustments.push('Continue from where the previous attempt left off');
      break;
    case 'hang_unrecoverable':
      adjustments.push('LLM provider stream connection was lost and could not be recovered');
      adjustments.push('Consider checking provider status or switching to a different model');
      break;
  }

  return adjustments;
}

export interface DependencyValidationResult {
  valid: boolean;
  errors: string[];
}

export function validateTaskDependencies(tasks: Task[]): DependencyValidationResult {
  const errors: string[] = [];
  const taskIds = new Set(tasks.map(t => t.id));

  for (const task of tasks) {
    const deps = task.dependsOn ?? [];
    if (deps.length === 0) continue;

    for (const depId of deps) {
      if (!taskIds.has(depId)) {
        errors.push(`Task "${task.id}" depends on "${depId}", which does not exist in the task list`);
      } else {
        const depTask = tasks.find(t => t.id === depId)!;
        if (getOrderValue(depTask.order) > getOrderValue(task.order)) {
          errors.push(
            `Task "${task.id}" (order: ${task.order}) depends on "${depId}" (order: ${depTask.order}), ` +
            `but dependencies must reference tasks with a lower or equal order value`
          );
        }
      }
    }
  }

  const visited = new Set<string>();
  const inStack = new Set<string>();
  const adj = new Map<string, string[]>();

  for (const task of tasks) {
    adj.set(task.id, (task.dependsOn ?? []).filter(depId => taskIds.has(depId)));
  }

  function hasCycle(nodeId: string): boolean {
    visited.add(nodeId);
    inStack.add(nodeId);

    for (const neighbor of adj.get(nodeId) ?? []) {
      if (!visited.has(neighbor)) {
        if (hasCycle(neighbor)) return true;
      } else if (inStack.has(neighbor)) {
        return true;
      }
    }

    inStack.delete(nodeId);
    return false;
  }

  for (const task of tasks) {
    if (!visited.has(task.id)) {
      if (hasCycle(task.id)) {
        errors.push('Circular dependency detected in the task dependency graph');
        break;
      }
    }
  }

  return { valid: errors.length === 0, errors };
}

export function findNextPendingTask(tasks: Task[]): Task | null {
  const passedIds = new Set(tasks.filter(t => t.passes).map(t => t.id));
  const ready = tasks.filter(t => {
    if (t.passes) return false;
    const deps = t.dependsOn ?? [];
    return deps.every(depId => passedIds.has(depId));
  });
  const sorted = sortTasksByOrder(ready);
  return sorted.length > 0 ? sorted[0] : null;
}

function updateTaskInList(
  tasks: Task[],
  taskId: string,
  updates: Partial<Pick<Task, 'passes' | 'attempts' | 'error' | 'durations'>>
): void {
  const task = tasks.find(t => t.id === taskId);
  if (!task) return;
  if (updates.passes !== undefined) task.passes = updates.passes;
  if (updates.attempts !== undefined) task.attempts = updates.attempts;
  if (updates.error !== undefined) task.error = updates.error;
  if (updates.durations !== undefined) {
    task.durations = [...(task.durations ?? []), ...updates.durations];
  }
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
  const tasks = readTasks(change.tasksPath);
  if (!tasks || tasks.length === 0) {
    return {
      completed: 0,
      failed: 0,
      skipped: 0,
      total: 0,
      taskResults: [],
      success: false,
    };
  }

  const validation = validateTaskDependencies(tasks);
  if (!validation.valid) {
    for (const err of validation.errors) {
      log.error('Task dependency validation failed', { issueId: context.issueId || '', error: err });
    }
    const result: RalphLoopResult = {
      completed: 0,
      failed: tasks.length,
      skipped: 0,
      total: tasks.length,
      taskResults: [],
      success: false,
      pauseReason: `Task dependency validation failed: ${validation.errors.join('; ')}`,
    };
    context.onLoopComplete?.(result);
    return result;
  }

  const sortedTasks = sortTasksByOrder(tasks);
  const timeoutConfig = getAgentTimeoutConfig(loadConfig());
  const perTaskTimeout = timeoutConfig.taskTimeout * 1000;

  const skipTaskIds = new Set(options.skipTaskIds ?? []);

  if (tasks.length > 0 && tasks.every(t => t.passes)) {
    log.info('All tasks already passed, returning success', {
      issueId: context.issueId || '',
      total: tasks.length,
    });
    const alreadyPassedResult: RalphLoopResult = {
      completed: tasks.length,
      failed: 0,
      skipped: tasks.length,
      total: tasks.length,
      taskResults: [],
      success: true,
    };
    context.onLoopComplete?.(alreadyPassedResult);
    return alreadyPassedResult;
  }

  for (const task of tasks) {
    if (skipTaskIds.has(task.id) && !task.passes) {
      task.passes = true;
      task.error = null;
    }
  }
  if (skipTaskIds.size > 0) {
    writeTasksFile(change.tasksPath, tasks);
    log.info('Marked tasks as passed from skipTaskIds', {
      issueId: context.issueId || '',
      skippedIds: [...skipTaskIds],
    });
  }

  if (skipTaskIds.size > 0 && tasks.every(t => t.passes)) {
    log.info('recovered-from-checkpoint', {
      issueId: context.issueId || '',
      total: tasks.length,
    });
    const recoveredResult: RalphLoopResult = {
      completed: tasks.length,
      failed: 0,
      skipped: 0,
      total: tasks.length,
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

  let learnings = loadLearningsFromDir(change.sessionMemoriesPath);

  const sseIssueId = String(context.issueNumber ?? '');
  const logIssueId = context.issueId || '';

  const pending = tasks.filter(t => !t.passes).length;
  const passed = tasks.filter(t => t.passes).length;
  log.info('Ralph loop entry', {
    issueId: logIssueId,
    total: sortedTasks.length,
    pending,
    passed,
  });

  const emitTaskUpdate = (
    taskExecutionId: string,
    taskId: string,
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

  const processedTaskIds = new Set<string>();

  while (true) {
    const nextTask = findNextPendingTask(tasks);
    if (!nextTask || processedTaskIds.has(nextTask.id)) {
      if (!nextTask) {
        const remainingPending = tasks.filter(t => !t.passes);
        if (remainingPending.length > 0) {
          const blockedIds = remainingPending.map(t => {
            const deps = (t.dependsOn ?? []).filter(d => !tasks.find(x => x.id === d)?.passes);
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

    const assembledContext = buildTaskContext({
      change,
      task: nextTask,
      learnings,
      isRetry: false,
      agentConfig: context.agentConfig,
    });

    let lastError: string | undefined;
    let lastCategory: FailureCategory = 'ac_not_met';
    let taskSuccess = false;
    let attemptsUsed = nextTask.attempts;
    let shouldPause = false;
    let pauseReason: string | undefined;
    let wipResumeContext: string | undefined;

    const maxRetries = options.maxRetries ?? 3;
    // Total attempts = 1 (initial) + maxRetries (retries). Ensure at least 1 attempt.
    const totalAttempts = Math.max(1, maxRetries + 1);

    for (let attempt = nextTask.attempts + 1; attempt <= nextTask.attempts + totalAttempts; attempt++) {
      const prompt = attempt > 1
        ? buildTaskContext({
            change,
            task: nextTask,
            learnings,
            failureReason: lastError,
            isRetry: true,
            wipResumeContext,
            agentConfig: context.agentConfig,
          }).fullPrompt
        : assembledContext.fullPrompt;

      emitTaskUpdate(taskExecutionId ?? '', nextTask.id, attemptsUsed, sortedTasks.length, 'started', attempt);

      log.info('Task attempt started', {
        issueId: logIssueId,
        taskId: nextTask.id,
        attempt,
      });

      writeTaskLog(context.workflowLogRepo, logIssueId, 'task_started', {
        taskId: nextTask.id,
        attempt,
        executionId: taskExecutionId,
      });

      const attemptStartTime = Date.now();

      const result = await _acpSessionRunner({
        cwd: context.worktreePath,
        task: prompt,
        taskId: nextTask.id,
        timeout: perTaskTimeout,
        issueId: context.issueId,
        projectId: context.projectId,
        executionId: taskExecutionId,
        eventBus: context.eventBus,
        workflowLogRepo: context.workflowLogRepo,
        sessionStreamLogRepo: context.sessionStreamLogRepo,
        coderSessionRepo: context.coderSessionRepo,
        issueNumber: context.issueNumber,
        onProcessSpawned: context.onProcessSpawned,
        stage: context.stage,
        model: context.model,
        title: `${nextTask.id}: ${nextTask.title}`,
        onBeforeKill: context.worktreeManager
          ? async (cwd: string) => {
              const hash = await context.worktreeManager!.createWipCommit(cwd, nextTask.id, attempt);
              return hash !== null;
            }
          : undefined,
      });

      const attemptDuration = Date.now() - attemptStartTime;

      attemptsUsed = attempt;
      if (result.success) {
        taskSuccess = true;
        updateTaskInList(tasks, nextTask.id, { passes: true, attempts: attempt, error: null, durations: [attemptDuration] });
        writeTasksFile(change.tasksPath, tasks);
        await commitTasksFile(change.tasksPath, context.worktreePath, nextTask.id, true);
        emitTaskUpdate(taskExecutionId ?? '', nextTask.id, attemptsUsed, sortedTasks.length, 'completed', attempt);
        completed++;
        emitLoopProgress(completed, failed, sortedTasks.length);
        context.onTaskComplete?.(nextTask, true, result.text);

        log.info('Task completed', {
          issueId: logIssueId,
          taskId: nextTask.id,
          attempt,
        });

        writeTaskLog(context.workflowLogRepo, logIssueId, 'task_completed', {
          taskId: nextTask.id,
          attempt,
          executionId: taskExecutionId,
        });

        options.onTaskCompleted?.(nextTask.id);

        break;
      } else {
        lastError = result.error ?? 'Unknown error';
        lastCategory = categorizeFailure(lastError, { wipCommitted: result.wipCommitted });

        if (lastCategory === 'timeout_with_wip' && context.worktreeManager) {
          const wipInfo = await context.worktreeManager.findWipCommit(context.worktreePath, nextTask.id);
          if (wipInfo) {
            wipResumeContext = [
              `Task ${nextTask.id} timed out on attempt ${attempt}.`,
              'A WIP commit was saved with the following progress:',
              '',
              'Modified files:',
              ...wipInfo.changedFiles.map(f => `- ${f}`),
              '',
              'Diff summary:',
              wipInfo.diffStat,
              '',
              'Continue from this state. Do NOT re-read or re-implement the files listed above.',
              'Focus on completing the remaining acceptance criteria.',
            ].join('\n');
          }
        }

        log.warn('Task attempt failed', {
          issueId: logIssueId,
          taskId: nextTask.id,
          attempt,
          category: lastCategory,
          error: lastError.slice(0, 200),
        });

        writeTaskLog(context.workflowLogRepo, logIssueId, 'task_failed', {
          taskId: nextTask.id,
          attempt,
          category: lastCategory,
          error: lastError.slice(0, 500),
          executionId: taskExecutionId,
        });

        const categoryConfig = FAILURE_CATEGORY_CONFIGS[lastCategory];
        const effectiveMaxAttempts = Math.min(maxRetries, categoryConfig.maxAttempts);

        await storeFailureLearning(change, nextTask, lastError, lastCategory, attempt);

        if (!categoryConfig.retryable) {
          shouldPause = true;
          pauseReason = `${lastCategory} failure: ${lastError}. This cannot be retried automatically.`;
          updateTaskInList(tasks, nextTask.id, { attempts: attempt, error: lastError, durations: [attemptDuration] });
          writeTasksFile(change.tasksPath, tasks);
          break;
        }

        if (attempt >= effectiveMaxAttempts + nextTask.attempts) {
          shouldPause = true;
          pauseReason = `Max retries (${effectiveMaxAttempts}) exceeded for ${lastCategory} failure: ${lastError}`;
          updateTaskInList(tasks, nextTask.id, { attempts: attempt, error: lastError, durations: [attemptDuration] });
          writeTasksFile(change.tasksPath, tasks);
          break;
        }

        updateTaskInList(tasks, nextTask.id, { durations: [attemptDuration] });
        writeTasksFile(change.tasksPath, tasks);

        emitTaskUpdate(taskExecutionId ?? '', nextTask.id, attemptsUsed, sortedTasks.length, 'retrying', attempt);

        writeTaskLog(context.workflowLogRepo, logIssueId, 'task_retrying', {
          taskId: nextTask.id,
          attempt,
          category: lastCategory,
          error: lastError?.slice(0, 500),
          executionId: taskExecutionId,
        });

        options.onRetry?.(nextTask, attempt, lastError);
      }
    }

    if (!taskSuccess) {
      if (attemptsUsed === nextTask.attempts) {
        updateTaskInList(tasks, nextTask.id, { passes: false, attempts: nextTask.attempts, error: `Skipped: task was not executed (attemptsUsed=${attemptsUsed}, no attempts made)` });
        writeTasksFile(change.tasksPath, tasks);
      }
      taskResults.push({
        taskId: nextTask.id,
        status: 'failed',
        attempts: attemptsUsed,
        error: lastError,
      });
      emitTaskUpdate(taskExecutionId ?? '', nextTask.id, attemptsUsed, sortedTasks.length, 'failed', attemptsUsed, lastError);
      context.onTaskComplete?.(nextTask, false, lastError ?? 'Max retries exceeded');

      if (shouldPause && context.onAskUser) {
        const question = `Task ${nextTask.id} failed and requires user intervention.\n\nReason: ${pauseReason}\n\nOptions:\n1. Retry this task\n2. Skip this task and continue\n3. Abort the build\n\nWhat would you like to do?`;
        const answer = await context.onAskUser(question, nextTask.id);

        if (answer.toLowerCase().includes('skip')) {
          updateTaskInList(tasks, nextTask.id, { passes: true, error: `Skipped: ${lastError}` });
          writeTasksFile(change.tasksPath, tasks);
          taskResults[taskResults.length - 1].status = 'skipped';
        } else if (answer.toLowerCase().includes('retry')) {
          taskResults.pop();
          continue;
        } else {
          failed++;
          const result: RalphLoopResult = {
            completed,
            failed,
            skipped,
            total: sortedTasks.length,
            taskResults,
            success: false,
            paused: true,
            pausedTaskId: nextTask.id,
            pauseReason,
          };
          context.onLoopComplete?.(result);
          return result;
        }
      } else if (shouldPause && !context.onAskUser) {
        updateTaskInList(tasks, nextTask.id, { passes: false, error: `Auto-skipped (no onAskUser): ${lastError}` });
        writeTasksFile(change.tasksPath, tasks);
        taskResults[taskResults.length - 1].status = 'skipped';
        failed++;
        skipped++;
        processedTaskIds.add(nextTask.id);
      } else {
        failed++;
        processedTaskIds.add(nextTask.id);
      }
    } else {
      taskResults.push({
        taskId: nextTask.id,
        status: 'completed',
        attempts: attemptsUsed,
      });
    }

    learnings = loadLearningsFromDir(change.sessionMemoriesPath);
  }

  const result: RalphLoopResult = {
    completed,
    failed,
    skipped,
    total: sortedTasks.length,
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