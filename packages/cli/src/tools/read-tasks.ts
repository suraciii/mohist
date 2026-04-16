import { z } from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import type { Task, TasksFile } from '../artifacts/change-artifacts-manager';

export interface ReadTasksContext {
  projectPath: string;
}

function formatTasks(tasksFile: TasksFile): string {
  const lines: string[] = [];

  lines.push(`# Tasks (version ${tasksFile.version})`);
  lines.push('');

  const completed = tasksFile.tasks.filter(t => t.passes).length;
  const failed = tasksFile.tasks.filter(t => t.error).length;
  const pending = tasksFile.tasks.filter(t => !t.passes && !t.error).length;
  lines.push(`Summary: ${completed} passed, ${failed} failed, ${pending} pending (${tasksFile.tasks.length} total)`);
  lines.push('');

  for (const task of tasksFile.tasks) {
    const status = task.passes ? 'PASS' : (task.error ? 'FAIL' : 'TODO');
    lines.push(`### ${task.id}: ${task.title} [${status}]`);
    lines.push(`- Order: ${task.order}`);
    if (task.dependsOn && task.dependsOn.length > 0) {
      lines.push(`- Depends on: ${task.dependsOn.join(', ')}`);
    }
    if (task.spec) {
      lines.push(`- Spec: ${task.spec}`);
    }
    lines.push(`- Passes: ${task.passes}`);
    lines.push(`- Attempts: ${task.attempts}`);
    if (task.error) {
      lines.push(`- Error: ${task.error}`);
    }
    lines.push('');
    lines.push(`Description: ${task.description}`);
    if (task.acceptanceCriteria && task.acceptanceCriteria.length > 0) {
      lines.push('');
      lines.push('Acceptance Criteria:');
      for (const ac of task.acceptanceCriteria) {
        lines.push(`  - [${task.passes ? 'x' : ' '}] ${ac}`);
      }
    }
    lines.push('');
  }

  return lines.join('\n');
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createReadTasksTool(context: ReadTasksContext): ToolInstance<any> {
  return Tool.define('read_tasks', {
    description:
      'Read the tasks.json file from a Change directory. Returns a structured task list with IDs, titles, descriptions, ' +
      'acceptance criteria, dependencies, passes/attempts/error status. Use this to understand the full scope of work before executing tasks.',
    parameters: z
      .object({
        change_path: z
          .string()
          .describe(
            'Path to the Change directory (relative to project root or absolute). ' +
            'The directory should contain a tasks.json file.',
          ),
      })
      .strict(),
    execute: async (params) => {
      const resolved = path.resolve(context.projectPath, params.change_path);

      if (
        !resolved.startsWith(context.projectPath + path.sep) &&
        resolved !== context.projectPath
      ) {
        return 'Error: path is outside the project directory';
      }

      const tasksPath = path.join(resolved, 'tasks.json');

      if (!fs.existsSync(tasksPath)) {
        return `Error: tasks.json not found at ${params.change_path}/tasks.json`;
      }

      let raw: string;
      try {
        raw = fs.readFileSync(tasksPath, 'utf-8');
      } catch (err: unknown) {
        const message = err instanceof Error ? err.message : String(err);
        return `Error: failed to read tasks.json: ${message}`;
      }

      let tasksFile: TasksFile;
      try {
        tasksFile = JSON.parse(raw);
      } catch {
        return `Error: tasks.json contains invalid JSON`;
      }

      if (!tasksFile.tasks || !Array.isArray(tasksFile.tasks)) {
        return `Error: tasks.json is missing required "tasks" array`;
      }

      return formatTasks(tasksFile);
    },
  });
}
