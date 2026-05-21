import type {
  WorkflowDefinitionSnapshot,
  WorkflowStageId,
} from '../model';
import type {
  WorkflowCheckDefinitionContext,
  WorkflowTaskDefinitionContext,
  WorkflowTasksFromDefinitionContext,
} from './types';

export function taskSourceDefinition(snapshot: WorkflowDefinitionSnapshot, stageId: WorkflowStageId): WorkflowTasksFromDefinitionContext | null {
  const source = snapshot.compiledStageDefinitions.find(stage => stage.stage === stageId)?.tasksFrom;
  if (!source) return null;
  if (typeof source === 'string') return { uses: source };
  return {
    uses: source.uses,
    with: source.with ? { ...source.with } : undefined,
  };
}

export function taskDefinition(snapshot: WorkflowDefinitionSnapshot, stageId: WorkflowStageId, taskId: string): WorkflowTaskDefinitionContext | null {
  const baseTaskId = baseRuntimeTaskId(taskId);
  const stage = snapshot.compiledStageDefinitions.find(candidate => candidate.stage === stageId);
  const task = stage?.tasks.find(candidate => candidate.id === taskId || candidate.id === baseTaskId)
    ?? stage?.checks
      .map(check => check.onFailure?.retry?.task)
      .find(candidate => candidate && (candidate.id === taskId || candidate.id === baseTaskId));
  if (!task) return null;
  return {
    id: taskId,
    title: task.title,
    uses: task.uses,
    with: task.with ? { ...task.with } : undefined,
  };
}

export function checkDefinition(snapshot: WorkflowDefinitionSnapshot, stageId: WorkflowStageId, checkName: string): WorkflowCheckDefinitionContext | null {
  const check = snapshot.compiledStageDefinitions
    .find(candidate => candidate.stage === stageId)
    ?.checks.find(candidate => candidate.name === checkName);
  if (!check) return null;
  return {
    name: check.name,
    title: check.title,
    uses: check.uses,
    with: check.with ? { ...check.with } : undefined,
  };
}

function baseRuntimeTaskId(taskId: string): string {
  return taskId.replace(/:\d+$/, '');
}
