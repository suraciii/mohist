import { z } from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { Tool, type ToolInstance } from '../agent-runtime/tool';

export interface TaskStatusContext {
  projectPath: string;
}

export type TaskStatusValue = 'pending' | 'in_progress' | 'completed' | 'failed' | 'skipped';

export interface TaskStatusEntry {
  id: string;
  status: TaskStatusValue;
  attempts: number;
  error?: string;
}

export interface TaskStatusFile {
  current_task_index: number;
  total_tasks: number;
  tasks: TaskStatusEntry[];
}

const STATUS_SCHEMA = z.enum(['pending', 'in_progress', 'completed', 'failed', 'skipped']);

function validateProjectPath(resolved: string, projectPath: string): string | null {
  if (
    !resolved.startsWith(projectPath + path.sep) &&
    resolved !== projectPath
  ) {
    return 'Error: change_path is outside the project directory';
  }
  return null;
}

function getTaskStatusPath(changePath: string): string {
  return path.join(changePath, 'task-status.json');
}

function readTaskStatusFile(filePath: string): TaskStatusFile | string {
  if (!fs.existsSync(filePath)) {
    return `Error: task-status.json not found at specified path`;
  }

  let raw: string;
  try {
    raw = fs.readFileSync(filePath, 'utf-8');
  } catch (err: unknown) {
    const message = err instanceof Error ? err.message : String(err);
    return `Error: failed to read task-status.json: ${message}`;
  }

  try {
    return JSON.parse(raw) as TaskStatusFile;
  } catch {
    return `Error: task-status.json contains invalid JSON`;
  }
}

function writeTaskStatusFile(filePath: string, data: TaskStatusFile): string | null {
  try {
    fs.writeFileSync(filePath, JSON.stringify(data, null, 2), 'utf-8');
    return null;
  } catch (err: unknown) {
    const message = err instanceof Error ? err.message : String(err);
    return `Error: failed to write task-status.json: ${message}`;
  }
}

function initializeTaskStatus(taskIds: string[]): TaskStatusFile {
  return {
    current_task_index: 0,
    total_tasks: taskIds.length,
    tasks: taskIds.map((id) => ({
      id,
      status: 'pending' as TaskStatusValue,
      attempts: 0,
    })),
  };
}

