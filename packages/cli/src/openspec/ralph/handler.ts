import * as fs from 'fs';
import * as path from 'path';
import type { OpenSpecChange } from '../detector';
import type { Task } from '../context-assembler';
import { buildTaskContext, loadLearningsFromDir } from '../context-assembler';
import { FAILURE_CATEGORY_CONFIGS, type FailureCategory } from './types';
import { categorizeFailure, readTasks } from './task-utils';
import type { RalphLoadedTask } from './loader';

type StageContext = {
  issue: { id: string; number: number; title: string; body: string; projectId: string };
  acpOptions: any;
  worktreeManager: any;
};

type StageTaskResult = {
  taskId: string;
  title: string;
  status: 'completed' | 'failed' | 'skipped';
  artifacts: string[];
  events?: string[];
  output?: unknown;
  attemptEvidence?: { executionId?: string; acpSessionId?: string; coderSessionId?: string; processPid?: number };
  attempts: number;
  duration: number;
  reason?: string;
  causedBy?: { type: string; checkName?: string; taskId?: string; message?: string };
  alreadyReported?: boolean;
  failureCategory?: string;
};
import type { AgentSessionOptions, AcpSessionResult } from '../../agent-runtime/agent-session';
import type { WorktreeManager } from '../../git/worktree-manager';
import type { SessionObserver } from '../../agent-runtime/session-observer';

export interface RalphTaskHandlerOptions {
  maxRetries?: number;
  attempt?: number;
  wipResumeContext?: string;
  onRetry?: (task: Task, attempt: number, error: string) => void;
  onRetryLog?: (taskId: string, attempt: number, category: FailureCategory, error: string, executionId?: string) => void;
  emitTaskUpdate?: (
    taskExecutionId: string,
    taskId: string,
    taskTitle: string,
    taskIndex: number,
    totalTasks: number,
    status: 'started' | 'completed' | 'failed' | 'retrying',
    attempt?: number,
    error?: string
  ) => void;
  issueId?: string;
  executionId?: string;
  stage?: string;
  model?: string;
  agentConfig?: unknown;
  taskTimeout?: number;
  onTasksPersisted?: () => void;
}

export interface RalphTaskHandlerDeps {
  worktreePath: string;
  acpSessionRunner: (options: AgentSessionOptions) => Promise<AcpSessionResult>;
  worktreeManager?: WorktreeManager;
  observers?: SessionObserver[];
  onBeforeKill?: (cwd: string) => Promise<boolean>;
}

