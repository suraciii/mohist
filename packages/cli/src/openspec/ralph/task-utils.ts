import * as fs from 'fs';
import type { Task } from '../context-assembler';
import { getOrderValue, type FailureCategory, type DependencyValidationResult } from './types';

export { getOrderValue, type FailureCategory, type DependencyValidationResult } from './types';

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

export function categorizeFailure(
  error: string,
  options?: { wipCommitted?: boolean; failureKind?: string }
): FailureCategory {
  const lowerError = error.toLowerCase();

  if (options?.failureKind === 'session_failed') {
    return 'session_failed';
  }

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