function formatTaskStatus(status: TaskStatusFile): string {
  const lines: string[] = [];

  lines.push(`# Task Status`);
  lines.push(`Current Task Index: ${status.current_task_index}`);
  lines.push(`Total Tasks: ${status.total_tasks}`);
  lines.push('');

  const completed = status.tasks.filter((t) => t.status === 'completed').length;
  const failed = status.tasks.filter((t) => t.status === 'failed').length;
  const pending = status.tasks.filter((t) => t.status === 'pending').length;
  const inProgress = status.tasks.filter((t) => t.status === 'in_progress').length;
  const skipped = status.tasks.filter((t) => t.status === 'skipped').length;

  lines.push(`Summary: ${completed} completed, ${failed} failed, ${inProgress} in_progress, ${skipped} skipped, ${pending} pending`);
  lines.push('');

  lines.push('## Tasks');
  for (const task of status.tasks) {
    let line = `- ${task.id}: ${task.status} (attempts: ${task.attempts})`;
    if (task.error) {
      line += ` — ${task.error}`;
    }
    lines.push(line);
  }

  const currentTask = status.tasks[status.current_task_index];
  if (currentTask) {
    lines.push('');
    lines.push(`Next task: ${currentTask.id} (${currentTask.status})`);
  }

  return lines.join('\n');
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createUpdateTaskStatusTool(context: TaskStatusContext): ToolInstance<any> {
  return Tool.define('update_task_status', {
    description:
      'Update the status of a specific task in task-status.json. ' +
      'Creates the file if it does not exist (initializes all tasks as pending). ' +
      'Supports statuses: pending, in_progress, completed, failed, skipped. ' +
      'Automatically increments attempts counter and tracks the current_task_index.',
    parameters: z
      .object({
        change_path: z
          .string()
          .describe('Path to the Change directory (relative to project root or absolute).'),
        task_id: z
          .string()
          .describe('The task identifier to update (e.g., "T-001").'),
        status: STATUS_SCHEMA.describe('New status for the task.'),
        error: z
          .string()
          .optional()
          .describe('Error message when status is "failed".'),
        all_task_ids: z
          .array(z.string())
          .optional()
          .describe(
            'List of all task IDs in order. Required when creating task-status.json for the first time. ' +
            'Used to initialize the full task list.',
          ),
      })
      .strict(),
    execute: async (params) => {
      const resolved = path.resolve(context.projectPath, params.change_path);
      const pathError = validateProjectPath(resolved, context.projectPath);
      if (pathError) return pathError;

      if (!fs.existsSync(resolved)) {
        return `Error: change directory does not exist: ${params.change_path}`;
      }

      const statusPath = getTaskStatusPath(resolved);

      if (!statusPath.startsWith(resolved + path.sep) && statusPath !== resolved) {
        return 'Error: task-status path escapes the change directory';
      }

      let statusFile: TaskStatusFile;

      if (fs.existsSync(statusPath)) {
        const existing = readTaskStatusFile(statusPath);
        if (typeof existing === 'string') return existing;
        statusFile = existing;
      } else {
        if (!params.all_task_ids || params.all_task_ids.length === 0) {
          return 'Error: task-status.json does not exist. Provide all_task_ids to initialize it.';
        }
        statusFile = initializeTaskStatus(params.all_task_ids);
      }

      const taskIndex = statusFile.tasks.findIndex((t) => t.id === params.task_id);
      if (taskIndex === -1) {
        if (params.all_task_ids) {
          statusFile.tasks.push({
            id: params.task_id,
            status: params.status,
            attempts: 1,
            error: params.error,
          });
          statusFile.total_tasks = statusFile.tasks.length;
        } else {
          return `Error: task "${params.task_id}" not found in task-status.json. ` +
            `Available tasks: ${statusFile.tasks.map((t) => t.id).join(', ')}. ` +
            `Provide all_task_ids to add new tasks.`;
        }
      } else {
        const task = statusFile.tasks[taskIndex];
        task.status = params.status;
        task.attempts += 1;
        if (params.error) {
          task.error = params.error;
        } else if (params.status === 'completed' || params.status === 'skipped') {
          delete task.error;
        }
      }

      let nextPendingIndex = statusFile.tasks.findIndex(
        (t) => t.status === 'pending' || t.status === 'in_progress',
      );
      if (nextPendingIndex === -1) {
        nextPendingIndex = statusFile.tasks.length;
      }
      statusFile.current_task_index = nextPendingIndex;

      const writeError = writeTaskStatusFile(statusPath, statusFile);
      if (writeError) return writeError;

      return `Task ${params.task_id} updated to "${params.status}" (attempt ${statusFile.tasks.find((t) => t.id === params.task_id)?.attempts ?? 'N/A'}). Current index: ${statusFile.current_task_index}.`;
    },
  });
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createGetTaskStatusTool(context: TaskStatusContext): ToolInstance<any> {
  return Tool.define('get_task_status', {
    description:
      'Read the current task execution status from task-status.json. ' +
      'Returns the current task index, all task statuses, and a summary of progress.',
    parameters: z
      .object({
        change_path: z
          .string()
          .describe('Path to the Change directory (relative to project root or absolute).'),
      })
      .strict(),
    execute: async (params) => {
      const resolved = path.resolve(context.projectPath, params.change_path);
      const pathError = validateProjectPath(resolved, context.projectPath);
      if (pathError) return pathError;

      const statusPath = getTaskStatusPath(resolved);

      if (!statusPath.startsWith(resolved + path.sep) && statusPath !== resolved) {
        return 'Error: task-status path escapes the change directory';
      }

      const statusFile = readTaskStatusFile(statusPath);
      if (typeof statusFile === 'string') return statusFile;

      return formatTaskStatus(statusFile);
    },
  });
}
