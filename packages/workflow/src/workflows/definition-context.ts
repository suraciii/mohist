import { WorkflowRun, type WorkflowStageId } from '../model';
import type {
  WorkflowCheckDefinitionContext,
  WorkflowTaskDefinitionContext,
  WorkflowTasksFromDefinitionContext,
} from './types';

export function taskSourceDefinition(run: WorkflowRun, stageId: WorkflowStageId): WorkflowTasksFromDefinitionContext | null {
  return run.tasksFromDefinition(stageId);
}

export function taskDefinition(run: WorkflowRun, stageId: WorkflowStageId, taskId: string): WorkflowTaskDefinitionContext | null {
  const task = run.taskDefinition(stageId, taskId);
  if (!task) return null;
  return {
    id: taskId,
    title: task.title,
    uses: task.uses,
    with: task.with ? { ...task.with } : undefined,
  };
}

export function checkDefinition(run: WorkflowRun, stageId: WorkflowStageId, checkName: string): WorkflowCheckDefinitionContext | null {
  const check = run.checkDefinition(stageId, checkName);
  if (!check) return null;
  return {
    name: check.name,
    title: check.title,
    uses: check.uses,
    with: check.with ? { ...check.with } : undefined,
  };
}
