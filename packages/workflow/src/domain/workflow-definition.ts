import { WorkflowDomainError } from './errors';

export type WorkflowStageId = string;

export interface TaskDefinition {
  id: string;
  title: string;
  source?: 'builtin' | 'project';
  uses?: string;
  with?: Record<string, unknown>;
  onSuccess?: {
    emit?: string[];
  };
  dependsOn?: string[];
}

export interface CheckDefinition {
  name: string;
  title: string;
  source?: 'builtin' | 'project';
  uses?: string;
  with?: Record<string, unknown>;
  onFailure?: CheckFailureAction;
}

export interface CheckFailureRetry {
  limit: number;
  task: TaskDefinition;
}

export interface CheckFailureAction {
  retry?: CheckFailureRetry;
}

export interface StageResetAction {
  tasks?: string[];
  checks?: 'all' | string[];
  approval?: boolean;
}

export interface StageEventPolicy {
  reset: StageResetAction;
}

export interface WorkflowTasksFromDefinition {
  uses: string;
  with?: Record<string, unknown>;
}

export type WorkflowTasksFromSource = string | WorkflowTasksFromDefinition;

export interface StageDefinition {
  stage: WorkflowStageId;
  tasks: TaskDefinition[];
  tasksFrom?: WorkflowTasksFromSource;
  checks: CheckDefinition[];
  on?: Record<string, StageEventPolicy>;
  requiresApproval?: boolean;
}

export interface WorkflowDefinition {
  id: string;
  name?: string;
  stages: StageDefinition[];
  defaults?: Record<string, unknown>;
  artifacts?: Record<string, string>;
}

export function validateWorkflowDefinition(definition: WorkflowDefinition): void {
  if (!definition.id || definition.id.trim().length === 0) {
    throw new WorkflowDomainError('WorkflowDefinition requires an id');
  }
  if (!Array.isArray(definition.stages) || definition.stages.length === 0) {
    throw new WorkflowDomainError(`WorkflowDefinition ${definition.id} requires at least one stage`);
  }

  const seenStages = new Set<WorkflowStageId>();
  for (const stage of definition.stages) {
    if (seenStages.has(stage.stage)) {
      throw new WorkflowDomainError(`WorkflowDefinition ${definition.id} declares duplicate stage ${stage.stage}`);
    }
    seenStages.add(stage.stage);

    const taskIds = new Set<string>();
    for (const task of stage.tasks) {
      if (taskIds.has(task.id)) {
        throw new WorkflowDomainError(`WorkflowDefinition ${definition.id} declares duplicate task ${stage.stage}:${task.id}`);
      }
      taskIds.add(task.id);
    }

    const checkNames = new Set<string>();
    for (const check of stage.checks) {
      if (checkNames.has(check.name)) {
        throw new WorkflowDomainError(`WorkflowDefinition ${definition.id} declares duplicate check ${stage.stage}:${check.name}`);
      }
      checkNames.add(check.name);
    }

  }
}
