import { WorkflowRun, type WorkflowStageId } from '../model';
import type {
  WorkflowCheckDefinitionContext,
  WorkflowTaskDefinitionContext,
  WorkflowTasksFromDefinitionContext,
} from './types';

export function taskSourceDefinition(run: WorkflowRun, stageId: WorkflowStageId): WorkflowTasksFromDefinitionContext | null {
  const source = run.stageRuns.find(stageRun => stageRun.stage === stageId)?.definition.tasksFrom;
  if (!source) return null;
  if (typeof source === 'string') return { uses: source };
  return {
    uses: source.uses,
    with: source.with ? { ...source.with } : undefined,
  };
}

export function taskDefinition(run: WorkflowRun, stageId: WorkflowStageId, taskId: string): WorkflowTaskDefinitionContext | null {
  const task = run.stageRuns.find(stageRun => stageRun.stage === stageId)?.definition.tasks.find(candidate => candidate.id === taskId);
  if (!task) return null;
  return {
    id: taskId,
    title: task.title,
    uses: task.uses,
    with: task.with ? { ...task.with } : undefined,
  };
}

export function checkDefinition(run: WorkflowRun, stageId: WorkflowStageId, checkName: string): WorkflowCheckDefinitionContext | null {
  const check = run.stageRuns.find(stageRun => stageRun.stage === stageId)?.definition.checks.find(candidate => candidate.name === checkName);
  if (!check) return null;
  return {
    name: check.name,
    title: check.title,
    uses: check.uses,
    with: check.with ? { ...check.with } : undefined,
  };
}
