import { Stage } from '../../types';
import type {
  CheckDefinition,
  StageDefinition,
  StageEventPolicy,
  TaskDefinition,
  WorkflowDefinition,
  WorkflowTasksFromSource,
} from './types';

export type WorkflowTaskSourceDefinition = Omit<TaskDefinition, 'source'> & {
  source?: TaskDefinition['source'];
};

export type WorkflowCheckSourceDefinition = Omit<CheckDefinition, 'source' | 'name'> & {
  id?: string;
  name?: string;
  source?: CheckDefinition['source'];
};

export interface WorkflowStageSourceDefinition {
  id?: Stage;
  stage?: Stage;
  tasks?: WorkflowTaskSourceDefinition[];
  tasksFrom?: WorkflowTasksFromSource;
  checks?: WorkflowCheckSourceDefinition[];
  on?: Record<string, StageEventPolicy>;
  approval?: boolean;
}

export interface WorkflowSourceDefinition {
  id: string;
  name?: string;
  defaults?: Record<string, unknown>;
  stages: WorkflowStageSourceDefinition[];
}

export interface ParseWorkflowDefinitionOptions {
  taskSource?: TaskDefinition['source'];
  checkSource?: CheckDefinition['source'];
}

export function parseWorkflowDefinitionSource(
  source: WorkflowSourceDefinition,
  options: ParseWorkflowDefinitionOptions = {},
): WorkflowDefinition {
  return {
    id: source.id,
    name: source.name,
    defaults: source.defaults ? { ...source.defaults } : undefined,
    stages: source.stages.map(stage => parseStageSource(stage, options)),
  };
}

function parseStageSource(
  source: WorkflowStageSourceDefinition,
  options: ParseWorkflowDefinitionOptions,
): StageDefinition {
  const stage = source.stage ?? source.id;
  if (!stage) {
    throw new Error(`Workflow stage requires id`);
  }

  const tasks = (source.tasks ?? []).map(task => ({
    ...task,
    source: task.source ?? options.taskSource,
    with: task.with ? { ...task.with } : undefined,
    emits: task.emits ? [...task.emits] : undefined,
    dependsOn: task.dependsOn ? [...task.dependsOn] : undefined,
  }));

  const checks = (source.checks ?? []).map(check => {
    const name = check.name ?? check.id;
    if (!name) {
      throw new Error(`Workflow check in stage ${stage} requires id`);
    }
    return {
      ...check,
      name,
      id: undefined,
      source: check.source ?? options.checkSource,
      with: check.with ? { ...check.with } : undefined,
    };
  });

  return {
    stage,
    tasks,
    tasksFrom: source.tasksFrom,
    checks,
    on: source.on ? Object.fromEntries(Object.entries(source.on).map(([event, policy]) => [event, { ...policy }])) : undefined,
    requiresApproval: source.approval || undefined,
    approvalCheckName: source.approval ? 'user-approval' : undefined,
  };
}
