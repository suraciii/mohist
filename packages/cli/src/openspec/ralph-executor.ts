import * as fs from 'fs';
import * as path from 'path';
import { spawn } from 'child_process';
import { Writable, Readable } from 'stream';
import { ClientSideConnection, ndJsonStream, PROTOCOL_VERSION } from '@agentclientprotocol/sdk';
import type { SessionNotification, RequestPermissionRequest, RequestPermissionResponse } from '@agentclientprotocol/sdk';
import type { OpenSpecChange } from './detector';
import type { Task } from './context-assembler';
import { loadLearningsFromDir, buildTaskContext } from './context-assembler';

export type FailureCategory = 'ac_not_met' | 'environment' | 'dependency' | 'timeout';

export interface FailureCategoryConfig {
  maxAttempts: number;
  retryable: boolean;
}

export const FAILURE_CATEGORY_CONFIGS: Record<FailureCategory, FailureCategoryConfig> = {
  ac_not_met: { maxAttempts: 3, retryable: true },
  environment: { maxAttempts: 2, retryable: true },
  dependency: { maxAttempts: 1, retryable: false },
  timeout: { maxAttempts: 1, retryable: false },
};

export function categorizeFailure(error: string): FailureCategory {
  const lowerError = error.toLowerCase();

  if (lowerError.includes('timeout') || lowerError.includes('timed out')) {
    return 'timeout';
  }

  const dependencyPatterns = [
    'cannot find module',
    'cannot find',
    'module not found',
    'no such module',
    'dependency',
    'unmet dependency',
    'peer dependency',
    "cannot find package",
    "failed to resolve",
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
    'env',
    'environment',
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
  onTaskStart?: (task: Task) => void;
  onTaskComplete?: (task: Task, success: boolean, output: string) => void;
  onLoopComplete?: (results: RalphLoopResult) => void;
  onAskUser?: (question: string, taskId: string) => Promise<string>;
}

export interface RalphLoopResult {
  completed: number;
  failed: number;
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

interface TaskStatusFile {
  current_task_index: number;
  total_tasks: number;
  tasks: TaskStatusEntry[];
}

interface TaskStatusEntry {
  id: string;
  status: 'pending' | 'in_progress' | 'completed' | 'failed' | 'skipped';
  attempts: number;
  error?: string;
}

const DEFAULT_TIMEOUT = 30 * 60 * 1000;

export function getOrderValue(order: number | string | undefined): number {
  if (order === undefined) return 999999;
  if (typeof order === 'number') return order;
  if (typeof order === 'string') {
    const num = parseInt(order.replace(/[^0-9]/g, ''), 10);
    return isNaN(num) ? 999999 : num;
  }
  return 999999;
}

export function sortTasksByOrder(tasks: Task[]): Task[] {
  return [...tasks].sort((a, b) => {
    const orderA = getOrderValue(a.order);
    const orderB = getOrderValue(b.order);
    return orderA - orderB;
  });
}

export function readTaskStatus(statusPath: string): TaskStatusFile | null {
  if (!fs.existsSync(statusPath)) {
    return null;
  }
  try {
    const content = fs.readFileSync(statusPath, 'utf-8');
    return JSON.parse(content) as TaskStatusFile;
  } catch {
    return null;
  }
}

export function readPrdTasks(prdPath: string): Task[] | null {
  if (!fs.existsSync(prdPath)) {
    return null;
  }
  try {
    const content = fs.readFileSync(prdPath, 'utf-8');
    const prd = JSON.parse(content);
    if (prd.tasks && Array.isArray(prd.tasks)) {
      return prd.tasks as Task[];
    }
    return null;
  } catch {
    return null;
  }
}

interface SpawnResult {
  success: boolean;
  output: string;
  error?: string;
}

async function executeTaskWithSpawn(
  _task: Task,
  fullPrompt: string,
  worktreePath: string
): Promise<SpawnResult> {
  return new Promise((resolve) => {
    const proc = spawn('opencode', ['acp'], {
      cwd: worktreePath,
      stdio: ['pipe', 'pipe', 'inherit'],
      env: Object.fromEntries(
        Object.entries(process.env).filter(
          ([key]) =>
            key !== 'OPENCODE_SERVER_PASSWORD' &&
            key !== 'OPENCODE_SERVER_USERNAME'
        )
      ),
    });

    let cleanupDone = false;
    let agentText = '';
    let sessionId = '';
    let timeoutId: NodeJS.Timeout | null = null;

    const doCleanup = () => {
      if (cleanupDone) return;
      cleanupDone = true;

      if (timeoutId) {
        clearTimeout(timeoutId);
        timeoutId = null;
      }

      try { proc.kill('SIGTERM'); } catch { /* already exited */ }

      setTimeout(() => {
        try { proc.kill('SIGKILL'); } catch { /* already exited */ }
      }, 5000);
    };

    const input = Writable.toWeb(proc.stdin) as WritableStream<Uint8Array>;
    const output = Readable.toWeb(proc.stdout) as ReadableStream<Uint8Array>;
    const stream = ndJsonStream(input, output);

    const cleanup = async () => {
      const results = await Promise.allSettled([
        stream.readable.cancel().catch(() => {}),
        stream.writable.abort().catch(() => {}),
      ]);
      results.forEach((result, index) => {
        if (result.status === 'rejected') {
          console.error(`[ralph-executor] Cleanup ${index} failed:`, result.reason);
        }
      });
      doCleanup();
    };

    const connection = new ClientSideConnection(
      () => ({
        sessionUpdate: async (notification: SessionNotification) => {
          try {
            const update = notification.update;
            const eventType = update.sessionUpdate;
            if (
              eventType === 'agent_message_chunk' &&
              update.content &&
              'text' in update.content
            ) {
              agentText += (update.content as { text: string }).text;
            }
          } catch (err) {
            console.error('[ralph-executor] sessionUpdate error:', err);
          }
        },
        requestPermission: async (
          params: RequestPermissionRequest
        ): Promise<RequestPermissionResponse> => {
          const allow = params.options.find(
            (o: { kind: string }) => o.kind === 'allow_once' || o.kind === 'allow_always'
          );
          if (allow) {
            return { outcome: { outcome: 'selected', optionId: allow.optionId } };
          }
          return { outcome: { outcome: 'cancelled' } };
        },
      }),
      stream
    );

    const timeoutPromise = new Promise<'timeout'>((resolve) => {
      timeoutId = setTimeout(() => resolve('timeout'), DEFAULT_TIMEOUT);
    });

    const runSession = async () => {
      try {
        const initResult = await Promise.race([
          connection.initialize({ protocolVersion: PROTOCOL_VERSION, clientInfo: { name: 'mohist', version: '0.1.0' } }),
          timeoutPromise,
        ]);

        if (initResult === 'timeout') {
          resolve({ success: false, output: agentText, error: 'Timed out during initialize' });
          return;
        }

        const sessionResult = await Promise.race([
          connection.newSession({ cwd: worktreePath, mcpServers: [] }),
          timeoutPromise,
        ]);

        if (sessionResult === 'timeout') {
          resolve({ success: false, output: agentText, error: 'Timed out during newSession' });
          return;
        }

        sessionId = sessionResult.sessionId;

        const promptResult = await Promise.race([
          connection.prompt({ sessionId, prompt: [{ type: 'text', text: fullPrompt }] }),
          timeoutPromise,
        ]);

        if (promptResult === 'timeout') {
          try { await connection.cancel({ sessionId }); } catch { /* cancel may fail */ }
          resolve({ success: false, output: agentText, error: `Timed out after ${DEFAULT_TIMEOUT / 1000}s` });
          return;
        }

        resolve({ success: true, output: agentText });
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        resolve({ success: false, output: agentText, error: message });
      } finally {
        await cleanup();
      }
    };

    runSession();
  });
}

function writeTaskStatus(statusPath: string, statusFile: TaskStatusFile): void {
  fs.writeFileSync(statusPath, JSON.stringify(statusFile, null, 2), 'utf-8');
}

function updateTaskStatusEntry(
  statusPath: string,
  taskId: string,
  status: 'pending' | 'in_progress' | 'completed' | 'failed' | 'skipped',
  error?: string
): TaskStatusFile {
  let statusFile: TaskStatusFile;

  if (fs.existsSync(statusPath)) {
    try {
      const content = fs.readFileSync(statusPath, 'utf-8');
      statusFile = JSON.parse(content) as TaskStatusFile;
    } catch {
      statusFile = { current_task_index: 0, total_tasks: 0, tasks: [] };
    }
  } else {
    statusFile = { current_task_index: 0, total_tasks: 0, tasks: [] };
  }

  const taskIndex = statusFile.tasks.findIndex(t => t.id === taskId);
  if (taskIndex >= 0) {
    statusFile.tasks[taskIndex].status = status;
    statusFile.tasks[taskIndex].attempts += 1;
    if (error) {
      statusFile.tasks[taskIndex].error = error;
    } else if (status === 'completed' || status === 'skipped') {
      delete statusFile.tasks[taskIndex].error;
    }
  }

  const nextPendingIndex = statusFile.tasks.findIndex(
    t => t.status === 'pending' || t.status === 'in_progress'
  );
  statusFile.current_task_index = nextPendingIndex >= 0 ? nextPendingIndex : statusFile.tasks.length;

  writeTaskStatus(statusPath, statusFile);
  return statusFile;
}

function initializeTaskStatus(statusPath: string, tasks: Task[]): TaskStatusFile {
  const statusFile: TaskStatusFile = {
    current_task_index: 0,
    total_tasks: tasks.length,
    tasks: tasks.map(t => ({
      id: t.id,
      status: 'pending' as const,
      attempts: 0,
    })),
  };
  writeTaskStatus(statusPath, statusFile);
  return statusFile;
}

export interface RalphExecutorOptions extends RalphTaskOptions {
  resumeFromTaskIndex?: number;
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
  }

  return adjustments;
}

export async function runRalphLoop(
  change: OpenSpecChange,
  context: RalphExecutorContext,
  options: RalphExecutorOptions = {}
): Promise<RalphLoopResult> {
  const resumeFromTaskIndex = options.resumeFromTaskIndex;

  const tasks = readPrdTasks(change.prdPath);
  if (!tasks || tasks.length === 0) {
    return {
      completed: 0,
      failed: 0,
      total: 0,
      taskResults: [],
      success: false,
    };
  }

  const sortedTasks = sortTasksByOrder(tasks);
  let statusFile = readTaskStatus(change.taskStatusPath);

  if (!statusFile) {
    statusFile = initializeTaskStatus(change.taskStatusPath, sortedTasks);
  }

  const startIndex = resumeFromTaskIndex ?? statusFile.current_task_index;
  const remainingTasks = sortedTasks.slice(startIndex);

  const taskResults: TaskResult[] = [];
  let completed = 0;
  let failed = 0;

  let learnings = loadLearningsFromDir(change.sessionMemoriesPath);

  for (let i = 0; i < remainingTasks.length; i++) {
    const task = remainingTasks[i];

    context.onTaskStart?.(task);

    const taskStatusEntry = statusFile.tasks.find(t => t.id === task.id);
    const currentAttempts = taskStatusEntry?.attempts ?? 0;

    const assembledContext = buildTaskContext({
      change,
      task,
      learnings,
      isRetry: false,
    });

    let lastError: string | undefined;
    let lastCategory: FailureCategory = 'ac_not_met';
    let taskSuccess = false;
    let attemptsUsed = 0;
    let shouldPause = false;
    let pauseReason: string | undefined;

    const maxRetries = options.maxRetries ?? 3;

    for (let attempt = currentAttempts + 1; attempt <= maxRetries + currentAttempts; attempt++) {
      const prompt = attempt > 1
        ? buildTaskContext({
            change,
            task,
            learnings,
            failureReason: lastError,
            isRetry: true,
          }).fullPrompt
        : assembledContext.fullPrompt;

      updateTaskStatusEntry(change.taskStatusPath, task.id, 'in_progress');

      const result = await executeTaskWithSpawn(task, prompt, context.worktreePath);

      attemptsUsed = attempt;
      if (result.success) {
        taskSuccess = true;
        updateTaskStatusEntry(change.taskStatusPath, task.id, 'completed');
        context.onTaskComplete?.(task, true, result.output);
        break;
      } else {
        lastError = result.error ?? 'Unknown error';
        lastCategory = categorizeFailure(lastError);

        const categoryConfig = FAILURE_CATEGORY_CONFIGS[lastCategory];
        const effectiveMaxAttempts = Math.min(maxRetries, categoryConfig.maxAttempts);

        await storeFailureLearning(change, task, lastError, lastCategory, attempt);

        if (!categoryConfig.retryable) {
          shouldPause = true;
          pauseReason = `${lastCategory} failure: ${lastError}. This cannot be retried automatically.`;
          break;
        }

        if (attempt >= effectiveMaxAttempts + currentAttempts) {
          shouldPause = true;
          pauseReason = `Max retries (${effectiveMaxAttempts}) exceeded for ${lastCategory} failure: ${lastError}`;
          break;
        }

        options.onRetry?.(task, attempt, lastError);
      }
    }

    if (!taskSuccess) {
      failed++;
      taskResults.push({
        taskId: task.id,
        status: 'failed',
        attempts: attemptsUsed,
        error: lastError,
      });
      updateTaskStatusEntry(change.taskStatusPath, task.id, 'failed', lastError);
      context.onTaskComplete?.(task, false, lastError ?? 'Max retries exceeded');

      if (shouldPause && context.onAskUser) {
        const question = `Task ${task.id} failed and requires user intervention.\n\nReason: ${pauseReason}\n\nOptions:\n1. Retry this task\n2. Skip this task and continue\n3. Abort the build\n\nWhat would you like to do?`;
        const answer = await context.onAskUser(question, task.id);

        if (answer.toLowerCase().includes('skip')) {
          updateTaskStatusEntry(change.taskStatusPath, task.id, 'skipped');
          taskResults[taskResults.length - 1].status = 'skipped';
        } else if (answer.toLowerCase().includes('retry')) {
          i--;
          continue;
        } else {
          const result: RalphLoopResult = {
            completed,
            failed,
            total: sortedTasks.length,
            taskResults,
            success: false,
            paused: true,
            pausedTaskId: task.id,
            pauseReason,
          };
          context.onLoopComplete?.(result);
          return result;
        }
      }
    } else {
      completed++;
      taskResults.push({
        taskId: task.id,
        status: 'completed',
        attempts: attemptsUsed,
      });
    }

    const updatedStatus = readTaskStatus(change.taskStatusPath);
    if (updatedStatus) {
      statusFile = updatedStatus;
    }

    learnings = loadLearningsFromDir(change.sessionMemoriesPath);
  }

  const result: RalphLoopResult = {
    completed,
    failed,
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