export async function executeRalphTask(
  loadedTask: RalphLoadedTask,
  ctx: StageContext,
  options: RalphTaskHandlerOptions,
  deps: RalphTaskHandlerDeps
): Promise<{
  stageTaskResult: StageTaskResult;
  paused?: boolean;
  pauseReason?: string;
  lastError?: string;
  lastCategory?: FailureCategory;
}> {
  const { task, change, totalTasks } = loadedTask;
  const {
    maxRetries = 3,
    attempt: initialAttempt = task.attempts + 1,
    wipResumeContext,
    onRetry,
    onRetryLog,
    emitTaskUpdate,
    executionId,
    stage = 'build',
    model,
    agentConfig,
    taskTimeout,
    onTasksPersisted,
  } = options;

  const assembledContext = buildTaskContext({
    change,
    task,
    learnings: loadLearningsFromDir(change.sessionMemoriesPath),
    isRetry: initialAttempt > task.attempts + 1,
    issueNumber: ctx.issue.number,
    issueTitle: ctx.issue.title,
    issueBody: ctx.issue.body,
    agentConfig: agentConfig as any,
  });

  const taskExecutionId = executionId ? `${executionId}-${task.id}` : undefined;

  let lastError: string | undefined;
  let lastCategory: FailureCategory = 'ac_not_met';
  let taskSuccess = false;
  let attemptsUsed = initialAttempt - 1;
  let shouldPause = false;
  let pauseReason: string | undefined;
  let wipContext: string | undefined = wipResumeContext;
  const attemptDurations: number[] = [];

  const effectiveMaxRetries = maxRetries ?? 3;
  const totalAttempts = Math.max(1, effectiveMaxRetries + 1);
  const finalAllowedAttempt = initialAttempt + totalAttempts - 1;
  const taskStartTime = Date.now();

  for (let attempt = initialAttempt; attempt <= finalAllowedAttempt; attempt++) {
    const prompt = attempt > initialAttempt
      ? buildTaskContext({
          change,
          task,
          learnings: loadLearningsFromDir(change.sessionMemoriesPath),
          failureReason: lastError,
          isRetry: true,
          wipResumeContext: wipContext,
          issueNumber: ctx.issue.number,
          issueTitle: ctx.issue.title,
          issueBody: ctx.issue.body,
          agentConfig: agentConfig as any,
        }).fullPrompt
      : assembledContext.fullPrompt;

    emitTaskUpdate?.(
      taskExecutionId ?? '',
      task.id,
      task.title,
      attemptsUsed,
      totalTasks,
      'started',
      attempt
    );

    const result = await deps.acpSessionRunner({
      cwd: deps.worktreePath,
      task: prompt,
      taskId: task.id,
      timeout: taskTimeout ?? (ctx.acpOptions as any)?.timeout ?? 600000,
      issueId: ctx.issue.id,
      projectId: ctx.issue.projectId,
      executionId: taskExecutionId,
      issueNumber: ctx.issue.number,
      stage,
      model,
      title: `${task.id}: ${task.title}`,
      observers: deps.observers,
      onBeforeKill: deps.onBeforeKill,
    });

    attemptsUsed = attempt;

    if (result.success) {
      taskSuccess = true;
      const attemptDuration = elapsedDuration(taskStartTime);
      attemptDurations.push(attemptDuration);
      persistTaskProgress(change, task, {
        passes: true,
        attempts: attempt,
        error: null,
        durations: [...attemptDurations],
      });
      onTasksPersisted?.();
      const stageTaskResult: StageTaskResult = {
        taskId: task.id,
        title: task.title,
        status: 'completed',
        artifacts: [],
        attempts: attempt,
        duration: elapsedDuration(taskStartTime),
        output: { text: result.text },
        alreadyReported: true,
      };
      emitTaskUpdate?.(taskExecutionId ?? '', task.id, task.title, attempt, totalTasks, 'completed', attempt);
      return { stageTaskResult };
    } else {
      lastError = result.error ?? 'Unknown error';
      lastCategory = categorizeFailure(lastError, { wipCommitted: result.wipCommitted, failureKind: result.failureKind });

      const attemptDuration = elapsedDuration(taskStartTime);
      attemptDurations.push(attemptDuration);
      persistTaskProgress(change, task, {
        passes: false,
        attempts: attempt,
        error: lastError,
        durations: [...attemptDurations],
      });
      onTasksPersisted?.();

      if (lastCategory === 'timeout_with_wip' && deps.worktreeManager) {
        const wipInfo = await deps.worktreeManager.findWipCommit(deps.worktreePath, task.id);
        if (wipInfo) {
          wipContext = [
            `Task ${task.id} timed out on attempt ${attempt}.`,
            'A WIP commit was saved with the following progress:',
            '',
            'Modified files:',
            ...wipInfo.changedFiles.map(f => `- ${f}`),
            '',
            'Diff summary:',
            wipInfo.diffStat ?? '',
            '',
            'Continue from this state. Do NOT re-read or re-implement the files listed above.',
            'Focus on completing the remaining acceptance criteria.',
          ].join('\n');
        }
      }

      const categoryConfig = FAILURE_CATEGORY_CONFIGS[lastCategory];
      const effectiveMaxAttempts = Math.min(effectiveMaxRetries, categoryConfig.maxAttempts);
      const categoryFinalAttempt = initialAttempt + effectiveMaxAttempts;

      await storeFailureLearning(change, task, lastError, lastCategory, attempt);

      if (!categoryConfig.retryable) {
        shouldPause = true;
        pauseReason = `${lastCategory} failure: ${lastError}. This cannot be retried automatically.`;
        const stageTaskResult: StageTaskResult = {
          taskId: task.id,
          title: task.title,
          status: 'failed',
          artifacts: [],
          attempts: attempt,
          duration: elapsedDuration(taskStartTime),
          reason: pauseReason,
          output: { error: lastError },
          alreadyReported: true,
          failureCategory: lastCategory,
        };
        emitTaskUpdate?.(taskExecutionId ?? '', task.id, task.title, attempt, totalTasks, 'failed', attempt, lastError);
        return { stageTaskResult, paused: true, pauseReason, lastError, lastCategory };
      }

      if (attempt >= categoryFinalAttempt) {
        shouldPause = true;
        pauseReason = `Max retries (${effectiveMaxAttempts}) exceeded for ${lastCategory} failure: ${lastError}`;
        const stageTaskResult: StageTaskResult = {
          taskId: task.id,
          title: task.title,
          status: 'failed',
          artifacts: [],
          attempts: attempt,
          duration: elapsedDuration(taskStartTime),
          reason: pauseReason,
          output: { error: lastError },
          alreadyReported: true,
          failureCategory: lastCategory,
        };
        emitTaskUpdate?.(taskExecutionId ?? '', task.id, task.title, attempt, totalTasks, 'failed', attempt, lastError);
        return { stageTaskResult, paused: true, pauseReason, lastError, lastCategory };
      }

      emitTaskUpdate?.(
        taskExecutionId ?? '',
        task.id,
        task.title,
        attemptsUsed,
        totalTasks,
        'retrying',
        attempt
      );

      onRetryLog?.(task.id, attempt, lastCategory, lastError ?? 'unknown', taskExecutionId);
      onRetry?.(task, attempt, lastError ?? 'unknown');
    }
  }

  const stageTaskResult: StageTaskResult = {
    taskId: task.id,
    title: task.title,
    status: taskSuccess ? 'completed' : 'failed',
    artifacts: [],
    attempts: attemptsUsed,
    duration: elapsedDuration(taskStartTime),
    reason: shouldPause ? pauseReason : lastError,
    output: { error: lastError },
    alreadyReported: !taskSuccess,
    failureCategory: taskSuccess ? undefined : lastCategory,
  };
  if (!taskSuccess) {
    emitTaskUpdate?.(taskExecutionId ?? '', task.id, task.title, attemptsUsed, totalTasks, 'failed', attemptsUsed, lastError);
  }
  return { stageTaskResult, paused: shouldPause, pauseReason, lastError, lastCategory };
}

function elapsedDuration(startTime: number): number {
  return Math.max(1, Date.now() - startTime);
}

function persistTaskProgress(
  change: OpenSpecChange,
  task: Task,
  updates: Pick<Task, 'passes' | 'attempts' | 'error' | 'durations'>,
): void {
  const tasks = readTasks(change.tasksPath);
  if (!tasks) return;

  const persistedTask = tasks.find(candidate => candidate.id === task.id);
  if (!persistedTask) return;

  persistedTask.passes = updates.passes;
  persistedTask.attempts = updates.attempts;
  persistedTask.error = updates.error;
  persistedTask.durations = [...(updates.durations ?? [])];

  task.passes = updates.passes;
  task.attempts = updates.attempts;
  task.error = updates.error;
  task.durations = [...(updates.durations ?? [])];

  fs.writeFileSync(change.tasksPath, JSON.stringify({ version: 1, tasks }, null, 2), 'utf-8');
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
